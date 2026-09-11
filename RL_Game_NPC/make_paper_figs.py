# -*- coding: utf-8 -*-
"""논문 figure 생성 — 경계 지도 / D·E·F 수렴 곡선 / 이관 기믹 성공률.

입력: runs_paper/<run>/metrics.jsonl (실험 캠페인 산출물)
출력: docs/paper/figs/*.pdf (+ 검수용 .png)
"""
import json, io, os
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "runs_paper")
OUT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                    "..", "docs", "paper", "figs"))
os.makedirs(OUT, exist_ok=True)

# 검증된 팔레트 (dataviz 기본 카테고리 슬롯 1~3 / status)
C_BLUE, C_ORANGE, C_AQUA = "#2a78d6", "#eb6834", "#1baf7a"
S_GOOD, S_SERIOUS, S_CRIT = "#0ca30c", "#ec835a", "#d03b3b"
INK, SUB, GRID = "#1a1a1a", "#5f5f5f", "#dddddd"

plt.rcParams.update({
    "font.family": "Malgun Gothic", "axes.unicode_minus": False,
    "font.size": 9, "axes.titlesize": 10, "axes.labelsize": 9,
    "pdf.fonttype": 42, "axes.edgecolor": SUB, "axes.linewidth": 0.8,
    "xtick.color": SUB, "ytick.color": SUB, "text.color": INK,
    "axes.labelcolor": INK,
})


def jl(run):
    with io.open(os.path.join(BASE, run, "metrics.jsonl"), encoding="utf-8") as f:
        return [json.loads(l) for l in f]


def rolling(vals, w=500):
    out = []
    s = 0.0
    from collections import deque
    q = deque()
    for v in vals:
        q.append(v); s += v
        if len(q) > w: s -= q.popleft()
        out.append(s / len(q))
    return out


def ev_rate_series(lines, num, fail, w=500):
    """이벤트 성공률 롤링: (성공 발생 에피소드 단위) num/(num+fail)."""
    vals = []
    for l in lines:
        n = l["events"].get(num, 0); m = l["events"].get(fail, 0)
        if n + m == 0:
            continue
        vals.append((sum(1 for _ in range(1)) * n / (n + m)))
    return rolling(vals, w)


def style_ax(ax):
    ax.spines[["top", "right"]].set_visible(False)
    ax.grid(True, axis="y", color=GRID, linewidth=0.6)
    ax.set_axisbelow(True)


# ─────────── Fig 1. 규칙–학습 경계 지도 ───────────
# 위치 = 설계 시점 분류(실패 비용 × 판단 결정성), 색/모양/라벨 = 실측 결과.
pts = [
    ("전멸기 진입\n(M1)", 0.86, 0.94, "crit", "성공 48% 유지\n승급 정체", "above"),
    ("낙인 산개\n(M2)", 0.7, 0.6, "crit", "성공 34% 유지\n(승률 -2.6%p)", "above"),
    ("임박 위험 회피\n(M3)", 0.5, 0.44, "part", "피격 +79%\n승률 -10.1%p", "above"),
    ("무력화 집중\n(M4)", 0.88, 0.24, "good", "성공률 유지\n승률 -7.2%p", "below"),
    ("어그로 관리\n(M5·역방향)", 0.3, 0.3, "good", "규칙화 무해\n승률 동등", "above"),
]

MARK = {"good": ("o", S_GOOD, "학습으로 유지됨"),
        "part": ("^", S_SERIOUS, "부분적으로 학습됨"),
        "crit": ("X", S_CRIT, "학습 실패(보장 필요)")}

fig, ax = plt.subplots(figsize=(5.6, 4.2))
ax.axhline(0.5, color=GRID, linewidth=0.8)
ax.axvline(0.5, color=GRID, linewidth=0.8)
for name, x, y, cls, note, place in pts:
    m, c, _ = MARK[cls]
    ax.scatter([x], [y], marker=m, s=110, color=c, edgecolors="white",
               linewidths=1.2, zorder=3)
    if place == "above":
        ax.annotate(name, (x, y), xytext=(0, 11), textcoords="offset points",
                    ha="center", fontsize=8.5, color=INK, fontweight="bold")
        ax.annotate(note, (x, y), xytext=(0, -13), textcoords="offset points",
                    ha="center", va="top", fontsize=7.5, color=SUB)
    else:  # below — 이름·주석 모두 마커 아래로(이웃 점 주석과의 충돌 회피)
        ax.annotate(name, (x, y), xytext=(0, -13), textcoords="offset points",
                    ha="center", va="top", fontsize=8.5, color=INK, fontweight="bold")
        ax.annotate(note, (x, y), xytext=(0, -41), textcoords="offset points",
                    ha="center", va="top", fontsize=7.5, color=SUB)
