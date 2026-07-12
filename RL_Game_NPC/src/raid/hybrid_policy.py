"""Layer 2 dispatcher — BT(Layer1) → RL(Layer2) 하이브리드 정책.

HybridPolicy 는 매 턴 BTGimmickLayer 를 먼저 물어보고(int → 확정), None 이면 역할별 RL
정책을 샘플링한다. RL 미로드/실패 시 FSMNpcPolicy 로 폴백해 "지금 당장도 게임 가능".

역할별 독립 네트워크(RoleActorCritic)는 구레포 ActorCritic(src/agent.py) 구조를 참고해
자체 정의하며 state_dict 키(shared/actor/critic)를 동일하게 맞춰 상호 호환한다.
obs 132 / actions 22.

**사람같음 장치(Layer 2 인퍼런스 전용, 파라미터화)**:
  (a) 관측 1턴 지연 버퍼 — RL 은 obs_delay_turns 턴 전 관측으로 행동(반응 지연 모사).
  (b) action stickiness — 직전 이동 액션과 같은 방향 이동 로그잇에 보너스(지터 억제).
두 장치는 BT 가 fire 하지 않은 턴(RL 결정 턴)에만 적용된다.
"""
from __future__ import annotations
from collections import deque
from typing import Optional, TYPE_CHECKING

from .config import RaidActionID, PartyRole
from .fsm_npc import FSMNpcPolicy

if TYPE_CHECKING:
    from .bt_layer import BTGimmickLayer

# 이동 액션 집합(stickiness 대상)
_MOVE_ACTIONS = frozenset(int(a) for a in (
    RaidActionID.MOVE_UP, RaidActionID.MOVE_DOWN, RaidActionID.MOVE_LEFT,
    RaidActionID.MOVE_RIGHT, RaidActionID.MOVE_UP_LEFT, RaidActionID.MOVE_UP_RIGHT,
    RaidActionID.MOVE_DOWN_LEFT, RaidActionID.MOVE_DOWN_RIGHT,
))


# ─────────────────── 역할별 Actor-Critic ───────────────────

