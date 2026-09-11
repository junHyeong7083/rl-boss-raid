"""train_raid.py — 2계층 하이브리드(BT+RL) 레이드 NPC 학습.

NUM2.md v3 아키텍처의 Layer 2(역할별 독립 PPO) 학습 스크립트.

핵심 설계:
  - 역할별 독립 네트워크: TankNet / HealerNet / SupportNet (RoleActorCritic).
    딜러(플레이어 슬롯)는 FSM 고정 + 도메인 랜덤화(PlayerModelWrapper).
  - **학습 중에도 Layer 1 BT 활성**. BT 가 fire 한 턴은 RL 샘플에서 제외 —
    RL 은 "자기가 결정한 턴"만 학습(semi-MDP: BT 턴 보상은 직전 RL 결정에 누적 귀속).
  - 보상 combat_only(기믹 보상 제거 — 기믹은 BT 소관, 학습 공간 축소).
  - 커리큘럼: boss_max_hp 12000 → 28000 → 55000 (승률 도달 시 다음 단계, 이어서 학습).
  - BC 웜스타트(옵션): FSM 시연 수집 → 행동 복제 사전학습.
  - **플레이어 모델 도메인 랜덤화**: 에피소드마다 aggressive/safe/novice 성향 샘플 →
    NPC 과적합 방지. eval 은 성향별 승률 분리 리포트, 로그에 성향 기록.

산출물(streamer 호환):
  models_raid/{role}_{stage}.pt  (단계별 역할 체크포인트)
  models_raid/final.pt           ({"nets": {role_name: state_dict}} — raid_streamer 로드 형식)
  models_raid/train_log.csv      (학습/평가/인간성 지표)

실행:
  set PYTHONIOENCODING=utf-8
  python train_raid.py --episodes 20000 --device cuda
  python train_raid.py --episodes 500 --device cpu           # 스모크
  python train_raid.py --bc-episodes 2000 --episodes 20000   # BC 웜스타트
  python train_raid.py --resume models_raid/final.pt --episodes 5000
"""
from __future__ import annotations
import argparse
import math
import os
import random
import time
from collections import deque
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

import numpy as np

from src.raid import (
    RaidEnv, RaidConfig, PartyRole, RaidActionID, FSMNpcPolicy,
    BTGimmickLayer, RewardComputer, build_role_net,
)
from src.raid.trust_gate import LicenseGate, LICENSED_RULES

TRAIN_ROLES = (PartyRole.TANK, PartyRole.HEALER, PartyRole.SUPPORT)
_MOVE_ACTIONS = [int(a) for a in (
    RaidActionID.MOVE_UP, RaidActionID.MOVE_DOWN, RaidActionID.MOVE_LEFT,
    RaidActionID.MOVE_RIGHT, RaidActionID.MOVE_UP_LEFT, RaidActionID.MOVE_UP_RIGHT,
    RaidActionID.MOVE_DOWN_LEFT, RaidActionID.MOVE_DOWN_RIGHT,
)]
_MOVE_SET = set(_MOVE_ACTIONS)


# ─────────────────── 하이퍼파라미터 ───────────────────

@dataclass
class TrainCfg:
    lr: float = 3e-4
    gamma: float = 0.99
    gae_lambda: float = 0.95
    clip_eps: float = 0.2
    epochs: int = 4
    batch_size: int = 512
    entropy_coef: float = 0.01
    value_coef: float = 0.5
    max_grad_norm: float = 0.5
    hidden: int = 256
    episodes_per_update: int = 8    # 이 에피소드 수마다 PPO 업데이트
    eval_interval: int = 200        # 에피소드 단위 평가 주기
    eval_episodes: int = 24         # 평가 에피소드 수(성향별 분리)
    winrate_window: int = 200       # 커리큘럼 승률 롤링 윈도우
    save_interval: int = 500


# ─────────────────── 플레이어 모델 도메인 랜덤화 ───────────────────

