# -*- coding: utf-8 -*-
"""캠페인 전 조건 최종 평가 — 각 final.pt 로 n=120 (성향 랜덤화, 논문 표 소스)."""
import json, io, os, sys
from src.raid import RaidConfig
from train_raid import RaidTrainer, TrainCfg, RULE_REWARD_GROUP

CONDS = [
    ("C_smdp_ext", "smdp", set(), set()),
    ("E_drop", "drop", set(), set()),
    ("D_naive", "naive", set(), set()),
    ("M1_seal", "smdp", {"seal_hide"}, set()),
    ("M2_brand", "smdp", {"brand_spread"}, set()),
    ("M3_dodge", "smdp", {"imminent_escape"}, set()),
    ("M4_stagger", "smdp", {"stagger_dps"}, set()),
    ("M5_aggro_bt", "smdp", set(), {"aggro_taunt"}),
]
out = {}
for name, mode, dis, extra in CONDS:
    cfg = RaidConfig(); cfg.boss_max_hp = 55000
    tr = RaidTrainer(cfg, TrainCfg(), "cpu", seed=777,
                     intervention_mode=mode, bt_disabled=dis, bt_extra=extra)
    tr.load(os.path.join("runs_paper", name, "final.pt"))
    r = tr.evaluate(120)
    out[name] = {"winrate": round(r["winrate"], 4),
                 "kill_steps": round(r["avg_kill_steps"], 1),
                 "gimmick": round(r["gimmick_success_rate"], 4),
                 "bt_ratio": round(r["bt_fire_ratio"], 4),
                 "disp": {k: round(v, 3) for k, v in r["disp_winrate"].items()}}
    print(name, out[name], flush=True)
with io.open("runs_paper/final_eval_n120.json", "w", encoding="utf-8") as f:
    json.dump(out, f, ensure_ascii=False, indent=2)
print("saved runs_paper/final_eval_n120.json")
