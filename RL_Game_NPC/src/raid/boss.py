"""보스 '혈월의 마수 군주' — facing, 스텝 시퀀스 구동, 어그로/페이즈/기믹 상태.

핵심(사용자 요구):
  - boss.facing (라디안) 상태. 패턴 시전 시작 시 타깃 방향으로 회전(속도 제한),
    시전 중 facing **고정** → 몸 방향과 장판 방향 불일치 원천 차단.
  - 패턴 = PatternStep 시퀀스. 스텝별 독립 텔레그래프.
"""
from __future__ import annotations
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple
import math
import random

from .config import RaidConfig, PatternID, PhaseID
from .patterns import PatternStep, PatternDef, PATTERN_REGISTRY, GIMMICK_COOLDOWNS, pick_counter_target
from .shapes import Shape, Pos, bake_shapes


def _rotate_toward(cur: float, target: float, max_delta: float) -> float:
    d = (target - cur + math.pi) % (2 * math.pi) - math.pi
    if abs(d) <= max_delta:
        return target
    return cur + max_delta * (1.0 if d > 0 else -1.0)


@dataclass
class ActivePattern:
    """현재 시전 중인 패턴 인스턴스. facing/origin 은 시전 시작 시 락되어 고정."""
    pattern_id: PatternID
    mode: str                       # "steps" | "counter" | "stagger" | "seal"
    origin: Pos
    facing: float
    steps: List[PatternStep] = field(default_factory=list)
    step_index: int = 0
    turns_remaining: int = 0        # 현재 스텝/기믹 창 카운트다운
    total_this_step: int = 0
    target_uids: List[int] = field(default_factory=list)
    world_shapes: List[Shape] = field(default_factory=list)
    guard_used: bool = False        # 같은 시퀀스에서 가드 딜타임 1회만
    extra: Dict = field(default_factory=dict)

    # boss_streamer / obs 호환 별칭
    @property
    def total_wind_up(self) -> int:
        return self.total_this_step

    @property
    def target_unit_ids(self) -> List[int]:
        return self.target_uids

    @property
    def shapes(self) -> List[Shape]:
        return self.world_shapes

    @property
    def num_steps(self) -> int:
        return len(self.steps)

    @property
    def anim(self) -> str:
        if 0 <= self.step_index < len(self.steps):
            return self.steps[self.step_index].anim
        return self.extra.get("anim", "")

    def current_step(self) -> Optional[PatternStep]:
        if 0 <= self.step_index < len(self.steps):
            return self.steps[self.step_index]
        return None

    def contains(self, pos: Pos) -> bool:
        return any(s.contains(pos) for s in self.world_shapes)


