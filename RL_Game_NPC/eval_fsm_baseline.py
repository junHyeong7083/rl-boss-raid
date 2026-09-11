# -*- coding: utf-8 -*-
"""조건 A(규칙 기반 FSM NPC) 새 환경 기준 성능 — n=120, 성향 랜덤화 동일 프로토콜."""
import json, io, random
from src.raid import RaidEnv, RaidConfig, FSMNpcPolicy, PartyRole
from train_raid import PlayerModelWrapper

cfg = RaidConfig(); cfg.boss_max_hp = 55000
rng = random.Random(777)
by = {d: [0, 0] for d in cfg.player_model_weights}
wins = 0; kills = []; gs = gt = 0
for ep in range(120):
    env = RaidEnv(cfg, seed=9000 + ep)
    env.reset()
    player = PlayerModelWrapper(env, cfg.player_slot, cfg, rng)
    fsm = {uid: FSMNpcPolicy(env, uid) for uid in env.units
           if env.units[uid].role != PartyRole.DEALER}
    while not env.done:
        acts = {"p0": player.act()}
        for uid, pol in fsm.items():
            acts[f"p{uid}"] = int(pol.act())
        env.step(acts)
        for evs in env.step_events.values():
            for e in evs:
                t = e.get("type", "")
                if t.endswith("_success") and t.split("_")[0] in ("counter","stagger","seal","guard","parry","mechanic"):
                    gs += 1; gt += 1
                elif t.endswith("_fail") and t.split("_")[0] in ("counter","stagger","seal","parry","mechanic"):
                    gt += 1
    d = player.disposition
    by[d][1] += 1
    if env.victory:
        wins += 1; by[d][0] += 1; kills.append(env.current_step)
res = {"winrate": round(wins/120, 4),
       "kill_steps": round(sum(kills)/max(1,len(kills)), 1),
       "gimmick": round(gs/max(1,gt), 4),
       "disp": {d: round(w/max(1,n), 3) for d, (w, n) in by.items()}}
print(json.dumps(res, ensure_ascii=False))
with io.open("runs_paper/final_eval_fsm_A.json", "w", encoding="utf-8") as f:
    json.dump(res, f, ensure_ascii=False, indent=2)
