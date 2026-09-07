"""비교군 FSM NPC — 탱커 / 힐러 / 서포터 / 딜러(관전용).

사용자 기술 역할 흐름 그대로:
  힐러 : 아군 HP 60% 미만 → 최저 HP 힐 우선. 위험 장판 안이면 탈출 우선. 그 외 딜러 근처 안전 위치.
  탱커 : 위험 회피 > 전멸기/기믹 수행 > 어그로(taunt) > 피격 임박 GUARD(딜타임) > 보스 근접.
  서포터: 위험 회피 > 기믹 > 버프/실드 > 중거리.
  딜러 : 카운터 창이면 counter, 아니면 회피 + 백어택 포지션 딜.

동일 RaidEnv.step() API 사용.
"""
from __future__ import annotations
import math
from typing import Optional, TYPE_CHECKING

from .config import RaidActionID, PartyRole, PatternID

if TYPE_CHECKING:
    from .env import RaidEnv


def _euclid(ax, ay, bx, by) -> float:
    return math.hypot(ax - bx, ay - by)


_DIRS = [
    (RaidActionID.MOVE_RIGHT, 1, 0), (RaidActionID.MOVE_DOWN_RIGHT, 1, 1),
    (RaidActionID.MOVE_DOWN, 0, 1), (RaidActionID.MOVE_DOWN_LEFT, -1, 1),
    (RaidActionID.MOVE_LEFT, -1, 0), (RaidActionID.MOVE_UP_LEFT, -1, -1),
    (RaidActionID.MOVE_UP, 0, -1), (RaidActionID.MOVE_UP_RIGHT, 1, -1),
]


