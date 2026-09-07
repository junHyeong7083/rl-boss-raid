#!/bin/bash
# 경계 재배치(M1~M5) + 개입 표본 처리(D/E/F) 실험 캠페인 — 순차 실행.
# 기준선(C)과 D/E 는 20k, M 조건은 수렴 곡선 목적이라 10k.
PY=/c/Users/user/miniconda3/envs/rl_game_npc/python.exe
cd "$(dirname "$0")"
run() {
  local dir="$1"; shift
  if [ -f "runs_paper/$dir/final.pt" ]; then echo "[skip] $dir"; return; fi
  echo "[start] $dir $(date '+%m-%d %H:%M')"
  PYTHONIOENCODING=utf-8 "$PY" train_raid.py --device cuda --bc-episodes 2000 \
    --model-dir "runs_paper/$dir" --run-label "$dir" "$@" \
    > "runs_paper/${dir}.log" 2>&1
  echo "[done ] $dir $(date '+%m-%d %H:%M')"
}
mkdir -p runs_paper
run C_smdp   --episodes 20000 --intervention-mode smdp
run D_naive  --episodes 20000 --intervention-mode naive
run E_drop   --episodes 20000 --intervention-mode drop
run M1_seal    --episodes 10000 --bt-disable seal_hide
run M2_brand   --episodes 10000 --bt-disable brand_spread
run M3_dodge   --episodes 10000 --bt-disable imminent_escape
run M4_stagger --episodes 10000 --bt-disable stagger_dps
run M5_aggro_bt --episodes 10000 --bt-extra aggro_taunt
echo "[ALL DONE] $(date)"
