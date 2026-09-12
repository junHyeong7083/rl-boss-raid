#!/bin/bash
# 레인 C — 그룹 동기 프로브 적용 제안 조건(유닛별 프로브 대비 ablation 쌍)
PY=/c/Users/user/miniconda3/envs/rl_game_npc/python.exe
cd "$(dirname "$0")"
run() {
  local dir="$1"; shift
  if [ -f "runs_license/$dir/final.pt" ]; then echo "[skip] $dir"; return; fi
  echo "[start] $dir $(date '+%m-%d %H:%M')"
  PYTHONIOENCODING=utf-8 "$PY" train_raid.py --device cuda --bc-episodes 2000 \
    --episodes 20000 --model-dir "runs_license/$dir" --run-label "$dir" "$@" \
    > "runs_license/${dir}.log" 2>&1
  echo "[done ] $dir $(date '+%m-%d %H:%M')"
}
run L_adapt_group --bt-mode adaptive --gimmick-rewards on
echo "[LANE C DONE] $(date)"
