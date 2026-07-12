"""Layer 1 — 기믹 인식 Behavior Tree (결정론적 규칙).

NUM2.md 2계층 하이브리드 아키텍처의 Layer 1. "이산·규칙 명확 = BT" 원칙에 따라
희소 협동 기믹(전멸기/임박회피/낙인/무력화/패링/돌진유도)은 규칙으로 확정 처리하고,
나머지(포지셔닝·어그로·힐 타깃·공격 타이밍)는 None 을 돌려 Layer 2 RL 로 fall-through 한다.

규칙 테이블은 NUM2.md 3.2/3.5 를 **현행 11패턴 메커니즘**(patterns.py / boss.py / env.py)으로
재작성한 것이며, 전투 로직은 FSMNpcPolicy(읽기 전용)의 검증된 헬퍼를 **조합(composition)** 으로
재사용한다(파일 수정 없이 로직 이식).

  대상: NPC(TANK/HEALER/SUPPORT) — 딜러(플레이어)는 BT 미적용.

  우선순위 규칙 (fire 시 int 액션 확정, 아니면 다음 규칙 → 최종 None):
    1) SEAL_WIPE 활성(전멸기 '혈월 강림')  → 최종 생존 기둥 뒤 LOS 은신     (최우선)
    2) 임박 위험(텔레그래프 안 & 잔여 ≤2턴) → 최속 탈출 (탱커 GUARD 딜타임 예외)
    3) CRIMSON_BRAND 낙인                    → 산개/이격
    4) 무력화 그로기(stagger_active)         → 근접 확보 + 스킬/평타 (탱커 TAUNT)
    5) YELLOW_BURST(패링 장판) 안            → 원 밖 탈출 (패링은 딜러 전용)
    6) FRENZY_RUSH 표식 대상이 나            → 기둥 방향 유도(보스 충돌 그로기)
    7) 기믹 없음                             → None (RL 로 위임)

각 규칙의 fire 여부/사유는 last_decision 으로 노출(연구 로깅용 BT/RL 발화 비율 측정).
"""
from __future__ import annotations
import math
from typing import Optional, Dict, TYPE_CHECKING

from .config import RaidActionID, PartyRole, PatternID
from .fsm_npc import FSMNpcPolicy

if TYPE_CHECKING:
    from .env import RaidEnv


