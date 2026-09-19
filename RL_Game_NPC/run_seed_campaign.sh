#!/bin/bash
# 다중 시드 통제 캠페인 — 5 레인 병렬(레인당 2런).
# 세션과 무관하게 살아남도록 PowerShell Start-Process 로 분리 실행한다(launch_campaign.ps1).
#
# 사용법: bash run_seed_campaign.sh <레인 1..5>
PY=/c/Users/user/miniconda3/envs/rl_game_npc/python.exe
cd "$(dirname "$0")"
EP=${EP:-10000}
SCHED=${SCHED:-1000,2500}
OUT=runs_seeds
mkdir -p "$OUT"

run() {           # run <이름> <시드> <추가인자...>
  local dir="$1"; local seed="$2"; shift 2
  if grep -q "DONE\] ep" "$OUT/${dir}.log" 2>/dev/null; then echo "[skip] $dir"; return; fi
  echo "[start] $dir $(date '+%m-%d %H:%M')"
  PYTHONIOENCODING=utf-8 "$PY" train_raid.py --device cuda --bc-episodes 2000 \
    --episodes "$EP" --seed "$seed" --curriculum-schedule "$SCHED" \
    --gimmick-rewards on --model-dir "$OUT/$dir" --run-label "$dir" "$@" \
    > "$OUT/${dir}.log" 2>&1
  echo "[done ] $dir $(date '+%m-%d %H:%M')"
}

case "$1" in
  1) run S_fix_s0   0 --bt-mode fixed;    run S_adapt_s0 0 --bt-mode adaptive ;;
  2) run S_fix_s1   1 --bt-mode fixed;    run S_adapt_s1 1 --bt-mode adaptive ;;
  3) run S_fix_s2   2 --bt-mode fixed;    run S_adapt_s2 2 --bt-mode adaptive ;;
  4) run S_mono_s0  0 --bt-mode mono;     run S_mono_s1  1 --bt-mode mono ;;
  5) run S_mono_s2  2 --bt-mode mono;     run S_none_s0  0 --bt-mode none ;;
  *) echo "usage: bash run_seed_campaign.sh {1..5}"; exit 1;;
esac
echo "[LANE $1 DONE] $(date)"
