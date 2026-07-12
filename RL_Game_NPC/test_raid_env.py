"""스모크 테스트 — RaidEnv ('혈월의 마수 군주') — 전투 시스템 대개편 반영.

검증:
  1. FSM 4인 런, 예외 없음 + 자연 패턴 발동 집계.
  2. 패턴 11종(YELLOW_BURST 포함) 강제 발동 확인.
  3. 폭주 돌진 - 기둥 충돌 그로기.
  4. 탱커 가드 딜타임.
  5. 카운터 저지(성공/facing·range miss/실패 강화돌진).
  6. 전멸기 '혈월 강림' LOS 은신 성공/실패 + 순차 기둥 폭발(pillar_explode).
  7. 딜러 방향 라인 평타 / Q·W 조준 설치기 / 궁극기.
  8. 패링(G) — YELLOW_BURST 파훼 성공/타이밍·facing 실패.
  9. 상시 스태거 게이지 — 명중 감소 / 파괴(stagger_break) / 그로기 중 미감소.
 10. 원형 아레나 HP 연동 축소(2턴 선형) + 스냅샷 arena_radius.
 11. 스냅샷 스키마 + 세션 제어 프로토콜.

실행:
  set PYTHONUTF8=1 && c:/Users/user/miniconda3/envs/rl_game_npc/python.exe test_raid_env.py
(print 에 이모지 사용 금지 — Windows cp949 콘솔)
"""
import json
import math
import os
import socket
import subprocess
import sys
import time

from src.raid import (
    RaidEnv, RaidConfig, FSMNpcPolicy, PartyRole, PatternID, RaidActionID,
    BTGimmickLayer, HybridPolicy, RewardComputer,
)

PASS = "[PASS]"
FAIL = "[FAIL]"
_failures = []


def check(cond, label):
    if cond:
        print(f"  {PASS} {label}")
    else:
        print(f"  {FAIL} {label}")
        _failures.append(label)


def _collect_events(env):
    return [e for evs in env.step_events.values() for e in evs]


def _stay(n=4):
    return {f"p{i}": int(RaidActionID.STAY) for i in range(n)}


# ─────────────────── 1. FSM 런 + 자연 패턴 발동 집계 ───────────────────

def test_smoke_run():
    print("\n== 1) FSM 런 + 자연 패턴 발동 집계 ==")
    env = RaidEnv(RaidConfig(), seed=7)
    env.reset(seed=7)
    policies = {i: FSMNpcPolicy(env, i) for i in range(4)}
    fired = {int(p): 0 for p in PatternID}
    prev_active = None
    exceptions = 0
    for t in range(1300):
        actions = {f"p{i}": policies[i].act() for i in range(4)}
        try:
            env.step(actions)
        except Exception as e:
            exceptions += 1
            print(f"  EXC at {t}: {e}")
            import traceback; traceback.print_exc()
            break
        ap = env.boss.active_pattern
        cur = int(ap.pattern_id) if ap is not None else None
        if cur is not None and cur != prev_active:
            fired[cur] += 1
        prev_active = cur
        if env.done:
            env.reset()
            policies = {i: FSMNpcPolicy(env, i) for i in range(4)}
    check(exceptions == 0, "예외 없음")
    print(f"  자연 발동 집계: { {PatternID(k).name: v for k, v in fired.items()} }")
    return fired


# ─────────────────── 2. 강제 발동으로 11종 전부 확인 ───────────────────

def test_all_patterns_forced():
    print("\n== 2) 패턴 11종 강제 발동 확인 ==")
    fired = {}
    for pid in PatternID:
        env = RaidEnv(RaidConfig(), seed=3)
        env.reset(seed=3)
        try:
            if pid == PatternID.SEAL_WIPE:
                env.force_seal()
            else:
                env.force_pattern(pid)
        except Exception as e:
            print(f"  EXC force {pid.name}: {e}")
            fired[pid] = False
            continue
        activated = (env.boss.active_pattern is not None
                     and int(env.boss.active_pattern.pattern_id) == int(pid))
        for _ in range(40):
            env.step(_stay())
            if env.boss.active_pattern is None or env.done:
                break
        fired[pid] = activated
        check(activated, f"{pid.name} 발동")
    return fired


# ─────────────────── 3. 폭주 돌진 - 기둥 그로기 ───────────────────

