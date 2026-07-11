"""패턴 = PatternStep 시퀀스 프레임워크 — '혈월의 마수 군주' 카탈로그.

패턴은 PatternStep 배열(콤보/다단/시간차)이다. 스텝마다 독립 텔레그래프를 가지며
Unity 에서 게이지가 스텝마다 정확히 100% 에서 발동한다.

step-기반 패턴(여기 정의):
  TRIPLE_CLAW / EARTH_CRUSH / FRENZY_RUSH / PILLAR_THROW /
  SPIN_SWEEP / BLOOD_ROAR / CRIMSON_BRAND

보스-상태 기믹(boss.py 에서 직접 구동, 여기서는 spec 만):
  COUNTER_RUSH / STAGGER_LIFT / SEAL_WIPE
"""
from __future__ import annotations
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple
import math
import random

from .config import RaidConfig, PatternID, PartyRole
from .shapes import RelShape, Pos

Ctx = Dict  # {boss_pos, aggro_top, party(uid->pos), roles(uid->int), dealer_uid, non_tanks[...]}


# ─────────────────── PatternStep ───────────────────

@dataclass
class PatternStep:
    """패턴의 한 스텝. 스텝별 독립 텔레그래프."""
    telegraph_turns: int          # 이 스텝의 예고 턴수
    shapes: List[RelShape]        # facing 상대 좌표 도형들
    damage: int
    anim: str                     # Unity 애니 트리거: slash|smash|shock|rush|throw|spin|roar|brand|counter_glow|lift
    kind: str = "aoe"             # aoe | rush_dash | brand
    extra: Dict = field(default_factory=dict)


# ─────────────────── PatternDef ───────────────────

@dataclass
class PatternDef:
    pattern_id: PatternID
    name: str
    cooldown: int
    face_toward_target: bool = True   # facing 을 target 방향으로 락할지

    def pick_target(self, cfg: RaidConfig, rng: random.Random, ctx: Ctx) -> Optional[int]:
        """이 패턴이 바라볼(그리고 조준할) 타깃 uid. 기본 = 어그로 top."""
        return ctx.get("aggro_top")

    def build(self, cfg: RaidConfig, rng: random.Random, ctx: Ctx,
              target_uid: Optional[int]) -> List[PatternStep]:
        raise NotImplementedError


class TripleClaw(PatternDef):
    def __init__(self):
        super().__init__(PatternID.TRIPLE_CLAW, "TripleClaw", cooldown=4)

    def build(self, cfg, rng, ctx, target_uid):
        side = math.radians(cfg.pat_claw_side_angle_deg)
        front = math.radians(cfg.pat_claw_front_angle_deg)
        r = cfg.pat_claw_range
        off = math.radians(35.0)
        return [
            PatternStep(2, [RelShape("fan", {"angle_rel": off, "width": side, "r": r})], 34, "slash"),
            PatternStep(2, [RelShape("fan", {"angle_rel": -off, "width": side, "r": r})], 34, "slash"),
            PatternStep(2, [RelShape("fan", {"angle_rel": 0.0, "width": front, "r": r * 1.15})], 48, "slash"),
        ]


class EarthCrush(PatternDef):
    def __init__(self):
        super().__init__(PatternID.EARTH_CRUSH, "EarthCrush", cooldown=6, face_toward_target=False)

    def build(self, cfg, rng, ctx, target_uid):
        return [
            PatternStep(3, [RelShape("circle", {"fwd": 0.0, "lat": 0.0, "r": cfg.pat_earth_center_r})], 42, "smash"),
            PatternStep(2, [RelShape("donut", {"r_in": cfg.pat_earth_donut_in, "r_out": cfg.pat_earth_donut_out})], 46, "shock"),
        ]


class FrenzyRush(PatternDef):
    def __init__(self):
        super().__init__(PatternID.FRENZY_RUSH, "FrenzyRush", cooldown=7)

    def build(self, cfg, rng, ctx, target_uid):
        diag = math.hypot(cfg.map_width, cfg.map_height)
        hw = cfg.pat_rush_width * 0.5
        # 단일 스텝: 직선 텔레그래프 → 발동 시 보스가 돌진 (kind=rush_dash)
        return [
            PatternStep(4, [RelShape("line", {"angle_rel": 0.0, "hw": hw, "length": diag, "start": 0.0})],
                        70, "rush", kind="rush_dash", extra={"enhanced": False}),
        ]


