"""RaidEnv 설정 및 열거형 — "혈월의 마수 군주" 레이드 (유클리드 연속 공간).

보스 컨셉: 로스트아크 '발탄' 모티프 — 늑대형 마수, 근접 브루저.
패턴 기하는 전부 보스 facing 기준 상대 좌표로 정의(shapes.py 참고),
스냅샷 직렬화 시 월드 절대 좌표로 변환해 Unity로 내보낸다.

전면 신규 설계 — 기존 src/boss 환경과 학습 호환 불필요.
"""
from __future__ import annotations
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Dict, List, Tuple


class PartyRole(IntEnum):
    """파티 역할 (4인 파티, DEALER = 플레이어 슬롯)."""
    DEALER = 0
    TANK = 1
    HEALER = 2
    SUPPORT = 3


class PatternID(IntEnum):
    """보스 패턴 11종 — '혈월의 마수 군주'."""
    TRIPLE_CLAW = 0     # 삼연 발톱 (전방 부채꼴 콤보 3스텝)
    EARTH_CRUSH = 1     # 대지 분쇄 (중심 원 → 충격파 도넛 2스텝)
    FRENZY_RUSH = 2     # 폭주 돌진 (직선 + 기둥 충돌 그로기 기믹)
    PILLAR_THROW = 3    # 기둥 투척 (랜덤 원 3개 시간차 폭격)
    SPIN_SWEEP = 4      # 회전 휩쓸기 (반원 2스텝, P2+)
    BLOOD_ROAR = 5      # 혈흔의 포효 (도넛, 몸쪽 안전, P2+)
    CRIMSON_BRAND = 6   # 붉은 낙인 (표식 산개, P2+)
    COUNTER_RUSH = 7    # 카운터 돌진 (파란 발광 창, 저지 기믹)
    STAGGER_LIFT = 8    # (폐지) 무력화 기둥 들어올리기 — 상시 스태거 게이지로 대체, 가중치 0
    SEAL_WIPE = 9       # 전멸기 '혈월 강림' (페이즈 전환, LOS 은신)
    YELLOW_BURST = 10   # 노란 확산 원 — 패링(G) 파훼 기믹 (플레이어 편향, kind="parry")


class PhaseID(IntEnum):
    P1 = 0
    P2 = 1
    P3 = 2


class RaidActionID(IntEnum):
    """NPC/플레이어 행동 공간 (20개).

    8방향 이동 + 대기(9) + 기본공격 + 역할 스킬 + 딜러 조준 설치기 2종/카운터/대시.
    역할별 허용:
      공통  : STAY, 8방향 MOVE, ATTACK_BASIC
      TANK  : ATTACK_SKILL, TAUNT, GUARD
      HEALER: ATTACK_SKILL, HEAL, CLEANSE
      SUPPORT: ATTACK_SKILL, BUFF_ATK, BUFF_SHIELD
      DEALER: ATTACK_SKILL(Q 혈창 투척, 지면 조준 AoE),
              SKILL_2(W 혈월 낙하, 지면 조준 AoE, 딜러 전용), COUNTER(E 저지),
              DASH(회피 기동기 — 조준 방향으로 즉시 이동)

    딜러 조준: env.step(actions, aim_points={"p0": (tx, ty)}) 로 sim 좌표 전달.
    aim 없으면 보스 위치 자동 조준(설치기) / 보스 반대·이동 방향(대시). 사거리 밖 조준점은 경계로 클램프.
    """
    STAY = 0
    MOVE_UP = 1
    MOVE_DOWN = 2
    MOVE_LEFT = 3
    MOVE_RIGHT = 4
    MOVE_UP_LEFT = 5
    MOVE_UP_RIGHT = 6
    MOVE_DOWN_LEFT = 7
    MOVE_DOWN_RIGHT = 8
    ATTACK_BASIC = 9        # 딜러: 지면 조준 설치기(쿨 없음) / 타 역할: 근접 평타
    ATTACK_SKILL = 10       # 딜러: Q 혈창 투척 (조준 AoE) / 타 역할: 강공격
    TAUNT = 11
    GUARD = 12
    HEAL = 13
    CLEANSE = 14
    BUFF_ATK = 15
    BUFF_SHIELD = 16
    COUNTER = 17            # 딜러 E 저지 액션
    SKILL_2 = 18            # 딜러 W 혈월 낙하 (대형 조준 AoE, 딜러 전용)
    DASH = 19              # 딜러 회피 기동기 — 조준 방향으로 dash_distance 즉시 이동
    ULTIMATE = 20          # 딜러 R 궁극기 '혈월 처형' (초대형 조준 AoE, 딜러 전용)
    PARRY = 21             # 딜러 G 패링 — YELLOW_BURST(노란 확산 원) 파훼 저지