def test_rush_pillar_grog():
    print("\n== 3) 폭주 돌진(표식 추격) - 기둥 충돌 그로기 ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=11)
    env.reset(seed=11)
    env.config.pat_target_player_bias = 0.0
    env.boss.x, env.boss.y = 5.0, 10.0
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[tank_uid] = 1000.0
    env.units[tank_uid].x, env.units[tank_uid].y = 5.0, 2.0
    env.force_pattern(PatternID.FRENZY_RUSH)
    check(env.get_snapshot()["boss"].get("rush_target") == tank_uid,
          f"표식 rush_target=탱커 ({env.get_snapshot()['boss'].get('rush_target')})")
    hit = False
    moved_during_charge = False
    n = cfg.pat_rush_windup_turns + cfg.pat_rush_charge_turns + 3
    for _ in range(n):
        prev = (env.boss.x, env.boss.y)
        env.step(_stay())
        mv = math.hypot(env.boss.x - prev[0], env.boss.y - prev[1])
        if mv > 1e-3:
            moved_during_charge = True
        if mv >= 3.0:
            _failures.append("돌진 턴 이동이 snap 임계(3.0) 이상")
        if any(e.get("type") == "rush_pillar_hit" for e in _collect_events(env)):
            hit = True
            break
    check(moved_during_charge, "보스가 실제로 이동(추격 돌진)")
    check(hit, "돌진이 기둥에 충돌해 rush_pillar_hit 발생")
    check(env.boss.grog_turns > 0, "충돌 후 보스 그로기(딜타임)")
    check(any(not p.alive for p in env.pillars), "충돌한 기둥 파괴됨")


def test_rush_snapshot_fields():
    print("\n== 3b) 돌진 표식 스냅샷 (rush_target/rush_left) ==")
    env = RaidEnv(RaidConfig(), seed=71)
    env.reset(seed=71)
    snap0 = env.get_snapshot()
    check(snap0["boss"].get("rush_target") == -1, "비활성 시 rush_target=-1")
    check(snap0["boss"].get("rush_left") == 0, "비활성 시 rush_left=0")
    env.config.pat_target_player_bias = 0.0
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[tank_uid] = 1000.0
    env.force_pattern(PatternID.FRENZY_RUSH)
    env.step(_stay())
    snap = env.get_snapshot()
    check(snap["boss"].get("rush_target") == tank_uid,
          f"windup 중 rush_target=표식 uid ({snap['boss'].get('rush_target')})")
    check(snap["boss"].get("rush_left", 0) > 0, "windup 중 rush_left>0")
    has_line = any(s.get("kind") == "line" for tg in snap["telegraphs"] for s in tg["shapes"])
    check(has_line, "돌진 조준선(line) 텔레그래프 존재")


# ─────────────────── 4. 탱커 가드 딜타임 ───────────────────

def test_guard_deal_time():
    print("\n== 4) 탱커 가드 딜타임 ==")
    env = RaidEnv(RaidConfig(), seed=5)
    env.reset(seed=5)
    env.boss.x, env.boss.y = 10.0, 10.0
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[tank_uid] = 1000.0
    tank = env.units[tank_uid]
    tank.x, tank.y = 12.0, 10.0
    env.config.skill_cooldowns[int(RaidActionID.GUARD)] = 0
    env.force_pattern(PatternID.TRIPLE_CLAW)
    guard_ok = False
    for _ in range(16):
        actions = _stay()
        actions[f"p{tank_uid}"] = int(RaidActionID.GUARD)
        env.step(actions)
        if any(e.get("type") == "guard_success" for e in _collect_events(env)):
            guard_ok = True
            break
    check(guard_ok, "가드 성공 이벤트 발생")
    check(env.boss.stun_turns >= 0, "보스 경직 상태 필드 존재")


# ─────────────────── 5. 카운터 저지 (facing 조건 포함) ───────────────────

def _force_counter(env, dealer_x, dealer_y, boss_facing, dealer_facing):
    env.boss.x, env.boss.y = 10.0, 10.0
    dealer_uid = env.config.player_slot
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[dealer_uid] = 1000.0
    d = env.units[dealer_uid]
    d.x, d.y = dealer_x, dealer_y
    env.force_pattern(PatternID.COUNTER_RUSH)
    env.boss.facing = boss_facing
    d.facing = dealer_facing
    return dealer_uid


def test_counter():
    print("\n== 5) 카운터 저지 성공 (전방+근접+보스 바라봄) ==")
    env = RaidEnv(RaidConfig(), seed=9)
    env.reset(seed=9)
    duid = _force_counter(env, 12.0, 10.0, boss_facing=0.0, dealer_facing=math.pi)
    check(env.boss.counter_window_turns > 0, "카운터 창 활성")
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.COUNTER)
    env.step(actions)
    ok = any(e.get("type") == "counter_success" for e in _collect_events(env))
    check(ok, "counter_success 발생")
    check(env.boss.grog_turns > 0, "저지 후 보스 그로기")
    # 성공 그로기 7턴 보장(발동 턴 1틱 차감 → 6 관측)
    check(env.boss.grog_turns >= env.config.counter_success_grog_turns - 1,
          f"카운터 성공 그로기 ~7턴 ({env.boss.grog_turns})")


def test_counter_facing_miss():
    print("\n== 5b) 카운터 miss (보스 안 바라봄 → reason=facing) ==")
    env = RaidEnv(RaidConfig(), seed=91)
    env.reset(seed=91)
    # 전방+근접이나 딜러가 보스 반대(동쪽)를 바라봄
    duid = _force_counter(env, 12.0, 10.0, boss_facing=0.0, dealer_facing=0.0)
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.COUNTER)
    env.step(actions)
    ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "counter_miss"), None)
    check(ev is not None and ev.get("reason") == "facing",
          f"facing miss (reason={ev.get('reason') if ev else '?'})")


def test_counter_miss():
    print("\n== 5c) 카운터 miss (거리/각도) ==")
    full_cd = RaidConfig().skill_cooldowns[int(RaidActionID.COUNTER)]
    # (a) 거리 밖
    env = RaidEnv(RaidConfig(), seed=61)
    env.reset(seed=61)
    duid = _force_counter(env, 3.0, 10.0, boss_facing=math.pi, dealer_facing=math.pi)
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.COUNTER)
    env.step(actions)
    ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "counter_miss"), None)
    check(ev is not None and ev.get("reason") == "range",
          f"거리 밖 reason=range ({ev.get('reason') if ev else '?'})")
    cd_left = env.units[duid].cooldowns.get(int(RaidActionID.COUNTER), 0)
    check(0 < cd_left <= full_cd // 2, f"miss 시 절반 쿨다운 (남은 {cd_left} <= {full_cd//2})")
    # (b) 각도 밖 (사거리 내·보스는 바라보나 보스 전방이 아님)
    env = RaidEnv(RaidConfig(), seed=62)
    env.reset(seed=62)
    duid = _force_counter(env, 12.0, 10.0, boss_facing=math.pi, dealer_facing=math.pi)
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.COUNTER)
    env.step(actions)
    ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "counter_miss"), None)
    check(ev is not None and ev.get("reason") == "angle",
          f"각도 밖 reason=angle ({ev.get('reason') if ev else '?'})")


def test_counter_fail_rush():
    print("\n== 5d) 카운터 실패 -> 강화 돌진 ==")
    env = RaidEnv(RaidConfig(), seed=13)
    env.reset(seed=13)
    env.force_pattern(PatternID.COUNTER_RUSH)
    fail_seen = False
    enhanced = False
    for _ in range(env.config.counter_window_turns + 2):
        env.step(_stay())
        if any(e.get("type") == "counter_fail" for e in _collect_events(env)):
            fail_seen = True
        ap = env.boss.active_pattern
        if ap is not None and ap.pattern_id == PatternID.FRENZY_RUSH:
            if ap.steps and ap.steps[0].extra.get("enhanced"):
                enhanced = True
        if fail_seen and enhanced:
            break
    check(fail_seen, "counter_fail 발생")
    check(enhanced, "실패 시 강화 돌진(enhanced) 발동")


# ─────────────────── 6. 전멸기 LOS + 순차 기둥 폭발 ───────────────────

def _fresh_env_for_aim(seed):
    env = RaidEnv(RaidConfig(), seed=seed)
    env.reset(seed=seed)
    env.boss.x, env.boss.y = 10.0, 10.0
    env.boss.active_pattern = None
    env.boss.invuln_turns = 0
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[tank_uid] = 1000.0
    return env


def _behind_pillar_pos(env, pillar):
    bx, by = env.boss.x, env.boss.y
    dx = pillar.x - bx; dy = pillar.y - by
    d = math.hypot(dx, dy) or 1.0
    return (pillar.x + dx / d * (pillar.radius + 0.4),
            pillar.y + dy / d * (pillar.radius + 0.4))