class FSMNpcPolicy:
    def __init__(self, env: "RaidEnv", uid: int):
        self.env = env
        self.uid = uid

    def act(self) -> int:
        env = self.env
        u = env.units[self.uid]
        if not u.alive:
            return int(RaidActionID.STAY)
        b = env.boss
        ap = b.active_pattern

        # 1) 전멸기 '혈월 강림': 기둥 뒤로 은신 (최우선)
        if ap is not None and ap.mode == "seal":
            return self._seal_hide(u)

        # 2) 위험 회피 (탱커 가드 딜타임은 예외 처리)
        if self._in_danger(u):
            if u.role == PartyRole.TANK:
                g = self._maybe_guard(u)
                if g is not None:
                    return g
            mv = self._safe_move(u)
            if mv is not None:
                return mv

        # 3) 카운터 창 (딜러)
        if b.counter_window_turns > 0 and u.role == PartyRole.DEALER:
            return self._counter_action(u)

        # 4) 무력화(스태거): 전원 딜 집중
        if b.stagger_active:
            if u.role == PartyRole.TANK and u.cooldowns.get(int(RaidActionID.TAUNT), 0) <= 0:
                return int(RaidActionID.TAUNT)
            return self._attack_or_approach(u, prefer_skill=True)

        # 5) 붉은 낙인 산개
        brand = self._brand_action(u)
        if brand is not None:
            return brand

        # 6) 역할별
        if u.role == PartyRole.TANK:
            return self._tank(u)
        if u.role == PartyRole.HEALER:
            return self._healer(u)
        if u.role == PartyRole.SUPPORT:
            return self._support(u)
        return self._dealer(u)

    # ── 유틸 ──
    def _world_shapes(self):
        return [s for tg in self.env.boss.telegraphs for s in tg.world_shapes]

    def _in_danger(self, u) -> bool:
        return any(s.contains((u.x, u.y)) for s in self._world_shapes())

    def _safe_move(self, u) -> Optional[int]:
        env = self.env
        shapes = self._world_shapes()
        best = None; best_clear = float("inf")
        for act, dx, dy in _DIRS:
            spd = u.move_speed * (0.7071 if dx and dy else 1.0)
            nx = u.x + dx * spd; ny = u.y + dy * spd
            if not env._in_arena(nx, ny, u.radius):
                continue
            if env._blocked_for_unit(u.uid, nx, ny):
                continue
            if not any(s.contains((nx, ny)) for s in shapes):
                # 한 걸음 완전 탈출 — 즉시 채택
                return int(act)
            # 부분 탈출 폴백: 대형 장판(예: 패링 원 r=3.0 > 이동 1.0/턴) 중심부에선
            # 한 걸음에 못 벗어난다. 이 방향으로 계속 갈 때 위험을 벗어나기까지
            # 남는 거리가 최소인 방향으로 전진해 다음 턴 탈출 확률을 높인다.
            clear = self._escape_distance((nx, ny), (dx, dy), shapes)
            if clear < best_clear:
                best_clear = clear; best = int(act)
        return best

    @staticmethod
    def _escape_distance(pos, direction, shapes,
                         step: float = 0.4, max_d: float = 8.0) -> float:
        dx, dy = direction
        norm = math.hypot(dx, dy)
        if norm < 1e-8:
            return max_d
        dx /= norm; dy /= norm
        t = 0.0
        while t < max_d:
            probe = (pos[0] + dx * t, pos[1] + dy * t)
            if not any(s.contains(probe) for s in shapes):
                return t
            t += step
        return max_d

    def _maybe_guard(self, u) -> Optional[int]:
        """피격 임박 스텝에 장판 안이면 GUARD (딜타임 유발)."""
        ap = self.env.boss.active_pattern
        if ap is None or ap.mode != "steps":
            return None
        if ap.turns_remaining <= 1 and ap.contains((u.x, u.y)) \
                and not ap.guard_used \
                and u.cooldowns.get(int(RaidActionID.GUARD), 0) <= 0:
            return int(RaidActionID.GUARD)
        return None

    def _seal_hide(self, u) -> Optional[int]:
        """웨이브제 전멸기 대응: 현재 '빛나는 석상'의 안전 원으로 진입 후 대기.
        각자 자기 방향에서 석상 둘레 링으로 접근 → 한 지점에 몰려 서로 밀치는 것 방지."""
        env = self.env
        ap = env.boss.active_pattern
        lit = env._seal_lit_pillar(ap) if (ap is not None and ap.mode == "seal") else None
        if lit is None:
            return int(RaidActionID.STAY)
        p = lit
        safe_r = p.radius + env.config.seal_safe_extra_r
        d = _euclid(u.x, u.y, p.x, p.y)
        # 이미 안전 원 안(경계 여유 0.2 안쪽)이면 대기
        if d <= safe_r - 0.2:
            return int(RaidActionID.STAY)
        ang = math.atan2(u.y - p.y, u.x - p.x) if d > 1e-6 else 0.0
        ring = p.radius + u.radius + 0.35     # 석상 몸통에 밀착한 표준 링(안전 원 깊숙이)
        tx = p.x + math.cos(ang) * ring
        ty = p.y + math.sin(ang) * ring
        return self._move_toward_avoiding(u, tx, ty)

    def _counter_action(self, u) -> int:
        env = self.env
        b = env.boss
        # 보스 전방 근접에 있으면 COUNTER, 아니면 전방으로 이동
        ang = math.atan2(u.y - b.y, u.x - b.x)
        diff = abs((ang - b.facing + math.pi) % (2 * math.pi) - math.pi)
        in_front = diff <= math.radians(env.config.counter_front_angle_deg) * 0.5
        in_range = env._boss_dist(u.x, u.y) <= env.config.counter_range
        if in_front and in_range and u.cooldowns.get(int(RaidActionID.COUNTER), 0) <= 0:
            return int(RaidActionID.COUNTER)
        # 보스 전방 지점으로 이동
        fx = b.x + math.cos(b.facing) * (b.config.boss_radius + 1.0)
        fy = b.y + math.sin(b.facing) * (b.config.boss_radius + 1.0)
        return self._move_toward(u, fx, fy)

    def _brand_action(self, u) -> Optional[int]:
        ap = self.env.boss.active_pattern
        if ap is None or ap.mode != "steps" or ap.pattern_id != PatternID.CRIMSON_BRAND:
            return None
        step = ap.current_step()
        if step is None:
            return None
        mark_uid = step.extra.get("target_uid")
        env = self.env
        others = [x for x in env.units.values() if x.alive]
        if mark_uid == u.uid:
            rest = [x for x in others if x.uid != u.uid]
            if rest:
                ax = sum(x.x for x in rest) / len(rest)
                ay = sum(x.y for x in rest) / len(rest)
                return self._move_away(u, ax, ay)
        else:
            mu = env.units.get(mark_uid)
            if mu and _euclid(u.x, u.y, mu.x, mu.y) < env.config.pat_brand_escape_distance + 1.0:
                return self._move_away(u, mu.x, mu.y)
        return None

    def _attack_or_approach(self, u, prefer_skill=False) -> int:
        env = self.env
        if env._boss_dist(u.x, u.y) <= u.attack_range:
            if prefer_skill and u.cooldowns.get(int(RaidActionID.ATTACK_SKILL), 0) <= 0:
                return int(RaidActionID.ATTACK_SKILL)
            return int(RaidActionID.ATTACK_BASIC)
        return self._move_toward(u, env.boss.x, env.boss.y)

    def _move_toward(self, u, tx, ty) -> int:
        return self._dir_action(tx - u.x, ty - u.y)

    def _move_toward_avoiding(self, u, tx, ty) -> int:
        """직진 방향이 비차단이면 그대로, 장애물(보스/기둥)에 막히면 8방향 중 목표에
        가장 가까워지는 비차단 방향으로 우회. (보스가 진행 방향에 딱 붙어 유닛을 벽으로
        미는 상황 — 전멸기 은신 합류 등 — 에서 제자리걸음 방지.)"""
        env = self.env
        direct = self._move_toward(u, tx, ty)

        def _cell(act):
            for a, dx, dy in _DIRS:
                if int(a) == int(act):
                    spd = u.move_speed * (0.7071 if dx and dy else 1.0)
                    return u.x + dx * spd, u.y + dy * spd
            return u.x, u.y

        nx, ny = _cell(direct)
        if env._in_arena(nx, ny, u.radius) and not env._blocked_for_unit(u.uid, nx, ny):
            return direct
        # 직진 차단 → 목표에 가장 가까워지는 비차단 우회 방향
        best = None; bestd = math.hypot(u.x - tx, u.y - ty)
        for act, dx, dy in _DIRS:
            spd = u.move_speed * (0.7071 if dx and dy else 1.0)
            cx = u.x + dx * spd; cy = u.y + dy * spd
            if not env._in_arena(cx, cy, u.radius):
                continue
            if env._blocked_for_unit(u.uid, cx, cy):
                continue
            d = math.hypot(cx - tx, cy - ty)
            if d < bestd:
                bestd = d; best = act
        return int(best) if best is not None else direct

    def _move_away(self, u, tx, ty) -> int:
        return self._dir_action(u.x - tx, u.y - ty)

    def _dir_action(self, dx, dy) -> int:
        if abs(dx) < 1e-6 and abs(dy) < 1e-6:
            return int(RaidActionID.STAY)
        ang = math.atan2(dy, dx)
        idx = int(round(ang / (math.pi / 4))) % 8
        return int(_DIRS[idx][0])

    # ── 역할 ──
    def _tank(self, u) -> int:
        env = self.env
        b = env.boss
        if b.top_aggro_uid() != u.uid and u.cooldowns.get(int(RaidActionID.TAUNT), 0) <= 0:
            return int(RaidActionID.TAUNT)
        if env._boss_dist(u.x, u.y) > u.attack_range:
            return self._move_toward(u, b.x, b.y)
        return int(RaidActionID.ATTACK_BASIC)

    def _healer(self, u) -> int:
        env = self.env
        for x in env.units.values():
            if not x.alive:
                continue
            if (x.hp / max(1, x.max_hp)) < 0.6:
                if _euclid(x.x, x.y, u.x, u.y) <= u.attack_range:
                    if u.cooldowns.get(int(RaidActionID.HEAL), 0) <= 0:
                        return int(RaidActionID.HEAL)
                else:
                    return self._move_toward(u, x.x, x.y)
        for x in env.units.values():
            if x.marked_turns > 0 and _euclid(x.x, x.y, u.x, u.y) <= u.attack_range + 0.5:
                if u.cooldowns.get(int(RaidActionID.CLEANSE), 0) <= 0:
                    return int(RaidActionID.CLEANSE)
        dealer = env.units[env.config.player_slot]
        if dealer.alive and _euclid(u.x, u.y, dealer.x, dealer.y) > 4.0:
            return self._move_toward(u, dealer.x, dealer.y)
        if env._boss_dist(u.x, u.y) <= u.attack_range:
            return int(RaidActionID.ATTACK_BASIC)
        return int(RaidActionID.STAY)

    def _support(self, u) -> int:
        env = self.env
        b = env.boss
        tank = next((x for x in env.units.values() if x.role == PartyRole.TANK and x.alive), None)
        if tank and (tank.hp / max(1, tank.max_hp)) < 0.6:
            if _euclid(tank.x, tank.y, u.x, u.y) <= u.attack_range + 0.5:
                if u.cooldowns.get(int(RaidActionID.BUFF_SHIELD), 0) <= 0:
                    return int(RaidActionID.BUFF_SHIELD)
            else:
                return self._move_toward(u, tank.x, tank.y)
        if b.grog_turns > 0:
            dealer = env.units[env.config.player_slot]
            if dealer.alive and _euclid(dealer.x, dealer.y, u.x, u.y) <= u.attack_range + 0.5:
                if u.cooldowns.get(int(RaidActionID.BUFF_ATK), 0) <= 0:
                    return int(RaidActionID.BUFF_ATK)
        if env._boss_dist(u.x, u.y) <= u.attack_range:
            if u.cooldowns.get(int(RaidActionID.ATTACK_SKILL), 0) <= 0:
                return int(RaidActionID.ATTACK_SKILL)
            return int(RaidActionID.ATTACK_BASIC)
        return self._move_toward(u, b.x, b.y)

    def _dealer(self, u) -> int:
        env = self.env
        b = env.boss
        boss_center_d = math.hypot(u.x - b.x, u.y - b.y)
        # 조준 설치기 — env 는 aim 미지정 시 보스 위치 자동 조준 (관전 모드)
        if boss_center_d <= env.config.aim_w_range \
                and u.cooldowns.get(int(RaidActionID.SKILL_2), 0) <= 0:
            return int(RaidActionID.SKILL_2)       # W 혈월 낙하
        if boss_center_d <= env.config.aim_q_range \
                and u.cooldowns.get(int(RaidActionID.ATTACK_SKILL), 0) <= 0:
            return int(RaidActionID.ATTACK_SKILL)  # Q 혈창 투척
        # 백어택 포지션 (보스 후방) — 기본공격 사거리로 접근
        if env._boss_dist(u.x, u.y) > u.attack_range:
            back_x = b.x - math.cos(b.facing) * (b.config.boss_radius + 0.8)
            back_y = b.y - math.sin(b.facing) * (b.config.boss_radius + 0.8)
            return self._move_toward(u, back_x, back_y)
        return int(RaidActionID.ATTACK_BASIC)
