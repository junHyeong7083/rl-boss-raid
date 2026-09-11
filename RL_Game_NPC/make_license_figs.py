# -*- coding: utf-8 -*-
"""면허 프로토콜 논문 figure — 신뢰 궤적(대표 그림) / 개입률 / 회수-망각 정합.

입력: runs_license/<run>/metrics.jsonl
출력: docs/paper/figs/*.pdf (+ 검수용 .png)
"""
import json, io, os, sys
from collections import deque
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.join(HERE, "runs_license")
OUT = os.path.normpath(os.path.join(HERE, "..", "docs", "paper", "figs"))
os.makedirs(OUT, exist_ok=True)

sys.path.insert(0, HERE)
from src.raid import RaidConfig
from src.raid.trust_gate import LicenseGate

C_BLUE, C_ORANGE, C_AQUA = "#2a78d6", "#eb6834", "#1baf7a"
S_GOOD, S_CRIT = "#0ca30c", "#d03b3b"
INK, SUB, GRID = "#1a1a1a", "#5f5f5f", "#dddddd"
BAND = "#dff0e3"      # 면허 보유 구간 음영

plt.rcParams.update({
    "font.family": "Malgun Gothic", "axes.unicode_minus": False,
    "font.size": 9, "axes.titlesize": 9.5, "axes.labelsize": 9,
    "pdf.fonttype": 42, "axes.edgecolor": SUB, "axes.linewidth": 0.8,
    "xtick.color": SUB, "ytick.color": SUB, "text.color": INK,
    "axes.labelcolor": INK,
})

RULE_KO = {
    "seal_hide": "전멸기 안전 석상 진입",
    "brand_spread": "낙인 산개",
    "yellow_escape": "패링 장판 이탈",
    "imminent_escape": "임박 위험 회피",
    "rush_lure": "돌진 기둥 유도",
    "stagger_dps": "무력화 집중",
}
ORDER = ["seal_hide", "brand_spread", "yellow_escape",
         "imminent_escape", "rush_lure", "stagger_dps"]


def jl(run):
    with io.open(os.path.join(BASE, run, "metrics.jsonl"), encoding="utf-8") as f:
        return [json.loads(l) for l in f]


def rolling(vals, w):
    out, q, s = [], deque(), 0.0
    for v in vals:
        q.append(v); s += v
        if len(q) > w: s -= q.popleft()
        out.append(s / len(q))
    return out


def style(ax):
    ax.spines[["top", "right"]].set_visible(False)
    ax.grid(True, axis="y", color=GRID, linewidth=0.6)
    ax.set_axisbelow(True)


def shade_handed(ax, handed, n):
    """면허 보유(이양) 구간을 음영으로 표시."""
    start = None
    for i, h in enumerate(handed):
        if h and start is None:
            start = i
        elif not h and start is not None:
            ax.axvspan(start, i, color=BAND, lw=0, zorder=0)
            start = None
    if start is not None:
        ax.axvspan(start, n, color=BAND, lw=0, zorder=0)


