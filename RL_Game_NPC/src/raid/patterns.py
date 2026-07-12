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

# ─────────────────── 텔레그래프 실시간 환산표 (TURN_SEC = 0.3s) ───────────────────
# Unity 가 raid_streamer 를 --turn-interval 0.3 으로 구동 → 1 turn = 0.3초 실시간.
# 사람이 보고 회피 가능하도록 telegraph_turns 를 "플레이타임(초)" 기준으로 설계한다.
# (파이썬 step 수가 아니라 실시간 초로 봐야 함 — 사용자 요구.)
#
#   카테고리                              실시간 목표      turns   비고
#   ─────────────────────────────────────────────────────────────────────────
#   소형/근접 부채꼴(삼연발톱 각 스텝)     1.2s            4       최소 회피 여유
#   중형 원/직선(돌진 windup,             1.5~1.8s        5~6
#     기둥투척 각 원, 낙인)
#   대형 AoE(대지분쇄 도넛, 회전휩쓸기,    2.1~2.4s        7~8
#     혈흔포효)
#
#   패턴별 스텝 telegraph (turns × 0.3 = 초):
#     TripleClaw   L/R/front   4 / 4 / 4          (1.2 / 1.2 / 1.2s)
#     EarthCrush   smash/donut 5 / 7              (1.5 / 2.1s)
#     FrenzyRush   windup      6                  (1.8s)
#     PillarThrow  1st/rest    6 / 5              (1.8 / 1.5s, 시간차 폭격)
#     SpinSweep    half1/half2 7 / 7              (2.1 / 2.1s)
#     BloodRoar    donut       8                  (2.4s)
#     CrimsonBrand brand       6                  (1.8s)
TURN_SEC = 0.3   # Unity turn-interval (실시간 초/턴). 텔레그래프 설계 기준.


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


# ─────────────────── 타겟 선택 헬퍼 (플레이어 편향) ───────────────────

def alive_dealer_uid(rng: random.Random, ctx: Ctx) -> Optional[int]:
    """살아있는 딜러(role==DEALER) uid. 없으면 None.

    ctx['party'] 는 살아있는 유닛만 담고(env._build_ctx), ctx['roles'] 로 역할을 판정.
    통상 딜러는 1명(player_slot)이지만 다중 딜러 대비 랜덤 선택.
    """
    party = ctx.get("party", {})
    roles = ctx.get("roles", {})
    dealers = [uid for uid in party if roles.get(uid) == int(PartyRole.DEALER)]
    if dealers:
        return rng.choice(dealers)
    # roles 가 없을 때의 폴백: 명시된 dealer_uid 가 살아있으면 사용
    duid = ctx.get("dealer_uid")
    if duid is not None and duid in party:
        return duid
    return None


def pick_aimed_target(cfg: RaidConfig, rng: random.Random, ctx: Ctx,
                      bias: float, fallback=None) -> Optional[int]:
    """조준형 패턴의 타깃 uid 선택.

    bias 확률로 살아있는 딜러(플레이어)를 직접 조준한다. 딜러가 없거나 확률에
    걸리지 않으면 fallback()(기본 = 어그로 top)으로 폴백 → 기존 조준 로직 보존.
    """
    if bias > 0.0 and rng.random() < bias:
        duid = alive_dealer_uid(rng, ctx)
        if duid is not None:
            return duid
    if fallback is not None:
        return fallback()
    return ctx.get("aggro_top")


def pick_counter_target(cfg: RaidConfig, rng: random.Random, ctx: Ctx) -> Optional[int]:
    """카운터 돌진(COUNTER_RUSH) 전용 타깃 선택 — 플레이어 편향을 더 높게 적용.

    카운터는 딜러(E) 전용 저지 기믹이므로 pat_counter_player_bias(기본 0.6)로
    플레이어를 자주 조준해 저지 상황을 유도한다. 딜러 사망 시 어그로 top 폴백.

    주의: 현재 카운터 시전 타깃 락은 boss.start_counter(read-only)에서 top_aggro
    로 직접 처리한다. 이 헬퍼는 카운터 조준을 플레이어 편향으로 라우팅하기 위한
    공용 진입점으로, boss.start_counter 가 top_aggro_uid() 대신 이 함수를
    호출하도록 한 줄만 바꾸면 편향이 활성화된다.
    """
    return pick_aimed_target(cfg, rng, ctx, cfg.pat_counter_player_bias)