def _seal_survivor(env):
    """시계방향 파괴 순서(각도 내림차순)의 꼬리 = 최종 생존 기둥."""
    cx, cy = env.config.arena_center
    order = sorted(env.pillars, key=lambda q: math.atan2(q.y - cy, q.x - cx), reverse=True)
    return order[-1]


def test_seal_success():
    print("\n== 6) 전멸기 LOS 은신 성공 + 순차 기둥 폭발 ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=21)
    env.reset(seed=21)
    env.boss.x, env.boss.y = 10.0, 10.0
    # 전원 "최종 생존 기둥" 뒤로 모음 (다른 기둥은 폭발해도 무관)
    surv = _seal_survivor(env)
    hx, hy = _behind_pillar_pos(env, surv)
    for i, u in enumerate(env.units.values()):
        u.x, u.y = hx + 0.1 * i, hy
    env.force_seal()
    cine_start = any(e.get("type") == "cinematic_start" for e in _collect_events(env))
    success = cine_end = explode_seen = False
    for _ in range(cfg.seal_wind_up_turns + 2):
        env.step(_stay())
        evs = _collect_events(env)
        if any(e.get("type") == "seal_success" for e in evs):
            success = True
        if any(e.get("type") == "cinematic_end" and e.get("success") for e in evs):
            cine_end = True
        if any(e.get("type") == "pillar_explode" for e in evs):
            explode_seen = True
        if env.boss.active_pattern is None:
            break
    check(cine_start, "cinematic_start 이벤트")
    check(explode_seen, "순차 기둥 폭발(pillar_explode) 발생")
    n_alive = sum(1 for p in env.pillars if p.alive)
    check(n_alive >= 1, f"최소 1개 기둥 생존 ({n_alive})")
    check(success, "seal_success (전원 은신)")
    check(cine_end, "cinematic_end success=True")
    check(all(u.alive for u in env.units.values()), "전원 생존")
    check(env.boss.grog_turns > 0, "성공 후 보스 그로기(딜타임)")


def test_seal_fail():
    print("\n== 6b) 전멸기 노출 실패(전멸) ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=22)
    env.reset(seed=22)
    env.boss.x, env.boss.y = 10.0, 10.0
    for u in env.units.values():
        u.x, u.y = 11.0, 10.0
    env.force_seal()
    cine_end_fail = False
    for _ in range(cfg.seal_wind_up_turns + 2):
        env.step(_stay())
        evs = _collect_events(env)
        if any(e.get("type") == "seal_fail" for e in evs):
            cine_end_fail = any(e.get("type") == "cinematic_end" and not e.get("success") for e in evs)
        if env.boss.active_pattern is None:
            break
    check(all(not u.alive for u in env.units.values()), "노출 시 전원 즉사(wipe)")
    check(cine_end_fail, "cinematic_end success=False")


# ─────────────────── 7. 딜러 방향 라인 평타 / Q·W / 궁극기 ───────────────────

def _dealer_cast(env, action, aim):
    dealer_uid = env.config.player_slot
    actions = _stay(); actions[f"p{dealer_uid}"] = int(action)
    aim_points = {f"p{dealer_uid}": aim} if aim is not None else None
    env.step(actions, aim_points=aim_points)
    casts = [e for e in env.step_events.get(dealer_uid, [])
             if e.get("type") == "player_skill_cast"]
    return casts[0] if casts else None


def test_basic_lineshot():
    print("\n== 7) 딜러 평타 = 방향 라인 스킬샷 ==")
    cfg = RaidConfig()
    # (a) 방향으로 보스 교차 -> 명중, tx/ty = 라인 끝점
    env = _fresh_env_for_aim(101)
    d = env.units[env.config.player_slot]
    d.x, d.y = 5.0, 10.0
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ATTACK_BASIC, (20.0, 10.0))   # +x 방향(보스는 5m 동쪽)
    check(ev is not None and ev.get("skill") == "basic", "basic player_skill_cast 이벤트")
    check(ev is not None and ev.get("hit") is True, "보스 원과 교차 명중 (hit=True)")
    check(env.boss.hp < hp0, "명중 시 보스 HP 감소")
    endx = d.x + cfg.aim_basic_range
    check(ev is not None and abs(ev.get("tx") - endx) < 1e-4 and abs(ev.get("ty") - 10.0) < 1e-4,
          f"tx/ty = 라인 끝점 ({ev.get('tx') if ev else '?'},{ev.get('ty') if ev else '?'} == {endx},10)")
    check(abs(d.facing - 0.0) < 1e-6, "조준 방향으로 facing 갱신")
    # (b) 사거리 내라도 방향이 빗나가면 미명중 (보스 반대 방향 조준)
    env = _fresh_env_for_aim(102)
    d = env.units[env.config.player_slot]
    d.x, d.y = 5.0, 10.0
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ATTACK_BASIC, (5.0, 20.0))   # +y 방향(보스는 동쪽) -> 빗나감
    check(ev is not None and ev.get("hit") is False, "다른 방향 라인은 빗나감 (hit=False)")
    check(env.boss.hp == hp0, "빗나감 시 보스 HP 불변")
    # (c) 방향만 사용: 사거리 밖 지점을 찍어도 그 방향으로 고정 길이 라인 -> 근접 보스 명중
    env = _fresh_env_for_aim(103)
    d = env.units[env.config.player_slot]
    d.x, d.y = 8.0, 10.0     # 보스와 2m
    ev = _dealer_cast(env, RaidActionID.ATTACK_BASIC, (100.0, 10.0))  # 멀리(방향만) +x
    check(ev is not None and ev.get("hit") is True, "사거리 밖 조준점이라도 방향으로 근접 명중")
    endx = 8.0 + cfg.aim_basic_range
    check(ev is not None and abs(ev.get("tx") - endx) < 1e-4, "라인 끝점은 고정 길이(방향만 사용)")