def _build_actor_critic(obs_size: int, action_size: int, hidden_size: int = 256):
    """torch 의존을 지연 임포트로 감싸 (torch 없는 환경에서도 BT/FSM 경로는 살아있게)."""
    import torch
    import torch.nn as nn
    import torch.nn.functional as F
    from torch.distributions import Categorical

    class RoleActorCritic(nn.Module):
        """역할 독립 PPO 네트워크. 구레포 ActorCritic 과 동일 키 구조."""

        def __init__(self, obs_size: int, action_size: int, hidden_size: int = 256):
            super().__init__()
            self.shared = nn.Sequential(
                nn.Linear(obs_size, hidden_size), nn.ReLU(),
                nn.Linear(hidden_size, hidden_size), nn.ReLU(),
            )
            self.actor = nn.Sequential(
                nn.Linear(hidden_size, hidden_size // 2), nn.ReLU(),
                nn.Linear(hidden_size // 2, action_size),
            )
            self.critic = nn.Sequential(
                nn.Linear(hidden_size, hidden_size // 2), nn.ReLU(),
                nn.Linear(hidden_size // 2, 1),
            )

        def forward(self, x):
            h = self.shared(x)
            return self.actor(h), self.critic(h)

        def get_action(self, x, deterministic: bool = False, temperature: float = 1.0,
                       logit_bonus=None):
            logits, value = self.forward(x)
            if logit_bonus is not None:
                logits = logits + logit_bonus
            if deterministic:
                action = torch.argmax(logits, dim=-1)
                probs = F.softmax(logits, dim=-1)
                dist = Categorical(probs)
                return action, dist.log_prob(action), value.squeeze(-1)
            t = max(1e-3, float(temperature))
            probs = F.softmax(logits / t, dim=-1)
            dist = Categorical(probs)
            action = dist.sample()
            return action, dist.log_prob(action), value.squeeze(-1)

        def evaluate_actions(self, x, actions):
            logits, value = self.forward(x)
            probs = F.softmax(logits, dim=-1)
            dist = Categorical(probs)
            return dist.log_prob(actions), value.squeeze(-1), dist.entropy()

    return RoleActorCritic(obs_size, action_size, hidden_size)


def build_role_net(obs_size: int, action_size: int, hidden_size: int = 256, device=None):
    """역할 네트워크 인스턴스 생성(+ 옵션 device 이동)."""
    net = _build_actor_critic(obs_size, action_size, hidden_size)
    if device is not None:
        net = net.to(device)
    return net


def load_role_nets(ckpt_path: str, cfg, device_str: str = "cpu"):
    """체크포인트 → {PartyRole: net} 로드. 형식: {"nets": {role_name_lower: state_dict}}.

    실패 시 예외 전파(호출부에서 폴백 판단). streamer/train_raid 공용.
    """
    import torch
    device = torch.device(device_str)
    ckpt = torch.load(ckpt_path, map_location=device)
    nets_state = ckpt.get("nets")
    if not nets_state:
        raise ValueError("checkpoint has no 'nets' dict")
    out = {}
    for role in (PartyRole.TANK, PartyRole.HEALER, PartyRole.SUPPORT):
        key = role.name.lower()
        state = nets_state.get(key)
        if state is None:
            continue
        net = build_role_net(cfg.obs_size, cfg.num_actions, device=device)
        net.load_state_dict(state)
        net.eval()
        out[role] = net
    if not out:
        raise ValueError("checkpoint 'nets' contained no known role states")
    return out, device


# ─────────────────── HybridPolicy ───────────────────

class HybridPolicy:
    """BT → RL dispatcher(+ 인간성 장치). streamer/eval 인퍼런스용.

    Args:
      bt: BTGimmickLayer (env/uid 를 내부에 보유)
      rl_net: 역할별 RoleActorCritic (None 이면 FSM 폴백)
      role: PartyRole
      device: torch.device (rl_net 있을 때만 필요)
      temperature: RL 샘플링 온도(>0, argmax 금지)
      obs_delay_turns: 관측 지연 버퍼 길이(0=지연 없음)
      action_stickiness: 직전 이동 방향 유지 로그잇 보너스(0=off)
    """

    def __init__(self, bt: "BTGimmickLayer", rl_net, role, device=None,
                 temperature: float = 1.0, obs_delay_turns: int = 1,
                 action_stickiness: float = 0.0):
        self.bt = bt
        self.env = bt.env
        self.uid = bt.uid
        self.role = role
        self.rl_net = rl_net
        self.device = device
        self.temperature = float(temperature)
        self.obs_delay_turns = int(obs_delay_turns)
        self.action_stickiness = float(action_stickiness)
        # RL 미로드 폴백(항상 준비 — "지금 당장도 게임 가능")
        self._fallback = FSMNpcPolicy(self.env, self.uid)
        self._obs_buf = deque(maxlen=self.obs_delay_turns + 1)
        self._last_move: Optional[int] = None
        self._last_step_seen = -1
        # 연구 로깅: 최근 발화 주체
        self.last_source = "init"      # "bt" | "rl" | "fsm_fallback"
        self.last_bt_rule = None

    def reset(self):
        self._obs_buf.clear()
        self._last_move = None
        self._last_step_seen = -1

    def _torch(self):
        import torch
        return torch

    def _delayed_obs(self):
        """관측 지연 버퍼: obs_delay_turns 턴 전 관측을 반환(부족하면 가장 오래된 것)."""
        env = self.env
        # 에피소드 리셋 감지(스텝 역행) → 버퍼 초기화
        if env.current_step < self._last_step_seen:
            self.reset()
        self._last_step_seen = env.current_step
        cur = env._observe(self.uid)
        self._obs_buf.append(cur)
        if self.obs_delay_turns <= 0 or len(self._obs_buf) <= self.obs_delay_turns:
            return self._obs_buf[0]
        return self._obs_buf[-1 - self.obs_delay_turns]

    def act(self) -> int:
        # 관측 지연 버퍼는 BT 발화 여부와 무관하게 **매 턴** 채운다.
        # (BT 가 길게 연속 발화한 뒤 첫 RL 턴이 수십 턴 전 관측을 보는 사고 방지 —
        #  버퍼를 RL 턴에만 채우면 '직전 RL 턴' 기준 지연이 되어버린다.)
        obs = self._delayed_obs() if self.rl_net is not None else None

        # Layer 1 BT 우선
        bt_a = self.bt.act()
        self.last_bt_rule = self.bt.last_decision.get("rule")
        if bt_a is not None:
            self.last_source = "bt"
            if int(bt_a) in _MOVE_ACTIONS:
                self._last_move = int(bt_a)
            return int(bt_a)

        # Layer 2 RL (미로드 → FSM 폴백)
        if self.rl_net is None:
            self.last_source = "fsm_fallback"
            a = int(self._fallback.act())
            if a in _MOVE_ACTIONS:
                self._last_move = a
            return a

        torch = self._torch()
        o = torch.as_tensor(obs, dtype=torch.float32, device=self.device).unsqueeze(0)
        logit_bonus = None
        if self.action_stickiness != 0.0 and self._last_move is not None:
            logit_bonus = torch.zeros(self.env.config.num_actions, device=self.device)
            logit_bonus[self._last_move] = self.action_stickiness
            logit_bonus = logit_bonus.unsqueeze(0)
        with torch.no_grad():
            action, _, _ = self.rl_net.get_action(
                o, deterministic=False, temperature=self.temperature,
                logit_bonus=logit_bonus)
        a = int(action.item())
        self.last_source = "rl"
        if a in _MOVE_ACTIONS:
            self._last_move = a
        return a


def build_hybrid_policies(env, cfg, ckpt_path: str, device_str: str = "cpu",
                          temperature: Optional[float] = None,
                          obs_delay_turns: Optional[int] = None,
                          action_stickiness: Optional[float] = None):
    """NPC 슬롯별 HybridPolicy 딕셔너리 반환. RL 로드 실패 시 BT+FSM 폴백으로 동작.

    반환: (policies: {uid: HybridPolicy}, eff_mode: str)
    """
    from .bt_layer import BTGimmickLayer
    temp = cfg.hybrid_temperature if temperature is None else temperature
    delay = cfg.hybrid_obs_delay_turns if obs_delay_turns is None else obs_delay_turns
    stick = cfg.hybrid_action_stickiness if action_stickiness is None else action_stickiness

    npc_slots = [i for i, r in enumerate(cfg.party_roles) if r != PartyRole.DEALER]
    role_nets = {}
    eff = "hybrid"
    try:
        role_nets, device = load_role_nets(ckpt_path, cfg, device_str)
    except Exception as e:
        print(f"[WARN] 하이브리드 RL 로드 실패 ({e}); BT+FSM 폴백", flush=True)
        role_nets, device = {}, None
        eff = "hybrid(bt+fsm)"

    policies = {}
    for uid in npc_slots:
        role = cfg.party_roles[uid]
        bt = BTGimmickLayer(env, uid)
        net = role_nets.get(role)
        policies[uid] = HybridPolicy(
            bt, net, role, device=device, temperature=temp,
            obs_delay_turns=delay, action_stickiness=stick)
    return policies, eff
