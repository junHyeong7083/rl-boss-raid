"""스모크 테스트 — RaidEnv ('혈월의 마수 군주').

검증:
  1. FSM 4인으로 500스텝 실행, 예외 없음.
  2. 패턴 10종 전부 최소 1회 발동.
  3. 폭주 돌진 - 기둥 충돌 그로기 재현.
  4. 탱커 가드 딜타임 재현.
  5. 카운터 저지 성공 재현.
  6. 전멸기 '혈월 강림' LOS 은신 성공/실패 양 케이스 재현.
  7. 스냅샷 스키마(facing/pillars/cooldowns/donut/cinematic) 검증.
  8. 세션 제어 프로토콜(ready->start->episode_end) 검증.

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


# ─────────────────── 1 + 2. 500스텝 FSM 런 + 패턴 발동 집계 ───────────────────

def test_smoke_run():
    print("\n== 1) 500스텝 FSM 런 + 자연 패턴 발동 집계 ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=7)
    env.reset(seed=7)
    policies = {i: FSMNpcPolicy(env, i) for i in range(4)}  # 딜러 포함 4인 FSM
    fired = {int(p): 0 for p in PatternID}
    prev_active = None
    exceptions = 0
    for t in range(500):
        actions = {f"p{i}": policies[i].act() for i in range(4)}
        try:
            env.step(actions)
        except Exception as e:
            exceptions += 1
            print(f"  EXC at {t}: {e}")
            break
        ap = env.boss.active_pattern
        cur = int(ap.pattern_id) if ap is not None else None
        if cur is not None and cur != prev_active:
            fired[cur] += 1
        prev_active = cur
        if env.done:
            env.reset()
            policies = {i: FSMNpcPolicy(env, i) for i in range(4)}
    check(exceptions == 0, "500스텝 예외 없음")
    print(f"  자연 발동 집계: { {PatternID(k).name: v for k, v in fired.items()} }")
    return fired


# ─────────────────── 2. 강제 발동으로 10종 전부 확인 ───────────────────

def test_all_patterns_forced():
    print("\n== 2) 패턴 10종 강제 발동 확인 ==")
    fired = {}
    for pid in PatternID:
        cfg = RaidConfig()
        env = RaidEnv(cfg, seed=3)
        env.reset(seed=3)
        # 보스 무적/그로기 해제 + 강제
        try:
            if pid == PatternID.SEAL_WIPE:
                env.force_seal()
            else:
                env.force_pattern(pid)
        except Exception as e:
            print(f"  EXC force {pid.name}: {e}")
            fired[pid] = False
            continue
        activated = env.boss.active_pattern is not None and int(env.boss.active_pattern.pattern_id) == int(pid)
        # 진행하여 임팩트/해소까지 (예외 없이)
        reached = False
        for _ in range(40):
            actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
            env.step(actions)
            evs = _collect_events(env)
            reached = reached or len(evs) > 0
            if env.boss.active_pattern is None:
                break
            if env.done:
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
    # 플레이어 편향 제거 → 표식은 어그로 top(탱커)로 고정.
    env.config.pat_target_player_bias = 0.0
    # 보스를 기둥 (5,5) 바로 위(북쪽)에 배치, 표식 타깃(탱커)을 아래(남쪽)로 → 추격 돌진이 기둥 관통
    env.boss.x, env.boss.y = 5.0, 10.0
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[tank_uid] = 1000.0
    env.units[tank_uid].x, env.units[tank_uid].y = 5.0, 2.0
    env.force_pattern(PatternID.FRENZY_RUSH)
    # 표식이 탱커에게 걸렸는지 (스냅샷 rush_target)
    check(env.get_snapshot()["boss"].get("rush_target") == tank_uid,
          f"표식 rush_target=탱커 ({env.get_snapshot()['boss'].get('rush_target')})")
    boss_start = (env.boss.x, env.boss.y)
    hit = False
    moved_during_charge = False
    n = cfg.pat_rush_windup_turns + cfg.pat_rush_charge_turns + 3
    for _ in range(n):
        prev = (env.boss.x, env.boss.y)
        actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
        env.step(actions)
        if math.hypot(env.boss.x - prev[0], env.boss.y - prev[1]) > 1e-3:
            moved_during_charge = True
        # 순간이동 아님: 매 턴 이동이 snap 임계(3.0) 미만
        check_move = math.hypot(env.boss.x - prev[0], env.boss.y - prev[1])
        if check_move >= 3.0:
            _failures.append("돌진 턴 이동이 snap 임계(3.0) 이상")
        if any(e.get("type") == "rush_pillar_hit" for e in _collect_events(env)):
            hit = True
            break
    check(moved_during_charge, "보스가 실제로 이동(추격 돌진)")
    check(hit, "돌진이 기둥에 충돌해 rush_pillar_hit 발생")
    check(env.boss.grog_turns > 0, "충돌 후 보스 그로기(딜타임)")
    check(any(not p.alive for p in env.pillars), "충돌한 기둥 파괴됨")


# ─────────────────── 4. 탱커 가드 딜타임 ───────────────────

def test_guard_deal_time():
    print("\n== 4) 탱커 가드 딜타임 ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=5)
    env.reset(seed=5)
    env.boss.x, env.boss.y = 10.0, 10.0
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[tank_uid] = 1000.0
    tank = env.units[tank_uid]
    tank.x, tank.y = 12.0, 10.0   # 보스 동쪽 근접 (전방)
    env.config.skill_cooldowns[int(RaidActionID.GUARD)] = 0  # 매 턴 가드 가능하게
    env.force_pattern(PatternID.TRIPLE_CLAW)
    guard_ok = False
    # TripleClaw 3스텝 × 4턴 = 12턴. 전방 유닛은 마지막(front) 스텝(턴 12)에 피격 → 여유 있게 반복.
    for _ in range(16):
        actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
        actions[f"p{tank_uid}"] = int(RaidActionID.GUARD)
        # 탱커는 계속 제자리 유지 위해 GUARD (이동X)
        env.step(actions)
        if any(e.get("type") == "guard_success" for e in _collect_events(env)):
            guard_ok = True
            break
    check(guard_ok, "가드 성공 이벤트 발생")
    check(env.boss.stun_turns >= 0, "보스 경직 상태 필드 존재")


# ─────────────────── 5. 카운터 저지 ───────────────────

def test_counter():
    print("\n== 5) 카운터 저지 성공 ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=9)
    env.reset(seed=9)
    env.boss.x, env.boss.y = 10.0, 10.0
    dealer_uid = env.config.player_slot
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[dealer_uid] = 1000.0
    dealer = env.units[dealer_uid]
    dealer.x, dealer.y = 12.0, 10.0   # 보스 전방 근접
    env.force_pattern(PatternID.COUNTER_RUSH)
    check(env.boss.counter_window_turns > 0, "카운터 창 활성")
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{dealer_uid}"] = int(RaidActionID.COUNTER)
    env.step(actions)
    ok = any(e.get("type") == "counter_success" for e in _collect_events(env))
    check(ok, "counter_success 발생")
    check(env.boss.grog_turns > 0, "저지 후 보스 그로기")


def test_counter_fail_rush():
    print("\n== 5b) 카운터 실패 -> 강화 돌진 ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=13)
    env.reset(seed=13)
    env.force_pattern(PatternID.COUNTER_RUSH)
    fail_seen = False
    enhanced = False
    for _ in range(env.config.counter_window_turns + 2):
        actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
        env.step(actions)
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


# ─────────────────── 5c. 대시(회피 기동기) ───────────────────

def test_dash():
    print("\n== 5c) 대시(회피 기동기) ==")
    # (a) 조준 방향으로 dash_distance 이동 + 쿨다운
    env = _fresh_env_for_aim(51)
    duid = env.config.player_slot
    dealer = env.units[duid]
    dealer.x, dealer.y = 10.0, 4.0        # 장애물(기둥/보스) 없는 위치
    x0, y0 = dealer.x, dealer.y
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{duid}"] = int(RaidActionID.DASH)
    env.step(actions, aim_points={f"p{duid}": (18.0, 4.0)})   # +x 방향
    ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "dash"), None)
    check(ev is not None, "dash 이벤트 방출")
    moved = math.hypot(dealer.x - x0, dealer.y - y0)
    check(abs(moved - env.config.dash_distance) < 0.6,
          f"대시 이동 거리 ~= dash_distance ({moved:.2f} vs {env.config.dash_distance})")
    check(dealer.x > x0 + 1.0, "조준(+x) 방향으로 이동")
    check(dealer.cooldowns.get(int(RaidActionID.DASH), 0) > 0, "대시 쿨다운 적용")
    check(ev is not None and abs(ev.get("tx") - dealer.x) < 1e-6, "dash 이벤트 tx=도착 좌표")

    # (b) 장애물(보스) 관통 금지 — 보스 쪽으로 대시하면 앞에서 정지
    env = _fresh_env_for_aim(52)
    duid = env.config.player_slot
    dealer = env.units[duid]
    env.boss.x, env.boss.y = 10.0, 10.0
    dealer.x, dealer.y = 7.0, 10.0        # 보스 서쪽 3m
    x0 = dealer.x
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{duid}"] = int(RaidActionID.DASH)
    env.step(actions, aim_points={f"p{duid}": (12.0, 10.0)})  # 보스 관통 방향
    moved = dealer.x - x0
    check(0.0 <= moved < env.config.dash_distance, f"보스 앞에서 정지 (이동 {moved:.2f} < 2.5)")
    check(env._boss_dist(dealer.x, dealer.y) >= 0.0, "보스와 겹치지 않음")

    # (c) aim 미지정 -> 보스 반대 방향 폴백
    env = _fresh_env_for_aim(53)
    duid = env.config.player_slot
    dealer = env.units[duid]
    env.boss.x, env.boss.y = 10.0, 10.0
    dealer.x, dealer.y = 8.0, 10.0        # 보스 서쪽 → 폴백은 서쪽(-x)
    x0 = dealer.x
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{duid}"] = int(RaidActionID.DASH)
    env.step(actions, aim_points=None)
    check(dealer.x < x0 - 0.5, "aim 미지정 시 보스 반대 방향으로 대시")


# ─────────────────── 5d. 카운터 miss (각도/거리) ───────────────────

def test_counter_miss():
    print("\n== 5d) 카운터 miss (각도/거리 조건 밖) ==")
    full_cd = RaidConfig().skill_cooldowns[int(RaidActionID.COUNTER)]

    # (a) 거리 밖 miss
    env = _fresh_env_for_aim(61)
    duid = env.config.player_slot
    dealer = env.units[duid]
    dealer.x, dealer.y = 3.0, 10.0        # 보스(10,10)와 7m (counter_range 2.0 밖)
    env.force_pattern(PatternID.COUNTER_RUSH)
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{duid}"] = int(RaidActionID.COUNTER)
    env.step(actions)
    ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "counter_miss"), None)
    check(ev is not None, "거리 밖 counter_miss 이벤트")
    check(ev is not None and ev.get("reason") == "range", f"reason=range ({ev.get('reason') if ev else '?'})")
    cd_left = dealer.cooldowns.get(int(RaidActionID.COUNTER), 0)
    check(0 < cd_left <= full_cd // 2, f"miss 시 절반 쿨다운 (남은 {cd_left} <= {full_cd//2})")

    # (b) 각도 밖 miss (사거리 내지만 후방)
    env = _fresh_env_for_aim(62)
    duid = env.config.player_slot
    dealer = env.units[duid]
    env.boss.x, env.boss.y = 10.0, 10.0
    dealer.x, dealer.y = 12.0, 10.0       # 보스 동쪽 근접(사거리 내)
    env.force_pattern(PatternID.COUNTER_RUSH)
    env.boss.facing = math.pi             # 보스가 딜러 반대편(서쪽)을 향함 → 딜러는 후방
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{duid}"] = int(RaidActionID.COUNTER)
    env.step(actions)
    ev = next((e for e in env.step_events.get(duid, []) if e.get("type") == "counter_miss"), None)
    check(ev is not None and ev.get("reason") == "angle",
          f"각도 밖 reason=angle ({ev.get('reason') if ev else '?'})")


# ─────────────────── 3b. 돌진 표식 스냅샷 ───────────────────

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
    env.step({f"p{i}": int(RaidActionID.STAY) for i in range(4)})
    snap = env.get_snapshot()
    check(snap["boss"].get("rush_target") == tank_uid,
          f"windup 중 rush_target=표식 uid ({snap['boss'].get('rush_target')})")
    check(snap["boss"].get("rush_left", 0) > 0, "windup 중 rush_left>0")
    has_line = any(s.get("kind") == "line" for tg in snap["telegraphs"] for s in tg["shapes"])
    check(has_line, "돌진 조준선(line) 텔레그래프 존재")


# ─────────────────── 6. 전멸기 LOS 성공/실패 ───────────────────

def _behind_pillar_pos(env, pillar):
    bx, by = env.boss.x, env.boss.y
    dx = pillar.x - bx; dy = pillar.y - by
    d = math.hypot(dx, dy) or 1.0
    return (pillar.x + dx / d * (pillar.radius + 0.4),
            pillar.y + dy / d * (pillar.radius + 0.4))


def test_seal_success():
    print("\n== 6) 전멸기 '혈월 강림' - LOS 은신 성공 ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=21)
    env.reset(seed=21)
    env.boss.x, env.boss.y = 10.0, 10.0
    # 4인을 각각 다른 기둥 뒤로 배치 (LOS 차단)
    for i, u in enumerate(env.units.values()):
        p = env.pillars[i % len(env.pillars)]
        u.x, u.y = _behind_pillar_pos(env, p)
    env.force_seal()
    cine_start = any(e.get("type") == "cinematic_start" for e in _collect_events(env))
    success = False
    cine_end = False
    for _ in range(cfg.seal_wind_up_turns + 2):
        actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
        env.step(actions)
        evs = _collect_events(env)
        if any(e.get("type") == "seal_success" for e in evs):
            success = True
        if any(e.get("type") == "cinematic_end" and e.get("success") for e in evs):
            cine_end = True
        if env.boss.active_pattern is None:
            break
    check(cine_start, "cinematic_start 이벤트")
    check(success, "seal_success (전원 은신)")
    check(cine_end, "cinematic_end success=True")
    check(all(u.alive for u in env.units.values()), "전원 생존")
    check(env.boss.grog_turns > 0, "성공 후 보스 그로기(딜타임)")


def test_seal_fail():
    print("\n== 6b) 전멸기 '혈월 강림' - 노출 실패(전멸) ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=22)
    env.reset(seed=22)
    env.boss.x, env.boss.y = 10.0, 10.0
    # 전원 보스 바로 옆 노출 (기둥 뒤 아님)
    for u in env.units.values():
        u.x, u.y = 11.0, 10.0
    env.force_seal()
    wipe = False
    cine_end_fail = False
    for _ in range(cfg.seal_wind_up_turns + 2):
        actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
        env.step(actions)
        evs = _collect_events(env)
        if any(e.get("type") == "seal_fail" for e in evs):
            cine_end_fail = any(e.get("type") == "cinematic_end" and not e.get("success") for e in evs)
        if env.boss.active_pattern is None:
            break
    wipe = all(not u.alive for u in env.units.values())
    check(wipe, "노출 시 전원 즉사(wipe)")
    check(cine_end_fail, "cinematic_end success=False")


# ─────────────────── 6c. 딜러 조준 설치기 (Q/W) ───────────────────

def _fresh_env_for_aim(seed):
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=seed)
    env.reset(seed=seed)
    env.boss.x, env.boss.y = 10.0, 10.0
    env.boss.active_pattern = None
    env.boss.invuln_turns = 0
    # 어그로를 탱커에 고정 (보스가 딜러 쪽으로 안 움직이게)
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    for u in env.units.values():
        env.boss.aggro[u.uid] = 0.0
    env.boss.aggro[tank_uid] = 1000.0
    return env


def _dealer_cast(env, action, aim):
    dealer_uid = env.config.player_slot
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{dealer_uid}"] = int(action)
    aim_points = {f"p{dealer_uid}": aim} if aim is not None else None
    env.step(actions, aim_points=aim_points)
    casts = [e for e in env.step_events.get(dealer_uid, [])
             if e.get("type") == "player_skill_cast"]
    return casts[0] if casts else None


def test_aim_skills():
    print("\n== 6c) 딜러 조준 설치기 Q/W ==")
    cfg = RaidConfig()

    # (a) Q 명중: 사거리 내 보스 조준
    env = _fresh_env_for_aim(41)
    dealer = env.units[env.config.player_slot]
    dealer.x, dealer.y = 5.0, 10.0    # 보스와 5m (Q 사거리 7 이내)
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ATTACK_SKILL, (10.0, 10.0))
    check(ev is not None and ev.get("skill") == "skill", "Q player_skill_cast(skill) 이벤트")
    check(ev is not None and ev.get("hit") is True, "Q 보스 명중 (hit=True)")
    check(env.boss.hp < hp0, "Q 명중 시 보스 HP 감소")
    check(ev is not None and abs(ev.get("radius", 0) - cfg.aim_q_radius) < 1e-6, "Q 반경 필드")

    # (b) Q 사거리 클램프: 사거리 밖(13m) 보스 조준 -> 경계(7m)로 클램프 -> 빗나감
    env = _fresh_env_for_aim(42)
    env.boss.x, env.boss.y = 15.0, 10.0
    dealer = env.units[env.config.player_slot]
    dealer.x, dealer.y = 2.0, 10.0    # 보스와 13m (Q 사거리 7 밖)
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ATTACK_SKILL, (15.0, 10.0))
    check(ev is not None and abs(ev.get("tx", 0) - 9.0) < 1e-4,
          f"Q 조준점 클램프 (tx={ev.get('tx') if ev else '?'} == 9.0)")
    check(ev is not None and ev.get("hit") is False, "Q 클램프 후 빗나감 (hit=False)")
    check(env.boss.hp == hp0, "빗나감 시 보스 HP 불변")

    # (c) Q 빗나감: 엉뚱한 곳 조준
    env = _fresh_env_for_aim(43)
    dealer = env.units[env.config.player_slot]
    dealer.x, dealer.y = 8.0, 10.0
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ATTACK_SKILL, (4.0, 15.0))
    check(ev is not None and ev.get("hit") is False, "Q 빗나감 케이스 (hit=False)")
    check(env.boss.hp == hp0, "빗나감 시 보스 HP 불변 (2)")

    # (d) W 혈월 낙하 (신규 액션 18): 명중 + 대형 피해
    env = _fresh_env_for_aim(44)
    dealer = env.units[env.config.player_slot]
    dealer.x, dealer.y = 4.0, 10.0    # 보스와 6m (W 사거리 9 이내)
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.SKILL_2, (10.0, 10.0))
    check(ev is not None and ev.get("skill") == "skill2", "W player_skill_cast(skill2) 이벤트")
    check(ev is not None and ev.get("hit") is True, "W 보스 명중")
    dmg = hp0 - env.boss.hp
    check(dmg >= cfg.aim_w_damage - cfg.boss_defense - 1, f"W 대형 피해 ({dmg})")

    # (e) aim 미지정 -> 보스 자동 조준 (FSM/관전 모드)
    env = _fresh_env_for_aim(45)
    dealer = env.units[env.config.player_slot]
    dealer.x, dealer.y = 5.0, 10.0
    ev = _dealer_cast(env, RaidActionID.ATTACK_SKILL, None)
    check(ev is not None and ev.get("hit") is True, "aim 미지정 시 보스 자동 조준 명중")

    # (f) W 는 딜러 전용 (탱커 사용 시 invalid)
    env = _fresh_env_for_aim(46)
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{tank_uid}"] = int(RaidActionID.SKILL_2)
    env.step(actions)
    invalid = any(e.get("type") == "invalid_action"
                  for e in env.step_events.get(tank_uid, []))
    check(invalid, "W 타 역할 사용 시 invalid_action")

    # (g) 스냅샷 딜러 cooldowns 키 = skill/skill2/counter/dash/ult
    keys = set(env.get_snapshot()["units"][env.config.player_slot]["cooldowns"].keys())
    check(keys == {"skill", "skill2", "counter", "dash", "ult"}, f"딜러 cooldowns 키 {sorted(keys)}")


# ─────────────────── 6e. 딜러 궁극기 '혈월 처형' (ULTIMATE) ───────────────────

def test_ultimate():
    print("\n== 6e) 딜러 궁극기 '혈월 처형' (ULTIMATE) ==")
    cfg = RaidConfig()

    # (a) 명중 + 대형 피해 + 쿨다운(200) 적용
    env = _fresh_env_for_aim(81)
    dealer = env.units[env.config.player_slot]
    dealer.x, dealer.y = 5.0, 10.0        # 보스와 5m (ult 사거리 9 이내)
    hp0 = env.boss.hp
    ev = _dealer_cast(env, RaidActionID.ULTIMATE, (10.0, 10.0))
    check(ev is not None and ev.get("skill") == "ult", "R player_skill_cast(ult) 이벤트")
    check(ev is not None and ev.get("hit") is True, "궁극 보스 명중 (hit=True)")
    check(ev is not None and "crit" in ev, "궁극 crit 필드 존재")
    check(ev is not None and abs(ev.get("radius", 0) - cfg.aim_ult_radius) < 1e-6,
          f"궁극 반경 필드({cfg.aim_ult_radius})")
    dmg = hp0 - env.boss.hp
    check(dmg >= cfg.aim_ult_damage - cfg.boss_defense - 1, f"궁극 대형 피해 ({dmg})")
    # 시전 스텝 종료 시 쿨다운 틱이 1 차감 → 200 세팅 후 199 로 관측(타 스킬과 동일 규칙).
    ult_cd_full = cfg.skill_cooldowns[int(RaidActionID.ULTIMATE)]
    check(dealer.cooldowns.get(int(RaidActionID.ULTIMATE), 0) >= ult_cd_full - 1,
          f"궁극 쿨다운 200 적용 (관측 {dealer.cooldowns.get(int(RaidActionID.ULTIMATE), 0)}/{ult_cd_full})")

    # (b) 무력화(스태거) 활성 중 대량 게이지 기여(ult_stagger_contrib)
    env = _fresh_env_for_aim(82)
    dealer = env.units[env.config.player_slot]
    dealer.x, dealer.y = 5.0, 10.0
    env.boss.stagger_active = True
    env.boss.stagger_gauge = cfg.stagger_gauge   # 200
    ev = _dealer_cast(env, RaidActionID.ULTIMATE, (10.0, 10.0))
    contrib_ev = next((e for e in env.step_events.get(env.config.player_slot, [])
                       if e.get("type") == "stagger_contribute"), None)
    check(contrib_ev is not None and abs(contrib_ev.get("amount", 0) - cfg.ult_stagger_contrib) < 1e-6,
          f"궁극 스태거 기여 {cfg.ult_stagger_contrib} (실제 {contrib_ev.get('amount') if contrib_ev else '?'})")
    check(abs(env.boss.stagger_gauge - (cfg.stagger_gauge - cfg.ult_stagger_contrib)) < 1e-6,
          f"스태거 게이지 차감 (={env.boss.stagger_gauge})")

    # (c) 딜러 전용 (탱커 사용 시 invalid_action)
    env = _fresh_env_for_aim(83)
    tank_uid = next(u.uid for u in env.units.values() if u.role == PartyRole.TANK)
    actions = {f"p{i}": int(RaidActionID.STAY) for i in range(4)}
    actions[f"p{tank_uid}"] = int(RaidActionID.ULTIMATE)
    env.step(actions)
    invalid = any(e.get("type") == "invalid_action"
                  for e in env.step_events.get(tank_uid, []))
    check(invalid, "궁극 타 역할 사용 시 invalid_action")

    # (d) 쿨다운 중 재사용 차단 (STAY 처리 — 이벤트 없음)
    env = _fresh_env_for_aim(84)
    dealer = env.units[env.config.player_slot]
    dealer.x, dealer.y = 5.0, 10.0
    _dealer_cast(env, RaidActionID.ULTIMATE, (10.0, 10.0))   # 1회 시전 → 쿨 200
    ev2 = _dealer_cast(env, RaidActionID.ULTIMATE, (10.0, 10.0))   # 쿨 중 재시도
    check(ev2 is None, "쿨다운 중 궁극 재시전 차단 (cast 이벤트 없음)")


def test_protocol_aim_parse():
    print("\n== 6d) 프로토콜 tx/ty 파싱 (SessionLink) ==")
    import raid_streamer
    link = raid_streamer.SessionLink({}, port=59999)  # 리슨 안 함 — 파서만 사용
    link._handle_line(b'{"action": 10, "tx": 12.5, "ty": 7.25}')
    a, aim = link.get_action()
    check(a == 10 and aim == (12.5, 7.25), f"tx/ty 파싱 (action={a}, aim={aim})")
    # 하위 호환: tx/ty 없는 기존 메시지
    link._handle_line(b'{"action": 9}')
    a, aim = link.get_action()
    check(a == 9 and aim is None, "tx/ty 없는 기존 형식 하위 호환 (aim=None)")
    # 소비 후 초기화
    a, aim = link.get_action()
    check(a == int(RaidActionID.STAY) and aim is None, "1회 소비 후 STAY/None 초기화")


# ─────────────────── 7. 스냅샷 스키마 ───────────────────

def test_snapshot_schema():
    print("\n== 7) 스냅샷 스키마 (facing/pillars/cooldowns/donut/cinematic) ==")
    cfg = RaidConfig()
    env = RaidEnv(cfg, seed=31)
    env.reset(seed=31)
    snap = env.get_snapshot()
    check("facing" in snap["boss"], "boss.facing 포함")
    check("counter_window" in snap["boss"], "boss.counter_window 포함")
    check("pillars" in snap and len(snap["pillars"]) == 4, "pillars 목록(4)")
    check("cooldowns" in snap["units"][0], "unit.cooldowns 포함")
    # donut shape 확인 (대지 분쇄 2스텝째)
    env.force_pattern(PatternID.EARTH_CRUSH)
    donut_seen = False
    for _ in range(10):
        env.step({f"p{i}": int(RaidActionID.STAY) for i in range(4)})
        for tg in env.get_snapshot()["telegraphs"]:
            for s in tg["shapes"]:
                if s.get("kind") == "donut":
                    donut_seen = True
        if env.boss.active_pattern is None:
            break
    check(donut_seen, "donut kind 스냅샷 출력")


# ─────────────────── 8. 세션 제어 프로토콜 ───────────────────

def test_session_protocol():
    print("\n== 8) 세션 제어 프로토콜 (ready->start->episode_end) ==")
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
        time.sleep(2.0)  # 로드 + 리슨 대기
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
        # episode_end 대기
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
        # 세션 로그 파일 생성 확인
        logdir = os.path.join(here, "session_logs")
        has_log = os.path.isdir(logdir) and any(f.startswith("session_") for f in os.listdir(logdir))
        check(has_log, "session_logs/*.jsonl 생성")
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=5)
        except Exception:
            proc.kill()


def cfg_obs_dim():
    return RaidConfig().obs_size


# ─────────────────── main ───────────────────

def main():
    print("=" * 60)
    print("RaidEnv 스모크 테스트 — 혈월의 마수 군주")
    print("=" * 60)

    natural = test_smoke_run()
    forced = test_all_patterns_forced()

    print("\n== 패턴 10종 발동 종합 ==")
    for pid in PatternID:
        nat = natural.get(int(pid), 0)
        frc = "OK" if forced.get(pid) else "NO"
        print(f"  {pid.name:14s} natural={nat}  forced={frc}")
    all_fired = all(forced.get(pid) for pid in PatternID)
    check(all_fired, "패턴 10종 전부 발동 확인")

    test_rush_pillar_grog()
    test_rush_snapshot_fields()
    test_guard_deal_time()
    test_counter()
    test_counter_fail_rush()
    test_dash()
    test_counter_miss()
    test_seal_success()
    test_seal_fail()
    test_aim_skills()
    test_ultimate()
    test_protocol_aim_parse()
    test_snapshot_schema()
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
