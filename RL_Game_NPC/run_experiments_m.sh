#!/bin/bash
# M1~M5 경계 재배치 실험 — C/D/E 메인 레인과 병렬로 도는 보조 레인.
# (train_raid 가 500ep 마다 final.pt 를 저장하므로, 메인 레인이 나중에 도달해도
#  final.pt 존재 체크로 자동 스킵됨 — 중복 실행 방지)
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
run M1_seal    --episodes 10000 --bt-disable seal_hide
run M2_brand   --episodes 10000 --bt-disable brand_spread
run M3_dodge   --episodes 10000 --bt-disable imminent_escape
run M4_stagger --episodes 10000 --bt-disable stagger_dps
run M5_aggro_bt --episodes 10000 --bt-extra aggro_taunt
echo "[M-LANE DONE] $(date)"