# 별칭 (boss_streamer 계열 재사용 호환 — BossActionID 이름도 노출)
BossActionID = RaidActionID


@dataclass
class RoleStats:
    hp: int
    mp: int
    attack: int
    defense: int
    attack_range: float
    move_speed: float = 1.0
    radius: float = 0.3


# 파티 유닛 스탯 (유클리드). 근접 브루저 보스 상대 → 탱커 고HP, 힐러 원거리.
ROLE_STATS: Dict[PartyRole, RoleStats] = {
    # attack 42→50: 스킬 쿨다운 상향(Q3→20/W7→40)으로 줄어든 딜러 DPS 보정. 평타가
    # 필러로 도는 재설계 하에서 FSM 승리 킬타임 중앙값을 목표(700턴 이하, ~602턴)로 복원.
    PartyRole.DEALER:  RoleStats(hp=340, mp=80,  attack=50, defense=5,  attack_range=1.4, move_speed=1.0, radius=0.3),
    PartyRole.TANK:    RoleStats(hp=700, mp=60,  attack=18, defense=22, attack_range=1.4, move_speed=0.9, radius=0.5),
    PartyRole.HEALER:  RoleStats(hp=280, mp=150, attack=12, defense=5,  attack_range=3.5, move_speed=1.0, radius=0.3),
    PartyRole.SUPPORT: RoleStats(hp=360, mp=120, attack=24, defense=10, attack_range=2.5, move_speed=1.0, radius=0.4),
}