ax.text(0.56, 1.06, "규칙(BT) 권장 영역", ha="left", va="center",
        fontsize=8.5, color=SUB, style="italic")
ax.text(0.03, 0.03, "학습(RL) 권장 영역", ha="left", va="bottom",
        fontsize=8.5, color=SUB, style="italic", transform=ax.transAxes)
ax.set_xlim(0, 1.05); ax.set_ylim(0, 1.1)
ax.set_xticks([0, 0.5, 1]); ax.set_xticklabels(["낮음", "", "높음"])
ax.set_yticks([0, 0.5, 1]); ax.set_yticklabels(["낮음", "", "높음"])
ax.set_xlabel("판단의 결정성 (정답이 명확한 정도)")
ax.set_ylabel("실패 비용")
style_ax(ax); ax.grid(False)
handles = [Line2D([], [], marker=MARK[k][0], color=MARK[k][1], linestyle="",
                  markersize=8, label=MARK[k][2]) for k in ("good", "part", "crit")]
ax.legend(handles=handles, loc="upper left", frameon=False, fontsize=8)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "fig_boundary_map.pdf"))
fig.savefig(os.path.join(OUT, "fig_boundary_map.png"), dpi=170)
plt.close(fig)

# ─────────── Fig 2. 개입 표본 처리 D/E/F — 승률 수렴 곡선 ───────────
fig, ax = plt.subplots(figsize=(5.6, 3.4))
runs = [("C_smdp", "SMDP 귀속 (F, 제안 채택)", "F", C_BLUE),
        ("E_drop", "Drop-only (E)", "E", C_AQUA),
        ("D_naive", "단순 혼합 (D)", "D", C_ORANGE)]
for run, label, code, color in runs:
    L = jl(run)
    wr = rolling([1.0 if l["victory"] else 0.0 for l in L], 500)
    xs = list(range(1, len(wr) + 1))
    ax.plot(xs, wr, color=color, linewidth=2.0)
    # 직접 라벨은 짧은 조건 기호만(범례와 중복·충돌 방지)
    ax.annotate(code, (xs[-1], wr[-1]), xytext=(5, 0),
                textcoords="offset points", va="center", fontsize=9,
                color=INK, fontweight="bold")
ax.set_xlabel("학습 에피소드")
ax.set_ylabel("승률 (500 에피소드 이동평균)")
ax.set_xlim(0, 21500)
ax.set_ylim(-0.02, 1.0)
style_ax(ax)
ax.legend(handles=[Line2D([], [], color=c, linewidth=2, label=l)
                   for _, l, _, c in runs],
          loc="upper left", frameon=False, fontsize=8)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "fig_def_curves.pdf"))
fig.savefig(os.path.join(OUT, "fig_def_curves.png"), dpi=170)
plt.close(fig)

# ─────────── Fig 3. 이관된 기믹의 성공률 (M1/M2) ───────────
fig, axes = plt.subplots(1, 2, figsize=(5.6, 2.9), sharey=True)
panels = [("M1_seal", "전멸기 진입 → RL (M1)", "seal_success", "seal_fail", 0.866),
          ("M2_brand", "낙인 산개 → RL (M2)", "mechanic_success", "mechanic_fail", 0.781)]
for ax, (run, title, num, fail, base_rate) in zip(axes, panels):
    L = jl(run)
    vals = []
    for l in L:
        n = l["events"].get(num, 0); m = l["events"].get(fail, 0)
        if n + m > 0:
            vals.append(n / (n + m))
    rr = rolling(vals, 200)
    ax.plot(range(1, len(rr) + 1), rr, color=C_BLUE, linewidth=2.0)
    ax.axhline(base_rate, color=SUB, linewidth=1.2, linestyle=(0, (4, 3)))
    ax.annotate("기준선 C (BT 보장)", (len(rr) * 0.98, base_rate),
                ha="right", va="bottom", fontsize=7.5, color=SUB)
    ax.set_title(title, fontsize=9)
    ax.set_xlabel("기믹 발생 횟수(누적)")
    ax.set_ylim(0, 1.0)
    style_ax(ax)
axes[0].set_ylabel("기믹 성공률\n(200회 이동평균)")
fig.tight_layout()
fig.savefig(os.path.join(OUT, "fig_moved_gimmicks.pdf"))
fig.savefig(os.path.join(OUT, "fig_moved_gimmicks.png"), dpi=170)
plt.close(fig)

print("saved to", OUT)