def main():
    cfg = RaidConfig()
    A = jl("L_adapt")
    n = len(A)

    # ── Fig 1. 행동별 신뢰 궤적 (대표 그림) ──
    fig, axes = plt.subplots(2, 3, figsize=(7.2, 4.2), sharex=True, sharey=True)
    for ax, rule in zip(axes.ravel(), ORDER):
        T = [(l["licenses"] or {}).get(rule, {}).get("T", 0.0) for l in A]
        H = [(l["licenses"] or {}).get(rule, {}).get("handed", False) for l in A]
        th, tr = cfg.license_tiers[rule][1], cfg.license_tiers[rule][2]
        shade_handed(ax, H, n)
        ax.plot(range(1, n + 1), T, color=C_BLUE, linewidth=1.6, zorder=3)
        ax.axhline(th, color=S_GOOD, linewidth=1.0, linestyle=(0, (4, 3)), zorder=2)
        ax.axhline(tr, color=S_CRIT, linewidth=1.0, linestyle=(0, (1, 2)), zorder=2)
        p_req = LicenseGate.required_success_rate(th, cfg.license_alpha, cfg.license_beta)
        ax.set_title("%s\n(발급 요구 신뢰도 %.0f%%)" % (RULE_KO[rule], p_req * 100),
                     fontsize=8.5)
        ax.set_ylim(0, 1.0)
        style(ax)
    for ax in axes[1]:
        ax.set_xlabel("학습 에피소드")
    for ax in axes[:, 0]:
        ax.set_ylabel("신뢰도 T")
    handles = [
        Line2D([], [], color=C_BLUE, lw=1.8, label="신뢰도 T"),
        Line2D([], [], color=S_GOOD, lw=1.2, ls=(0, (4, 3)), label="발급 임계"),
        Line2D([], [], color=S_CRIT, lw=1.2, ls=(0, (1, 2)), label="정지 임계"),
        Line2D([], [], color=BAND, lw=8, label="면허 보유(규칙 침묵)"),
    ]
    fig.legend(handles=handles, loc="lower center", ncol=4, frameon=False,
               fontsize=8.5, bbox_to_anchor=(0.5, -0.02))
    fig.tight_layout(rect=(0, 0.05, 1, 1))
    fig.savefig(os.path.join(OUT, "fig_license_trajectory.pdf"))
    fig.savefig(os.path.join(OUT, "fig_license_trajectory.png"), dpi=170)
    plt.close(fig)

    # ── Fig 2. 보장 계층 개입률 ──
    fig, ax = plt.subplots(figsize=(5.6, 3.0))
    runs = [("L_adapt", "면허 프로토콜(제안)", C_BLUE),
            ("L_mono", "발급만(회수 없음)", C_ORANGE),
            ("L_fix", "고정 BT", C_AQUA)]
    drawn = []
    for run, label, color in runs:
        try:
            L = jl(run)
        except FileNotFoundError:
            continue
        ratio = rolling([l.get("bt_fire_ratio", 0.0) for l in L], 300)
        ax.plot(range(1, len(ratio) + 1), ratio, color=color, linewidth=1.8)
        drawn.append((label, color))
    ax.set_xlabel("학습 에피소드")
    ax.set_ylabel("보장 계층 개입 비율")
    ax.set_ylim(0, None)
    style(ax)
    ax.legend(handles=[Line2D([], [], color=c, lw=1.8, label=l) for l, c in drawn],
              loc="upper right", frameon=False, fontsize=8.5)
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, "fig_license_intervention.pdf"))
    fig.savefig(os.path.join(OUT, "fig_license_intervention.png"), dpi=170)
    plt.close(fig)

    # ── Fig 3. 정지(회수)와 기믹 성공률 붕괴의 시간 정합 ──
    # "감사가 망각을 조기 검출한다"를 보이는 그림: 회수 시점 전후의 기믹 성공률.
    ev = []
    for i, l in enumerate(A):
        for e in (l.get("license_events") or []):
            if e.get("event") == "recall":
                ev.append((i, e["rule"]))
    if ev:
        fig, ax = plt.subplots(figsize=(5.6, 3.0))
        gs = rolling([l.get("gimmick_success_rate", 0.0) for l in A], 200)
        ax.plot(range(1, n + 1), gs, color=C_BLUE, linewidth=1.6, zorder=3)
        for i, rule in ev:
            ax.axvline(i + 1, color=S_CRIT, linewidth=0.9, alpha=0.55, zorder=2)
        ax.set_xlabel("학습 에피소드")
        ax.set_ylabel("기믹 성공률 (200 에피소드 이동평균)")
        ax.set_ylim(0, 1.0)
        style(ax)
        ax.legend(handles=[Line2D([], [], color=C_BLUE, lw=1.8, label="기믹 성공률"),
                           Line2D([], [], color=S_CRIT, lw=1.0, label="면허 정지(회수) 시점")],
                  loc="lower right", frameon=False, fontsize=8.5)
        fig.tight_layout()
        fig.savefig(os.path.join(OUT, "fig_license_recall.pdf"))
        fig.savefig(os.path.join(OUT, "fig_license_recall.png"), dpi=170)
        plt.close(fig)

    print("saved to", OUT, "| recalls:", len(ev))


if __name__ == "__main__":
    main()