def test_aim_skills():
    print("\n== 7b) 딜러 조준 설치기 Q/W ==")
    cfg = RaidConfig()
    # (a) Q 명중
    env = _fresh_env_for_aim(41)
    env.units[env.config.player_slot].x, env.units[env.config.player_slot].y = 5.0, 10.0
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ATTACK_SKILL, (10.0, 10.0))
    check(ev is not None and ev.get("skill") == "skill", "Q player_skill_cast(skill)")
    check(ev is not None and ev.get("hit") is True, "Q 보스 명중")
    check(env.boss.hp < hp0, "Q 명중 시 HP 감소")
    check(ev is not None and abs(ev.get("radius", 0) - cfg.aim_q_radius) < 1e-6, "Q 반경 필드")
    # (b) Q 사거리 클램프 -> 빗나감
    env = _fresh_env_for_aim(42)
    env.boss.x, env.boss.y = 15.0, 10.0
    env.units[env.config.player_slot].x, env.units[env.config.player_slot].y = 2.0, 10.0
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ATTACK_SKILL, (15.0, 10.0))
    check(ev is not None and abs(ev.get("tx", 0) - 9.0) < 1e-4, f"Q 조준점 클램프 (tx={ev.get('tx') if ev else '?'})")
    check(ev is not None and ev.get("hit") is False, "Q 클램프 후 빗나감")
    check(env.boss.hp == hp0, "빗나감 시 HP 불변")
    # (c) W 명중 대형 피해
    env = _fresh_env_for_aim(44)
    env.units[env.config.player_slot].x, env.units[env.config.player_slot].y = 4.0, 10.0
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.SKILL_2, (10.0, 10.0))
    check(ev is not None and ev.get("skill") == "skill2", "W player_skill_cast(skill2)")
    check(ev is not None and ev.get("hit") is True, "W 보스 명중")
    check(hp0 - env.boss.hp >= cfg.aim_w_damage - cfg.boss_defense - 1, f"W 대형 피해 ({hp0-env.boss.hp})")
    # (d) aim 미지정 -> 보스 자동 조준
    env = _fresh_env_for_aim(45)
    env.units[env.config.player_slot].x, env.units[env.config.player_slot].y = 5.0, 10.0
    ev = _dealer_cast(env, RaidActionID.ATTACK_SKILL, None)
    check(ev is not None and ev.get("hit") is True, "aim 미지정 시 보스 자동 조준 명중")
    # (e) W 딜러 전용
    env = _fresh_env_for_aim(46)
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    actions = _stay(); actions[f"p{tank_uid}"] = int(RaidActionID.SKILL_2)
    env.step(actions)
    check(any(e.get("type") == "invalid_action" for e in env.step_events.get(tank_uid, [])),
          "W 타 역할 사용 시 invalid_action")
    # (f) 딜러 cooldowns 키 = skill/skill2/counter/dash/ult/parry
    keys = set(env.get_snapshot()["units"][env.config.player_slot]["cooldowns"].keys())
    check(keys == {"skill", "skill2", "counter", "dash", "ult", "parry"},
          f"딜러 cooldowns 키 {sorted(keys)}")


def test_ultimate():
    print("\n== 7c) 딜러 궁극기 '혈월 처형' ==")
    cfg = RaidConfig()
    # (a) 명중 + 대형 피해 + 쿨다운 + 상시 스태거 대량 기여(ULTIMATE=80)
    env = _fresh_env_for_aim(81)
    env.units[env.config.player_slot].x, env.units[env.config.player_slot].y = 5.0, 10.0
    g0 = env.boss.stagger_gauge
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ULTIMATE, (10.0, 10.0))
    check(ev is not None and ev.get("skill") == "ult", "R player_skill_cast(ult)")
    check(ev is not None and ev.get("hit") is True, "궁극 보스 명중")
    check(ev is not None and "crit" in ev, "궁극 crit 필드")
    check(hp0 - env.boss.hp >= cfg.aim_ult_damage - cfg.boss_defense - 1, f"궁극 대형 피해 ({hp0-env.boss.hp})")
    ult_amt = cfg.stagger_values[int(RaidActionID.ULTIMATE)]
    contrib = next((e for e in env.step_events.get(env.config.player_slot, [])
                    if e.get("type") == "stagger_contribute"), None)
    check(contrib is not None and abs(contrib.get("amount", 0) - ult_amt) < 1e-6,
          f"궁극 스태거 기여 {ult_amt} (실제 {contrib.get('amount') if contrib else '?'})")
    check(abs(env.boss.stagger_gauge - (g0 - ult_amt)) < 1e-6, f"게이지 {ult_amt} 차감 (={env.boss.stagger_gauge:.0f})")
    ult_cd_full = cfg.skill_cooldowns[int(RaidActionID.ULTIMATE)]
    dcd = env.units[env.config.player_slot].cooldowns.get(int(RaidActionID.ULTIMATE), 0)
    check(dcd >= ult_cd_full - 1, f"궁극 쿨다운 적용 ({dcd}/{ult_cd_full})")
    # (b) 딜러 전용
    env = _fresh_env_for_aim(83)
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    actions = _stay(); actions[f"p{tank_uid}"] = int(RaidActionID.ULTIMATE)
    env.step(actions)
    check(any(e.get("type") == "invalid_action" for e in env.step_events.get(tank_uid, [])),
          "궁극 타 역할 사용 시 invalid_action")


def test_dash():
    print("\n== 7d) 대시(회피 기동기) ==")
    # (a) 조준 방향으로 이동 + facing + 쿨다운
    env = _fresh_env_for_aim(51)
    duid = env.config.player_slot
    d = env.units[duid]
    d.x, d.y = 10.0, 4.0
    x0, y0 = d.x, d.y
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.DASH)
    env.step(actions, aim_points={f"p{duid}": (18.0, 4.0)})
    ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "dash"), None)
    check(ev is not None, "dash 이벤트 방출")
    moved = math.hypot(d.x - x0, d.y - y0)
    check(abs(moved - env.config.dash_distance) < 0.6, f"이동 ~= dash_distance ({moved:.2f})")
    check(d.x > x0 + 1.0, "조준(+x) 방향 이동")
    check(abs(d.facing - 0.0) < 1e-6, "대시 방향으로 facing 갱신")
    check(d.cooldowns.get(int(RaidActionID.DASH), 0) > 0, "대시 쿨다운")
    # (b) 보스 관통 금지
    env = _fresh_env_for_aim(52)
    duid = env.config.player_slot
    d = env.units[duid]
    env.boss.x, env.boss.y = 10.0, 10.0
    d.x, d.y = 7.0, 10.0
    x0 = d.x
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.DASH)
    env.step(actions, aim_points={f"p{duid}": (12.0, 10.0)})
    check(0.0 <= d.x - x0 < env.config.dash_distance, f"보스 앞 정지 ({d.x-x0:.2f})")
    # (c) aim 미지정 -> 보스 반대 방향
    env = _fresh_env_for_aim(53)
    duid = env.config.player_slot
    d = env.units[duid]
    env.boss.x, env.boss.y = 10.0, 10.0
    d.x, d.y = 8.0, 10.0
    x0 = d.x
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.DASH)
    env.step(actions, aim_points=None)
    check(d.x < x0 - 0.5, "aim 미지정 시 보스 반대 방향 대시")


# ─────────────────── 8. 패링(G) — YELLOW_BURST 파훼 ───────────────────

