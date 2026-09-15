# -*- coding: utf-8 -*-
"""행동 다양성 평가 — "보장 계층 개입을 줄이면 NPC 행동이 다양해지는가".

개입률 감소 자체는 공학적 편의일 뿐이고, 게임 NPC 맥락에서 그것이 가치가 되려면
'규칙이 물러난 만큼 행동이 다양해진다'는 연결이 측정되어야 한다. 이 스크립트는
학습이 끝난 각 조건의 정책을 같은 조건(배포 설정)으로 굴려 다음을 잰다.

  · 행동 분포 엔트로피 H(a)  — 역할별, bits. 규칙 주도 턴은 상태가 같으면 같은 행동을
    내므로 개입이 많을수록 낮아진다.
  · 최빈 행동 점유율          — 한 행동으로 쏠리는 정도(낮을수록 다양)
  · 공간 방문 엔트로피        — 아레나를 격자로 나눈 방문 분포의 엔트로피(포지셔닝 다양성)
  · 보장 계층 개입률          — 위 지표들과의 상관을 보기 위한 동반 측정

면허 상태(T, 보유 여부)는 학습 산출물(metrics.jsonl 마지막 줄)에서 복원한다 —
게이트는 런타임 상태라 체크포인트에 없고, 복원하지 않으면 제안 조건이 고정 BT 처럼
동작해 비교가 무의미해진다.
"""
import json, io, os, math, sys, argparse
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import torch

from src.raid import (RaidEnv, RaidConfig, PartyRole, BTGimmickLayer,
                      build_role_net)
from src.raid.trust_gate import LicenseGate, LICENSED_RULES
from train_raid import PlayerModelWrapper, TrainCfg
import random

ROLE_NAMES = {PartyRole.TANK: "tank", PartyRole.HEALER: "healer",
              PartyRole.SUPPORT: "support"}


def entropy(counter):
    n = sum(counter.values())
    if n == 0:
        return 0.0
    h = 0.0
    for c in counter.values():
        p = c / n
        if p > 0:
            h -= p * math.log2(p)
    return h


def load_nets(path, cfg, device):
    ckpt = torch.load(path, map_location=device, weights_only=False)
    nets = {}
    for role, name in ROLE_NAMES.items():
        net = build_role_net(cfg.obs_size, cfg.num_actions, 256, device)
        net.load_state_dict(ckpt["nets"][name])
        net.eval()
        nets[role] = net
    return nets


def restore_gate(gate, run_dir):
    """학습 종료 시점의 면허 상태(T, 보유)를 metrics.jsonl 에서 복원."""
    p = os.path.join(run_dir, "metrics.jsonl")
    last = None
    with io.open(p, encoding="utf-8") as f:
        for line in f:
            last = line
    if not last:
        return {}
    lic = (json.loads(last).get("licenses") or {})
    for rule, v in lic.items():
        if rule in gate.licenses:
            gate.licenses[rule].trust = float(v.get("T", 0.0))
            gate.licenses[rule].handed = bool(v.get("handed", False))
    return {r: v.get("handed", False) for r, v in lic.items()}


def evaluate(run_dir, bt_mode, episodes=40, seed=4242):
    cfg = RaidConfig()
    device = torch.device("cpu")
    nets = load_nets(os.path.join(run_dir, "final.pt"), cfg, device)
    rng = random.Random(seed)

    env = RaidEnv(cfg, seed=seed)
    env.reset()
    npc_uids = [u for u, r in enumerate(cfg.party_roles) if r != PartyRole.DEALER]
    disabled = set(LICENSED_RULES) if bt_mode == "none" else set()
    gate_mode = {"adaptive": "adaptive", "mono": "mono"}.get(bt_mode)
    gate = (LicenseGate(cfg, npc_uids, mode=gate_mode, rng=random.Random(seed + 1))
            if gate_mode else None)
    handed = restore_gate(gate, run_dir) if gate is not None else {}

    acts = {r: Counter() for r in ROLE_NAMES}
    cells = Counter()
    bt_turns = rl_turns = 0
    wins = 0
    cell = 1.5      # 공간 격자 한 칸(sim)

    for ep in range(episodes):
        env = RaidEnv(cfg, seed=seed + ep)
        env.reset()
        player = PlayerModelWrapper(env, cfg.player_slot, cfg, rng)
        bts = {u: BTGimmickLayer(env, u, disabled_rules=disabled, gate=gate)
               for u in npc_uids}
        while not env.done:
            actions = {"p0": player.act()}
            for uid in npc_uids:
                u = env.units[uid]
                a = bts[uid].act()
                if a is not None:
                    bt_turns += 1
                else:
                    rl_turns += 1
                    obs = torch.as_tensor(env._observe(uid),
                                          dtype=torch.float32).unsqueeze(0)
                    with torch.no_grad():
                        act, _, _ = nets[u.role].get_action(obs, deterministic=False)
                    a = int(act.item())
                actions[f"p{uid}"] = int(a)
                if u.alive:
                    acts[u.role][int(a)] += 1
                    cells[(int(u.x / cell), int(u.y / cell))] += 1
            env.step(actions)
        if gate is not None:
            gate.end_episode()
        wins += 1 if env.victory else 0

    per_role = {}
    for role, name in ROLE_NAMES.items():
        c = acts[role]
        n = sum(c.values())
        per_role[name] = {
            "H": round(entropy(c), 3),
            "top1": round((max(c.values()) / n) if n else 0.0, 3),
            "n_actions": len(c),
        }
    return {
        "winrate": round(wins / episodes, 3),
        "intervention": round(bt_turns / max(1, bt_turns + rl_turns), 3),
        "action_entropy_mean": round(
            sum(v["H"] for v in per_role.values()) / len(per_role), 3),
        "spatial_entropy": round(entropy(cells), 3),
        "per_role": per_role,
        "licensed": sorted(r for r, h in handed.items() if h),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--episodes", type=int, default=40)
    ap.add_argument("--base", type=str, default="runs_seeds")
    args = ap.parse_args()

    CONDS = []
    for s in (0, 1, 2):
        CONDS.append((f"S_fix_s{s}", "fixed", "고정 BT"))
        CONDS.append((f"S_adapt_s{s}", "adaptive", "면허 프로토콜"))
        CONDS.append((f"S_mono_s{s}", "mono", "발급만"))
    CONDS.append(("S_none_s0", "none", "BT 없음"))

    out = {}
    print(f"{'런':16s} {'조건':12s} {'개입률':>7s} {'행동H':>7s} {'공간H':>7s} {'승률':>7s}  면허")
    print("-" * 78)
    for run, mode, label in CONDS:
        d = os.path.join(args.base, run)
        if not os.path.exists(os.path.join(d, "final.pt")):
            continue
        try:
            r = evaluate(d, mode, episodes=args.episodes)
        except Exception as e:
            print(f"{run:16s} ERR {e}")
            continue
        out[run] = dict(r, label=label, mode=mode)
        print(f"{run:16s} {label:12s} {r['intervention']:7.3f} "
              f"{r['action_entropy_mean']:7.3f} {r['spatial_entropy']:7.3f} "
              f"{r['winrate']:7.1%}  {r['licensed']}")
    with io.open(os.path.join(args.base, "diversity.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    print("\nsaved:", os.path.join(args.base, "diversity.json"))


if __name__ == "__main__":
    main()