@dataclass
class RaidConfig:
    # ── 맵 (연속 공간) ──
    map_width: float = 20.0
    map_height: float = 20.0

    # ── 원형 아레나 (HP 연동 축소) ──
    # 이동/대시/보스 이동 클램프는 사각 경계가 아니라 이 원(중심 arena_center, 반경 = 유효반경 - u.radius).
    # 유효 반경은 보스 HP 구간에 따라 티어별로 축소(티어 변경 시 2턴에 걸쳐 선형). 전멸기 중엔 보류.
    # HP 100~75 / 75~50 / 50~25 / 25~0% → arena_radius_tiers[0..3].
    arena_center: Tuple[float, float] = (10.0, 10.0)
    arena_radius_tiers: Tuple[float, float, float, float] = (9.6, 8.8, 8.2, 7.6)
    arena_shrink_turns: int = 2               # 티어 변경 시 축소에 걸리는 턴수(선형)
    pillar_inner_margin: float = 2.0          # 기둥이 (유효반경 - 이 값) 밖이면 중심 쪽으로 비율 이동

    # ── 파티 ──
    party_roles: List[PartyRole] = field(default_factory=lambda: [
        PartyRole.DEALER, PartyRole.TANK, PartyRole.HEALER, PartyRole.SUPPORT
    ])
    player_slot: int = 0

    # ── 보스 ──
    # 목표 플레이타임 3분 기준 실측 튜닝 (FSM 4인 n=14: 승리 킬타임 중앙값 178.8s,
    # 파티 지속 DPS ~92/턴 × 목표 630턴). 2000 이던 시절엔 18초 컷이었음.
    boss_max_hp: int = 55000
    boss_base_attack: int = 30
    boss_defense: int = 3
    boss_radius: float = 1.0
    boss_move_speed: float = 0.9
    boss_attack_range: float = 1.8
    boss_rotate_speed: float = 3.14159        # 시전 시 최대 회전 (rad). facing 락에 사용
    boss_start_facing: float = 0.0

    # ── 전투: 크리티컬 (로스트아크식 타격감) ──
    # 파티 유닛이 보스에게 주는 모든 피해(기본공격/Q 혈창/W 혈월 등)에 확률 판정.
    # 크리 시 데미지에 crit_multiplier 를 곱하고(int 반올림) 피격 이벤트에 crit=True 를 실어 보낸다.
    # Unity FloatingTextManager 가 crit 플래그로 크게·주황금색·펀치 스케일 연출을 분기.
    crit_chance: float = 0.25                 # 크리티컬 발생 확률 (0~1)
    crit_multiplier: float = 1.8              # 크리티컬 데미지 배수

    # ── 어그로 ──
    aggro_decay: float = 0.95
    aggro_damage_weight: float = 1.0
    aggro_taunt_bonus: float = 200.0

    # ── 타겟 편향 (플레이어 압박/참여 유도) ──
    # 조준형(타겟 지정) 패턴이 어그로 대상(보통 탱커) 대신 플레이어(딜러)를 직접
    # 조준할 확률. "서 있으면 맞는다"는 압박을 만들어 플레이어의 회피·포지셔닝
    # 참여를 강제한다. 살아있는 딜러가 없으면 기존 어그로 로직으로 폴백.
    pat_target_player_bias: float = 0.35
    # 카운터 돌진(COUNTER_RUSH)은 딜러(E) 전용 저지 기믹 → 플레이어 조준 확률을
    # 더 높게 잡아 플레이어가 직접 저지해야 하는 상황을 자주 만든다.
    pat_counter_player_bias: float = 0.6

    # ── 기둥(Pillar) 오브젝트 ──
    # 폭주 돌진 유도(그로기) + 전멸기 LOS 은신에 공용. 고정 4개.
    pillar_positions: List[Tuple[float, float]] = field(default_factory=lambda: [
        (5.0, 5.0), (15.0, 5.0), (5.0, 15.0), (15.0, 15.0),
    ])
    pillar_radius: float = 1.2
    pillar_respawn_turns: int = 20            # 돌진에 파괴된 기둥 재생성 쿨다운

    # ── 페이즈 ──
    # P2 진입 75%, P3 진입 50% — 전멸기는 P3 진입(50%) 시에만 강제(상용 페이싱).
    phase_hp_thresholds: Tuple[float, float] = (0.75, 0.50)
    phase_transition_invuln_turns: int = 2

    # ── 에피소드 ──
    max_steps: int = 1300              # 6.5분 타임아웃 — 3분 목표 전투에 여유

    # ── 관측 / 행동 ──
    # 관측 구성 (총 128차원) — 상세 표는 docs/RAID_V2_DESIGN.md 및 아래 OBS_LAYOUT:
    #   Self(16) + Allies(24) + Boss(12) + PatternCh(11x4=44) + Danger(8)
    #   + Escape(4) + Coop(8) + Pillars(12) + Player(4) = 132
    obs_size: int = 132
    num_actions: int = 22

    # ── 대시(회피 기동기, 딜러) ──
    # 조준 방향(aim_points)으로 dash_distance 즉시 이동. aim 없으면 이동 방향/보스 반대 방향 폴백.
    # 이동 판정은 슬라이딩/탈출 규칙 재사용 — 경로를 0.5 서브스텝으로 나눠 장애물 앞까지만.
    dash_distance: float = 2.5

    # ── 플레이어(딜러) 클라이언트 권위 이동 ──
    # 싱글플레이 기준: 플레이어 위치는 Unity 클라가 소유하고 매 턴 sim 좌표(px,py)로 보고한다.
    # env 는 보고 위치를 아레나/기둥/보스 충돌 클램프 후, 직전 턴 위치에서 아래 속도 상한 이내로
    # 제한(순간이동 방지 새니티)해 채택한다. 보고가 없으면 기존 위치 유지. 사람이 조종 중일 때는
    # 딜러의 MOVE/DASH 이동 효과를 무시(위치는 스트림 소유) — DASH 는 쿨다운/이벤트만 유지.
    # player_speed_cap: 이동 3.33 + 대시 여유 감안한 상한(sim/s). turn_seconds: 한 턴 실시간(초).
    turn_seconds: float = 0.3
    player_speed_cap: float = 6.0

    # ── 스킬 쿨타임 (Unity 스킬바 UI 연동) ──
    # 액션 ID → 쿨다운 턴수. 기본공격/이동/대기는 0(쿨다운 없음).
    # 쿨다운 중 해당 액션 선택 시 STAY 처리. 값은 밸런스 재량(조정 가능).
    # 쿨다운 재설계 (TURN_SEC=0.3s): 스킬을 "가끔 쓰는 한 방"으로, 평타를 필러로.
    # 쿨 없는 평타가 스킬 사이를 메우면서 "평타 쓸 이유"를 복원한다.
    skill_cooldown: int = 5   # (레거시 참조용 기본값)
    skill_cooldowns: Dict[int, int] = field(default_factory=lambda: {
        int(RaidActionID.ATTACK_SKILL): 20,  # 딜러 Q 혈창 투척 (20턴=6.0s) — 타 역할 강공격도 공유
        int(RaidActionID.SKILL_2): 40,       # 딜러 W 혈월 낙하 (40턴=12.0s)
        int(RaidActionID.COUNTER): 25,       # 딜러 E 저지 (25턴=7.5s)
        int(RaidActionID.DASH): 17,          # 딜러 대시 회피 (17턴=5.1s)
        int(RaidActionID.ULTIMATE): 200,     # 딜러 R 궁극 '혈월 처형' (200턴=60.0s)
        int(RaidActionID.PARRY): 10,         # 딜러 G 패링 (10턴=3.0s)
        int(RaidActionID.TAUNT): 6,          # (6턴=1.8s)
        int(RaidActionID.GUARD): 4,          # (4턴=1.2s)
        int(RaidActionID.HEAL): 4,           # (4턴=1.2s)
        int(RaidActionID.CLEANSE): 8,        # (8턴=2.4s)
        int(RaidActionID.BUFF_ATK): 8,       # (8턴=2.4s)
        int(RaidActionID.BUFF_SHIELD): 6,    # (6턴=1.8s)
    })

    # ── 딜러 조준 설치기 (로아식 지면 지정 AoE, 즉시 발동 — 텔레그래프 없음) ──
    # Q 혈창 투척 (ATTACK_SKILL)
    aim_q_radius: float = 1.8
    aim_q_range: float = 7.0
    aim_q_damage: int = 45
    # W 혈월 낙하 (SKILL_2)
    aim_w_radius: float = 3.0
    aim_w_range: float = 9.0
    aim_w_damage: int = 110
    # R 궁극기 '혈월 처형' (ULTIMATE) — 초대형 조준 AoE, 딜러 전용, 크리 판정 적용.
    # 무력화(스태거) 활성 중이면 대량 게이지 기여(ult_stagger_contrib).
    aim_ult_radius: float = 4.0
    aim_ult_range: float = 9.0
    aim_ult_damage: int = 400
    ult_stagger_contrib: float = 60.0
    # 평타 (딜러 ATTACK_BASIC) — 롤 제리 Q 식 "방향 라인 스킬샷".
    # 쿨 없음, 데미지는 유닛 attack 그대로(별도 상수 없음). aim_points(tx,ty)는 "방향 지시점"으로만
    # 사용 — 플레이어 위치에서 그 방향으로 고정 길이 aim_basic_range(5.0), 반폭 aim_basic_halfwidth(0.5)
    # 라인 판정(보스 원과 교차 시 명중). 사거리 내 어디를 찍든 방향만 사용.
    # (aim_basic_radius 는 레거시 — 라인화 후 미사용.)
    aim_basic_radius: float = 1.2
    aim_basic_range: float = 5.0
    aim_basic_halfwidth: float = 0.5

    # ── 패턴별 쿨타임 배율 (페이즈별) ──
    phase_cooldown_scale: Tuple[float, float, float] = (1.0, 0.85, 0.7)

    # ── 패턴 간 휴식 간격 (실시간 페이싱) ──
    # 보스가 한 패턴을 끝낸 직후 곧바로 다음 패턴을 시전하지 않도록 강제 휴식.
    # TURN=0.3s → 5턴 = 1.5초. 그로기/무적/페이즈전환 시간과 자연 중첩(env.py).
    pattern_gap_turns: int = 5

    # ── 페이즈별 패턴 가중치 (SEAL_WIPE 은 페이즈 전환 시 강제 — 여기 없음) ──
    # STAGGER_LIFT 은 상시 스태거 게이지 시스템으로 대체 — 풀에서 제거(가중치 0).
    # YELLOW_BURST(패링 파훼) 는 P1 부터 0.12 로 풀에 상시 포함.
    pattern_weights: Dict[int, Dict[int, float]] = field(default_factory=lambda: {
        int(PhaseID.P1): {
            int(PatternID.TRIPLE_CLAW): 0.28, int(PatternID.EARTH_CRUSH): 0.22,
            int(PatternID.FRENZY_RUSH): 0.22, int(PatternID.PILLAR_THROW): 0.18,
            int(PatternID.COUNTER_RUSH): 0.10, int(PatternID.YELLOW_BURST): 0.12,
        },
        int(PhaseID.P2): {
            int(PatternID.TRIPLE_CLAW): 0.16, int(PatternID.EARTH_CRUSH): 0.14,
            int(PatternID.FRENZY_RUSH): 0.14, int(PatternID.PILLAR_THROW): 0.12,
            int(PatternID.COUNTER_RUSH): 0.08, int(PatternID.SPIN_SWEEP): 0.12,
            int(PatternID.BLOOD_ROAR): 0.10, int(PatternID.CRIMSON_BRAND): 0.08,
            int(PatternID.YELLOW_BURST): 0.12,
        },
        int(PhaseID.P3): {
            int(PatternID.TRIPLE_CLAW): 0.14, int(PatternID.EARTH_CRUSH): 0.12,
            int(PatternID.FRENZY_RUSH): 0.14, int(PatternID.PILLAR_THROW): 0.10,
            int(PatternID.COUNTER_RUSH): 0.08, int(PatternID.SPIN_SWEEP): 0.12,
            int(PatternID.BLOOD_ROAR): 0.12, int(PatternID.CRIMSON_BRAND): 0.10,
            int(PatternID.YELLOW_BURST): 0.12,
        },
    })

    # ── 패턴 기하 파라미터 ──
    pat_claw_range: float = 3.8
    pat_claw_side_angle_deg: float = 60.0     # 좌/우 스텝 부채꼴 각
    pat_claw_front_angle_deg: float = 90.0    # 대형 전방 스텝 각
    pat_earth_center_r: float = 3.0
    pat_earth_donut_in: float = 3.0
    pat_earth_donut_out: float = 8.0
    pat_rush_width: float = 2.5               # 돌진 직선 반폭 (full width 2.5 → hw 1.25)
    # ── FRENZY_RUSH(폭주 돌진) "표식 추격 돌진" ──
    # windup 동안 보스→타겟 조준선을 매 턴 재베이크(origin=보스 현재 위치). fire 시
    # 보스가 그 시점 타겟 방향으로 charge_turns 에 걸쳐 실제 이동(턴당 charge_speed).
    # charge_speed 는 스냅샷 보간 순간이동 임계(3.0) 미만으로 유지.
    pat_rush_windup_turns: int = 8            # 조준선 예고 (8턴=2.4s)
    pat_rush_charge_turns: int = 3            # 실제 돌진 지속 턴수
    pat_rush_charge_speed: float = 2.8        # 돌진 턴당 최대 이동 (snap 임계 3.0 미만)
    pat_rush_length_bonus: float = 2.0        # 조준선 길이 = 보스→타겟 거리 + bonus
    pat_rush_length_max: float = 12.0         # 조준선 최대 길이
    pat_throw_radius: float = 2.5
    pat_throw_count: int = 3
    pat_spin_radius: float = 6.0
    pat_roar_in: float = 2.0
    pat_roar_out: float = 9.0
    pat_brand_radius: float = 3.5
    pat_brand_escape_distance: float = 4.0

    # ── 카운터 돌진 기믹 ──
    # 실시간(TURN=0.3s) 기준: 3턴=0.9초는 저지 반응에 너무 짧음 → 6턴=1.8초.
    counter_window_turns: int = 6
    counter_front_angle_deg: float = 70.0     # 보스 전방 판정 각 (타이트화 100→70)
    counter_range: float = 2.0                # 보스 표면 + 근접 판정 거리 (2.2→2.0)
    counter_fail_damage_scale: float = 1.5    # 실패 시 강화 돌진 배율
    counter_miss_cooldown_scale: float = 0.5  # 각도/거리 조건 밖 시도 시 쿨다운 절반만 적용(재시도 여지)
    counter_facing_angle_deg: float = 60.0    # 성공 조건: 딜러가 보스를 바라봄(facing vs 보스방향 각차 ≤ 60°)
    counter_success_grog_turns: int = 7       # 저지 성공 시 보스 그로기 7턴(2.1s) 보장

    # ── 패링 (딜러 G) — YELLOW_BURST(노란 확산 원) 파훼 ──
    # 노란 원: 플레이어 편향 타겟 중심, 판정용 반경 3.0, telegraph 7턴(2.1s), 발동 대미지 55.
    # 딜러가 마지막 parry_window_turns(2턴) 창에서 PARRY + 보스 바라봄(≤60°) + 원 안 → 무효 + 보스 그로기 4턴.
    parry_radius: float = 3.0
    parry_telegraph_turns: int = 7
    parry_damage: int = 55
    parry_window_turns: int = 2               # 발동 직전 몇 턴 안에 PARRY 를 눌러야 유효한가
    parry_facing_angle_deg: float = 60.0
    parry_success_grog_turns: int = 4
    parry_fail_cooldown_scale: float = 0.5    # 조건 미달 PARRY 는 쿨 절반

    # ── 무력화 (스태거) — 상시 게이지 신시스템 ──
    # 게이지는 stagger_max 에서 시작해 스킬 명중마다 감소, 0 도달 → 보스 그로기(stagger_break_grog_turns)
    # + stagger_break 이벤트 + 게이지 max 리셋. 그로기 중엔 게이지 감소 없음.
    # stagger_values: 액션ID → 감소량(상=궁80/중=W30·카운터40/하=Q12·패링25·평타3).
    stagger_max: float = 300.0
    stagger_break_grog_turns: int = 8         # 무력화 파괴 시 딜타임 8턴(2.4s)
    stagger_values: Dict[int, float] = field(default_factory=lambda: {
        int(RaidActionID.ATTACK_BASIC): 3.0,   # 평타(딜러 라인샷 / 타 역할 근접)
        int(RaidActionID.ATTACK_SKILL): 12.0,  # Q 혈창 / 타 역할 강공격
        int(RaidActionID.SKILL_2): 30.0,       # W 혈월 낙하
        int(RaidActionID.ULTIMATE): 80.0,      # R 궁극 '혈월 처형'
        int(RaidActionID.PARRY): 25.0,         # 패링 성공
        int(RaidActionID.COUNTER): 40.0,       # 카운터 성공
    })
    # (레거시 STAGGER_LIFT 패턴 참조용 — 풀에서 제거됐으나 코드 잔존)
    stagger_gauge: float = 200.0
    stagger_window_turns: int = 10
    stagger_contrib_basic: float = 12.0
    stagger_contrib_skill: float = 25.0
    stagger_contrib_taunt: float = 35.0
    stagger_fail_damage: int = 120
    stagger_success_grog_turns: int = 4

    # ── 전멸기 '혈월 강림' (LOS 은신) ──
    # ~10초: Unity 시네마틱 인트로 3.6초 + 실제 파훼 이동 ~6.4초 (준비시간 확보).
    seal_wind_up_turns: int = 30
    seal_grog_turns: int = 8                  # 성공 시 딜타임 (파훼 보상 확대)
    # 판정: 유닛-보스 선분이 살아있는 기둥 원과 교차하면 "은신 성공"

    # ── 탱커 가드 딜타임 ──
    guard_reduction: float = 0.8              # 피해 80% 경감
    guard_stun_turns: int = 2                 # 가드 성공 시 보스 경직 (딜타임)

    # ── 돌진 기둥 충돌 그로기 ──
    rush_pillar_grog_turns: int = 3

    # ── 일반 기믹 그로기 ──
    brand_success_grog_turns: int = 1

    # ─── 보상 설계 철학 (rewards.py) ───
    # 1. 위험(발동 임박) 시 양수 보상 차단 — 도망만
    # 2. 딜/힐/기믹 파훼는 큰 보상
    # 3. 시간 패널티로 장기전 억제
    # 4. 비참여(보스에서 멀리) 패널티
    rw_boss_damage_per_hp: float = 0.18
    rw_boss_kill: float = 500.0
    rw_phase_clear: float = 25.0
    rw_player_alive_step: float = 0.02
    rw_wipe: float = -150.0
    rw_time_penalty: float = -0.02
    rw_danger_stay: float = -5.0
    rw_danger_hit: float = -8.0
    rw_death: float = -15.0
    rw_dodge_safe: float = 1.0

    # 역할 보상
    rw_heal_per_hp: float = 0.5
    rw_heal_critical: float = 3.0
    rw_tank_aggro_hold: float = 1.5
    rw_tank_aggro_lose: float = -1.0
    rw_taunt_good: float = 2.0
    rw_buff_hit: float = 2.5
    rw_guard_success: float = 12.0

    # 기믹 보상
    rw_counter_success: float = 30.0
    rw_counter_fail: float = -15.0
    rw_parry_success: float = 30.0
    rw_parry_fail: float = -8.0
    rw_stagger_success: float = 40.0          # stagger_break(무력화 파괴) 보상
    rw_stagger_fail: float = -20.0            # (레거시 — 상시 게이지 신시스템엔 미방출)
    rw_stagger_contribution: float = 0.3
    rw_brand_spread: float = 4.0
    rw_rush_pillar_lure: float = 15.0         # 돌진을 기둥으로 유도 성공
    rw_seal_hidden: float = 3.0               # LOS 은신 유지 (매 턴)
    rw_seal_success: float = 60.0
    rw_seal_fail: float = -100.0

    engage_distance: float = 5.0
    disengage_distance: float = 9.0


