#!/bin/bash
# 면허 캠페인 레인 B (레인 A와 병렬)
PY=/c/Users/user/miniconda3/envs/rl_game_npc/python.exe
cd "$(dirname "$0")"
EP=${EP:-20000}
run() {
  local dir="$1"; shift
  if [ -f "runs_license/$dir/final.pt" ]; then echo "[skip] $dir"; return; fi
  echo "[start] $dir $(date '+%m-%d %H:%M')"
  PYTHONIOENCODING=utf-8 "$PY" train_raid.py --device cuda --bc-episodes 2000 \
    --episodes "$EP" --model-dir "runs_license/$dir" --run-label "$dir" "$@" \
    > "runs_license/${dir}.log" 2>&1
  echo "[done ] $dir $(date '+%m-%d %H:%M')"
}
mkdir -p runs_license
run L_mono  --bt-mode mono --gimmick-rewards on
run L_none  --bt-mode none --gimmick-rewards on
echo "[LANE B DONE] $(date)"