class PlayerModelWrapper:
    """FSM 딜러 정책을 감싸 에피소드별 성향(aggressive/safe/novice) + 공통 노이즈를 부여.

    env 수정 없이 딜러 액션만 후처리. new_episode() 로 매 에피소드 성향을 재샘플.
    """

    def __init__(self, env: "RaidEnv", uid: int, cfg: RaidConfig, rng: random.Random):
        self.env = env
        self.uid = uid
        self.cfg = cfg
        self.rng = rng
        self.fsm = FSMNpcPolicy(env, uid)
        self.disposition = "aggressive"
        self.params = cfg.player_model_params["aggressive"]
        self.new_episode()

    def new_episode(self):
        if not self.cfg.player_model_randomize:
            self.disposition = "aggressive"
        else:
            names = list(self.cfg.player_model_weights.keys())
            weights = [self.cfg.player_model_weights[n] for n in names]
            self.disposition = self.rng.choices(names, weights=weights, k=1)[0]
        self.params = self.cfg.player_model_params[self.disposition]
        return self.disposition

    def _random_move(self) -> int:
        return self.rng.choice(_MOVE_ACTIONS)

    def _jitter(self, action: int) -> int:
        idx = _MOVE_ACTIONS.index(action)
        shift = self.rng.choice((-1, 1))
        return _MOVE_ACTIONS[(idx + shift) % len(_MOVE_ACTIONS)]

    def act(self) -> int:
        env = self.env
        u = env.units[self.uid]
        if not u.alive:
            return int(RaidActionID.STAY)
        p = self.params
        b = env.boss
        ap = b.active_pattern
        world_shapes = [s for tg in b.telegraphs for s in tg.world_shapes]
        in_tele = any(s.contains((u.x, u.y)) for s in world_shapes)

        # 1) 회피 결정 — 성향별 evade_turns 로 회피 시점 제어.
        if in_tele and ap is not None and ap.mode == "steps":
            if ap.turns_remaining <= int(p["evade_turns"]):
                mv = self.fsm._safe_move(u)
                if mv is not None:
                    # novice 는 회피를 종종 실수(오행동)
                    if self.rng.random() < p["random_move_prob"]:
                        return self._random_move()
                    return mv
            else:
                # 아직 회피 시점 아님 → 공격 우선(aggressive 성향 구현)
                a = self.fsm._attack_or_approach(u, prefer_skill=False)
                return self._apply_noise(a)

        # 2) 기본 FSM 딜러 행동 + 공통 노이즈
        return self._apply_noise(int(self.fsm.act()))

    def _apply_noise(self, a: int) -> int:
        p = self.params
        rng = self.rng
        # 가끔 아무것도 안 함(idle)
        if rng.random() < p["idle_prob"]:
            return int(RaidActionID.STAY)
        # 스킬 사용 타이밍 랜덤 오프셋 — 스킬을 미루고 평타/대기로 다운그레이드
        skill_ids = (int(RaidActionID.ATTACK_SKILL), int(RaidActionID.SKILL_2),
                     int(RaidActionID.ULTIMATE))
        if a in skill_ids and rng.random() < p["skill_defer_prob"]:
            a = int(RaidActionID.ATTACK_BASIC)
        # 오행동(무작위 이동)
        if rng.random() < p["random_move_prob"]:
            return self._random_move()
        # 이동 방향 지터
        if a in _MOVE_SET and rng.random() < p["jitter_prob"]:
            return self._jitter(a)
        return a


# ─────────────────── 롤아웃 버퍼 + GAE ───────────────────

class RoleBuffer:
    def __init__(self):
        self.obs: List[np.ndarray] = []
        self.act: List[int] = []
        self.logp: List[float] = []
        self.val: List[float] = []
        self.rew: List[float] = []
        self.done: List[bool] = []
        self.adv: List[float] = []
        self.ret: List[float] = []

    def add(self, obs, act, logp, val, rew, done):
        self.obs.append(obs); self.act.append(act); self.logp.append(logp)
        self.val.append(val); self.rew.append(rew); self.done.append(done)

    def __len__(self):
        return len(self.obs)

    def compute_gae(self, gamma, lam):
        self.adv = [0.0] * len(self.rew)
        self.ret = [0.0] * len(self.rew)
        gae = 0.0
        for t in reversed(range(len(self.rew))):
            next_val = 0.0 if (t == len(self.rew) - 1 or self.done[t]) else self.val[t + 1]
            nonterm = 0.0 if self.done[t] else 1.0
            delta = self.rew[t] + gamma * next_val * nonterm - self.val[t]
            gae = delta + gamma * lam * nonterm * gae
            self.adv[t] = gae
            self.ret[t] = gae + self.val[t]

    def clear(self):
        self.__init__()


# ─────────────────── 학습기 ───────────────────

# BT 규칙을 껐을 때 RL 에 복원해야 하는 기믹 보상 그룹 (경계 재배치 실험 M1~M4)
RULE_REWARD_GROUP = {
    "seal_hide": "seal",
    "brand_spread": "brand",
    "stagger_dps": "stagger",
    "imminent_escape": None,   # 위험 페널티는 combat_only 에도 이미 있음
    "yellow_escape": None,
    "rush_lure": "rush",
}


