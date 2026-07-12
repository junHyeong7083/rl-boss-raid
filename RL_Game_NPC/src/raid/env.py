"""RaidEnv — '혈월의 마수 군주' 레이드 환경 (유클리드 연속 공간, 턴제).

- 위치: float (0 ~ map). 이동 8방향, 원형 충돌(파티원끼리 통과, 보스/기둥만 차단).
- 패턴: PatternStep 시퀀스. facing 상대 기하 → 월드 bake. 스텝별 텔레그래프.
- 기믹: 카운터 / 무력화(스태거) / 전멸기(LOS 은신) / 돌진-기둥 그로기 / 탱커 가드 딜타임.

API (boss_streamer 계열 재사용 호환):
  RaidEnv(config), reset()->obs, step(actions)->(obs,rew,done,info),
  done/victory/wipe, get_snapshot(), current_step, step_events, boss, units.
"""
from __future__ import annotations
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple
import math
import numpy as np
import random

from .config import (
    RaidConfig, PartyRole, PatternID, PhaseID, RaidActionID,
    ROLE_STATS, ROLE_SKILLS, SKILL_KEYS, SKILL_BAR,
)
from .boss import Boss, ActivePattern
from .patterns import PATTERN_REGISTRY, PatternStep
from .shapes import (
    Shape, Pos, sample_danger_sensor, segment_intersects_circle,
    _point_in_line_segment,
)
from .rewards import RewardComputer


@dataclass
class PartyUnit:
    uid: int
    role: PartyRole
    x: float
    y: float
    hp: int
    mp: int
    max_hp: int
    max_mp: int
    attack: int
    defense: int
    attack_range: float
    move_speed: float
    radius: float
    alive: bool = True
    cooldowns: Dict[int, int] = field(default_factory=dict)
    buff_atk: int = 0
    buff_shield: int = 0
    buff_guard: int = 0
    marked_turns: int = 0
    total_damage_dealt: int = 0
    total_damage_taken: int = 0
    total_heal_done: int = 0


@dataclass
class Pillar:
    x: float
    y: float
    radius: float
    alive: bool = True
    respawn_timer: int = 0