@dataclass
class Boss:
    config: RaidConfig
    x: float = 10.0
    y: float = 10.0
    facing: float = 0.0
    hp: int = 0
    phase: PhaseID = PhaseID.P1
    invuln_turns: int = 0
    grog_turns: int = 0
    stun_turns: int = 0

    # 스태거 (상시 게이지 신시스템)
    #   stagger_gauge: stagger_max 에서 시작, 스킬 명중마다 감소. 0 도달 → 파괴(그로기).
    #   stagger_active: 이제 "무력화 파괴 그로기(딜타임) 중 여부" 로 재정의(True=딜타임).
    #   stagger_break_turns: 파괴 그로기 잔여 턴(stagger_active 유지용).
    stagger_active: bool = False
    stagger_gauge: float = 0.0
    stagger_break_turns: int = 0

    # 카운터 창
    counter_window_turns: int = 0

    aggro: Dict[int, float] = field(default_factory=dict)
    cooldowns: Dict[int, int] = field(default_factory=dict)
    recent_patterns: List[int] = field(default_factory=list)
    active_pattern: Optional[ActivePattern] = None

    rng: random.Random = field(default_factory=random.Random)

    def __post_init__(self):
        if self.hp <= 0:
            self.hp = self.config.boss_max_hp
        self.x = self.config.map_width / 2.0
        self.y = self.config.map_height / 2.0
        self.facing = self.config.boss_start_facing
        # 상시 스태거 게이지: 최대치에서 시작
        self.stagger_gauge = self.config.stagger_max

    # ── boss_streamer 호환: telegraphs 목록 ──
    @property
    def telegraphs(self) -> List[ActivePattern]:
        if self.active_pattern is not None and self.active_pattern.mode == "steps":
            return [self.active_pattern]
        return []

    @property
    def stagger_turns(self) -> int:
        ap = self.active_pattern
        return ap.turns_remaining if (ap and ap.mode == "stagger") else 0

    # ── 상태 질의 ──
    def is_incapacitated(self) -> bool:
        return self.invuln_turns > 0 or self.grog_turns > 0 or self.stun_turns > 0

    def is_busy(self) -> bool:
        return self.active_pattern is not None

    # ── 어그로 ──
    def add_aggro(self, uid: int, amount: float):
        self.aggro[uid] = self.aggro.get(uid, 0.0) + amount

    def decay_aggro(self):
        for k in list(self.aggro.keys()):
            self.aggro[k] *= self.config.aggro_decay

    def top_aggro_uid(self) -> Optional[int]:
        if not self.aggro:
            return None
        return max(self.aggro.items(), key=lambda kv: kv[1])[0]

    # ── 페이즈 ──
    def check_phase_transition(self) -> bool:
        hp_ratio = self.hp / self.config.boss_max_hp
        th = self.config.phase_hp_thresholds
        new_phase = self.phase
        if self.phase == PhaseID.P1 and hp_ratio <= th[0]:
            new_phase = PhaseID.P2
        elif self.phase == PhaseID.P2 and hp_ratio <= th[1]:
            new_phase = PhaseID.P3
        if new_phase != self.phase:
            self.phase = new_phase
            self.invuln_turns = self.config.phase_transition_invuln_turns
            self.active_pattern = None
            self.stagger_active = False
            self.stagger_break_turns = 0
            self.stagger_gauge = self.config.stagger_max   # 페이즈 전환 시 게이지 리셋
            self.counter_window_turns = 0
            return True
        return False

    # ── 쿨타임 ──
    def tick_cooldowns(self):
        for k in list(self.cooldowns.keys()):
            self.cooldowns[k] = max(0, self.cooldowns[k] - 1)

    def _can_use(self, pid_int: int) -> bool:
        return self.cooldowns.get(pid_int, 0) <= 0

    # ── 패턴 선택 ──
    def select_pattern(self) -> Optional[PatternID]:
        weights = self.config.pattern_weights[int(self.phase)]
        candidates: List[int] = []
        probs: List[float] = []
        for pid_int, w in weights.items():
            if w <= 0 or not self._can_use(pid_int):
                continue
            penalty = 0.5 if pid_int in self.recent_patterns[-3:] else 1.0
            candidates.append(pid_int)
            probs.append(w * penalty)
        if not candidates:
            return None
        total = sum(probs)
        r = self.rng.random() * total
        acc = 0.0
        for pid_int, p in zip(candidates, probs):
            acc += p
            if r <= acc:
                return PatternID(pid_int)
        return PatternID(candidates[-1])

    # ── facing 락 ──
    def _lock_facing(self, target_pos: Optional[Pos], do_face: bool) -> float:
        if do_face and target_pos is not None:
            desired = math.atan2(target_pos[1] - self.y, target_pos[0] - self.x)
        elif target_pos is not None:
            # 비조준 패턴도 몸은 어그로 방향으로 자연스럽게
            desired = math.atan2(target_pos[1] - self.y, target_pos[0] - self.x)
        else:
            desired = self.facing
        self.facing = _rotate_toward(self.facing, desired, self.config.boss_rotate_speed)
        return self.facing

    def _set_cooldown(self, pid: PatternID):
        scale = self.config.phase_cooldown_scale[int(self.phase)]
        base = PATTERN_REGISTRY[pid].cooldown if pid in PATTERN_REGISTRY else GIMMICK_COOLDOWNS.get(pid, 6)
        self.cooldowns[int(pid)] = max(1, int(base * scale))
        self.recent_patterns.append(int(pid))
        if len(self.recent_patterns) > 10:
            self.recent_patterns.pop(0)

    # ── step-기반 패턴 시작 ──
    def start_step_pattern(self, pid: PatternID, ctx: Dict):
        pdef: PatternDef = PATTERN_REGISTRY[pid]
        target_uid = pdef.pick_target(self.config, self.rng, ctx)
        target_pos = ctx.get("party", {}).get(target_uid) if target_uid is not None else None
        facing = self._lock_facing(target_pos, pdef.face_toward_target)
        steps = pdef.build(self.config, self.rng, ctx, target_uid)
        origin = (self.x, self.y)
        step0 = steps[0]
        ap = ActivePattern(
            pattern_id=pid, mode="steps", origin=origin, facing=facing,
            steps=steps, step_index=0,
            turns_remaining=step0.telegraph_turns, total_this_step=step0.telegraph_turns,
            target_uids=[target_uid] if target_uid is not None else [],
            world_shapes=bake_shapes(step0.shapes, origin[0], origin[1], facing),
        )
        self.active_pattern = ap
        self._set_cooldown(pid)
        return ap

    def start_counter(self, ctx: Dict):
        # 카운터는 딜러(E) 전용 저지 기믹 — 플레이어 편향 타깃(pat_counter_player_bias)으로 저지 상황 유도.
        target_uid = pick_counter_target(self.config, self.rng, ctx)
        target_pos = ctx.get("party", {}).get(target_uid) if target_uid is not None else None
        facing = self._lock_facing(target_pos, True)
        self.counter_window_turns = self.config.counter_window_turns
        ap = ActivePattern(
            pattern_id=PatternID.COUNTER_RUSH, mode="counter",
            origin=(self.x, self.y), facing=facing,
            turns_remaining=self.config.counter_window_turns,
            total_this_step=self.config.counter_window_turns,
            extra={"anim": "counter_glow"},
        )
        self.active_pattern = ap
        self._set_cooldown(PatternID.COUNTER_RUSH)
        return ap

    def start_stagger(self, ctx: Dict):
        target_uid = self.top_aggro_uid()
        target_pos = ctx.get("party", {}).get(target_uid) if target_uid is not None else None
        facing = self._lock_facing(target_pos, False)
        self.stagger_active = True
        self.stagger_gauge = self.config.stagger_gauge
        ap = ActivePattern(
            pattern_id=PatternID.STAGGER_LIFT, mode="stagger",
            origin=(self.x, self.y), facing=facing,
            turns_remaining=self.config.stagger_window_turns,
            total_this_step=self.config.stagger_window_turns,
            extra={"anim": "lift"},
        )
        self.active_pattern = ap
        self._set_cooldown(PatternID.STAGGER_LIFT)
        return ap

    def start_seal(self, ctx: Dict):
        """전멸기 '혈월 강림' — LOS 은신. 페이즈 전환 시 강제."""
        facing = self.facing
        ap = ActivePattern(
            pattern_id=PatternID.SEAL_WIPE, mode="seal",
            origin=(self.x, self.y), facing=facing,
            turns_remaining=self.config.seal_wind_up_turns,
            total_this_step=self.config.seal_wind_up_turns,
            extra={"anim": "blood_moon", "hold": 0},
        )
        self.active_pattern = ap
        return ap

    # ── 스텝 전개 ──
    def advance_step(self) -> Optional[PatternStep]:
        ap = self.active_pattern
        if ap is None:
            return None
        ap.step_index += 1
        if ap.step_index >= len(ap.steps):
            self.active_pattern = None
            return None
        step = ap.steps[ap.step_index]
        ap.turns_remaining = step.telegraph_turns
        ap.total_this_step = step.telegraph_turns
        ap.world_shapes = bake_shapes(step.shapes, ap.origin[0], ap.origin[1], ap.facing)
        return step

    # ── 피격 ──
    def take_damage(self, amount: int, attacker_uid: int) -> int:
        if self.invuln_turns > 0:
            return 0
        dmg = max(1, amount - self.config.boss_defense)
        actual = min(dmg, self.hp)
        self.hp -= actual
        self.add_aggro(attacker_uid, actual * self.config.aggro_damage_weight)
        return actual

    # ── 상시 스태거 게이지 ──
    def reduce_stagger(self, amount: float) -> Tuple[float, bool]:
        """스킬 명중 스태거 감소. (적용량, 파괴여부) 반환.

        무력화 게이지는 stagger_max 에서 시작해 감소하고, 0 도달 시 파괴:
        보스 그로기(stagger_break_grog_turns) + 게이지 max 리셋. 그로기/무적/파괴딜타임
        중엔 감소하지 않는다(적용량 0).
        """
        if amount <= 0 or self.is_incapacitated() or self.stagger_active:
            return 0.0, False
        self.stagger_gauge -= amount
        if self.stagger_gauge <= 0.0:
            self.stagger_gauge = self.config.stagger_max
            g = self.config.stagger_break_grog_turns
            self.grog_turns = max(self.grog_turns, g)
            self.stagger_active = True
            self.stagger_break_turns = g
            return amount, True
        return amount, False

    def tick_end_of_turn(self):
        if self.invuln_turns > 0:
            self.invuln_turns -= 1
        if self.grog_turns > 0:
            self.grog_turns -= 1
        if self.stun_turns > 0:
            self.stun_turns -= 1
        if self.stagger_break_turns > 0:
            self.stagger_break_turns -= 1
            if self.stagger_break_turns <= 0:
                self.stagger_active = False
        self.decay_aggro()
        self.tick_cooldowns()