class RaidTrainer:
    def __init__(self, cfg: RaidConfig, tcfg: TrainCfg, device_str: str, seed: int = 0,
                 intervention_mode: str = "smdp",
                 bt_disabled=None, bt_extra=None, bt_mode: str = "fixed",
                 gimmick_rewards: str = "auto"):
        """intervention_mode — BT 개입 턴의 학습 표본 처리(제거 실험 D/E/F):
          "smdp"  (F, 기본): 개입 턴 표본 제외 + 보상을 직전 RL 결정에 누적 (현행)
          "naive" (D): BT 행동을 정책 표본처럼 버퍼에 포함(단순 혼합) — logp/V 는 현 정책으로 평가
          "drop"  (E): 개입 턴 표본 제외 + 해당 턴 보상도 폐기
        bt_disabled/bt_extra — BT 규칙 on/off (경계 재배치 실험 M1~M5)."""
        import torch
        self.torch = torch
        self.cfg = cfg
        self.tcfg = tcfg
        self.device = torch.device(device_str)
        self.rng = random.Random(seed)
        np.random.seed(seed); torch.manual_seed(seed)
        if intervention_mode not in ("smdp", "naive", "drop"):
            raise ValueError(f"unknown intervention_mode: {intervention_mode}")
        self.intervention_mode = intervention_mode
        self.bt_mode = bt_mode
        if bt_mode == "none":
            bt_disabled = set(bt_disabled or ()) | set(LICENSED_RULES)
        self.bt_disabled = frozenset(bt_disabled or ())
        self.bt_extra = frozenset(bt_extra or ())

        self.env = RaidEnv(cfg, seed=seed)
        # combat_only 보상 + 꺼진 BT 규칙의 기믹 보상 그룹 선택 복원(RL 이 학습해야 하므로)
        if gimmick_rewards == "on":
            # 면허 캠페인: 네 조건의 보상 함수를 동일하게 맞춘다(게이팅만 변인으로 남김).
            gated = set(LICENSED_RULES)
        elif gimmick_rewards == "off":
            gated = set()
        else:                                  # auto — 기존 동작
            gated = set(self.bt_disabled)
            if bt_mode in ("adaptive", "mono"):
                # 이양 후에는 RL 이 그 기믹을 수행하므로 해당 보상을 학습 내내 활성화
                gated |= set(LICENSED_RULES)
        groups = {g for r in gated for g in [RULE_REWARD_GROUP.get(r)] if g}
        self.reward_groups = groups
        self.env.reward_computer = RewardComputer(cfg, mode="combat_only",
                                                  gimmick_groups=groups)

        # 역할별 네트워크 + 옵티마이저
        self.nets = {}
        self.opts = {}
        for role in TRAIN_ROLES:
            net = build_role_net(cfg.obs_size, cfg.num_actions, tcfg.hidden, self.device)
            self.nets[role] = net
            self.opts[role] = torch.optim.Adam(net.parameters(), lr=tcfg.lr)

        # NPC uid ↔ role
        self.npc_uids = [i for i, r in enumerate(cfg.party_roles) if r != PartyRole.DEALER]
        self.uid_role = {i: cfg.party_roles[i] for i in self.npc_uids}
        gate_mode = {"adaptive": "adaptive", "mono": "mono"}.get(bt_mode)
        self.gate = (LicenseGate(cfg, self.npc_uids, mode=gate_mode,
                                 rng=random.Random(seed + 1))
                     if gate_mode else None)
        self.bts = {uid: BTGimmickLayer(self.env, uid,
                                        disabled_rules=self.bt_disabled,
                                        extra_rules=self.bt_extra,
                                        gate=self.gate)
                    for uid in self.npc_uids}
        self.player = PlayerModelWrapper(self.env, cfg.player_slot, cfg, self.rng)

        self.buffers = {role: RoleBuffer() for role in TRAIN_ROLES}

    # ── 액션 선택 ──
    def _rl_action(self, role, obs, deterministic=False):
        torch = self.torch
        o = torch.as_tensor(obs, dtype=torch.float32, device=self.device).unsqueeze(0)
        with torch.no_grad():
            a, logp, val = self.nets[role].get_action(o, deterministic=deterministic)
        return int(a.item()), float(logp.item()), float(val.item())

    def _eval_action(self, role, obs, act):
        """주어진 (obs, act)의 현 정책 logp/V — naive(단순 혼합) 모드에서 BT 행동을
        정책 표본처럼 취급할 때 사용."""
        torch = self.torch
        o = torch.as_tensor(obs, dtype=torch.float32, device=self.device).unsqueeze(0)
        a = torch.as_tensor([int(act)], dtype=torch.long, device=self.device)
        with torch.no_grad():
            logp, val, _ = self.nets[role].evaluate_actions(o, a)
        return float(logp.item()), float(val.item())

    # ── 한 에피소드 롤아웃(학습 표본 수집) ──
    def run_episode(self, collect=True) -> Dict:
        env = self.env
        env.reset()
        self.player.new_episode()
        # semi-MDP: uid 별 열린 RL 결정 트랜지션(다음 결정까지 보상 누적)
        pending = {role: None for role in TRAIN_ROLES}
        # 관측 지연 버퍼 — 배포(HybridPolicy, hybrid_obs_delay_turns)와 동일 지연으로
        # 학습해야 학습/배포 불일치가 없다. BT 턴에도 매 턴 채운다.
        delay = int(getattr(self.cfg, "hybrid_obs_delay_turns", 0))
        obs_hist = {uid: deque(maxlen=delay + 1) for uid in self.npc_uids}

        # 인간성/기믹 지표 집계
        bt_fires = 0
        rl_turns = 0
        move_changes = 0
        move_total = 0
        last_move = {uid: None for uid in self.npc_uids}
        gimmick_success = 0
        gimmick_total = 0
        # 논문용 상세 수집: 이벤트 타입별 횟수(파티 전체), 역할별 보상 합,
        # 탱커 어그로 1위 점유율, BT 규칙별 발화 횟수
        ev_counts: Dict[str, int] = {}
        role_rewards: Dict[str, float] = {}
        tank_uid = next((uid for uid in self.npc_uids
                         if self.uid_role[uid] == PartyRole.TANK), None)
        tank_top = 0
        total_turns = 0
        for bt in self.bts.values():
            bt.reset_counts()

        def flush(role, done_flag):
            pen = pending[role]
            if pen is not None:
                self.buffers[role].add(pen["obs"], pen["act"], pen["logp"],
                                       pen["val"], pen["rew"], done_flag)
                pending[role] = None

        while not env.done:
            actions = {"p0": self.player.act()}
            rl_decisions = {}   # uid -> (role, obs, act, logp, val)
            bt_decisions = {}   # uid -> (role, obs, act) — naive(단순 혼합) 모드 전용
            for uid in self.npc_uids:
                obs_hist[uid].append(env._observe(uid))
            for uid in self.npc_uids:
                role = self.uid_role[uid]
                a = self.bts[uid].act()
                if a is not None:
                    bt_fires += 1
                    actions[f"p{uid}"] = int(a)
                    if collect and self.intervention_mode == "naive":
                        bt_decisions[uid] = (role, obs_hist[uid][0], int(a))
                else:
                    rl_turns += 1
                    obs = obs_hist[uid][0]   # delay 턴 전 관측(배포와 동일)
                    act, logp, val = self._rl_action(role, obs, deterministic=False)
                    actions[f"p{uid}"] = act
                    rl_decisions[uid] = (role, obs, act, logp, val)
                # 이동 방향 전환율(인간성 지표)
                a_final = actions[f"p{uid}"]
                if a_final in _MOVE_SET:
                    move_total += 1
                    if last_move[uid] is not None and last_move[uid] != a_final:
                        move_changes += 1
                    last_move[uid] = a_final

            _, rewards, _, infos = env.step(actions)
            done = env.done

            # 기믹 성공/실패 집계(연구 로깅용 — 학습 보상엔 미반영)
            for evs in env.step_events.values():
                for e in evs:
                    t = e.get("type", "")
                    if t.endswith("_success") and t.split("_")[0] in (
                            "counter", "stagger", "seal", "guard", "parry", "mechanic"):
                        gimmick_success += 1; gimmick_total += 1
                    elif t.endswith("_fail") and t.split("_")[0] in (
                            "counter", "stagger", "seal", "parry", "mechanic"):
                        gimmick_total += 1

            # 논문용 수집: 이벤트 타입별 카운트 / 역할별 보상 / 탱커 어그로 점유율
            total_turns += 1
            if tank_uid is not None and env.boss.top_aggro_uid() == tank_uid:
                tank_top += 1
            for evs in env.step_events.values():
                for e in evs:
                    t = e.get("type", "")
                    ev_counts[t] = ev_counts.get(t, 0) + 1
            for uid in self.npc_uids:
                rname = self.uid_role[uid].name.lower()
                role_rewards[rname] = role_rewards.get(rname, 0.0) \
                    + float(rewards.get(f"p{uid}", 0.0))

            # 보상 귀속 — intervention_mode 에 따라 BT 개입 턴 처리 분기(제거 실험 D/E/F):
            #   smdp(F): RL 턴이면 pending 닫고 새로 열고, BT 턴 보상은 직전 pending 에 누적
            #   naive(D): BT 턴도 정책 표본으로 버퍼에 포함(logp/V 는 현 정책 평가값)
            #   drop(E): BT 턴 표본 제외 + 그 턴 보상 폐기
            for uid in self.npc_uids:
                role = self.uid_role[uid]
                rw = float(rewards.get(f"p{uid}", 0.0))
                if uid in rl_decisions:
                    if collect:
                        flush(role, done_flag=False)
                        _, obs, act, logp, val = rl_decisions[uid]
                        pending[role] = {"obs": obs, "act": act, "logp": logp,
                                         "val": val, "rew": rw}
                elif uid in bt_decisions:
                    # naive: BT 행동을 "정책이 고른 것처럼" 표본화 (단순 혼합의 정의)
                    flush(role, done_flag=False)
                    _, obs, act = bt_decisions[uid]
                    logp, val = self._eval_action(role, obs, act)
                    pending[role] = {"obs": obs, "act": act, "logp": logp,
                                     "val": val, "rew": rw}
                else:
                    if collect and pending[role] is not None \
                            and self.intervention_mode != "drop":
                        pending[role]["rew"] += rw

        # 에피소드 종료 — 열린 트랜지션 flush(done=True)
        if collect:
            for role in TRAIN_ROLES:
                flush(role, done_flag=True)

        if self.gate is not None:
            self.gate.end_episode()
        rule_fires: Dict[str, int] = {}
        for bt in self.bts.values():
            for rule, n in bt.fire_counts.items():
                rule_fires[rule] = rule_fires.get(rule, 0) + n
        return {
            "victory": env.victory, "wipe": env.wipe, "steps": env.current_step,
            "boss_hp_ratio": env.boss.hp / self.cfg.boss_max_hp,
            "bt_fire_ratio": bt_fires / max(1, bt_fires + rl_turns),
            "move_change_ratio": move_changes / max(1, move_total),
            "gimmick_success_rate": gimmick_success / max(1, gimmick_total),
            "disposition": self.player.disposition,
            # 논문용 상세(metrics.jsonl 로 직렬화)
            "events": ev_counts,
            "rule_fires": rule_fires,
            "role_rewards": {k: round(v, 2) for k, v in role_rewards.items()},
            "tank_top_aggro_ratio": round(tank_top / max(1, total_turns), 4),
            # 면허 프로토콜 상태(신뢰 궤적·이양/회수 사건) — 대표 그림 소스
            "licenses": self.gate.snapshot() if self.gate is not None else None,
            "license_events": self.gate.pop_events() if self.gate is not None else [],
        }

    # ── PPO 업데이트 ──
    def update(self) -> Dict[str, float]:
        torch = self.torch
        tcfg = self.tcfg
        stats = {}
        for role in TRAIN_ROLES:
            buf = self.buffers[role]
            if len(buf) < 2:
                buf.clear(); continue
            buf.compute_gae(tcfg.gamma, tcfg.gae_lambda)
            obs = torch.as_tensor(np.array(buf.obs), dtype=torch.float32, device=self.device)
            act = torch.as_tensor(np.array(buf.act), dtype=torch.long, device=self.device)
            old_logp = torch.as_tensor(np.array(buf.logp), dtype=torch.float32, device=self.device)
            adv = torch.as_tensor(np.array(buf.adv), dtype=torch.float32, device=self.device)
            ret = torch.as_tensor(np.array(buf.ret), dtype=torch.float32, device=self.device)
            adv = (adv - adv.mean()) / (adv.std() + 1e-8)

            n = len(buf)
            net = self.nets[role]; opt = self.opts[role]
            ploss = vloss = ent = 0.0; nb = 0
            clip_frac = kl_sum = 0.0   # 학습 안정성 지표(제거 실험 비교용)
            for _ in range(tcfg.epochs):
                idx = np.random.permutation(n)
                for s in range(0, n, tcfg.batch_size):
                    bi = idx[s:s + tcfg.batch_size]
                    bi_t = torch.as_tensor(bi, dtype=torch.long, device=self.device)
                    b_obs = obs[bi_t]; b_act = act[bi_t]
                    b_old = old_logp[bi_t]; b_adv = adv[bi_t]; b_ret = ret[bi_t]
                    new_logp, val, entropy = net.evaluate_actions(b_obs, b_act)
                    ratio = torch.exp(new_logp - b_old)
                    s1 = ratio * b_adv
                    s2 = torch.clamp(ratio, 1 - tcfg.clip_eps, 1 + tcfg.clip_eps) * b_adv
                    policy_loss = -torch.min(s1, s2).mean()
                    value_loss = ((val - b_ret) ** 2).mean()
                    entropy_loss = entropy.mean()
                    loss = policy_loss + tcfg.value_coef * value_loss - tcfg.entropy_coef * entropy_loss
                    opt.zero_grad(); loss.backward()
                    torch.nn.utils.clip_grad_norm_(net.parameters(), tcfg.max_grad_norm)
                    opt.step()
                    ploss += policy_loss.item(); vloss += value_loss.item()
                    ent += entropy_loss.item(); nb += 1
                    with torch.no_grad():
                        clip_frac += float((torch.abs(ratio - 1.0) > tcfg.clip_eps)
                                           .float().mean().item())
                        kl_sum += float((b_old - new_logp).mean().item())
            stats[role.name.lower()] = {
                "policy_loss": ploss / max(1, nb), "value_loss": vloss / max(1, nb),
                "entropy": ent / max(1, nb), "samples": n,
                "clip_frac": clip_frac / max(1, nb), "approx_kl": kl_sum / max(1, nb),
            }
            buf.clear()
        return stats

    # ── BC 웜스타트: FSM 시연 → 행동 복제 ──
    def behavior_clone(self, n_episodes: int, epochs: int = 3):
        torch = self.torch
        if n_episodes <= 0:
            return
        print(f"[BC] FSM 시연 {n_episodes} 에피소드 수집...", flush=True)
        demo = {role: {"obs": [], "act": []} for role in TRAIN_ROLES}
        # 시연용 FSM NPC(순수 FSM — BT/RL 없이 행동 복제 타깃)
        fsm_npcs = {uid: FSMNpcPolicy(self.env, uid) for uid in self.npc_uids}
        delay = int(getattr(self.cfg, "hybrid_obs_delay_turns", 0))
        for ep in range(n_episodes):
            self.env.reset()
            self.player.new_episode()
            # BC 도 롤아웃과 동일한 관측 지연 사용(학습/배포 일치)
            obs_hist = {uid: deque(maxlen=delay + 1) for uid in self.npc_uids}
            while not self.env.done:
                actions = {"p0": self.player.act()}
                for uid in self.npc_uids:
                    obs_hist[uid].append(self.env._observe(uid))
                    role = self.uid_role[uid]
                    a = int(fsm_npcs[uid].act())
                    demo[role]["obs"].append(obs_hist[uid][0]); demo[role]["act"].append(a)
                    actions[f"p{uid}"] = a
                self.env.step(actions)
        for role in TRAIN_ROLES:
            X = demo[role]["obs"]; Y = demo[role]["act"]
            if not X:
                continue
            # 메모리 안전화(2026-09-08): 아레나 확대로 에피소드가 길어져 시연이 역할당
            # 100만 표본(~510MB)에 달함 — 전체를 GPU 에 올리다 병렬 실행 시 OOM/세그폴트.
            # 시연은 CPU 텐서로 보관하고 미니배치만 GPU 로 옮긴다. 리스트는 즉시 해제.
            obs_np = np.asarray(X, dtype=np.float32)
            act_np = np.asarray(Y, dtype=np.int64)
            demo[role]["obs"] = None; demo[role]["act"] = None
            X = Y = None
            obs = torch.from_numpy(obs_np)
            act = torch.from_numpy(act_np)
            net = self.nets[role]; opt = self.opts[role]
            n = obs.shape[0]
            for e in range(epochs):
                idx = np.random.permutation(n)
                tot = 0.0; nb = 0
                for s in range(0, n, self.tcfg.batch_size):
                    bi = torch.from_numpy(idx[s:s + self.tcfg.batch_size])
                    b_obs = obs[bi].to(self.device)
                    b_act = act[bi].to(self.device)
                    logits, _ = net(b_obs)
                    loss = torch.nn.functional.cross_entropy(logits, b_act)
                    opt.zero_grad(); loss.backward(); opt.step()
                    tot += loss.item(); nb += 1
                print(f"[BC] {role.name.lower()} epoch {e+1}/{epochs} ce={tot/max(1,nb):.3f} "
                      f"(n={n})", flush=True)

    # ── 평가(성향별 승률 분리) ──
    def evaluate(self, n_episodes: int) -> Dict:
        by_disp = {d: {"win": 0, "n": 0, "kill_steps": []} for d in self.cfg.player_model_weights}
        wins = 0
        kill_steps = []
        bt_ratios = []
        move_ratios = []
        gim_rates = []
        for _ in range(n_episodes):
            r = self.run_episode(collect=False)
            d = r["disposition"]
            by_disp[d]["n"] += 1
            if r["victory"]:
                wins += 1
                by_disp[d]["win"] += 1
                kill_steps.append(r["steps"])
                by_disp[d]["kill_steps"].append(r["steps"])
            bt_ratios.append(r["bt_fire_ratio"])
            move_ratios.append(r["move_change_ratio"])
            gim_rates.append(r["gimmick_success_rate"])
        disp_wr = {d: (by_disp[d]["win"] / by_disp[d]["n"] if by_disp[d]["n"] else 0.0)
                   for d in by_disp}
        return {
            "winrate": wins / max(1, n_episodes),
            "avg_kill_steps": (sum(kill_steps) / len(kill_steps)) if kill_steps else 0.0,
            "bt_fire_ratio": sum(bt_ratios) / max(1, len(bt_ratios)),
            "move_change_ratio": sum(move_ratios) / max(1, len(move_ratios)),
            "gimmick_success_rate": sum(gim_rates) / max(1, len(gim_rates)),
            "disp_winrate": disp_wr,
        }

    # ── 저장/로드 ──
    def save(self, path: str):
        torch = self.torch
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        torch.save({"nets": {role.name.lower(): self.nets[role].state_dict()
                             for role in TRAIN_ROLES},
                    "obs_size": self.cfg.obs_size, "num_actions": self.cfg.num_actions},
                   path)

    def save_role_stage(self, stage: int, model_dir: str):
        torch = self.torch
        os.makedirs(model_dir, exist_ok=True)
        for role in TRAIN_ROLES:
            torch.save({"nets": {role.name.lower(): self.nets[role].state_dict()}},
                       os.path.join(model_dir, f"{role.name.lower()}_{stage}.pt"))

    def load(self, path: str):
        torch = self.torch
        ckpt = torch.load(path, map_location=self.device)
        nets_state = ckpt.get("nets", {})
        for role in TRAIN_ROLES:
            st = nets_state.get(role.name.lower())
            if st is not None:
                self.nets[role].load_state_dict(st)
        print(f"[RESUME] loaded nets from {path}", flush=True)


