#!/bin/bash
# 행동별 면허 프로토콜 실험 캠페인 (논문 본 실험).
#   L-fix    : 고정 BT (기준선 — 규칙이 항상 개입)
#   L-adapt  : 제안 — 면허 발급 + 감사 + 회수
#   L-mono   : 발급만, 회수 없음 (단조 완화 — 회수의 기여를 분리하는 핵심 ablation)
#   L-none   : 보장 계층 전면 해제 (순수 RL + 기믹 보상)
# 중단되어도 final.pt 존재 시 스킵하므로 재실행하면 이어서 진행된다.
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
run L_adapt --bt-mode adaptive --gimmick-rewards on
run L_fix   --bt-mode fixed --gimmick-rewards on
echo "[LANE A DONE] $(date)"