# 스냅샷/스킬바 UI 용: 액션 ID ↔ 의미있는 스킬 키 이름
# (Unity 스킬바가 이 키로 쿨다운 게이지를 표시. RAID_V2_DESIGN.md 매핑 표 참고)
SKILL_KEYS: Dict[int, str] = {
    int(RaidActionID.ATTACK_SKILL): "skill",     # 딜러 Q
    int(RaidActionID.SKILL_2): "skill2",         # 딜러 W
    int(RaidActionID.COUNTER): "counter",        # 딜러 E
    int(RaidActionID.DASH): "dash",              # 딜러 대시 (회피 기동기)
    int(RaidActionID.ULTIMATE): "ult",           # 딜러 R 궁극 '혈월 처형'
    int(RaidActionID.PARRY): "parry",            # 딜러 G 패링
    int(RaidActionID.TAUNT): "taunt",
    int(RaidActionID.GUARD): "guard",
    int(RaidActionID.HEAL): "heal",
    int(RaidActionID.CLEANSE): "cleanse",
    int(RaidActionID.BUFF_ATK): "buff_atk",
    int(RaidActionID.BUFF_SHIELD): "buff_shield",
}

# 역할별 고유 스킬 2종 (관측 Self 블록 cd_roleA/cd_roleB 에 노출).
# 딜러: W(대형 설치기)/E(카운터). Q 쿨다운은 공통 cd_skill 슬롯이 커버.
ROLE_SKILLS: Dict[PartyRole, Tuple[int, int]] = {
    PartyRole.DEALER:  (int(RaidActionID.SKILL_2), int(RaidActionID.COUNTER)),
    PartyRole.TANK:    (int(RaidActionID.TAUNT), int(RaidActionID.GUARD)),
    PartyRole.HEALER:  (int(RaidActionID.HEAL), int(RaidActionID.CLEANSE)),
    PartyRole.SUPPORT: (int(RaidActionID.BUFF_ATK), int(RaidActionID.BUFF_SHIELD)),
}