# ─────────────────── PatternDef ───────────────────

@dataclass
class PatternDef:
    pattern_id: PatternID
    name: str
    cooldown: int
    face_toward_target: bool = True   # facing 을 target 방향으로 락할지

    def pick_target(self, cfg: RaidConfig, rng: random.Random, ctx: Ctx) -> Optional[int]:
        """이 패턴이 바라볼(그리고 조준할) 타깃 uid.

        pat_target_player_bias 확률로 살아있는 딜러(플레이어)를 직접 조준,
        아니면 어그로 top(기존 로직). 딜러 사망 시 어그로 top 폴백.
        """
        return pick_aimed_target(cfg, rng, ctx, cfg.pat_target_player_bias)

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
            # 소형/근접 부채꼴 → 1.2s = 4턴 (각 스텝 독립 예고)
            PatternStep(4, [RelShape("fan", {"angle_rel": off, "width": side, "r": r})], 34, "slash"),
            PatternStep(4, [RelShape("fan", {"angle_rel": -off, "width": side, "r": r})], 34, "slash"),
            PatternStep(4, [RelShape("fan", {"angle_rel": 0.0, "width": front, "r": r * 1.15})], 48, "slash"),
        ]


class EarthCrush(PatternDef):
    def __init__(self):
        super().__init__(PatternID.EARTH_CRUSH, "EarthCrush", cooldown=6, face_toward_target=False)

    def build(self, cfg, rng, ctx, target_uid):
        return [
            # 중형 중심 원 → 1.5s = 5턴, 대형 충격파 도넛 → 2.1s = 7턴
            PatternStep(5, [RelShape("circle", {"fwd": 0.0, "lat": 0.0, "r": cfg.pat_earth_center_r})], 42, "smash"),
            PatternStep(7, [RelShape("donut", {"r_in": cfg.pat_earth_donut_in, "r_out": cfg.pat_earth_donut_out})], 46, "shock"),
        ]


