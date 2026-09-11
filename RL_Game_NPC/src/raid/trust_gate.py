"""행동별 면허 프로토콜 (Per-Behavior Licensing) — 보장 계층의 발급·감사·정지.

보장 계층(BT)의 각 규칙이 학습 정책(RL)에게 그 행동에 대한 '면허'를 발급하고,
발급 후에도 계속 감사하며, 성적이 무너지면 정지(회수)한다.

  발급(handover) : 신뢰 T_r ≥ θ_hand(r) → 규칙 침묵, RL 이 해당 기믹을 전담
  감사(probe)    : 규칙이 발화할 상황에서 확률 ε_r 로 RL 에게 실집행을 맡겨 결과 채점
  정지(recall)   : T_r < θ_recall(r) → 규칙 복귀 (θ_recall < θ_hand, 히스테리시스)

설계 근거(docs/설계_검증기반_제어권이양.md):
  · 협동 게임에서는 섀도(비집행) 평가가 원리적으로 불가능하다 — NPC 의 행동이 팀 결과를
    인과적으로 바꾸므로, 신뢰를 추정할 유일한 방법이 '실집행 프로브'이고 그 비용은
    해당 행동의 실패 비용에 비례한다. 따라서 프로브 예산 ε_max 는 실패 비용에 역비례로
    배분하고(전멸급은 거의 0), 발급 임계 θ_hand 는 실패 비용에 비례해 높인다.
  · 신뢰 갱신은 비대칭(β > α): 한 번의 실패가 여러 번의 성공보다 크게 반영된다.
    성공 T += α(1-T), 실패 T *= (1-β) 의 고정점에서, 면허 발급에 필요한 감사 성공률은
        p_req(r) = θ_hand(r)·β / ( α(1-θ_hand(r)) + θ_hand(r)·β )
    로 θ_hand 에 단조 증가한다. 즉 **실패 비용이 큰 행동일수록 더 높은 신뢰도를 요구**한다는
    설계 의도가 파라미터 한 줄이 아니라 닫힌 형태로 보장된다(α=0.12, β=0.30 기준
    전멸급 97.9% … 딜손실급 78.9%).
  · 발급에는 최소 감사 횟수(license_min_audits)를 요구해 우연한 연속 성공으로 면허가
    나가는 것을 막는다.
  · 정지(회수) 후에는 **면허 정지 기간**(license_suspend_audits 회의 감사 동안 재발급 금지)을
    둔다. 임계 히스테리시스만으로는 신뢰가 발급/정지 경계에 걸칠 때 재발급이 반복되는
    채터링이 생긴다(스위칭 시스템의 최소 체류시간 요건과 같은 역할).
  · 감사는 '한 턴'이 아니라 '기믹 1회(trial)' 단위로 한다 — 결과가 나중에 확정되므로
    (전멸기 웨이브 폭발, 낙인 착탄, 무력화 창 종료) 이벤트로 판정한다.

선행과의 구분(신규성 조사 2026-09-12):
  ADVICE(2024)는 shield 임계를 전역 단일값으로 비단조 조정하고, Neural Simplex(2020)는
  상태 안전성 기반으로 학습 컨트롤러에 복귀한다. 본 구조는 (a) 규칙(행동) 단위 신뢰를
  (b) 실집행 프로브로 추적하며 (c) 회수 트리거가 상태 위험이 아니라 '정책 망각'이라는
  점에서 구별된다. "회수가 존재한다"는 신규성 주장이 아님.
"""
from __future__ import annotations
from typing import Dict, List, Optional, Set
import random


# 규칙별 감사 채점 규약.
#   fail_ev    : 이 이벤트가 관측되면 실패 확정
#   success_ev : 이 이벤트가 관측되면 성공 확정
#   close_ev   : 이 이벤트가 오면 trial 종료(판정)
#   default    : close_ev 없이 trial 이 교체/종료될 때의 기본 판정
SCORERS: Dict[str, dict] = {
    # 전멸기: 웨이브 폭발(pillar_explode)로 종료. 해당 웨이브에 안전 원 밖이면 즉사(seal_fail).
    "seal_hide": {"fail_ev": {"seal_fail", "death"}, "success_ev": set(),
                  "close_ev": {"pillar_explode"}, "default": "success"},
    # 낙인 산개: 착탄 시 mechanic_success/fail 로 판정.
    "brand_spread": {"fail_ev": {"mechanic_fail"}, "success_ev": {"mechanic_success"},
                     "close_ev": {"mechanic_success", "mechanic_fail"}, "default": "fail"},
    # 임박 위험 회피: 해당 스텝의 **장판 피해**를 맞으면 실패(보스 평타 피격은 무관).
    "imminent_escape": {"fail_ev": {"damage_taken", "death"}, "success_ev": set(),
                        "close_ev": set(), "default": "success",
                        "fail_src": {"pattern"}},
    # 패링 장판 이탈: 그 장판(parry) 피해를 맞으면 실패.
    "yellow_escape": {"fail_ev": {"damage_taken", "death"}, "success_ev": set(),
                      "close_ev": set(), "default": "success",
                      "fail_src": {"parry"}},
    # 무력화 집중: 창 안에 게이지를 파괴(stagger_break)해야 성공.
    "stagger_dps": {"fail_ev": {"stagger_fail"}, "success_ev": {"stagger_break"},
                    "close_ev": {"stagger_break", "stagger_fail"}, "default": "fail"},
    # 돌진 유도: 기둥 충돌(rush_pillar_hit)을 만들어야 성공.
    "rush_lure": {"fail_ev": set(), "success_ev": {"rush_pillar_hit"},
                  "close_ev": {"rush_pillar_hit"}, "default": "fail"},
}