class BTGimmickLayer:
    """단일 NPC(uid)의 Layer 1 기믹 BT.

    act() -> Optional[int]:  int = 규칙 확정 액션, None = fall-through(RL 소관).
    last_decision: {"rule": name|None, "fired": bool, "reason": str}
    """

    def __init__(self, env: "RaidEnv", uid: int):
        self.env = env
        self.uid = uid
        # 검증된 전투/회피 헬퍼를 재사용(파일 수정 없이 조합). BT 는 이 헬퍼들의
        # _seal_hide/_safe_move/_maybe_guard/_brand_action/_attack_or_approach/
        # _move_toward_avoiding 만 골라 호출한다.
        self._fsm = FSMNpcPolicy(env, uid)
        self.last_decision: Dict[str, object] = {"rule": None, "fired": False, "reason": "init"}

    # ── 로깅 헬퍼 ──
    def _fire(self, rule: str, reason: str, action: int) -> int:
        self.last_decision = {"rule": rule, "fired": True, "reason": reason}
        return int(action)

    def _pass(self, reason: str) -> None:
        self.last_decision = {"rule": None, "fired": False, "reason": reason}
        return None

    # ── 진입점 ──
    def act(self) -> Optional[int]:
        env = self.env
        u = env.units[self.uid]
        if not u.alive:
            return self._pass("dead")
        b = env.boss
        ap = b.active_pattern

        # 1) 전멸기 '혈월 강림' — 최종 생존 기둥 뒤 LOS 은신 (최우선)
        if ap is not None and ap.mode == "seal":
            return self._fire("seal_hide", "seal_wipe_active", self._fsm._seal_hide(u))

        # 2) 임박 위험 — 내가 텔레그래프 안 & 현재 스텝 잔여 ≤2턴 → 최속 탈출.
        #    (여유 있으면(잔여>2) fire 안 함 → RL 이 선제 포지셔닝. NUM2.md 시나리오 B/C.)
        if ap is not None and ap.mode == "steps" and ap.turns_remaining <= 2 \
                and any(s.contains((u.x, u.y)) for s in self._fsm._world_shapes()):
            # 탱커 가드 딜타임 예외: 피격 임박 스텝이면 GUARD 로 경감+보스 경직.
            if u.role == PartyRole.TANK:
                g = self._fsm._maybe_guard(u)
                if g is not None:
                    return self._fire("tank_guard", "imminent_guard_dealtime", g)
            mv = self._fsm._safe_move(u)
            if mv is not None:
                return self._fire("imminent_escape", "in_telegraph_le2turns", mv)
            # 탈출로가 전부 막힘(희소) — RL 에 위임.

        # 3) 붉은 낙인(CRIMSON_BRAND) 산개/이격.
        brand = self._fsm._brand_action(u)
        if brand is not None:
            return self._fire("brand_spread", "crimson_brand_mark", brand)

        # 4) 무력화 그로기(stagger_active) — 근접 확보 + 딜 집중. 탱커는 TAUNT 우선.
        if b.stagger_active:
            if u.role == PartyRole.TANK \
                    and u.cooldowns.get(int(RaidActionID.TAUNT), 0) <= 0:
                return self._fire("stagger_taunt", "stagger_window_tank", int(RaidActionID.TAUNT))
            return self._fire("stagger_dps", "stagger_window_focus",
                              self._fsm._attack_or_approach(u, prefer_skill=True))

        # 5) YELLOW_BURST(노란 확산 원 = 패링 장판) — NPC 는 파훼 불가 → 원 밖 탈출.
        #    (패링은 딜러 G 전용. 임박 전이라도 원 안이면 미리 빠진다.)
        if ap is not None and ap.mode == "steps" \
                and ap.pattern_id == PatternID.YELLOW_BURST \
                and ap.contains((u.x, u.y)):
            mv = self._fsm._safe_move(u)
            if mv is not None:
                return self._fire("yellow_escape", "parry_field_npc_evac", mv)

        # 6) FRENZY_RUSH 표식 대상이 나(NPC) → 기둥 방향 유도(보스 충돌 그로기).
        if ap is not None and ap.mode == "steps" \
                and ap.pattern_id == PatternID.FRENZY_RUSH \
                and env._rush_target_uid(ap) == self.uid:
            lure = self._pillar_lure_point(u)
            if lure is not None:
                return self._fire("rush_lure", "frenzy_rush_target_self",
                                  self._fsm._move_toward_avoiding(u, lure[0], lure[1]))

        # 7) 기믹 없음 → RL 위임.
        return self._pass("no_gimmick")

    # ── 유도 헬퍼: 보스 돌진을 기둥에 충돌시키도록 기둥 뒤로 이동 ──
    def _pillar_lure_point(self, u):
        """살아있는 기둥 중 보스→기둥 라인이 나에게 가장 잘 정렬되는(우회 최소) 기둥을 골라
        그 '보스 반대편 바로 뒤' 지점을 반환. 그 지점으로 서면 보스 돌진 경로가 기둥에 막혀
        기둥 충돌 그로기(rush_pillar_hit)를 유발한다. 기둥 없으면 None."""
        env = self.env
        b = env.boss
        best = None
        best_cost = 1e18
        for p in env.pillars:
            if not p.alive:
                continue
            dx = p.x - b.x
            dy = p.y - b.y
            d = math.hypot(dx, dy) or 1.0
            # 기둥 바로 뒤(보스 반대편) 지점
            tx = p.x + dx / d * (p.radius + u.radius + 0.4)
            ty = p.y + dy / d * (p.radius + u.radius + 0.4)
            # 우회 비용 = 내가 그 지점까지 가야 하는 거리 (가까운 기둥 선호)
            cost = math.hypot(tx - u.x, ty - u.y)
            if cost < best_cost:
                best_cost = cost
                best = (tx, ty)
        return best