class FrenzyRush(PatternDef):
    def __init__(self):
        super().__init__(PatternID.FRENZY_RUSH, "FrenzyRush", cooldown=7)

    def build(self, cfg, rng, ctx, target_uid):
        hw = cfg.pat_rush_width * 0.5
        # "표식 추격 돌진": 단일 스텝(kind=rush_dash). windup 동안 env 가 매 턴 조준선을
        # 보스→타겟 현재 위치로 재베이크(origin=보스 위치)하고, 발동 시 그 시점 타겟
        # 방향으로 보스가 실제로 이동(charge). 초기 길이는 재베이크 전 1턴용 근사치.
        bx, by = ctx.get("boss_pos", (cfg.map_width / 2.0, cfg.map_height / 2.0))
        tp = ctx.get("party", {}).get(target_uid)
        if tp is not None:
            length = min(cfg.pat_rush_length_max,
                         math.hypot(tp[0] - bx, tp[1] - by) + cfg.pat_rush_length_bonus)
        else:
            length = cfg.pat_rush_length_max
        return [
            PatternStep(cfg.pat_rush_windup_turns,
                        [RelShape("line", {"angle_rel": 0.0, "hw": hw, "length": length, "start": 0.0})],
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
            # 중형 원 시간차 폭격: 첫 원 1.8s=6턴, 후속 원 1.5s=5턴
            # (스텝은 순차 진행 → 각 원이 5턴 간격으로 착탄, 각 예고 최소 1.5s 회피 여유)
            tel = 6 if i == 0 else 5
            shp = RelShape("circle", {"cx": cx, "cy": cy, "r": cfg.pat_throw_radius}, world=True)
            steps.append(PatternStep(tel, [shp], 44, "throw"))
        return steps


class SpinSweep(PatternDef):
    def __init__(self):
        super().__init__(PatternID.SPIN_SWEEP, "SpinSweep", cooldown=7, face_toward_target=False)

    def build(self, cfg, rng, ctx, target_uid):
        r = cfg.pat_spin_radius
        half = math.pi   # 반원 (full angle = 180°)
        # 대형 회전 휩쓸기 → 각 반원 2.1s = 7턴
        return [
            PatternStep(7, [RelShape("fan", {"angle_rel": 0.0, "width": half, "r": r})], 40, "spin"),
            PatternStep(7, [RelShape("fan", {"angle_rel": math.pi, "width": half, "r": r})], 40, "spin"),
        ]


class BloodRoar(PatternDef):
    def __init__(self):
        super().__init__(PatternID.BLOOD_ROAR, "BloodRoar", cooldown=8, face_toward_target=False)

    def build(self, cfg, rng, ctx, target_uid):
        # 대형 도넛 AoE(몸쪽 안전) → 2.4s = 8턴
        return [
            PatternStep(8, [RelShape("donut", {"r_in": cfg.pat_roar_in, "r_out": cfg.pat_roar_out})],
                        90, "roar"),
        ]


class CrimsonBrand(PatternDef):
    def __init__(self):
        super().__init__(PatternID.CRIMSON_BRAND, "CrimsonBrand", cooldown=8, face_toward_target=False)

    def pick_target(self, cfg, rng, ctx):
        # 플레이어 편향 우선, 폴백은 기존 로직(비탱커 중 랜덤 → 파티 중 랜덤).
        def _fallback():
            non_tanks = ctx.get("non_tanks", [])
            if non_tanks:
                return rng.choice(non_tanks)
            party = list(ctx.get("party", {}).keys())
            return rng.choice(party) if party else None
        return pick_aimed_target(cfg, rng, ctx, cfg.pat_target_player_bias,
                                 fallback=_fallback)

    def build(self, cfg, rng, ctx, target_uid):
        # 표식 대상 중심 원. env 가 매 턴 대상 위치로 shape 를 갱신(follow).
        party = ctx.get("party", {})
        tp = party.get(target_uid, ctx.get("boss_pos", (10.0, 10.0)))
        shp = RelShape("circle", {"cx": tp[0], "cy": tp[1], "r": cfg.pat_brand_radius}, world=True)
        # 낙인(표식 산개) → 1.8s = 6턴 (산개 회피 여유 확보)
        return [
            PatternStep(6, [shp], 80, "brand", kind="brand",
                        extra={"target_uid": target_uid}),
        ]


class YellowBurst(PatternDef):
    """노란 확산 원 — 패링(딜러 G) 파훼 기믹.

    플레이어 편향 타겟 중심에 판정용 반경 parry_radius 원을 고정(시전 시점 타겟 위치).
    반경 0→3.0 확산 연출은 Unity 몫. telegraph parry_telegraph_turns(7=2.1s), kind="parry".
    발동 시 원 안 유닛에 parry_damage(55). 단 딜러가 마지막 창에서 PARRY + 보스 바라봄 +
    원 안이면 무효 + 보스 그로기(env._impact_parry 처리).
    """
    def __init__(self):
        super().__init__(PatternID.YELLOW_BURST, "YellowBurst", cooldown=8)

    def build(self, cfg, rng, ctx, target_uid):
        party = ctx.get("party", {})
        tp = party.get(target_uid, ctx.get("boss_pos", (10.0, 10.0)))
        shp = RelShape("circle", {"cx": tp[0], "cy": tp[1], "r": cfg.parry_radius}, world=True)
        return [
            PatternStep(cfg.parry_telegraph_turns, [shp], cfg.parry_damage, "burst",
                        kind="parry", extra={"target_uid": target_uid}),
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
    PatternID.YELLOW_BURST: YellowBurst(),
}

# 보스-상태 기믹 (step 시퀀스 없이 boss.py 에서 구동)
GIMMICK_COOLDOWNS: Dict[PatternID, int] = {
    PatternID.COUNTER_RUSH: 10,
    PatternID.STAGGER_LIFT: 12,
    PatternID.SEAL_WIPE: 999,   # 페이즈 전환 시에만
}