class RaidEnv:
    def __init__(self, config: Optional[RaidConfig] = None, seed: Optional[int] = None):
        self.config = config or RaidConfig()
        self.rng = random.Random(seed)

        self.units: Dict[int, PartyUnit] = {}
        self.boss: Optional[Boss] = None
        self.pillars: List[Pillar] = []
        self.current_step = 0
        self.done = False
        self.wipe = False
        self.victory = False

        self.reward_computer = RewardComputer(self.config)
        self.step_events: Dict[int, List[dict]] = {}

        self._prev_unit_positions: Dict[int, Tuple[float, float]] = {}
        self._prev_boss_pos: Tuple[float, float] = (0.0, 0.0)
        self._counter_success = False
        self._aim_points: Dict[str, Tuple[float, float]] = {}
        # 패턴 종료 후 강제 휴식 카운터(실시간 페이싱). >0 이면 신규 패턴 시전 보류.
        self._pattern_gap_remaining = 0

        self.reset()

    # ────────────── 편의 ──────────────
    def agent_ids(self) -> List[str]:
        return [f"p{i}" for i in range(len(self.config.party_roles))]

    def uid_of(self, agent_id: str) -> int:
        return int(agent_id[1:])

    def party_positions(self) -> Dict[int, Pos]:
        return {u.uid: (u.x, u.y) for u in self.units.values() if u.alive}

    def party_roles(self) -> Dict[int, int]:
        return {u.uid: int(u.role) for u in self.units.values()}

    def _build_ctx(self) -> Dict:
        return {
            "boss_pos": (self.boss.x, self.boss.y),
            "aggro_top": self.boss.top_aggro_uid(),
            "party": self.party_positions(),
            "roles": self.party_roles(),
            "dealer_uid": self.config.player_slot,
            "non_tanks": [u.uid for u in self.units.values()
                          if u.alive and u.role != PartyRole.TANK],
        }

    # ────────────── reset ──────────────
    def reset(self, seed: Optional[int] = None) -> Dict[str, np.ndarray]:
        if seed is not None:
            self.rng = random.Random(seed)
        cfg = self.config
        self.current_step = 0
        self.done = False
        self.wipe = False
        self.victory = False
        self.step_events.clear()
        self._counter_success = False
        self._pattern_gap_remaining = 0
        self.units.clear()

        # 기둥 (고정 4개)
        self.pillars = [Pillar(px, py, cfg.pillar_radius) for (px, py) in cfg.pillar_positions]

        # 배치
        margin = 3.0
        boss_x = self.rng.uniform(margin, cfg.map_width - margin)
        boss_y = self.rng.uniform(margin, cfg.map_height - margin)
        angle = self.rng.uniform(0, 2 * math.pi)
        dist = self.rng.uniform(5.0, 8.0)
        cx = min(max(boss_x + math.cos(angle) * dist, margin), cfg.map_width - margin)
        cy = min(max(boss_y + math.sin(angle) * dist, margin), cfg.map_height - margin)

        for i, role in enumerate(cfg.party_roles):
            st = ROLE_STATS[role]
            sx = min(max(cx + self.rng.uniform(-1.0, 1.0), st.radius), cfg.map_width - st.radius)
            sy = min(max(cy + self.rng.uniform(-1.0, 1.0), st.radius), cfg.map_height - st.radius)
            self.units[i] = PartyUnit(
                uid=i, role=role, x=sx, y=sy, hp=st.hp, mp=st.mp,
                max_hp=st.hp, max_mp=st.mp, attack=st.attack, defense=st.defense,
                attack_range=st.attack_range, move_speed=st.move_speed, radius=st.radius,
            )

        self.boss = Boss(config=cfg, rng=self.rng)
        self.boss.x = boss_x
        self.boss.y = boss_y
        self.boss.hp = cfg.boss_max_hp
        for u in self.units.values():
            self.boss.aggro[u.uid] = self.rng.uniform(0, 5)

        self._prev_boss_pos = (self.boss.x, self.boss.y)
        self._prev_unit_positions = {u.uid: (u.x, u.y) for u in self.units.values()}
        return self._get_all_observations()

    # ────────────── step ──────────────
    def step(self, actions: Dict[str, int],
             aim_points: Optional[Dict[str, Tuple[float, float]]] = None):
        """aim_points: {"p0": (tx, ty)} — 딜러 조준 설치기(Q/W)의 sim 좌표 조준점.
        없으면 보스 위치 자동 조준. 사거리 밖은 경계로 클램프."""
        cfg = self.config
        self._aim_points = aim_points or {}
        self._prev_boss_pos = (self.boss.x, self.boss.y)
        self._prev_unit_positions = {u.uid: (u.x, u.y) for u in self.units.values()}
        self.step_events = {uid: [] for uid in self.units}
        self.current_step += 1
        self._counter_success = False
        # 이번 턴 시작 시 패턴 진행 여부 (종료 감지용 — 패턴 간 휴식 gap 트리거).
        had_pattern = self.boss.active_pattern is not None

        # 1. 페이즈 전이 처리 (상용 페이싱)
        #    - P1→P2 (HP 75%): phase_clear 이벤트만. 전멸기 없음.
        #      (invuln 2턴은 boss.check_phase_transition 이 기존대로 세팅.)
        #    - P2→P3 (HP 50%): phase_clear + 전멸기 '혈월 강림' 강제 발동 + 시네마틱.
        #    전투 극초반(구 66%)에 전멸기가 터지던 문제 제거 — 전멸기는 P3 진입 시에만.
        phase_changed = self.boss.check_phase_transition()
        if phase_changed:
            for uid in self.units:
                self.step_events[uid].append({"type": "phase_clear"})
            if self.boss.phase == PhaseID.P3:
                # 숨을 기둥이 없어 파훼 불가능한 상황 원천 차단 — 전멸기 시작 전 기둥 전부 재생성.
                self._respawn_all_pillars()
                self.boss.start_seal(self._build_ctx())
                for uid in self.units:
                    self.step_events[uid].append({
                        "type": "cinematic_start",
                        "pattern": int(PatternID.SEAL_WIPE),
                        "duration_turns": cfg.seal_wind_up_turns,
                    })

        # 2. 파티 행동 (가드 버프/카운터 시도/딜 먼저 처리)
        self._resolve_party_actions(actions)

        # 3. 보스 패턴 구동 (임팩트 — 가드 버프 이미 세팅됨)
        self._tick_boss_pattern()

        # 3.5. 패턴이 이번 턴에 종료됐으면 휴식 gap 시작 (실시간 페이싱).
        #      페이즈 전환으로 패턴이 취소된 경우도 자연 포함(had_pattern 기록 시점이 전환 이전).
        #      카운터 실패 → 즉시 강화 돌진처럼 곧바로 새 패턴이 세팅된 경우는 active 가
        #      여전히 non-None 이라 gap 이 걸리지 않음(의도된 연계).
        if had_pattern and self.boss.active_pattern is None:
            self._pattern_gap_remaining = cfg.pattern_gap_turns

        # 4. 보스 idle → 신규 패턴 시전 (단, 휴식 gap 소진 후에만)
        if (self.boss.active_pattern is None and not self.boss.is_incapacitated()
                and self._pattern_gap_remaining <= 0):
            pid = self.boss.select_pattern()
            if pid is not None:
                self._start_pattern(pid)

        # 5. 보스 이동 (idle & 정상 상태일 때만 어그로 추격)
        if self.boss.active_pattern is None and not self.boss.is_incapacitated():
            top = self.boss.top_aggro_uid()
            if top is not None and top in self.units and self.units[top].alive:
                t = self.units[top]
                self._move_boss_toward(t.x, t.y)

        # 6. 기둥 재생성 틱
        for p in self.pillars:
            if not p.alive:
                p.respawn_timer -= 1
                if p.respawn_timer <= 0:
                    p.alive = True

        # 7. 버프/쿨다운 틱
        for u in self.units.values():
            if u.buff_atk > 0: u.buff_atk -= 1
            if u.buff_shield > 0: u.buff_shield -= 1
            if u.buff_guard > 0: u.buff_guard -= 1
            if u.marked_turns > 0: u.marked_turns -= 1
            for k in list(u.cooldowns.keys()):
                u.cooldowns[k] = max(0, u.cooldowns[k] - 1)

        # 7.5. 패턴 간 휴식 gap 카운트다운 (무조건 — 그로기/무적/페이즈전환 시간과 자연 중첩).
        if self._pattern_gap_remaining > 0:
            self._pattern_gap_remaining -= 1

        # 8. 보스 end-of-turn
        self.boss.tick_end_of_turn()

        # 9. 종료 조건
        self.victory = self.boss.hp <= 0
        self.wipe = all((not u.alive) for u in self.units.values())
        self.done = (self.victory or self.wipe or self.current_step >= cfg.max_steps)

        obs = self._get_all_observations()
        rewards = self.reward_computer.compute(self)
        dones = {aid: self.done for aid in self.agent_ids()}
        infos = self._build_infos()
        return obs, rewards, dones, infos

    # ────────────── 패턴 시작 (모드 분기) ──────────────
    def _start_pattern(self, pid: PatternID):
        ctx = self._build_ctx()
        if pid == PatternID.COUNTER_RUSH:
            self.boss.start_counter(ctx)
        elif pid == PatternID.STAGGER_LIFT:
            self.boss.start_stagger(ctx)
            for uid in self.units:
                self.step_events[uid].append({"type": "stagger_start"})
        else:
            self.boss.start_step_pattern(pid, ctx)

    # ────────────── 파티 행동 ──────────────
    def _resolve_party_actions(self, actions: Dict[str, int]):
        # 이동
        move_intents: Dict[int, Tuple[float, float]] = {}
        for aid, action in actions.items():
            uid = self.uid_of(aid)
            u = self.units.get(uid)
            if not u or not u.alive:
                continue
            move_intents[uid] = self._move_delta(u, action)
        for uid in sorted(move_intents.keys()):
            dx, dy = move_intents[uid]
            if dx == 0 and dy == 0:
                continue
            u = self.units[uid]
            nx = min(max(u.x + dx, u.radius), self.config.map_width - u.radius)
            ny = min(max(u.y + dy, u.radius), self.config.map_height - u.radius)
            if not self._blocked_for_unit(uid, nx, ny):
                u.x, u.y = nx, ny
                continue
            # 충돌 슬라이딩: 대각/직선 이동이 장애물(보스/기둥)에 막히면 축 성분으로
            # 분해해 미끄러진다. (없으면 기둥 뒤 은신 등에서 벽에 붙어 영구 제자리걸음)
            sx = min(max(u.x + dx, u.radius), self.config.map_width - u.radius)
            if dx != 0 and not self._blocked_for_unit(uid, sx, u.y):
                u.x = sx
                continue
            sy = min(max(u.y + dy, u.radius), self.config.map_height - u.radius)
            if dy != 0 and not self._blocked_for_unit(uid, u.x, sy):
                u.y = sy
        # 비이동
        for aid, action in actions.items():
            uid = self.uid_of(aid)
            u = self.units.get(uid)
            if not u or not u.alive:
                continue
            self._execute_non_move(u, action)

    def _move_delta(self, u: PartyUnit, action: int) -> Tuple[float, float]:
        s = u.move_speed
        d = s * 0.7071
        A = RaidActionID
        return {
            int(A.MOVE_UP): (0.0, -s), int(A.MOVE_DOWN): (0.0, s),
            int(A.MOVE_LEFT): (-s, 0.0), int(A.MOVE_RIGHT): (s, 0.0),
            int(A.MOVE_UP_LEFT): (-d, -d), int(A.MOVE_UP_RIGHT): (d, -d),
            int(A.MOVE_DOWN_LEFT): (-d, d), int(A.MOVE_DOWN_RIGHT): (d, d),
        }.get(int(action), (0.0, 0.0))

    def _obstacle_circles(self):
        """보스 + 살아있는 기둥. 파티원끼리는 통과."""
        obs = [(self.boss.x, self.boss.y, self.config.boss_radius)]
        for p in self.pillars:
            if p.alive:
                obs.append((p.x, p.y, p.radius))
        return obs

    def _blocked_for_unit(self, uid: int, nx: float, ny: float) -> bool:
        u = self.units[uid]
        for ox, oy, orad in self._obstacle_circles():
            limit = u.radius + orad - 0.05
            if math.hypot(nx - ox, ny - oy) < limit:
                # 탈출 허용: 이미 장애물과 겹쳐 있으면(보스가 유닛 위로 이동해 오는 등)
                # 거리가 멀어지는 이동은 통과 — 없으면 보스 몸속에 영구 감금된다.
                if math.hypot(u.x - ox, u.y - oy) < limit and \
                   math.hypot(nx - ox, ny - oy) >= math.hypot(u.x - ox, u.y - oy) - 1e-6:
                    continue
                return True
        return False

    def _execute_non_move(self, u: PartyUnit, action: int):
        a = RaidActionID(action)
        move_set = {RaidActionID.STAY, RaidActionID.MOVE_UP, RaidActionID.MOVE_DOWN,
                    RaidActionID.MOVE_LEFT, RaidActionID.MOVE_RIGHT,
                    RaidActionID.MOVE_UP_LEFT, RaidActionID.MOVE_UP_RIGHT,
                    RaidActionID.MOVE_DOWN_LEFT, RaidActionID.MOVE_DOWN_RIGHT}
        if a in move_set:
            return

        role = u.role
        allowed = {
            RaidActionID.ATTACK_BASIC: True,
            RaidActionID.ATTACK_SKILL: True,
            RaidActionID.TAUNT: role == PartyRole.TANK,
            RaidActionID.GUARD: role == PartyRole.TANK,
            RaidActionID.HEAL: role == PartyRole.HEALER,
            RaidActionID.CLEANSE: role == PartyRole.HEALER,
            RaidActionID.BUFF_ATK: role == PartyRole.SUPPORT,
            RaidActionID.BUFF_SHIELD: role == PartyRole.SUPPORT,
            RaidActionID.COUNTER: role == PartyRole.DEALER,
            RaidActionID.SKILL_2: role == PartyRole.DEALER,
            RaidActionID.DASH: role == PartyRole.DEALER,
        }
        if not allowed.get(a, False):
            self.step_events[u.uid].append({"type": "invalid_action"})
            return
        # 쿨다운 중이면 STAY 처리 (페널티 없음)
        if u.cooldowns.get(int(a), 0) > 0:
            return

        cd = self.config.skill_cooldowns.get(int(a), 0)

        if a == RaidActionID.ATTACK_BASIC:
            if role == PartyRole.DEALER:
                # 딜러 평타 = 롤 논스마트키식 지면 조준 설치기 (쿨 없음, 데미지=유닛 attack).
                # Q/W 와 동일한 지면 조준 AoE 경로. aim 없으면(NPC 딜러/FSM) 보스 자동 조준.
                self._do_aim_skill(u, "basic",
                                   self.config.aim_basic_radius,
                                   self.config.aim_basic_range,
                                   u.attack, is_skill=False)
            else:
                # 탱커/힐러/서포터 평타 = 기존 근접 로직 유지 (FSM NPC 호환).
                self._do_attack(u, skill=False)
        elif a == RaidActionID.ATTACK_SKILL:
            u.cooldowns[int(a)] = cd
            if role == PartyRole.DEALER:
                # Q 혈창 투척 — 지면 조준 AoE (즉시 발동)
                self._do_aim_skill(u, "skill",
                                   self.config.aim_q_radius,
                                   self.config.aim_q_range,
                                   self.config.aim_q_damage)
            else:
                self._do_attack(u, skill=True)
        elif a == RaidActionID.SKILL_2:
            u.cooldowns[int(a)] = cd
            # W 혈월 낙하 — 대형 지면 조준 AoE (딜러 전용)
            self._do_aim_skill(u, "skill2",
                               self.config.aim_w_radius,
                               self.config.aim_w_range,
                               self.config.aim_w_damage)
        elif a == RaidActionID.TAUNT:
            u.cooldowns[int(a)] = cd
            self.boss.add_aggro(u.uid, self.config.aggro_taunt_bonus)
            if self.boss.stagger_active:
                self.boss.stagger_gauge -= self.config.stagger_contrib_taunt
                self.step_events[u.uid].append({"type": "stagger_contribute",
                                                "amount": self.config.stagger_contrib_taunt})
            self.step_events[u.uid].append({"type": "taunt"})
        elif a == RaidActionID.GUARD:
            u.cooldowns[int(a)] = cd
            u.buff_guard = 1
        elif a == RaidActionID.HEAL:
            u.cooldowns[int(a)] = cd
            self._do_heal(u)
        elif a == RaidActionID.CLEANSE:
            u.cooldowns[int(a)] = cd
            self._do_cleanse(u)
        elif a == RaidActionID.BUFF_ATK:
            u.cooldowns[int(a)] = cd
            self._do_buff(u, "atk")
        elif a == RaidActionID.BUFF_SHIELD:
            u.cooldowns[int(a)] = cd
            self._do_buff(u, "shield")
        elif a == RaidActionID.COUNTER:
            # 쿨다운은 _try_counter 내부에서 결정(성공=풀, 각도/거리 miss=절반).
            self._try_counter(u, cd)
        elif a == RaidActionID.DASH:
            u.cooldowns[int(a)] = cd
            self._do_dash(u)

    # ────────────── 액션 효과 ──────────────
    def _do_attack(self, u: PartyUnit, skill: bool):
        if self._boss_dist(u.x, u.y) > u.attack_range:
            self.step_events[u.uid].append({"type": "invalid_action"})
            return
        dmg = u.attack * (2 if skill else 1)
        if u.buff_atk > 0:
            dmg = int(dmg * 1.3)
        # 크리티컬 판정 (로스트아크식 타격감) — 데미지 배수 + 이벤트 crit 플래그
        crit = self.rng.random() < self.config.crit_chance
        if crit:
            dmg = int(round(dmg * self.config.crit_multiplier))
        actual = self.boss.take_damage(dmg, u.uid)
        u.total_damage_dealt += actual
        self.step_events[u.uid].append({"type": "damage", "amount": actual, "skill": skill, "crit": crit})
        if self.boss.stagger_active:
            contrib = (self.config.stagger_contrib_skill if skill else self.config.stagger_contrib_basic)
            self.boss.stagger_gauge -= contrib
            self.step_events[u.uid].append({"type": "stagger_contribute", "amount": contrib})

    def _do_aim_skill(self, u: PartyUnit, skill_key: str,
                      radius: float, max_range: float, damage: int,
                      is_skill: bool = True):
        """딜러 지면 지정 AoE (로아식 설치기). 즉시 발동 — 텔레그래프 없음.

        - 조준점: step() 의 aim_points["p<uid>"] (sim 좌표). 없으면 보스 위치 자동 조준.
        - 사거리 밖 조준점은 사거리 경계로 클램프.
        - AoE 원 안에 보스(몸통 원 겹침) 있으면 피해. 파티원 프렌들리파이어 없음.
        - 이벤트: player_skill_cast (Unity 폭발 VFX + 명중 표시용). skill 필드로 Q/W/평타 구분.
        - is_skill: Q/W(True) vs 평타(False) — damage 이벤트 skill 플래그·스태거 기여량 구분.
        """
        aim = self._aim_points.get(f"p{u.uid}")
        if aim is None:
            tx, ty = self.boss.x, self.boss.y
        else:
            tx, ty = float(aim[0]), float(aim[1])
        # 사거리 클램프
        dx = tx - u.x; dy = ty - u.y
        d = math.hypot(dx, dy)
        if d > max_range:
            tx = u.x + dx / d * max_range
            ty = u.y + dy / d * max_range
        # 맵 경계 클램프
        tx = min(max(tx, 0.0), self.config.map_width)
        ty = min(max(ty, 0.0), self.config.map_height)

        hit = math.hypot(tx - self.boss.x, ty - self.boss.y) <= radius + self.config.boss_radius
        actual = 0
        crit = False
        if hit:
            dmg = damage
            if u.buff_atk > 0:
                dmg = int(dmg * 1.3)
            # 크리티컬 판정 (로스트아크식 타격감) — 데미지 배수 + 이벤트 crit 플래그
            crit = self.rng.random() < self.config.crit_chance
            if crit:
                dmg = int(round(dmg * self.config.crit_multiplier))
            actual = self.boss.take_damage(dmg, u.uid)
            u.total_damage_dealt += actual
            if actual > 0:
                self.step_events[u.uid].append({"type": "damage", "amount": actual, "skill": is_skill, "crit": crit})
            if self.boss.stagger_active:
                contrib = (self.config.stagger_contrib_skill if is_skill
                           else self.config.stagger_contrib_basic)
                self.boss.stagger_gauge -= contrib
                self.step_events[u.uid].append({"type": "stagger_contribute",
                                                "amount": contrib})
        self.step_events[u.uid].append({
            "type": "player_skill_cast", "skill": skill_key,
            "tx": float(tx), "ty": float(ty), "radius": float(radius),
            "hit": bool(hit), "amount": int(actual), "crit": bool(crit),
        })

    def _do_heal(self, u: PartyUnit):
        cands = [x for x in self.units.values()
                 if x.alive and math.hypot(x.x - u.x, x.y - u.y) <= u.attack_range]
        if not cands:
            return
        target = min(cands, key=lambda x: x.hp / max(1, x.max_hp))
        amount = min(target.max_hp - target.hp, 80)
        target.hp += amount
        u.total_heal_done += amount
        self.step_events[u.uid].append({"type": "heal", "target": target.uid, "amount": amount})

    def _do_cleanse(self, u: PartyUnit):
        for x in self.units.values():
            if not x.alive:
                continue
            if math.hypot(x.x - u.x, x.y - u.y) > u.attack_range + 0.5:
                continue
            if x.marked_turns > 0:
                x.marked_turns = 0
                self.step_events[u.uid].append({"type": "cleanse", "target": x.uid})

    def _do_buff(self, u: PartyUnit, kind: str):
        cands = [x for x in self.units.values() if x.alive and x.uid != u.uid]
        if not cands:
            return
        target = min(cands, key=lambda x: math.hypot(x.x - u.x, x.y - u.y))
        if math.hypot(target.x - u.x, target.y - u.y) > u.attack_range + 0.5:
            return
        if kind == "atk":
            target.buff_atk = 3
        else:
            target.buff_shield = 3
        self.step_events[u.uid].append({"type": "buff", "target": target.uid, "kind": kind})

    def _try_counter(self, u: PartyUnit, cd: int):
        """딜러 저지: 카운터 창 중 보스 전방 근접에서 성공.

        각도/거리 조건 밖 시도는 무시하지 않고 counter_miss(reason=angle|range) 이벤트를
        방출하고 쿨다운을 절반만 적용(재시도 여지). 성공 시 풀 쿨다운.
        """
        cfg = self.config
        half_cd = max(1, int(cd * cfg.counter_miss_cooldown_scale))
        if self.boss.counter_window_turns <= 0:
            # 카운터 창이 없을 때의 헛 시도 — 절반 쿨(재시도 여지)
            u.cooldowns[int(RaidActionID.COUNTER)] = half_cd
            self.step_events[u.uid].append({"type": "counter_whiff"})
            return
        # 전방/거리 판정
        ang_to_boss = math.atan2(u.y - self.boss.y, u.x - self.boss.x)
        diff = abs((ang_to_boss - self.boss.facing + math.pi) % (2 * math.pi) - math.pi)
        in_front = diff <= math.radians(cfg.counter_front_angle_deg) * 0.5
        in_range = self._boss_dist(u.x, u.y) <= cfg.counter_range
        if in_front and in_range:
            u.cooldowns[int(RaidActionID.COUNTER)] = cd
            self._counter_success = True
            self.step_events[u.uid].append({"type": "counter_hit"})
        else:
            reason = "range" if not in_range else "angle"
            u.cooldowns[int(RaidActionID.COUNTER)] = half_cd
            self.step_events[u.uid].append({"type": "counter_miss",
                                            "uid": int(u.uid), "reason": reason})

    def _do_dash(self, u: PartyUnit):
        """딜러 회피 기동기 — 조준 방향으로 dash_distance 즉시 이동.

        방향: aim_points 지정 시 그 지점 방향. 없으면 이번 턴 이동 방향, 그것도 없으면
        보스 반대 방향(폴백). 이동 판정은 슬라이딩/탈출 규칙을 재사용 — 경로를 0.5
        서브스텝으로 나눠 장애물(보스/기둥)에 막히면 그 앞까지만 이동(관통 금지).
        Unity 잔상용 dash 이벤트(도착 좌표 tx/ty) 방출.
        """
        cfg = self.config
        aim = self._aim_points.get(f"p{u.uid}")
        if aim is not None:
            dx = float(aim[0]) - u.x
            dy = float(aim[1]) - u.y
        else:
            px, py = self._prev_unit_positions.get(u.uid, (u.x, u.y))
            mdx, mdy = u.x - px, u.y - py
            if abs(mdx) > 1e-6 or abs(mdy) > 1e-6:
                dx, dy = mdx, mdy              # 이번 턴 이동 방향
            else:
                dx, dy = u.x - self.boss.x, u.y - self.boss.y   # 보스 반대 방향
        d = math.hypot(dx, dy)
        if d < 1e-6:
            dx, dy, d = 1.0, 0.0, 1.0
        ux, uy = dx / d, dy / d
        remaining = cfg.dash_distance
        substep = 0.5
        while remaining > 1e-6:
            s = min(substep, remaining)
            nx = min(max(u.x + ux * s, u.radius), cfg.map_width - u.radius)
            ny = min(max(u.y + uy * s, u.radius), cfg.map_height - u.radius)
            if (nx == u.x and ny == u.y) or self._blocked_for_unit(u.uid, nx, ny):
                break
            u.x, u.y = nx, ny
            remaining -= s
        self.step_events[u.uid].append({
            "type": "dash", "uid": int(u.uid),
            "tx": float(u.x), "ty": float(u.y),
        })

    # ────────────── 보스 관련 ──────────────
    def _boss_dist(self, x: float, y: float) -> float:
        return max(0.0, math.hypot(x - self.boss.x, y - self.boss.y) - self.config.boss_radius)

    def _move_boss_toward(self, tx: float, ty: float):
        b = self.boss
        dx = tx - b.x; dy = ty - b.y
        dist = math.hypot(dx, dy)
        if dist < 1e-4:
            return
        step = min(self.config.boss_move_speed, dist)
        nx = b.x + dx / dist * step
        ny = b.y + dy / dist * step
        nx = min(max(nx, self.config.boss_radius), self.config.map_width - self.config.boss_radius)
        ny = min(max(ny, self.config.boss_radius), self.config.map_height - self.config.boss_radius)
        # 기둥 충돌 회피 (통과 금지)
        for p in self.pillars:
            if p.alive and math.hypot(nx - p.x, ny - p.y) < self.config.boss_radius + p.radius - 0.05:
                return
        b.x, b.y = nx, ny
        b.facing = math.atan2(dy, dx)

    # ────────────── 보스 패턴 구동 ──────────────
    def _tick_boss_pattern(self):
        ap = self.boss.active_pattern
        if ap is None:
            return
        if ap.mode == "counter":
            self._tick_counter(ap)
        elif ap.mode == "stagger":
            self._tick_stagger(ap)
        elif ap.mode == "seal":
            self._tick_seal(ap)
        else:  # steps
            self._tick_steps(ap)

    def _tick_steps(self, ap: ActivePattern):
        step = ap.current_step()
        if step is None:
            self.boss.active_pattern = None
            return
        # 폭주 돌진(표식 추격): windup 재베이크 + charge 이동을 전용 틱에서 처리
        if step.kind == "rush_dash":
            self._tick_rush(ap, step)
            return
        # 붉은 낙인: shape 가 대상 위치를 따라감
        if step.kind == "brand":
            tuid = step.extra.get("target_uid")
            mu = self.units.get(tuid)
            if mu and mu.alive:
                ap.world_shapes = [Shape("circle", {"cx": mu.x, "cy": mu.y,
                                                    "r": self.config.pat_brand_radius})]
        ap.turns_remaining -= 1
        if ap.turns_remaining > 0:
            return
        # 임팩트 (rush_dash 는 _tick_rush 에서 별도 처리)
        if step.kind == "brand":
            self._impact_brand(ap, step)
        else:
            self._impact_aoe(ap, step.damage)
        self.boss.advance_step()

    def _impact_aoe(self, ap: ActivePattern, damage: int):
        for u in self.units.values():
            if not u.alive:
                continue
            if ap.contains((u.x, u.y)):
                self._hit_unit_with_guard(u, damage, ap)

    def _hit_unit_with_guard(self, u: PartyUnit, damage: int, ap: ActivePattern):
        """탱커 가드 딜타임: 시퀀스당 1회, 피해 80% 경감 + 보스 2턴 경직."""
        dmg = damage
        if (u.role == PartyRole.TANK and u.buff_guard > 0 and not ap.guard_used):
            dmg = int(damage * (1.0 - self.config.guard_reduction))
            ap.guard_used = True
            self.boss.stun_turns = max(self.boss.stun_turns, self.config.guard_stun_turns)
            for uid in self.units:
                self.step_events[uid].append({"type": "guard_success",
                                              "tank": u.uid,
                                              "stun": self.config.guard_stun_turns})
        self._deal_damage_to_unit(u, dmg)

    # ── 폭주 돌진 "표식 추격 돌진" ──
    def _rush_target_uid(self, ap: ActivePattern) -> Optional[int]:
        return ap.target_uids[0] if ap.target_uids else None

    def _rush_dir_to_target(self, ap: ActivePattern) -> Tuple[float, float]:
        """보스 현재 위치 → 표식 타겟 현재 위치 방향 (타겟 사망 시 마지막 facing 유지)."""
        b = self.boss
        tu = self.units.get(self._rush_target_uid(ap))
        if tu is not None and tu.alive:
            dx, dy = tu.x - b.x, tu.y - b.y
        else:
            dx, dy = math.cos(ap.facing), math.sin(ap.facing)
        d = math.hypot(dx, dy)
        if d < 1e-6:
            return math.cos(ap.facing), math.sin(ap.facing)
        return dx / d, dy / d

    def _rebake_rush_telegraph(self, ap: ActivePattern):
        """windup: 보스에게서 뻗어나오는 조준선을 매 턴 타겟 방향으로 재베이크."""
        cfg = self.config
        b = self.boss
        dirx, diry = self._rush_dir_to_target(ap)
        ap.facing = math.atan2(diry, dirx)
        ap.origin = (b.x, b.y)
        ap.extra["rush_dir"] = (dirx, diry)
        tu = self.units.get(self._rush_target_uid(ap))
        tdist = math.hypot(tu.x - b.x, tu.y - b.y) if (tu and tu.alive) else cfg.pat_rush_length_max
        length = min(cfg.pat_rush_length_max, tdist + cfg.pat_rush_length_bonus)
        hw = cfg.pat_rush_width * 0.5
        ap.world_shapes = [Shape("line", {"ax": b.x, "ay": b.y,
                                          "bx": b.x + dirx * length,
                                          "by": b.y + diry * length, "hw": hw})]

    def _rebake_charge_telegraph(self, ap: ActivePattern):
        """charge: 남은 돌진 경로를 조준선으로 표시(FSM 회피/센서 호환)."""
        cfg = self.config
        b = self.boss
        dirx, diry = ap.extra.get("rush_dir", (math.cos(ap.facing), math.sin(ap.facing)))
        length = max(0.0, ap.extra.get("rush_charge_left", 0)) * cfg.pat_rush_charge_speed
        hw = cfg.pat_rush_width * 0.5
        ap.origin = (b.x, b.y)
        ap.world_shapes = [Shape("line", {"ax": b.x, "ay": b.y,
                                          "bx": b.x + dirx * length,
                                          "by": b.y + diry * length, "hw": hw})]

    def _tick_rush(self, ap: ActivePattern, step: PatternStep):
        phase = ap.extra.get("rush_phase", "windup")
        if phase == "windup":
            # 매 턴 조준선 재베이크 (보스 현재 위치 → 타겟 현재 위치)
            self._rebake_rush_telegraph(ap)
            ap.turns_remaining -= 1
            if ap.turns_remaining > 0:
                return
            # windup 종료 → charge 진입 (즉시 텔레포트 없음). fire 시점 방향 락.
            self._rebake_rush_telegraph(ap)
            ap.extra["rush_phase"] = "charge"
            ap.extra["rush_charge_left"] = self.config.pat_rush_charge_turns
            ap.extra["rush_hit_uids"] = set()
            self._rebake_charge_telegraph(ap)
            return
        self._rush_charge_tick(ap, step)

    def _rush_charge_tick(self, ap: ActivePattern, step: PatternStep):
        """돌진 1턴: 보스가 fire 방향으로 charge_speed 만큼(0.5 서브스텝) 전진.
        기둥 충돌 시 정지 + 파괴 + 그로기 + rush_pillar_hit, 벽 도달 시 정지.
        경로폭 내 유닛은 시퀀스당 1회 피해(기존 데미지/가드 유지)."""
        cfg = self.config
        b = self.boss
        dirx, diry = ap.extra.get("rush_dir", (math.cos(ap.facing), math.sin(ap.facing)))
        b.facing = math.atan2(diry, dirx)
        R = cfg.boss_radius
        start = (b.x, b.y)
        remaining = cfg.pat_rush_charge_speed
        substep = 0.5
        hit_pillar = None
        wall = False
        while remaining > 1e-6:
            s = min(substep, remaining)
            nx = b.x + dirx * s
            ny = b.y + diry * s
            if not (R <= nx <= cfg.map_width - R and R <= ny <= cfg.map_height - R):
                wall = True
                break
            for p in self.pillars:
                if p.alive and math.hypot(nx - p.x, ny - p.y) < R + p.radius - 0.05:
                    hit_pillar = p
                    break
            if hit_pillar is not None:
                break
            b.x, b.y = nx, ny
            remaining -= s
        end = (b.x, b.y)

        # 경로폭 내 유닛 피해 (이번 턴 스윕 구간, 시퀀스당 1회)
        hit_set = ap.extra.setdefault("rush_hit_uids", set())
        hw = cfg.pat_rush_width * 0.5
        for u in self.units.values():
            if not u.alive or u.uid in hit_set:
                continue
            if _point_in_line_segment((u.x, u.y), start, end, hw + u.radius):
                hit_set.add(u.uid)
                self._hit_unit_with_guard(u, step.damage, ap)

        if hit_pillar is not None:
            hit_pillar.alive = False
            hit_pillar.respawn_timer = cfg.pillar_respawn_turns
            b.grog_turns = max(b.grog_turns, cfg.rush_pillar_grog_turns)
            for uid in self.units:
                self.step_events[uid].append({"type": "rush_pillar_hit",
                                              "grog": cfg.rush_pillar_grog_turns})
            self.boss.active_pattern = None
            return
        if wall:
            self.boss.active_pattern = None
            return
        ap.extra["rush_charge_left"] = ap.extra.get("rush_charge_left", 0) - 1
        if ap.extra["rush_charge_left"] <= 0:
            self.boss.active_pattern = None
        else:
            self._rebake_charge_telegraph(ap)

    def _impact_brand(self, ap: ActivePattern, step: PatternStep):
        tuid = step.extra.get("target_uid")
        mu = self.units.get(tuid)
        if not mu or not mu.alive:
            self.boss.active_pattern = None if False else self.boss.active_pattern
            return
        others = [x for x in self.units.values() if x.uid != mu.uid and x.alive]
        min_d = min((math.hypot(x.x - mu.x, x.y - mu.y) for x in others), default=99.0)
        if min_d >= self.config.pat_brand_escape_distance:
            # 산개 성공 — 대상만 경미 피해
            self._deal_damage_to_unit(mu, max(1, step.damage // 4))
            self.boss.grog_turns = max(self.boss.grog_turns, self.config.brand_success_grog_turns)
            for uid in self.units:
                self.step_events[uid].append({"type": "mechanic_success",
                                              "pattern": int(PatternID.CRIMSON_BRAND)})
        else:
            for u in self.units.values():
                if u.alive and math.hypot(u.x - mu.x, u.y - mu.y) <= self.config.pat_brand_radius:
                    self._deal_damage_to_unit(u, step.damage)
            for uid in self.units:
                self.step_events[uid].append({"type": "mechanic_fail",
                                              "pattern": int(PatternID.CRIMSON_BRAND)})

    # ── 카운터 창 ──
    def _tick_counter(self, ap: ActivePattern):
        if self._counter_success:
            self.boss.grog_turns = max(self.boss.grog_turns, 3)
            self.boss.counter_window_turns = 0
            for uid in self.units:
                self.step_events[uid].append({"type": "counter_success"})
            self.boss.active_pattern = None
            return
        ap.turns_remaining -= 1
        self.boss.counter_window_turns = ap.turns_remaining
        if ap.turns_remaining <= 0:
            self.boss.counter_window_turns = 0
            for uid in self.units:
                self.step_events[uid].append({"type": "counter_fail"})
            self.boss.active_pattern = None
            # 즉시 강화 돌진
            self.boss.start_step_pattern(PatternID.FRENZY_RUSH, self._build_ctx())
            eap = self.boss.active_pattern
            scale = self.config.counter_fail_damage_scale
            for s in eap.steps:
                s.damage = int(s.damage * scale)
                s.extra["enhanced"] = True

    # ── 무력화 (스태거) ──
    def _tick_stagger(self, ap: ActivePattern):
        if self.boss.stagger_gauge <= 0:
            self.boss.grog_turns = max(self.boss.grog_turns, self.config.stagger_success_grog_turns)
            self.boss.stagger_active = False
            self.boss.active_pattern = None
            for uid in self.units:
                self.step_events[uid].append({"type": "stagger_success"})
            return
        ap.turns_remaining -= 1
        if ap.turns_remaining <= 0:
            for u in self.units.values():
                if u.alive:
                    self._deal_damage_to_unit(u, self.config.stagger_fail_damage)
            for uid in self.units:
                self.step_events[uid].append({"type": "stagger_fail"})
            self.boss.stagger_active = False
            self.boss.active_pattern = None

    # ── 전멸기 '혈월 강림' (LOS 은신) ──
    def _unit_hidden(self, u: PartyUnit) -> bool:
        """유닛-보스 선분이 살아있는 기둥과 교차하면 은신."""
        for p in self.pillars:
            if not p.alive:
                continue
            if segment_intersects_circle((u.x, u.y), (self.boss.x, self.boss.y),
                                         (p.x, p.y), p.radius):
                return True
        return False

    def _tick_seal(self, ap: ActivePattern):
        hidden_flags = {u.uid: (u.alive and self._unit_hidden(u)) for u in self.units.values()}
        # 판정은 "생존자 전원 은신" — 이미 죽은 파티원이 기믹을 자동 실패시키지 않게.
        all_hidden = all(hidden_flags[u.uid] for u in self.units.values() if u.alive)
        for uid in self.units:
            self.step_events[uid].append({"type": "seal_holding",
                                          "hidden": bool(hidden_flags.get(uid, False)),
                                          "turns_left": ap.turns_remaining})
        ap.turns_remaining -= 1
        if ap.turns_remaining <= 0:
            if all_hidden:
                self.boss.grog_turns = max(self.boss.grog_turns, self.config.seal_grog_turns)
                for uid in self.units:
                    self.step_events[uid].append({"type": "seal_success"})
                    self.step_events[uid].append({"type": "cinematic_end", "success": True})
            else:
                for u in self.units.values():
                    if u.alive:
                        u.hp = 0
                        u.alive = False
                        self.step_events[u.uid].append({"type": "death"})
                    self.step_events[u.uid].append({"type": "seal_fail"})
                    self.step_events[u.uid].append({"type": "cinematic_end", "success": False})
            self.boss.active_pattern = None

    def _deal_damage_to_unit(self, u: PartyUnit, amount: int):
        actual = max(1, amount - u.defense)
        if u.buff_shield > 0:
            actual = int(actual * 0.7)
        u.hp -= actual
        u.total_damage_taken += actual
        if u.hp <= 0:
            u.hp = 0
            u.alive = False
            self.step_events[u.uid].append({"type": "death"})
        else:
            self.step_events[u.uid].append({"type": "damage_taken", "amount": actual})

    # ────────────── 테스트/디버그 강제 발동 ──────────────
    def force_pattern(self, pid: PatternID):
        """스모크 테스트용: 지정 패턴 강제 시전 (쿨다운/페이즈 무시)."""
        self.boss.active_pattern = None
        self.boss.grog_turns = 0
        self.boss.stun_turns = 0
        self.boss.invuln_turns = 0
        self.step_events = {uid: [] for uid in self.units}
        self._start_pattern(pid)

    def _respawn_all_pillars(self):
        """전멸기(LOS 은신) 시작 직전 호출 — 돌진에 파괴된 기둥을 전부 즉시 재생성.
        숨을 기둥이 없어 파훼가 원천적으로 불가능한 상황을 차단한다."""
        for p in self.pillars:
            if not p.alive:
                p.alive = True
            p.respawn_timer = 0

    def force_seal(self):
        self.boss.active_pattern = None
        self.step_events = {uid: [] for uid in self.units}
        # 숨을 기둥이 없어 파훼 불가능한 상황 원천 차단 — 전멸기 시작 전 기둥 전부 재생성.
        self._respawn_all_pillars()
        self.boss.start_seal(self._build_ctx())
        for uid in self.units:
            self.step_events.setdefault(uid, []).append({
                "type": "cinematic_start", "pattern": int(PatternID.SEAL_WIPE),
                "duration_turns": self.config.seal_wind_up_turns})

    # ────────────── 관찰 ──────────────
    def _get_all_observations(self) -> Dict[str, np.ndarray]:
        return {aid: self._observe(self.uid_of(aid)) for aid in self.agent_ids()}

    def _skill_cd_norm(self, u: PartyUnit, action_id: int) -> float:
        cd_max = float(max(1, self.config.skill_cooldowns.get(action_id, self.config.skill_cooldown)))
        return u.cooldowns.get(action_id, 0) / cd_max

    def _observe(self, uid: int) -> np.ndarray:
        u = self.units[uid]
        cfg = self.config
        b = self.boss
        v: List[float] = []
        top_uid = b.top_aggro_uid()
        world_shapes = [s for tg in b.telegraphs for s in tg.world_shapes]

        # [1] Self (16)
        v += [u.hp / max(1, u.max_hp), u.mp / max(1, u.max_mp),
              u.x / cfg.map_width, u.y / cfg.map_height]
        role_oh = [0.0] * 4; role_oh[int(u.role)] = 1.0
        v += role_oh
        v.append(u.radius)
        v.append(self._skill_cd_norm(u, int(RaidActionID.ATTACK_SKILL)))
        sa, sb = ROLE_SKILLS[u.role]
        v.append(self._skill_cd_norm(u, sa))
        v.append(self._skill_cd_norm(u, sb))
        v.append(self.current_step / max(1, cfg.max_steps))
        total_aggro = sum(b.aggro.values()) + 1e-6
        v.append(b.aggro.get(uid, 0.0) / total_aggro)
        v.append(1.0 if top_uid == uid else 0.0)
        v.append(1.0 if u.buff_guard > 0 else 0.0)

        # [2] Allies (24)
        allies = sorted((a for a in self.units.values() if a.uid != uid), key=lambda a: a.uid)
        for i in range(3):
            if i < len(allies):
                a = allies[i]
                v += [(a.x - u.x) / 10.0, (a.y - u.y) / 10.0,
                      a.hp / max(1, a.max_hp), 1.0 if a.alive else 0.0]
                a_oh = [0.0] * 4; a_oh[int(a.role)] = 1.0
                v += a_oh
            else:
                v += [0.0] * 8

        # [3] Boss (12)
        bdx = b.x - u.x; bdy = b.y - u.y
        bdist = math.hypot(bdx, bdy)
        v += [bdx / 10.0, bdy / 10.0, bdist / 10.0, b.hp / cfg.boss_max_hp]
        ph_oh = [0.0] * 3; ph_oh[int(b.phase)] = 1.0
        v += ph_oh
        v += [1.0 if b.grog_turns > 0 or b.stun_turns > 0 else 0.0,
              1.0 if b.invuln_turns > 0 else 0.0,
              math.sin(b.facing), math.cos(b.facing),
              1.0 if b.counter_window_turns > 0 else 0.0]

        # [4] Pattern channels (10 x 4 = 40)
        ap = b.active_pattern
        for pid_int in range(10):
            active = 1.0 if (ap is not None and int(ap.pattern_id) == pid_int) else 0.0
            if active:
                turns_norm = ap.turns_remaining / max(1, ap.total_this_step)
                am_I = 1.0 if uid in ap.target_uids else 0.0
                in_danger = 1.0 if ap.contains((u.x, u.y)) else 0.0
                v += [1.0, turns_norm, am_I, in_danger]
            else:
                v += [0.0, 0.0, 0.0, 0.0]

        # [5] Danger sensor (8)
        v += sample_danger_sensor((u.x, u.y), world_shapes)

        # [6] Escape (4)
        in_d = 0.0; edx = 0.0; edy = 0.0; urg = 0.0
        danger_here = [s for s in world_shapes if s.contains((u.x, u.y))]
        if danger_here and ap is not None:
            in_d = 1.0
            urg = 1.0 - ap.turns_remaining / max(1, ap.total_this_step)
            best = 1e9
            for di in range(8):
                theta = di * math.pi / 4
                for sm in (0.5, 1.0, 1.5, 2.0, 3.0):
                    tx = u.x + math.cos(theta) * sm
                    ty = u.y + math.sin(theta) * sm
                    if not any(s.contains((tx, ty)) for s in world_shapes):
                        if sm < best:
                            best = sm; edx = math.cos(theta); edy = math.sin(theta)
                        break
        v += [in_d, edx, edy, urg]

        # [7] Coop (8): stagger(2) + seal_los(4) + counter(2)
        v += [1.0 if b.stagger_active else 0.0,
              b.stagger_gauge / max(1.0, cfg.stagger_gauge)]
        seal_active = 1.0 if (ap is not None and ap.mode == "seal") else 0.0
        hidden = 1.0 if (seal_active and self._unit_hidden(u)) else 0.0
        v += [seal_active, bdx / 10.0, bdy / 10.0, hidden]
        # counter alignment (딜러가 전방 정렬돼 있으면 1 근처)
        align = 0.0
        if b.counter_window_turns > 0:
            ang = math.atan2(u.y - b.y, u.x - b.x)
            diff = abs((ang - b.facing + math.pi) % (2 * math.pi) - math.pi)
            align = max(0.0, 1.0 - diff / math.pi)
        v += [1.0 if b.counter_window_turns > 0 else 0.0, align]

        # [8] Pillars (12): 4 x (dx/10, dy/10, alive)
        for i in range(4):
            if i < len(self.pillars):
                p = self.pillars[i]
                v += [(p.x - u.x) / 10.0, (p.y - u.y) / 10.0, 1.0 if p.alive else 0.0]
            else:
                v += [0.0, 0.0, 0.0]

        # [9] Player (4)
        player = self.units[cfg.player_slot]
        pdx = player.x - u.x; pdy = player.y - u.y
        v += [pdx / 10.0, pdy / 10.0, math.hypot(pdx, pdy) / 10.0,
              player.hp / max(1, player.max_hp)]

        arr = np.array(v, dtype=np.float32)
        exp = cfg.obs_size
        if arr.shape[0] < exp:
            arr = np.pad(arr, (0, exp - arr.shape[0]))
        elif arr.shape[0] > exp:
            arr = arr[:exp]
        return arr

    # ────────────── info ──────────────
    def _build_infos(self) -> Dict[str, dict]:
        return {
            aid: {
                "events": self.step_events.get(self.uid_of(aid), []),
                "victory": self.victory, "wipe": self.wipe,
                "step": self.current_step,
                "boss_hp_ratio": self.boss.hp / self.config.boss_max_hp,
                "phase": int(self.boss.phase),
            }
            for aid in self.agent_ids()
        }

    # ────────────── 스냅샷 (Unity 송신용) ──────────────
    def _unit_cooldowns(self, u: PartyUnit) -> Dict[str, int]:
        """스킬바 키 이름 → 남은 쿨다운 (Unity 스킬바 UI 용).
        딜러: {"skill": Q, "skill2": W, "counter": E}."""
        out: Dict[str, int] = {}
        for aid in SKILL_BAR[u.role]:
            out[SKILL_KEYS[aid]] = int(u.cooldowns.get(aid, 0))
        return out

    def get_snapshot(self) -> dict:
        b = self.boss
        ap = b.active_pattern
        # 폭주 돌진 "표식 추격" 필드 — 비활성 시 rush_target=-1.
        rush_ap = ap if (ap is not None and ap.mode == "steps"
                         and int(ap.pattern_id) == int(PatternID.FRENZY_RUSH)) else None
        if rush_ap is not None and rush_ap.target_uids:
            rush_target = int(rush_ap.target_uids[0])
            rush_left = int(rush_ap.extra.get("rush_charge_left", rush_ap.turns_remaining))
        else:
            rush_target, rush_left = -1, 0
        telegraphs = []
        for tg in b.telegraphs:  # steps 모드만
            telegraphs.append({
                "pattern": int(tg.pattern_id),
                "step_index": int(tg.step_index),
                "num_steps": int(tg.num_steps),
                "anim": tg.anim,
                "turns_remaining": int(tg.turns_remaining),
                "total_wind_up": int(tg.total_wind_up),
                "shapes": [s.to_dict() for s in tg.world_shapes],
                "target_uids": [int(x) for x in tg.target_uids],
            })
        return {
            "step": self.current_step,
            "boss": {
                "x": float(b.x), "y": float(b.y), "facing": float(b.facing),
                "hp": int(b.hp), "max_hp": int(self.config.boss_max_hp),
                "phase": int(b.phase),
                "invuln": int(b.invuln_turns), "grog": int(b.grog_turns),
                "stun": int(b.stun_turns),
                "stagger_active": bool(b.stagger_active),
                "stagger_gauge": float(b.stagger_gauge),
                "counter_window": int(b.counter_window_turns),
                "radius": float(self.config.boss_radius),
                "vx": float(b.x - self._prev_boss_pos[0]),
                "vy": float(b.y - self._prev_boss_pos[1]),
                "active_pattern": int(ap.pattern_id) if ap is not None else -1,
                "active_mode": ap.mode if ap is not None else "",
                "rush_target": rush_target,
                "rush_left": rush_left,
            },
            "units": [
                {
                    "uid": int(u.uid), "role": int(u.role),
                    "x": float(u.x), "y": float(u.y),
                    "hp": int(u.hp), "max_hp": int(u.max_hp),
                    "alive": bool(u.alive),
                    "marked": bool(u.marked_turns > 0),
                    "buff_atk": int(u.buff_atk), "buff_shield": int(u.buff_shield),
                    "buff_guard": int(u.buff_guard),
                    "radius": float(u.radius),
                    "cooldowns": self._unit_cooldowns(u),
                    "vx": float(u.x - self._prev_unit_positions.get(u.uid, (u.x, u.y))[0]),
                    "vy": float(u.y - self._prev_unit_positions.get(u.uid, (u.x, u.y))[1]),
                }
                for u in self.units.values()
            ],
            "pillars": [
                {"x": float(p.x), "y": float(p.y), "radius": float(p.radius),
                 "alive": bool(p.alive), "respawn_in": int(p.respawn_timer)}
                for p in self.pillars
            ],
            "telegraphs": telegraphs,
            "events": [
                {"uid": uid, **event}
                for uid, event_list in self.step_events.items()
                for event in event_list
            ],
            "done": bool(self.done),
            "victory": bool(self.victory),
            "wipe": bool(self.wipe),
        }