LICENSED_RULES = tuple(SCORERS.keys())


class RuleLicense:
    """규칙 하나의 면허 상태."""

    __slots__ = ("name", "eps_max", "theta_hand", "theta_recall", "alpha", "beta",
                 "min_audits", "suspend_audits", "suspended_until",
                 "trust", "handed", "probes", "audits_ok", "audits_fail",
                 "handovers", "recalls", "fires", "silent")

    def __init__(self, name: str, eps_max: float, theta_hand: float,
                 theta_recall: float, alpha: float, beta: float,
                 min_audits: int = 20, suspend_audits: int = 40):
        self.name = name
        self.eps_max = eps_max
        self.theta_hand = theta_hand
        self.theta_recall = theta_recall
        self.alpha = alpha
        self.beta = beta
        self.min_audits = min_audits
        self.suspend_audits = suspend_audits
        self.suspended_until = 0        # 이 감사 횟수에 도달하기 전에는 재발급 금지
        self.trust = 0.0
        self.handed = False
        # 누적 통계(연구 로깅)
        self.probes = 0
        self.audits_ok = 0
        self.audits_fail = 0
        self.handovers = 0
        self.recalls = 0
        self.fires = 0      # 규칙이 실제로 발화한 횟수
        self.silent = 0     # 침묵(프로브+이양)한 횟수