def _setup_yellow_burst(env, dealer_pos, dealer_facing):
    env.config.pat_target_player_bias = 1.0   # 확실히 딜러 조준
    duid = env.config.player_slot
    d = env.units[duid]
    d.x, d.y = dealer_pos
    for u in env.units.values():
        if u.uid != duid:
            u.x, u.y = 1.0, 1.0     # 다른 유닛은 원 밖으로
    env.force_pattern(PatternID.YELLOW_BURST)
    d.facing = dealer_facing
    return duid


def test_parry():
    print("\n== 8) 패링(G) — 노란 확산 원 파훼 ==")
    cfg = RaidConfig()
    # (a) 성공: 창 안 + 원 안 + 보스 바라봄 -> parry_success + 무피해 + 그로기
    env = _fresh_env_for_aim(201)
    duid = _setup_yellow_burst(env, (7.0, 10.0), dealer_facing=0.0)  # 보스(10,10) 동쪽에서 서쪽 바라봄? -> dir to boss=0(east)
    d = env.units[duid]
    succ = dmg = False
    for _ in range(10):
        ap = env.boss.active_pattern
        a = int(RaidActionID.PARRY) if (ap and ap.turns_remaining <= cfg.parry_window_turns) else int(RaidActionID.STAY)
        actions = _stay(); actions[f"p{duid}"] = a
        env.step(actions)
        for e in env.step_events.get(duid, []):
            if e.get("type") == "parry_success":
                succ = True
            if e.get("type") == "damage_taken":
                dmg = True
        if env.boss.active_pattern is None:
            break
    check(succ, "parry_success 발생")
    check(not dmg, "패링 성공 시 딜러 무피해")
    check(env.boss.grog_turns > 0, "패링 성공 후 보스 그로기")
    # (b) 실패 timing: 창 밖(초반)에 PARRY -> parry_fail reason=timing, 쿨 절반
    env = _fresh_env_for_aim(202)
    duid = _setup_yellow_burst(env, (7.0, 10.0), dealer_facing=0.0)
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.PARRY)
    env.step(actions)   # turns_remaining ~7 (창 밖)
    ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "parry_fail"), None)
    check(ev is not None and ev.get("reason") == "timing", f"창 밖 parry_fail timing ({ev.get('reason') if ev else '?'})")
    full_cd = cfg.skill_cooldowns[int(RaidActionID.PARRY)]
    cd_left = env.units[duid].cooldowns.get(int(RaidActionID.PARRY), 0)
    check(0 < cd_left <= full_cd // 2 + 1, f"timing 실패 쿨 절반 ({cd_left})")
    # (c) 실패 facing: 창 안·원 안이나 보스 안 바라봄 -> reason=facing
    env = _fresh_env_for_aim(203)
    duid = _setup_yellow_burst(env, (7.0, 10.0), dealer_facing=0.0)
    facing_fail = False
    for _ in range(10):
        ap = env.boss.active_pattern
        if ap and ap.turns_remaining <= cfg.parry_window_turns:
            env.units[duid].facing = math.pi   # 보스 반대(서쪽) 바라봄
            actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.PARRY)
            env.step(actions)
            ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "parry_fail"), None)
            if ev is not None:
                facing_fail = (ev.get("reason") == "facing")
            break
        env.step(_stay())
    check(facing_fail, "미조준 PARRY -> parry_fail reason=facing")
    # (d) 미파훼 시 원 안 유닛 피해 55
    env = _fresh_env_for_aim(204)
    duid = _setup_yellow_burst(env, (7.0, 10.0), dealer_facing=0.0)
    d = env.units[duid]
    hp0 = d.hp
    for _ in range(10):
        env.step(_stay())   # 아무도 패링 안 함
        if env.boss.active_pattern is None:
            break
    check(d.hp < hp0, f"미파훼 시 원 안 유닛 피해 (dmg={hp0-d.hp})")


# ─────────────────── 9. 상시 스태거 게이지 ───────────────────

def test_stagger_gauge():
    print("\n== 9) 상시 스태거 게이지 — 감소/파괴/그로기 ==")
    cfg = RaidConfig()
    env = _fresh_env_for_aim(301)
    d = env.units[env.config.player_slot]
    d.x, d.y = 6.0, 10.0
    check(abs(env.boss.stagger_gauge - cfg.stagger_max) < 1e-6, "게이지 stagger_max 에서 시작")
    # 평타(라인샷) 반복 -> 감소량 3, 결국 파괴(stagger_break) + 그로기 + max 리셋
    broke = False
    contrib_amt = None
    for _ in range(150):
        env.boss.x, env.boss.y = 10.0, 10.0   # 보스 고정(명중 보장) — 게이지 감소 검증에 집중
        env.boss.grog_turns = 0
        ev = _dealer_cast(env, RaidActionID.ATTACK_BASIC, (10.0, 10.0))
        for e in env.step_events.get(env.config.player_slot, []):
            if e.get("type") == "stagger_contribute" and contrib_amt is None:
                contrib_amt = e.get("amount")
            if e.get("type") == "stagger_break":
                broke = True
        if broke:
            break
    check(contrib_amt == cfg.stagger_values[int(RaidActionID.ATTACK_BASIC)],
          f"평타 스태거 감소량 = {cfg.stagger_values[int(RaidActionID.ATTACK_BASIC)]} ({contrib_amt})")
    check(broke, "게이지 0 도달 시 stagger_break 발생")
    check(env.boss.stagger_active, "파괴 후 stagger_active(그로기 딜타임)=True")
    check(env.boss.grog_turns > 0, "파괴 후 보스 그로기")
    check(abs(env.boss.stagger_gauge - cfg.stagger_max) < 1e-6, "파괴 후 게이지 max 리셋")
    # 그로기 중엔 게이지 감소 없음
    g_before = env.boss.stagger_gauge
    _dealer_cast(env, RaidActionID.ATTACK_BASIC, (10.0, 10.0))
    check(abs(env.boss.stagger_gauge - g_before) < 1e-6, "그로기 중 게이지 감소 없음")
    # 스냅샷 상시 노출
    snap = env.get_snapshot()["boss"]
    check("stagger_gauge" in snap and "stagger_max" in snap, "스냅샷 stagger_gauge/stagger_max 상시 노출")


# ─────────────────── 10. 원형 아레나 HP 연동 축소 ───────────────────