# ─────────────────── CSV 로깅 ───────────────────

CSV_HEADER = ("episode,stage,boss_hp,roll_winrate,disposition,ep_victory,ep_steps,"
              "bt_fire_ratio,move_change_ratio,gimmick_success_rate,"
              "eval_winrate,eval_kill_steps,eval_bt_ratio,eval_move_change,"
              "wr_aggressive,wr_safe,wr_novice,"
              "tank_ploss,healer_ploss,support_ploss,"
              "tank_ent,healer_ent,support_ent\n")


def append_csv(path, row: dict):
    new = not os.path.exists(path)
    with open(path, "a", encoding="utf-8") as f:
        if new:
            f.write(CSV_HEADER)
        f.write(",".join(str(row.get(k, "")) for k in CSV_HEADER.strip().split(",")) + "\n")


# ─────────────────── 메인 ───────────────────

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--episodes", type=int, default=20000, help="총 학습 에피소드")
    ap.add_argument("--bc-episodes", type=int, default=0, help="BC 웜스타트 시연 에피소드(0=off)")
    ap.add_argument("--stage-wins", type=float, default=None,
                    help="커리큘럼 단계 상승 승률 임계(0~1). 기본 config.curriculum_advance_winrate")
    ap.add_argument("--device", type=str, default="cuda")
    ap.add_argument("--resume", type=str, default=None, help="이어서 학습할 final.pt 경로")
    ap.add_argument("--model-dir", type=str, default="models_raid")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--no-randomize-player", action="store_true",
                    help="플레이어 모델 도메인 랜덤화 비활성(aggressive 고정)")
    # ── 제거 실험(D/E/F) + 경계 재배치 실험(M1~M5) ──
    ap.add_argument("--intervention-mode", type=str, default="smdp",
                    choices=["smdp", "naive", "drop"],
                    help="BT 개입 턴 표본 처리: smdp(F,기본)/naive(D,단순 혼합)/drop(E,보상 폐기)")
    ap.add_argument("--bt-disable", type=str, default="",
                    help="끌 BT 규칙(콤마): seal_hide,brand_spread,stagger_dps,imminent_escape,yellow_escape,rush_lure")
    ap.add_argument("--bt-extra", type=str, default="",
                    help="추가 BT 규칙(콤마): aggro_taunt (M5 역방향)")
    ap.add_argument("--gimmick-rewards", type=str, default="auto",
                    choices=["auto", "on", "off"],
                    help="기믹 보상 그룹 활성화. on=조건 무관 항상 활성(면허 캠페인의 통제 비교용)")
    ap.add_argument("--bt-mode", type=str, default="fixed",
                    choices=["fixed", "adaptive", "mono", "none"],
                    help="보장 계층 운영: fixed(고정 BT)/adaptive(면허 발급+회수)/"
                         "mono(발급만, 회수 없음)/none(BT 전면 해제)")
    ap.add_argument("--run-label", type=str, default="",
                    help="실험 조건 라벨(run_meta.json/metrics.jsonl 에 기록)")
    args = ap.parse_args()

    import torch
    device_str = args.device
    if device_str.startswith("cuda") and not torch.cuda.is_available():
        print("[WARN] CUDA 미탑재 → cpu 폴백", flush=True)
        device_str = "cpu"

    cfg = RaidConfig()
    if args.no_randomize_player:
        cfg.player_model_randomize = False
    tcfg = TrainCfg()
    advance_wr = args.stage_wins if args.stage_wins is not None else cfg.curriculum_advance_winrate

    stages = list(cfg.curriculum_boss_hp)
    csv_path = os.path.join(args.model_dir, "train_log.csv")
    os.makedirs(args.model_dir, exist_ok=True)

    bt_disabled = {s.strip() for s in args.bt_disable.split(",") if s.strip()}
    bt_extra = {s.strip() for s in args.bt_extra.split(",") if s.strip()}
    trainer = RaidTrainer(cfg, tcfg, device_str, seed=args.seed,
                          intervention_mode=args.intervention_mode,
                          bt_disabled=bt_disabled, bt_extra=bt_extra,
                          bt_mode=args.bt_mode,
                          gimmick_rewards=args.gimmick_rewards)
    if args.resume:
        trainer.load(args.resume)
    if args.bc_episodes > 0:
        trainer.behavior_clone(args.bc_episodes)

    # 실험 조건 메타 기록 — 논문 재현성/조건 추적용
    import json as _json
    meta = {
        "run_label": args.run_label or args.model_dir,
        "intervention_mode": args.intervention_mode,
        "bt_mode": args.bt_mode, "gimmick_rewards": args.gimmick_rewards,
        "license_tiers": (dict(cfg.license_tiers)
                          if args.bt_mode in ("adaptive", "mono") else None),
        "license_alpha": cfg.license_alpha, "license_beta": cfg.license_beta,
        "bt_disabled": sorted(bt_disabled), "bt_extra": sorted(bt_extra),
        "reward_groups": sorted(trainer.reward_groups),
        "episodes": args.episodes, "bc_episodes": args.bc_episodes,
        "seed": args.seed, "curriculum": list(stages),
        "seal": {"waves": cfg.seal_waves, "wave_turns": cfg.seal_wave_turns},
        "map": [cfg.map_width, cfg.map_height],
        "cooldowns": {"heal": cfg.skill_cooldowns.get(int(RaidActionID.HEAL)),
                      "guard": cfg.skill_cooldowns.get(int(RaidActionID.GUARD))},
        "started_at": time.strftime("%Y-%m-%d %H:%M:%S"),
    }
    with open(os.path.join(args.model_dir, "run_meta.json"), "w", encoding="utf-8") as f:
        _json.dump(meta, f, ensure_ascii=False, indent=2)
    metrics_path = os.path.join(args.model_dir, "metrics.jsonl")

    print(f"[TRAIN] device={device_str} episodes={args.episodes} "
          f"stages(HP)={stages} advance_winrate={advance_wr} "
          f"randomize_player={cfg.player_model_randomize} "
          f"mode={args.intervention_mode} bt_off={sorted(bt_disabled)} "
          f"bt_extra={sorted(bt_extra)}", flush=True)

    stage = 0
    cfg.boss_max_hp = stages[stage]
    recent_wins = deque(maxlen=tcfg.winrate_window)
    t0 = time.time()
    ep = 0
    last_eval = {}

    while ep < args.episodes:
        # 업데이트 배치 수집
        batch_results = []
        for _ in range(tcfg.episodes_per_update):
            if ep >= args.episodes:
                break
            r = trainer.run_episode(collect=True)
            recent_wins.append(1 if r["victory"] else 0)
            batch_results.append(r)
            ep += 1
            # 논문용 에피소드 단위 상세 로그(수렴 곡선·기믹별 성공률·행동 분포 분석 소스)
            with open(metrics_path, "a", encoding="utf-8") as f:
                f.write(_json.dumps({
                    "ep": ep, "stage": stage, "boss_hp": cfg.boss_max_hp,
                    "victory": bool(r["victory"]), "steps": int(r["steps"]),
                    "disposition": r["disposition"],
                    "bt_fire_ratio": round(r["bt_fire_ratio"], 4),
                    "gimmick_success_rate": round(r["gimmick_success_rate"], 4),
                    "tank_top_aggro_ratio": r["tank_top_aggro_ratio"],
                    "events": r["events"], "rule_fires": r["rule_fires"],
                    "role_rewards": r["role_rewards"],
                    "licenses": r.get("licenses"),
                    "license_events": r.get("license_events") or [],
                }, ensure_ascii=False) + "\n")
        stats = trainer.update()

        roll_wr = sum(recent_wins) / max(1, len(recent_wins))

        # 커리큘럼 단계 상승
        advanced = False
        if roll_wr >= advance_wr and stage < len(stages) - 1 \
                and len(recent_wins) >= min(tcfg.winrate_window, 50):
            trainer.save_role_stage(stage, args.model_dir)
            trainer.save(os.path.join(args.model_dir, "final.pt"))
            stage += 1
            cfg.boss_max_hp = stages[stage]
            recent_wins.clear()
            advanced = True
            print(f"[CURRICULUM] stage↑ → {stage} (boss_hp={cfg.boss_max_hp}) "
                  f"at ep={ep} roll_wr={roll_wr:.3f}", flush=True)

        # 평가
        do_eval = (ep % tcfg.eval_interval < tcfg.episodes_per_update) or advanced
        if do_eval:
            last_eval = trainer.evaluate(tcfg.eval_episodes)
            spd = ep / max(1e-6, (time.time() - t0)) * 60.0
            dwr = last_eval["disp_winrate"]
            if trainer.gate is not None:
                snap = trainer.gate.snapshot()
                handed = [r for r, v in snap.items() if v["handed"]]
                tstr = ", ".join("%s:%.2f" % (r, v["T"]) for r, v in snap.items())
                print("[LICENSE] ep=%d 개입률=%.2f 이양=%s T={%s}"
                      % (ep, trainer.gate.intervention_ratio(), handed, tstr), flush=True)
            print(f"[EVAL] ep={ep} stage={stage} hp={cfg.boss_max_hp} "
                  f"roll_wr={roll_wr:.3f} eval_wr={last_eval['winrate']:.3f} "
                  f"kill={last_eval['avg_kill_steps']:.0f} "
                  f"BT%={last_eval['bt_fire_ratio']:.2f} "
                  f"moveChg={last_eval['move_change_ratio']:.2f} "
                  f"gimmick={last_eval['gimmick_success_rate']:.2f} "
                  f"| wr[agg={dwr.get('aggressive',0):.2f} safe={dwr.get('safe',0):.2f} "
                  f"nov={dwr.get('novice',0):.2f}] {spd:.0f} ep/min", flush=True)

        # CSV 로그(배치 마지막 에피소드 기준)
        lr = batch_results[-1] if batch_results else {}
        dwr = last_eval.get("disp_winrate", {})

        def _pl(role):
            return round(stats.get(role, {}).get("policy_loss", float("nan")), 4) if stats.get(role) else ""

        def _en(role):
            # 엔트로피 붕괴(결정론화) 감시용 — v26 에서 16만 ep 이후 성능 역진의 선행 신호였다.
            return round(stats.get(role, {}).get("entropy", float("nan")), 4) if stats.get(role) else ""
        append_csv(csv_path, {
            "episode": ep, "stage": stage, "boss_hp": cfg.boss_max_hp,
            "roll_winrate": round(roll_wr, 4), "disposition": lr.get("disposition", ""),
            "ep_victory": int(lr.get("victory", False)), "ep_steps": lr.get("steps", ""),
            "bt_fire_ratio": round(lr.get("bt_fire_ratio", 0), 4),
            "move_change_ratio": round(lr.get("move_change_ratio", 0), 4),
            "gimmick_success_rate": round(lr.get("gimmick_success_rate", 0), 4),
            "eval_winrate": round(last_eval.get("winrate", 0), 4) if last_eval else "",
            "eval_kill_steps": round(last_eval.get("avg_kill_steps", 0), 1) if last_eval else "",
            "eval_bt_ratio": round(last_eval.get("bt_fire_ratio", 0), 4) if last_eval else "",
            "eval_move_change": round(last_eval.get("move_change_ratio", 0), 4) if last_eval else "",
            "wr_aggressive": round(dwr.get("aggressive", 0), 4) if dwr else "",
            "wr_safe": round(dwr.get("safe", 0), 4) if dwr else "",
            "wr_novice": round(dwr.get("novice", 0), 4) if dwr else "",
            "tank_ploss": _pl("tank"), "healer_ploss": _pl("healer"),
            "support_ploss": _pl("support"),
            "tank_ent": _en("tank"), "healer_ent": _en("healer"),
            "support_ent": _en("support"),
        })

        # 주기 저장
        if ep % tcfg.save_interval < tcfg.episodes_per_update:
            trainer.save(os.path.join(args.model_dir, "final.pt"))

    trainer.save_role_stage(stage, args.model_dir)
    trainer.save(os.path.join(args.model_dir, "final.pt"))
    dur = time.time() - t0
    print(f"[DONE] episodes={ep} time={dur/60:.1f}min "
          f"speed={ep/max(1e-6,dur)*60:.0f} ep/min "
          f"final stage={stage} hp={cfg.boss_max_hp}", flush=True)
    print(f"[DONE] checkpoints -> {args.model_dir}/final.pt (+ role_stage), log -> {csv_path}",
          flush=True)


if __name__ == "__main__":
    main()
