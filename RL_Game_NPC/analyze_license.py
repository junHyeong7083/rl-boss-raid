# -*- coding: utf-8 -*-
"""면허 캠페인 최종 분석 — 논문 표 소스.

조건별 (승률, 기믹 성공률, 보장 계층 개입률, 규칙별 기믹 성공률, 면허 상태)를
학습 말미 구간에서 집계하고 JSON 으로 저장한다.
"""
import json, io, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.join(HERE, "runs_license")
TAIL = 2000          # 학습 말미 집계 구간(에피소드)

CONDS = [
    ("L_fix",           "고정 BT (보장 계층 상시 개입)"),
    ("L_adapt_group",   "면허 프로토콜 (제안, 그룹 동기 프로브)"),
    ("L_adapt_perunit", "면허 프로토콜 (유닛별 프로브 — 과대추정 결함)"),
    ("L_mono_perunit",  "발급만, 회수 없음 (단조 완화)"),
    ("L_none",          "보장 계층 없음 (순수 RL)"),
]


def rate(t, ok_ev, fail_ev):
    a = sum(l["events"].get(ok_ev, 0) for l in t)
    b = sum(l["events"].get(fail_ev, 0) for l in t)
    return a / max(1, a + b) if (a + b) else None


def summarize(run):
    p = os.path.join(BASE, run, "metrics.jsonl")
    if not os.path.exists(p):
        return None
    L = [json.loads(l) for l in io.open(p, encoding="utf-8")]
    done = os.path.exists(os.path.join(BASE, run, "final.pt")) and len(L) >= 19000
    t = L[-TAIL:]
    lic = L[-1].get("licenses") or {}
    ev = [e for l in L for e in (l.get("license_events") or [])]
    out = {
        "episodes": len(L), "complete": bool(done),
        "winrate": round(sum(l["victory"] for l in t) / len(t), 4),
        "gimmick": round(sum(l["gimmick_success_rate"] for l in t) / len(t), 4),
        "intervention": round(sum(l["bt_fire_ratio"] for l in t) / len(t), 4),
        "brand": rate(t, "mechanic_success", "mechanic_fail"),
        "kill_steps": round(sum(l["steps"] for l in t if l["victory"])
                            / max(1, sum(l["victory"] for l in t)), 1),
        "handed": sorted(r for r, v in lic.items() if v.get("handed")),
        "trust": {r: v["T"] for r, v in lic.items()},
        "handovers": sum(1 for e in ev if e["event"] == "handover"),
        "recalls": sum(1 for e in ev if e["event"] == "recall"),
    }
    if out["brand"] is not None:
        out["brand"] = round(out["brand"], 4)
    return out


def main():
    res = {}
    print(f"{'조건':42s} {'승률':>7s} {'기믹':>7s} {'개입률':>7s} {'낙인':>7s} {'발급/회수':>10s}")
    print("-" * 88)
    for run, label in CONDS:
        s = summarize(run)
        if s is None:
            print(f"{label:42s} {'(미완료)':>7s}")
            continue
        res[run] = dict(s, label=label)
        b = f"{s['brand']:.1%}" if s["brand"] is not None else "-"
        mark = "" if s["complete"] else f"  ← 진행중 {s['episodes']}/20000"
        print(f"{label:42s} {s['winrate']:6.1%} {s['gimmick']:7.1%} "
              f"{s['intervention']:7.2f} {b:>7s} {s['handovers']:5d}/{s['recalls']:<4d}{mark}")
    print()
    for run, label in CONDS:
        if run in res and res[run]["handed"]:
            print(f"  {label}: 최종 면허 보유 = {res[run]['handed']}")
    with io.open(os.path.join(BASE, "final_summary.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=2)
    print("\nsaved:", os.path.join(BASE, "final_summary.json"))


if __name__ == "__main__":
    main()