def test_arena_shrink():
    print("\n== 10) 원형 아레나 HP 연동 축소 ==")
    cfg = RaidConfig()
    env = _fresh_env_for_aim(401)
    r0 = env.arena_radius
    check(abs(r0 - cfg.arena_radius_tiers[0]) < 1e-6, f"초기 반경 = 티어0 ({r0})")
    check("arena_radius" in env.get_snapshot()["boss"], "스냅샷 boss.arena_radius 포함")
    # HP 70% -> 티어1(8.8) 목표, 2턴 선형 축소
    env.boss.hp = int(cfg.boss_max_hp * 0.70)
    radii = []
    for _ in range(3):
        env.step(_stay())
        radii.append(round(env.arena_radius, 3))
    target = cfg.arena_radius_tiers[1]
    check(radii[-1] <= target + 1e-6 and radii[-1] >= target - 1e-6, f"2턴 뒤 목표 반경 도달 ({radii})")
    check(radii[0] > target + 1e-6, "1턴차엔 중간값(선형 축소)")
    # 경계 밖 유닛 안쪽으로 밀림
    env2 = _fresh_env_for_aim(402)
    u = env2.units[1]
    u.x, u.y = 10.0, 19.5      # 경계 밖(중심에서 9.5)
    env2.boss.hp = int(cfg.boss_max_hp * 0.20)   # 티어3(7.6)
    for _ in range(3):
        env2.step(_stay())
    cx, cy = cfg.arena_center
    check(math.hypot(u.x - cx, u.y - cy) <= env2.arena_radius - u.radius + 1e-3,
          "경계 밖 유닛이 아레나 안으로 밀림")


# ─────────────────── 11. 스냅샷 스키마 + 프로토콜 ───────────────────

def test_snapshot_schema():
    print("\n== 11) 스냅샷 스키마 ==")
    env = RaidEnv(RaidConfig(), seed=31)
    env.reset(seed=31)
    snap = env.get_snapshot()
    b = snap["boss"]
    check("facing" in b, "boss.facing")
    check("counter_window" in b, "boss.counter_window")
    check("arena_radius" in b, "boss.arena_radius")
    check("stagger_gauge" in b and "stagger_max" in b, "boss.stagger_gauge/stagger_max")
    check("pillars" in snap and len(snap["pillars"]) == 4, "pillars 목록(4)")
    check("facing" in snap["units"][0], "unit.facing")
    check("cooldowns" in snap["units"][0], "unit.cooldowns")
    # donut shape (대지 분쇄 2스텝째)
    env.force_pattern(PatternID.EARTH_CRUSH)
    donut_seen = False
    for _ in range(10):
        env.step(_stay())
        for tg in env.get_snapshot()["telegraphs"]:
            for s in tg["shapes"]:
                if s.get("kind") == "donut":
                    donut_seen = True
        if env.boss.active_pattern is None:
            break
    check(donut_seen, "donut kind 스냅샷 출력")


def test_player_pos_stream():
    print("\n== 10b) 플레이어 위치 스트림 채택(클라이언트 권위) ==")
    cfg = RaidConfig()
    duid = cfg.player_slot
    max_d = cfg.player_speed_cap * cfg.turn_seconds
    # (a) 상한 이내 보고 → 그대로 채택
    env = _fresh_env_for_aim(501)
    d = env.units[duid]
    d.x, d.y = 10.0, 13.0
    env.step(_stay(), player_pos=(10.0, 13.0 + max_d * 0.5))   # 상한 절반 이동
    check(abs(d.x - 10.0) < 1e-6 and abs(d.y - (13.0 + max_d * 0.5)) < 1e-6,
          f"상한 이내 보고 채택 ({d.x:.2f},{d.y:.2f})")
    # (b) 과속 보고 → 상한 거리로 클램프
    env = _fresh_env_for_aim(502)
    d = env.units[duid]
    d.x, d.y = 10.0, 10.0
    env.step(_stay(), player_pos=(10.0, 10.0 + 5.0))   # 5.0 >> max_d
    moved = math.hypot(d.x - 10.0, d.y - 10.0)
    check(abs(moved - max_d) < 1e-6, f"과속 보고 상한 클램프 (moved={moved:.2f} == {max_d:.2f})")
    # (c) 경계 밖 보고 → 아레나 안으로 클램프
    env = _fresh_env_for_aim(503)
    d = env.units[duid]
    cx, cy = cfg.arena_center
    d.x, d.y = cx, cy + env.arena_radius - 0.2     # 경계 근처
    env.step(_stay(), player_pos=(cx, cy + env.arena_radius + 5.0))   # 한참 밖
    dist_c = math.hypot(d.x - cx, d.y - cy)
    check(dist_c <= env.arena_radius - d.radius + 1e-6, f"경계 밖 보고 아레나 클램프 (r={dist_c:.2f})")
    # (d) 사람 조종 중 MOVE 액션은 위치에 영향 없음(스트림이 소유) — 보스에서 떨어진 곳
    env = _fresh_env_for_aim(504)
    env.boss.x, env.boss.y = 15.0, 15.0
    d = env.units[duid]
    d.x, d.y = 8.0, 8.0
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.MOVE_UP)
    env.step(actions, player_pos=(8.5, 8.0))
    check(abs(d.x - 8.5) < 1e-6 and abs(d.y - 8.0) < 1e-6,
          f"MOVE 액션 무시·스트림 위치 채택 ({d.x:.2f},{d.y:.2f})")
    # (e) 보고 없으면 위치 유지 + facing 델타 갱신 — 보스에서 떨어진 곳
    env = _fresh_env_for_aim(505)
    env.boss.x, env.boss.y = 15.0, 15.0
    d = env.units[duid]
    d.x, d.y = 8.0, 8.0
    env.step(_stay(), player_pos=None)   # 보고 없음 → MOVE 도 없으니 제자리
    check(abs(d.x - 8.0) < 1e-6 and abs(d.y - 8.0) < 1e-6, "보고 없으면 위치 유지")
    env.step(_stay(), player_pos=(9.0, 8.0))
    check(abs(d.facing - 0.0) < 1e-6, "채택 시 이동 델타로 facing 갱신(+x → 0)")
    # (f) 사람 조종 중 DASH: 위치 이동 없음 + dash 이벤트/쿨다운 유지 — 보스에서 떨어진 곳
    env = _fresh_env_for_aim(506)
    env.boss.x, env.boss.y = 15.0, 15.0
    d = env.units[duid]
    d.x, d.y = 8.0, 8.0
    actions = _stay(); actions[f"p{duid}"] = int(RaidActionID.DASH)
    env.step(actions, player_pos=(8.3, 8.0))
    dash_ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "dash"), None)
    check(dash_ev is not None, "사람 조종 DASH dash 이벤트 방출")
    check(d.cooldowns.get(int(RaidActionID.DASH), 0) > 0, "사람 조종 DASH 쿨다운 유지")
    check(abs(d.x - 8.3) < 1e-6, "DASH 이동 효과 무시(위치=스트림 채택값)")
    # (g) 보스 몸통 안 보고 → 몸통 밖으로 밀림
    env = _fresh_env_for_aim(507)
    d = env.units[duid]
    env.boss.x, env.boss.y = 10.0, 10.0
    d.x, d.y = 10.0 + cfg.boss_radius + d.radius, 10.0   # 보스 표면 근처
    env.step(_stay(), player_pos=(10.0, 10.0))   # 보스 중심으로 보고
    surf = math.hypot(d.x - 10.0, d.y - 10.0)
    check(surf >= cfg.boss_radius + d.radius - 0.1, f"보스 몸통 밖으로 밀림 (d={surf:.2f})")


