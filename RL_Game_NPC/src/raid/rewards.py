"""보상 계산기 (뼈대) — '혈월의 마수 군주' 레이드.

원칙(사용자 지시 유지):
  1. 위험(발동 임박) 영역 안 → 양수 보상 차단, 도망만.
  2. 위험 밖 → 역할 수행 보상 (딜/힐/버프/어그로).
  3. 기믹 파훼(카운터/스태거/돌진-기둥/전멸기 LOS/가드 딜타임)는 큰 보상.
  4. 시간 패널티 + 비참여 패널티.

세부 튜닝은 후속. 여기서는 형태와 이벤트 연결만 제공.
"""
from __future__ import annotations
import math
from typing import Dict, TYPE_CHECKING

from .config import RaidConfig, PartyRole, PatternID

if TYPE_CHECKING:
    from .env import RaidEnv


def _dist(ax, ay, bx, by) -> float:
    return math.hypot(ax - bx, ay - by)


class RewardComputer:
    """레이드 보상 계산기.

    mode:
      "full"        — 기존 전체 보상(전투 + 기믹 파훼). env 기본값, 기존 호출 무변경.
      "combat_only" — 2계층 하이브리드 RL(Layer 2) 전용. 기믹 파훼 보상
                      (가드/카운터/패링/스태거 파괴/은신/산개/돌진유도)을 제거하고
                      순수 전투 보상(딜·힐·어그로·버프·생존·위험 페널티·시간)만 남긴다.
                      기믹은 Layer 1 BT 소관 → RL 학습 공간 축소·수렴 가속(NUM2.md 3.3).
    """

    # 기믹 이벤트 타입 → 보상 그룹 (경계 재배치 실험 M1~M5 의 선택적 보상 복원용)
    _EVENT_GROUP = {
        "guard_success": "guard",
        "counter_success": "counter", "counter_fail": "counter",
        "parry_success": "parry", "parry_fail": "parry",
        "stagger_break": "stagger", "stagger_fail": "stagger",
        "stagger_contribute": "stagger",
        "rush_pillar_hit": "rush",
        "mechanic_success": "brand", "mechanic_fail": "brand",
        "seal_holding": "seal", "seal_success": "seal", "seal_fail": "seal",
    }

    def __init__(self, cfg: RaidConfig, mode: str = "full", gimmick_groups=None):
        """gimmick_groups: combat_only 모드에서 선택적으로 복원할 기믹 보상 그룹 집합.
        경계 재배치 실험(BT 규칙을 끄고 해당 기믹을 RL 로 내릴 때)에서 그 기믹의
        학습 신호를 살리기 위해 사용. 예: {"seal"} — 전멸기 보상만 복원.
        그룹: guard/counter/parry/stagger/rush/brand/seal."""
        self.cfg = cfg
        if mode not in ("full", "combat_only"):
            raise ValueError(f"unknown reward mode: {mode}")
        self.mode = mode
        self.gimmick_groups = frozenset(gimmick_groups or ())

    def _group_on(self, group: str) -> bool:
        return self.mode == "full" or group in self.gimmick_groups

    def compute(self, env: "RaidEnv") -> Dict[str, float]:
        gimmicks = (self.mode == "full")
        cfg = self.cfg
        out: Dict[str, float] = {}
        player = env.units[cfg.player_slot]
        ap = env.boss.active_pattern

        # 전멸기/페이즈 전이 중엔 자리잡기가 우선 → 역할 보상 감쇠
        special = (ap is not None and ap.mode in ("seal",)) or env.boss.invuln_turns > 0
        role_scale = 0.3 if special else 1.0

        for uid, u in env.units.items():
            aid = f"p{uid}"
            events = env.step_events.get(uid, [])
            r = 0.0
            if not u.alive:
                out[aid] = r
                continue

            boss_dist = env._boss_dist(u.x, u.y)
            in_danger = ap is not None and ap.mode == "steps" and ap.contains((u.x, u.y)) \
                and ap.turns_remaining <= 2

            # ── 위험 모드: 도망만 ──
            if in_danger:
                r += cfg.rw_danger_stay
                for e in events:
                    if e.get("type") == "damage_taken":
                        r += cfg.rw_danger_hit
                    elif e.get("type") == "death":
                        r += cfg.rw_death
            else:
                # 안전 모드 역할 수행
                if boss_dist <= cfg.engage_distance:
                    r += cfg.rw_dodge_safe * role_scale
                elif boss_dist > cfg.disengage_distance:
                    r += -0.5

                dmg = sum(e.get("amount", 0) for e in events if e.get("type") == "damage")

                if u.role == PartyRole.TANK:
                    if env.boss.top_aggro_uid() == u.uid:
                        r += cfg.rw_tank_aggro_hold * role_scale
                    else:
                        r += cfg.rw_tank_aggro_lose * role_scale
                    if any(e.get("type") == "taunt" for e in events):
                        r += cfg.rw_taunt_good
                    r += dmg * 0.1 * role_scale
                elif u.role == PartyRole.HEALER:
                    for e in events:
                        if e.get("type") == "heal":
                            amt = e.get("amount", 0)
                            r += amt * cfg.rw_heal_per_hp
                            tgt = env.units.get(e.get("target"))
                            if tgt and (tgt.hp / max(1, tgt.max_hp)) < 0.3:
                                r += cfg.rw_heal_critical
                elif u.role == PartyRole.SUPPORT:
                    for e in events:
                        if e.get("type") == "buff":
                            r += cfg.rw_buff_hit
                    r += dmg * 0.15 * role_scale
                else:  # DEALER
                    r += dmg * cfg.rw_boss_damage_per_hp

                tanking = (u.role == PartyRole.TANK
                           and env.boss.top_aggro_uid() == u.uid)
                for e in events:
                    if e.get("type") == "damage_taken":
                        r += cfg.rw_tank_hit_tanking if tanking else cfg.rw_hit_taken
                    elif e.get("type") == "death":
                        r += cfg.rw_death

            # ── 페이즈 클리어(전투 진척) — 두 모드 공통 유지 ──
            for e in events:
                if e.get("type") == "phase_clear":
                    r += cfg.rw_phase_clear

            # ── 기믹 이벤트 (full 모드 전용 — combat_only 는 BT 소관이라 제거.
            #     단, gimmick_groups 로 복원된 그룹은 combat_only 에서도 반영) ──
            for e in events:
                t = e.get("type")
                grp = self._EVENT_GROUP.get(t)
                if grp is None or not self._group_on(grp):
                    continue
                if t == "guard_success":
                    r += cfg.rw_guard_success
                elif t == "counter_success":
                    r += cfg.rw_counter_success
                elif t == "counter_fail":
                    r += cfg.rw_counter_fail
                elif t == "parry_success":
                    r += cfg.rw_parry_success
                elif t == "parry_fail":
                    r += cfg.rw_parry_fail
                elif t == "stagger_break":
                    r += cfg.rw_stagger_success
                elif t == "stagger_fail":
                    r += cfg.rw_stagger_fail
                elif t == "stagger_contribute":
                    r += e.get("amount", 0) * cfg.rw_stagger_contribution
                elif t == "rush_pillar_hit":
                    r += cfg.rw_rush_pillar_lure
                elif t == "mechanic_success":
                    r += 20.0
                elif t == "mechanic_fail":
                    r += -15.0
                elif t == "seal_holding":
                    if e.get("hidden"):
                        r += cfg.rw_seal_hidden
                elif t == "seal_success":
                    r += cfg.rw_seal_success
                elif t == "seal_fail":
                    r += cfg.rw_seal_fail

            # ── 붉은 낙인 산개 shaping (full 또는 brand 그룹 복원 시) ──
            if self._group_on("brand") and ap is not None and ap.mode == "steps" and ap.pattern_id == PatternID.CRIMSON_BRAND:
                step = ap.current_step()
                if step is not None:
                    mark_uid = step.extra.get("target_uid")
                    ideal = cfg.pat_brand_escape_distance
                    urgency = 1.0 - ap.turns_remaining / max(1, ap.total_this_step)
                    if mark_uid == u.uid:
                        others = [x for x in env.units.values() if x.uid != u.uid and x.alive]
                        if others:
                            md = min(_dist(x.x, x.y, u.x, u.y) for x in others)
                            r += min(md / ideal, 1.0) * cfg.rw_brand_spread * (0.3 + urgency)
                    elif mark_uid in env.units:
                        mu = env.units[mark_uid]
                        d = _dist(mu.x, mu.y, u.x, u.y)
                        r += min(d / ideal, 1.0) * cfg.rw_brand_spread * (0.3 + urgency)

            # ── 스태거 집결 딜 (full 또는 stagger 그룹 복원 시) ──
            if self._group_on("stagger") and env.boss.stagger_active and boss_dist <= u.attack_range:
                r += 0.5

            # 생존/참여
            if player.alive:
                r += cfg.rw_player_alive_step
            r += cfg.rw_time_penalty

            if env.done:
                if env.victory:
                    r += cfg.rw_boss_kill
                elif env.wipe:
                    r += cfg.rw_wipe

            out[aid] = r
        return out