class LicenseGate:
    """보장 계층 전체의 면허 상태. 파티의 모든 NPC 가 하나의 게이트를 공유한다
    (면허의 대상이 개별 유닛이 아니라 '해당 역할 정책들의 기믹 수행 능력'이므로)."""

    def __init__(self, cfg, npc_uids, mode: str = "adaptive", rng=None):
        """mode: adaptive(발급+회수) / mono(발급만, 회수 없음 — 단조 완화 ablation)
                 / fixed(항상 규칙 발화 — 프로브 없음, 기준선)"""
        if mode not in ("adaptive", "mono", "fixed"):
            raise ValueError(f"unknown gate mode: {mode}")
        self.mode = mode
        self.cfg = cfg
        self.npc_uids = list(npc_uids)
        self.rng = rng or random.Random(0)
        tiers = getattr(cfg, "license_tiers", {})
        self.licenses: Dict[str, RuleLicense] = {}
        for rule in LICENSED_RULES:
            t = tiers.get(rule)
            if t is None:
                continue
            eps, th, tr = t
            if mode == "fixed":
                eps, th = 0.0, 2.0      # 프로브 없음 + 발급 불가
            self.licenses[rule] = RuleLicense(
                rule, eps, th, tr,
                getattr(cfg, "license_alpha", 0.12),
                getattr(cfg, "license_beta", 0.30),
                getattr(cfg, "license_min_audits", 20),
                getattr(cfg, "license_suspend_audits", 40))
        self.trials: Dict[str, dict] = {}
        self._last_step = -1
        self._stagger_prev = False
        self.stagger_epoch = 0
        # 이번 에피소드에 발생한 이양/회수 이벤트(로깅)
        self.events: List[dict] = []

    # ── 매 턴 1회 동기화: 직전 턴 이벤트로 열린 trial 을 채점 ──
    def sync(self, env):
        step = env.current_step
        if step == self._last_step:
            return
        self._last_step = step
        if env.boss.stagger_active and not self._stagger_prev:
            self.stagger_epoch += 1
        self._stagger_prev = env.boss.stagger_active

        if not self.trials:
            return
        for key, tr in list(self.trials.items()):
            rule, uid = key
            sc = SCORERS[rule]
            fail_src = sc.get("fail_src")
            close = False
            for e in env.step_events.get(uid, ()):   # 그 유닛의 직전 턴 이벤트만 본다
                t = e.get("type")
                if t in sc["fail_ev"]:
                    # 출처 필터: 그 규칙의 실패로 인한 피해만 실패로 센다
                    if fail_src is None or e.get("src") in fail_src:
                        tr["failed"] = True
                if t in sc["success_ev"]:
                    tr["succeeded"] = True
                if t in sc["close_ev"]:
                    close = True
            if close:
                self._close(key, tr)

    # ── 규칙 발화 지점에서의 면허 조회 ──
    def allow_fire(self, rule: str, uid: int, trial_id: str) -> bool:
        """True = 규칙이 발화(보장 계층이 운전), False = 침묵(RL 이 운전, 감사 대상).

        감사 단위(trial)는 (규칙, 유닛)별 기믹 1회. 신뢰 T 는 규칙 단위로 공유한다
        — 면허의 대상이 개별 유닛이 아니라 '그 기믹을 수행하는 학습 정책의 능력'이므로."""
        lic = self.licenses.get(rule)
        if lic is None:
            return True                      # 면허 관리 대상이 아닌 규칙은 항상 발화
        key = (rule, uid)
        cur = self.trials.get(key)
        if cur is None or cur["id"] != trial_id:
            if cur is not None:
                self._close(key, cur)        # 기믹이 바뀌었으면 이전 trial 판정
            drive = self._decide(lic)
            self.trials[key] = {"id": trial_id, "mode": drive,
                                "failed": False, "succeeded": False}
            cur = self.trials[key]
            if drive == "probe":
                lic.probes += 1
        if cur["mode"] == "supervised":
            lic.fires += 1
            return True
        lic.silent += 1
        return False

    def _decide(self, lic: RuleLicense) -> str:
        if lic.handed:
            return "handover"
        if lic.eps_max > 0.0 and self.rng.random() < self._eps(lic):
            return "probe"
        return "supervised"

    def _eps(self, lic: RuleLicense) -> float:
        """프로브 확률: 신뢰가 낮을 때도 최소한의 탐색을 하되, 신뢰가 오를수록 증가."""
        return min(lic.eps_max, 0.25 * lic.eps_max + 0.75 * lic.eps_max * lic.trust)

    # ── trial 판정 및 신뢰 갱신 ──
    def _close(self, key, tr: dict):
        rule = key[0]
        self.trials.pop(key, None)
        if tr["mode"] == "supervised":
            return                            # BT 가 운전한 회차는 RL 능력의 증거가 아님
        lic = self.licenses[rule]
        sc = SCORERS[rule]
        if tr["failed"]:
            ok = False
        elif tr["succeeded"]:
            ok = True
        else:
            ok = (sc["default"] == "success")
        if ok:
            lic.audits_ok += 1
            lic.trust += lic.alpha * (1.0 - lic.trust)
        else:
            lic.audits_fail += 1
            lic.trust *= (1.0 - lic.beta)
        self._update_license(lic)

    def _update_license(self, lic: RuleLicense):
        audits = lic.audits_ok + lic.audits_fail
        if not lic.handed:
            if (lic.trust >= lic.theta_hand and audits >= lic.min_audits
                    and audits >= lic.suspended_until):
                lic.handed = True
                lic.handovers += 1
                self.events.append({"rule": lic.name, "event": "handover",
                                    "trust": round(lic.trust, 4), "audits": audits})
        else:
            if self.mode != "mono" and lic.trust < lic.theta_recall:
                lic.handed = False
                lic.recalls += 1
                lic.suspended_until = audits + lic.suspend_audits   # 면허 정지 기간
                self.events.append({"rule": lic.name, "event": "recall",
                                    "trust": round(lic.trust, 4), "audits": audits})

    # ── 에피소드 경계 ──
    def end_episode(self):
        """미판정 trial 을 기본 규약으로 닫는다(에피소드 종료)."""
        for key, tr in list(self.trials.items()):
            self._close(key, tr)
        self.trials.clear()
        self._last_step = -1
        self._stagger_prev = False

    # ── 로깅 ──
    @staticmethod
    def required_success_rate(theta: float, alpha: float, beta: float) -> float:
        """임계 theta 에 대응하는 감사 성공률(신뢰 갱신 T*=pα/(pα+(1-p)β) 의 역산).

        theta_hand 에 적용하면 '면허 발급에 필요한 성공률', theta_recall 에 적용하면
        '면허 정지가 일어나는 성공률'이 된다. 두 값의 간격이 곧 노이즈 내성이며,
        정지가 우발적 실패가 아니라 지속적 성능 하락(망각)에만 반응함을 보장한다."""
        num = theta * beta
        return num / max(1e-9, alpha * (1.0 - theta) + num)

    def snapshot(self) -> Dict[str, dict]:
        return {r: {"T": round(l.trust, 4), "handed": bool(l.handed),
                    "susp": max(0, l.suspended_until - (l.audits_ok + l.audits_fail)),
                    "probe": l.probes, "ok": l.audits_ok, "fail": l.audits_fail,
                    "fire": l.fires, "silent": l.silent,
                    "hand_n": l.handovers, "recall_n": l.recalls}
                for r, l in self.licenses.items()}

    def pop_events(self) -> List[dict]:
        ev, self.events = self.events, []
        return ev

    def intervention_ratio(self) -> float:
        """보장 계층이 실제로 운전한 비율(발화 / (발화+침묵))."""
        f = sum(l.fires for l in self.licenses.values())
        s = sum(l.silent for l in self.licenses.values())
        return f / max(1, f + s)