def test_protocol_pos_parse():
    print("\n== 11d) 프로토콜 px/py 파싱 (SessionLink) ==")
    import raid_streamer
    link = raid_streamer.SessionLink({}, port=59998)
    check(link.get_player_pos() is None, "초기 위치 보고 None")
    link._handle_line(b'{"px": 12.5, "py": 7.25}')
    check(link.get_player_pos() == (12.5, 7.25), f"px/py 파싱 ({link.get_player_pos()})")
    # 최신값만 유효(덮어쓰기), 소비하지 않음(유지)
    link._handle_line(b'{"px": 3.0, "py": 4.0}')
    check(link.get_player_pos() == (3.0, 4.0), "최신값 덮어쓰기")
    check(link.get_player_pos() == (3.0, 4.0), "소비하지 않음(유지)")
    # px/py 라인은 action 을 건드리지 않음
    a, aim = link.get_action()
    check(a == int(RaidActionID.STAY) and aim is None, "px/py 라인은 action 불변")
    link.reset_player_pos()
    check(link.get_player_pos() is None, "reset_player_pos 후 None")


def test_protocol_aim_parse():
    print("\n== 11b) 프로토콜 tx/ty 파싱 (SessionLink) ==")
    import raid_streamer
    link = raid_streamer.SessionLink({}, port=59999)
    link._handle_line(b'{"action": 10, "tx": 12.5, "ty": 7.25}')
    a, aim = link.get_action()
    check(a == 10 and aim == (12.5, 7.25), f"tx/ty 파싱 (action={a}, aim={aim})")
    link._handle_line(b'{"action": 9}')
    a, aim = link.get_action()
    check(a == 9 and aim is None, "tx/ty 없는 형식 하위 호환 (aim=None)")
    a, aim = link.get_action()
    check(a == int(RaidActionID.STAY) and aim is None, "1회 소비 후 STAY/None 초기화")


def cfg_obs_dim():
    return RaidConfig().obs_size


def test_session_protocol():
    print("\n== 11c) 세션 제어 프로토콜 (ready->start->episode_end) ==")
    here = os.path.dirname(os.path.abspath(__file__))
    env_vars = dict(os.environ)
    env_vars["PYTHONUTF8"] = "1"
    proc = subprocess.Popen(
        [sys.executable, "raid_streamer.py", "--mode", "fsm",
         "--no-player", "--turn-interval", "0", "--max-steps", "40"],
        cwd=here, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        env=env_vars, text=True,
    )
    try:
        time.sleep(2.0)
        sock = None
        for _ in range(20):
            try:
                sock = socket.create_connection(("127.0.0.1", 5006), timeout=1.0)
                break
            except OSError:
                time.sleep(0.3)
        check(sock is not None, "TCP 5006 접속")
        if sock is None:
            return
        sock.settimeout(8.0)
        buf = b""

        def read_msg():
            nonlocal buf
            while b"\n" not in buf:
                data = sock.recv(1024)
                if not data:
                    return None
                buf += data
            line, buf = buf.split(b"\n", 1)
            return json.loads(line.decode("utf-8"))

        m = read_msg()
        check(m is not None and m.get("type") == "ready", f"ready 수신 ({m})")
        check(m is not None and m.get("obs_dim") == cfg_obs_dim(), "ready.obs_dim 일치")
        sock.sendall(b'{"cmd":"start"}\n')
        m = read_msg()
        check(m is not None and m.get("type") == "started", f"started 수신 ({m})")
        end = None
        for _ in range(5):
            m = read_msg()
            if m is None:
                break
            if m.get("type") == "episode_end":
                end = m
                break
        check(end is not None, f"episode_end 수신 ({end})")
        if end:
            check(end.get("result") in ("victory", "wipe", "timeout"), "result 필드 유효")
        sock.sendall(b'{"cmd":"quit"}\n')
        sock.close()
        logdir = os.path.join(here, "session_logs")
        has_log = os.path.isdir(logdir) and any(f.startswith("session_") for f in os.listdir(logdir))
        check(has_log, "session_logs/*.jsonl 생성")
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=5)
        except Exception:
            proc.kill()


# ─────────────────── BT 계층 (Layer 1) ───────────────────

def test_bt_seal():
    print("\n== BT) 전멸기 은신 규칙 ==")
    env = RaidEnv(RaidConfig(), seed=11)
    env.reset(seed=11)
    env.boss.start_seal(env._build_ctx())
    bt = BTGimmickLayer(env, 1)   # 탱커
    a = bt.act()
    check(a is not None, "SEAL 활성 시 BT fire (int 반환)")
    check(bt.last_decision["rule"] == "seal_hide", "규칙명 seal_hide")


def test_bt_imminent_escape():
    print("\n== BT) 임박 위험 회피 규칙 ==")
    env = RaidEnv(RaidConfig(), seed=12)
    env.reset(seed=12)
    b = env.boss
    b.start_step_pattern(PatternID.EARTH_CRUSH, env._build_ctx())
    ap = b.active_pattern
    ap.turns_remaining = 1                      # 임박(≤2턴)
    healer = env.units[2]
    healer.x, healer.y = b.x + 2.5, b.y         # 중심 원(r=3) 안, 가장자리 근처
    bt = BTGimmickLayer(env, 2)
    a = bt.act()
    check(a is not None and int(a) in range(env.config.num_actions),
          "임박 텔레그래프 안 → BT 탈출 액션")
    check(bt.last_decision["rule"] == "imminent_escape", "규칙명 imminent_escape")
    # 여유(잔여 5턴)면 fire 안 함 → RL 위임(None)
    ap.turns_remaining = 5
    bt2 = BTGimmickLayer(env, 2)
    check(bt2.act() is None, "잔여 5턴(여유)이면 BT pass(None) → RL 위임")


