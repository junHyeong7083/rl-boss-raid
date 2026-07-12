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
        best = None; best_clear = -1.0
        for act, dx, dy in _DIRS:
            spd = u.move_speed * (0.7071 if dx and dy else 1.0)
            nx = u.x + dx * spd; ny = u.y + dy * spd
            if not (u.radius <= nx <= env.config.map_width - u.radius and
                    u.radius <= ny <= env.config.map_height - u.radius):
                continue
            if env._blocked_for_unit(u.uid, nx, ny):
                continue
            if any(s.contains((nx, ny)) for s in shapes):
                continue
            # 여유 확보: 근접 위험 최소인 방향
            return int(act)
        return best

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
        env = self.env
        b = env.boss
        if env._unit_hidden(u):
            return int(RaidActionID.STAY)
        # 가장 가까운 살아있는 기둥의 보스 반대편으로 이동
        pillars = [p for p in env.pillars if p.alive]
        if not pillars:
            return int(RaidActionID.STAY)
        p = min(pillars, key=lambda p: _euclid(u.x, u.y, p.x, p.y))
        dirx = p.x - b.x; diry = p.y - b.y
        d = math.hypot(dirx, diry) or 1.0
        tx = p.x + dirx / d * (p.radius + 0.5)
        ty = p.y + diry / d * (p.radius + 0.5)

        # 기둥에 이미 붙어 있으면(직선 접근이 기둥 몸통에 막힘) 둘레를 따라 접선 우회:
        # 현재 각도에서 목표 각도(보스 반대편) 쪽으로 호를 그리며 돈다. 반경은 충돌
        # 한계(u.radius + p.radius)보다 여유 있게 잡아 슬라이딩 없이도 진행 가능.
        dist_p = _euclid(u.x, u.y, p.x, p.y)
        # 우회 발동 반경: 한 스텝(1.0) 전진이 기둥 충돌권에 걸릴 수 있는 거리까지 넉넉히.
        if dist_p <= p.radius + u.radius + 1.15:
            ang_u = math.atan2(u.y - p.y, u.x - p.x)
            ang_t = math.atan2(diry, dirx)                    # 은신 지점 방향(보스 반대편)
            diff = (ang_t - ang_u + math.pi) % (2 * math.pi) - math.pi
            if abs(diff) > 0.15:                              # 아직 반대편이 아님 → 호 이동
                step = 0.6 if diff > 0 else -0.6
                orbit_r = p.radius + u.radius + 0.45
                tx = p.x + math.cos(ang_u + step) * orbit_r
                ty = p.y + math.sin(ang_u + step) * orbit_r
        return self._move_toward(u, tx, ty)

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
