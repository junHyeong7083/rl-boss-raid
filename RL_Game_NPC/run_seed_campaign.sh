#!/bin/bash
# 다중 시드 통제 캠페인 — 조건 간 승률 차이를 방어 가능하게 만드는 본 실험.
#
# 20k 단일 시드 캠페인에서 최종 난이도 진입 시점이 런마다 ep 112~3848 로 흔들려
# 조건 간 비교가 오염됨을 확인했다. 따라서 (a) 커리큘럼을 고정 스케줄로 통제하고
# (b) 조건마다 시드 3개를 돌린다.
#
# 사용법:  bash run_seed_campaign.sh <레인번호>
#   레인 1: fix/adapt/mono seed 0 + none seed 0
#   레인 2: fix/adapt/mono seed 1
#   레인 3: fix/adapt/mono seed 2
PY=/c/Users/user/miniconda3/envs/rl_game_npc/python.exe
cd "$(dirname "$0")"
EP=${EP:-12000}
SCHED=${SCHED:-1200,3000}
OUT=runs_seeds
mkdir -p "$OUT"

run() {           # run <이름> <시드> <추가인자...>
  local dir="$1"; local seed="$2"; shift 2
  if [ -f "$OUT/$dir/final.pt" ]; then echo "[skip] $dir"; return; fi
  echo "[start] $dir $(date '+%m-%d %H:%M')"
  PYTHONIOENCODING=utf-8 "$PY" train_raid.py --device cuda --bc-episodes 2000 \
    --episodes "$EP" --seed "$seed" --curriculum-schedule "$SCHED" \
    --gimmick-rewards on --model-dir "$OUT/$dir" --run-label "$dir" "$@" \
    > "$OUT/${dir}.log" 2>&1
  echo "[done ] $dir $(date '+%m-%d %H:%M')"
}

case "$1" in
  1)
    run S_fix_s0   0 --bt-mode fixed
    run S_adapt_s0 0 --bt-mode adaptive
    run S_mono_s0  0 --bt-mode mono
    run S_none_s0  0 --bt-mode none
    ;;
  2)
    run S_fix_s1   1 --bt-mode fixed
    run S_adapt_s1 1 --bt-mode adaptive
    run S_mono_s1  1 --bt-mode mono
    ;;
  3)
    run S_fix_s2   2 --bt-mode fixed
    run S_adapt_s2 2 --bt-mode adaptive
    run S_mono_s2  2 --bt-mode mono
    ;;
  *)
    echo "usage: bash run_seed_campaign.sh {1|2|3}"; exit 1;;
esac
echo "[LANE $1 DONE] $(date)"