def test_bt_brand():
    print("\n== BT) 붉은 낙인 산개 규칙 ==")
    env = RaidEnv(RaidConfig(), seed=13)
    env.reset(seed=13)
    b = env.boss
    b.start_step_pattern(PatternID.CRIMSON_BRAND, env._build_ctx())
    ap = b.active_pattern
    marked = ap.target_uids[0] if ap.target_uids else 1
    if marked == env.config.player_slot:       # 딜러면 NPC 로 강제 교체(BT 는 NPC 대상)
        marked = 1
        ap.target_uids = [1]
        ap.steps[ap.step_index].extra["target_uid"] = 1
    bt = BTGimmickLayer(env, marked)
    a = bt.act()
    check(a is not None, "낙인 대상 NPC → BT fire (산개)")
    check(bt.last_decision["rule"] == "brand_spread", "규칙명 brand_spread")


def test_bt_stagger():
    print("\n== BT) 무력화 그로기 집중 규칙 ==")
    env = RaidEnv(RaidConfig(), seed=14)
    env.reset(seed=14)
    env.boss.stagger_active = True
    env.units[1].cooldowns[int(RaidActionID.TAUNT)] = 0
    bt_tank = BTGimmickLayer(env, 1)
    a = bt_tank.act()
    check(int(a) == int(RaidActionID.TAUNT), "스태거 창 탱커 → TAUNT")
    check(bt_tank.last_decision["rule"] == "stagger_taunt", "규칙명 stagger_taunt")
    bt_heal = BTGimmickLayer(env, 2)
    a2 = bt_heal.act()
    check(a2 is not None and bt_heal.last_decision["rule"] == "stagger_dps",
          "스태거 창 힐러 → 근접/딜(stagger_dps)")


def test_bt_fallthrough():
    print("\n== BT) 기믹 없음 → fall-through ==")
    env = RaidEnv(RaidConfig(), seed=15)
    env.reset(seed=15)
    env.boss.active_pattern = None
    env.boss.stagger_active = False
    bt = BTGimmickLayer(env, 1)
    check(bt.act() is None, "기믹 없음 → None(RL 위임)")
    check(bt.last_decision["rule"] is None, "규칙명 None")


# ─────────────────── 하이브리드 폴백 ───────────────────

def test_hybrid_fallback():
    print("\n== Hybrid) RL 미로드 폴백(BT+FSM) ==")
    env = RaidEnv(RaidConfig(), seed=16)
    env.reset(seed=16)
    bt = BTGimmickLayer(env, 1)
    pol = HybridPolicy(bt, rl_net=None, role=PartyRole.TANK, device=None,
                       temperature=1.0, obs_delay_turns=1, action_stickiness=0.6)
    # 기믹 없음 → RL 없음 → FSM 폴백 액션
    a = pol.act()
    check(isinstance(a, int) and 0 <= a < env.config.num_actions,
          "RL 미로드 시 FSM 폴백으로 유효 액션 반환")
    check(pol.last_source in ("fsm_fallback", "bt"), "last_source 기록")
    # SEAL 활성 → BT 가 가져감
    env.boss.start_seal(env._build_ctx())
    a2 = pol.act()
    check(isinstance(a2, int), "SEAL 시에도 유효 액션")
    check(pol.last_source == "bt", "SEAL 턴 last_source=bt")


# ─────────────────── combat_only 보상 회귀 ───────────────────

def test_reward_combat_only():
    print("\n== Reward) combat_only 모드 회귀 ==")
    env = RaidEnv(RaidConfig(), seed=17)
    env.reset(seed=17)
    env.boss.active_pattern = None
    full = RewardComputer(env.config, mode="full")
    combat = RewardComputer(env.config, mode="combat_only")

    # (1) 기믹 이벤트(카운터 성공)는 combat_only 에서 제거
    env.step_events = {uid: [{"type": "counter_success"}] for uid in env.units}
    rf = full.compute(env)
    rc = combat.compute(env)
    diffs = [round(rf[f"p{uid}"] - rc[f"p{uid}"], 3)
             for uid in env.units if env.units[uid].alive]
    check(all(abs(d - env.config.rw_counter_success) < 1e-3 for d in diffs),
          "counter_success 보상이 full 에만 반영(combat_only 제거)")

    # (2) 페이즈 클리어(전투 진척)는 두 모드 공통 유지
    env.step_events = {uid: [{"type": "phase_clear"}] for uid in env.units}
    rf2 = full.compute(env)
    rc2 = combat.compute(env)
    same = [abs(rf2[f"p{uid}"] - rc2[f"p{uid}"]) < 1e-6
            for uid in env.units if env.units[uid].alive]
    check(all(same), "phase_clear 는 combat_only 에서도 유지(전투 진척)")

    # (3) 기본 호출(mode 미지정)은 full 과 동일(기존 호출 무변경)
    default = RewardComputer(env.config)
    check(default.mode == "full", "mode 미지정 기본값 full")


# ─────────────────── main ───────────────────

def main():
    print("=" * 60)
    print("RaidEnv 스모크 테스트 — 혈월의 마수 군주 (대개편)")
    print("=" * 60)

    natural = test_smoke_run()
    forced = test_all_patterns_forced()

    print("\n== 패턴 발동 종합 ==")
    for pid in PatternID:
        nat = natural.get(int(pid), 0)
        frc = "OK" if forced.get(pid) else "NO"
        print(f"  {pid.name:14s} natural={nat}  forced={frc}")
    check(all(forced.get(pid) for pid in PatternID), "패턴 11종 전부 강제 발동 확인")

    test_rush_pillar_grog()
    test_rush_snapshot_fields()
    test_guard_deal_time()
    test_counter()
    test_counter_facing_miss()
    test_counter_miss()
    test_counter_fail_rush()
    test_seal_success()
    test_seal_fail()
    test_basic_lineshot()
    test_aim_skills()
    test_ultimate()
    test_dash()
    test_parry()
    test_stagger_gauge()
    test_arena_shrink()
    test_player_pos_stream()
    test_snapshot_schema()
    test_protocol_aim_parse()
    test_protocol_pos_parse()

    # 2계층 하이브리드(BT+RL) 신규 검증
    test_bt_seal()
    test_bt_imminent_escape()
    test_bt_brand()
    test_bt_stagger()
    test_bt_fallthrough()
    test_hybrid_fallback()
    test_reward_combat_only()

    test_session_protocol()

    print("\n" + "=" * 60)
    if _failures:
        print(f"결과: 실패 {len(_failures)}건")
        for f in _failures:
            print(f"  - {f}")
        sys.exit(1)
    else:
        print("결과: 전체 통과")
        sys.exit(0)


if __name__ == "__main__":
    main()