class PillarThrow(PatternDef):
    def __init__(self):
        super().__init__(PatternID.PILLAR_THROW, "PillarThrow", cooldown=6, face_toward_target=False)

    def build(self, cfg, rng, ctx, target_uid):
        party = ctx.get("party", {})
        uids = list(party.keys())
        rng.shuffle(uids)
        centers: List[Pos] = []
        for i in range(cfg.pat_throw_count):
            if i < len(uids):
                px, py = party[uids[i]]
                cx = min(max(px + rng.uniform(-2.0, 2.0), 1.5), cfg.map_width - 1.5)
                cy = min(max(py + rng.uniform(-2.0, 2.0), 1.5), cfg.map_height - 1.5)
            else:
                cx = rng.uniform(2.0, cfg.map_width - 2.0)
                cy = rng.uniform(2.0, cfg.map_height - 2.0)
            centers.append((cx, cy))
        steps: List[PatternStep] = []
        for i, (cx, cy) in enumerate(centers):
            tel = 3 if i == 0 else 1   # 1턴 간격 시간차 폭격
            shp = RelShape("circle", {"cx": cx, "cy": cy, "r": cfg.pat_throw_radius}, world=True)
            steps.append(PatternStep(tel, [shp], 44, "throw"))
        return steps


class SpinSweep(PatternDef):
    def __init__(self):
        super().__init__(PatternID.SPIN_SWEEP, "SpinSweep", cooldown=7, face_toward_target=False)

    def build(self, cfg, rng, ctx, target_uid):
        r = cfg.pat_spin_radius
        half = math.pi   # 반원 (full angle = 180°)
        return [
            PatternStep(2, [RelShape("fan", {"angle_rel": 0.0, "width": half, "r": r})], 40, "spin"),
            PatternStep(2, [RelShape("fan", {"angle_rel": math.pi, "width": half, "r": r})], 40, "spin"),
        ]


class BloodRoar(PatternDef):
    def __init__(self):
        super().__init__(PatternID.BLOOD_ROAR, "BloodRoar", cooldown=8, face_toward_target=False)

    def build(self, cfg, rng, ctx, target_uid):
        return [
            PatternStep(5, [RelShape("donut", {"r_in": cfg.pat_roar_in, "r_out": cfg.pat_roar_out})],
                        90, "roar"),
        ]


class CrimsonBrand(PatternDef):
    def __init__(self):
        super().__init__(PatternID.CRIMSON_BRAND, "CrimsonBrand", cooldown=8, face_toward_target=False)

    def pick_target(self, cfg, rng, ctx):
        non_tanks = ctx.get("non_tanks", [])
        if non_tanks:
            return rng.choice(non_tanks)
        party = list(ctx.get("party", {}).keys())
        return rng.choice(party) if party else None

    def build(self, cfg, rng, ctx, target_uid):
        # 표식 대상 중심 원. env 가 매 턴 대상 위치로 shape 를 갱신(follow).
        party = ctx.get("party", {})
        tp = party.get(target_uid, ctx.get("boss_pos", (10.0, 10.0)))
        shp = RelShape("circle", {"cx": tp[0], "cy": tp[1], "r": cfg.pat_brand_radius}, world=True)
        return [
            PatternStep(6, [shp], 80, "brand", kind="brand",
                        extra={"target_uid": target_uid}),
        ]


# step-기반 패턴 레지스트리
PATTERN_REGISTRY: Dict[PatternID, PatternDef] = {
    PatternID.TRIPLE_CLAW: TripleClaw(),
    PatternID.EARTH_CRUSH: EarthCrush(),
    PatternID.FRENZY_RUSH: FrenzyRush(),
    PatternID.PILLAR_THROW: PillarThrow(),
    PatternID.SPIN_SWEEP: SpinSweep(),
    PatternID.BLOOD_ROAR: BloodRoar(),
    PatternID.CRIMSON_BRAND: CrimsonBrand(),
}

# 보스-상태 기믹 (step 시퀀스 없이 boss.py 에서 구동)
GIMMICK_COOLDOWNS: Dict[PatternID, int] = {
    PatternID.COUNTER_RUSH: 10,
    PatternID.STAGGER_LIFT: 12,
    PatternID.SEAL_WIPE: 999,   # 페이즈 전환 시에만
}