# 스냅샷 cooldowns 노출용 스킬바 구성 (역할별 전체 스킬).
# 딜러는 {"skill": Q, "skill2": W, "counter": E} 3키.
SKILL_BAR: Dict[PartyRole, Tuple[int, ...]] = {
    PartyRole.DEALER:  (int(RaidActionID.ATTACK_SKILL), int(RaidActionID.SKILL_2),
                        int(RaidActionID.COUNTER), int(RaidActionID.DASH),
                        int(RaidActionID.ULTIMATE), int(RaidActionID.PARRY)),
    PartyRole.TANK:    (int(RaidActionID.TAUNT), int(RaidActionID.GUARD)),
    PartyRole.HEALER:  (int(RaidActionID.HEAL), int(RaidActionID.CLEANSE)),
    PartyRole.SUPPORT: (int(RaidActionID.BUFF_ATK), int(RaidActionID.BUFF_SHIELD)),
}


# 관측 벡터 블록 오프셋 (문서/디버그용) — 합계 132
OBS_LAYOUT: List[Tuple[str, int]] = [
    ("self", 16),
    ("allies", 24),
    ("boss", 12),
    ("pattern_channels", 44),   # 11 패턴 x 4 (active, turns_norm, am_I_target, in_danger_here)
    ("danger_sensor", 8),
    ("escape", 4),
    ("coop", 8),                # stagger(2) + seal_los(4) + counter(2)
    ("pillars", 12),            # 4 x (dx/10, dy/10, alive)
    ("player", 4),
]
