# RAID V2 설계 — "혈월의 마수 군주"

로스트아크식 1보스 4파티 레이드 환경 (신규 `src/raid/`). 기존 `src/boss/` 와 독립이며 학습 호환 불필요.
**보스 수정 금지 원칙**: 기존 `src/boss/` 는 포트폴리오 보호 대상. 본 모듈은 전면 신규 작성.

- 좌표계: 20×20 유클리드 연속 float, 원형 충돌(파티원끼리 통과, 보스/기둥만 차단), 턴제(Unity 0.3초/턴 재생)
- 파티: Dealer(플레이어 슬롯 p0) / Tank(p1) / Healer(p2) / Support(p3) + 보스 1
- 진입점: `from src.raid import RaidEnv, RaidConfig, FSMNpcPolicy`

---

## 1. 보스 컨셉

**혈월의 마수 군주** — 로스트아크 '발탄' 모티프. 늑대형 마수, 근접 브루저.
파란 발광(카운터)·기둥 뽑기(무력화)·혈월 강림(전멸기) 3대 연출 기믹 보유.

핵심 설계:
- **facing (라디안) 상태**: 모든 패턴 기하는 facing 기준 **상대 좌표**로 정의(`shapes.py::RelShape`), 시전 시작 시 타깃 방향으로 회전(속도 제한 `boss_rotate_speed`) 후 **facing 고정**. 스냅샷 직렬화 때 월드 절대 좌표로 변환(`RelShape.bake → Shape`). → "몸 방향과 장판 방향 불일치" 원천 차단.
- **패턴 = PatternStep 시퀀스**: 스텝마다 독립 텔레그래프. Unity 게이지가 스텝마다 정확히 100%에서 발동.
- **Pillar(기둥) 오브젝트**: 고정 4개 `(5,5)(15,5)(5,15)(15,15)`, 반경 1.2. 폭주 돌진 유도(그로기) + 전멸기 LOS 은신에 공용. 돌진에 맞으면 파괴 → `pillar_respawn_turns`(20턴) 후 재생성.

---

## 2. 패턴 카탈로그 (10종)

telegraph = 각 스텝 예고 턴수. anim = Unity 애니 트리거. 좌표/각도는 모두 **facing 상대**.

| # | ID (name) | 페이즈 | 스텝 / telegraph | 도형(상대) | 피해 | 회피법 | anim | 이펙트 키 |
|---|-----------|--------|------------------|-----------|------|--------|------|-----------|
| 1 | TRIPLE_CLAW (삼연 발톱) | P1+ | 3스텝 / 2·2·2 | fan +35°(60°) → fan −35°(60°) → fan 0°(90°, 대형 r×1.15) | 34/34/48 | 부채꼴 밖(측면/후방)으로 회피, 콤보 방향 예측 | `slash` | claw_fx |
| 2 | EARTH_CRUSH (대지 분쇄) | P1+ | 2스텝 / 3·2 | circle r3 (중심) → **donut** r3~r8 (충격파) | 42/46 | 1스텝 중심 밖 → 2스텝 도넛 안(보스 근접)으로 | `smash`/`shock` | quake_fx |
| 3 | FRENZY_RUSH (폭주 돌진) | P1+ | 1스텝 / 4 | line 폭2.5, 맵끝까지 (kind=rush_dash) | 70 | 직선 밖 측면 회피. **기믹: 경로에 기둥 있으면 보스 그로기 3턴** | `rush` | rush_fx |
| 4 | PILLAR_THROW (기둥 투척) | P1+ | 3스텝 / 3·1·1 | circle r2.5 ×3 (월드 랜덤, 파티 인근) | 44 | 1턴 간격 시간차 낙하 지점 회피 | `throw` | rock_fx |
| 5 | SPIN_SWEEP (회전 휩쓸기) | P2+ | 2스텝 / 2·2 | fan 0°(180°, 전반원) → fan 180°(180°, 후반원) r6 | 40/40 | 타이밍 맞춰 반대 반원으로 이동 | `spin` | spin_fx |
| 6 | BLOOD_ROAR (혈흔의 포효) | P2+ | 1스텝 / 5 | **donut** r2~r9 (몸쪽만 안전) | 90 | 보스 근접(r2 이내)으로 전원 붙기 | `roar` | roar_fx |
| 7 | CRIMSON_BRAND (붉은 낙인) | P2+ | 1스텝 / 6 | 대상 추적 circle r3.5 (kind=brand) | 80 | 아군과 거리 4+ 유지 → 대상만 경미(÷4) + 보스 그로기 | `brand` | brand_fx |
| 8 | COUNTER_RUSH (카운터 돌진) | P1+ | 창 3턴 (mode=counter) | 없음(파란 발광) | — | 딜러가 **보스 전방 근접에서 COUNTER** → 저지+그로기 3턴. 실패 시 즉시 강화 돌진(×1.5) | `counter_glow` | counter_fx |
| 9 | STAGGER_LIFT (무력화) | P2+ | 창 6턴 (mode=stagger) | 없음(기둥 들기) | 실패 120 | 전원 딜 집중으로 게이지 200 소진 → 기둥 낙하 + 그로기 4턴. 실패 시 광역 투척 | `lift` | stagger_fx |
| 10 | SEAL_WIPE (전멸기 '혈월 강림') | P3 진입(HP 50%) | wind_up 30턴 (mode=seal) | 없음(기둥 LOS) | 전멸 | 전원 각자 기둥 뒤로 은신(유닛-보스 선분이 기둥 원 교차). 전원 성공 → 생존+그로기 8턴, 한 명 노출 → **전원 즉사** | `blood_moon` | wipe_cine |

### 파티 플레이 협동 포인트
- **탱커 가드 딜타임**: 시전 중 패턴의 피격 판정 순간 탱커가 장판 안에서 GUARD 중이면 → 피해 80% 경감 + `guard_success` + 보스 2턴 경직(딜타임). **같은 패턴 시퀀스에서 1회만**.
- **돌진 유도**: 탱커가 어그로로 보스를 기둥 방향으로 돌게 유도 → 돌진이 기둥 충돌 → 그로기 딜타임.
- **카운터**: 딜러 전용 저지.
- **무력화**: 전원 딜 집중.
- **전멸기**: 4인 전원 LOS 은신 협동.

---

## 3. 액션 공간 (19) — 딜러는 로아식 조준 설치기 킷

| ID | 액션 | 허용 역할 | 스킬키(스냅샷) | 쿨다운(턴) |
|----|------|-----------|----------------|-----------|
| 0 | STAY | 전원 | — | 0 |
| 1–8 | MOVE (8방향) | 전원 | — | 0 |
| 9 | ATTACK_BASIC | 전원 | — | 0 |
| 10 | ATTACK_SKILL — 딜러: **Q 혈창 투척**(조준 AoE) / 타 역할: 강공격 | 전원 | `skill` | 3 |
| 11 | TAUNT | TANK | `taunt` | 6 |
| 12 | GUARD | TANK | `guard` | 4 |
| 13 | HEAL (최저 HP 아군 자동 타깃) | HEALER | `heal` | 4 |
| 14 | CLEANSE | HEALER | `cleanse` | 8 |
| 15 | BUFF_ATK | SUPPORT | `buff_atk` | 8 |
| 16 | BUFF_SHIELD | SUPPORT | `buff_shield` | 6 |
| 17 | COUNTER — 딜러 **E 저지** | DEALER | `counter` | 8 |
| 18 | SKILL_2 — 딜러 **W 혈월 낙하**(대형 조준 AoE) | DEALER | `skill2` | 7 |

### 딜러 조준 설치기 (지면 지정 AoE, 즉시 발동 — 텔레그래프 없음)

| 스킬 | 액션 | 반경 | 사거리 | 피해 | 쿨다운 |
|------|------|------|--------|------|--------|
| Q 혈창 투척 | 10 | 1.8 | 7 | 45 | 3턴 |
| W 혈월 낙하 | 18 | 3.0 | 9 | 110 | 7턴 |

- 조준점: `env.step(actions, aim_points={"p0": (tx, ty)})` — sim 좌표(0~20).
- 조준점이 사거리 밖이면 **사거리 경계로 클램프**. aim 미지정(None) 시 **보스 위치 자동 조준** (FSM 관전 모드 포함).
- 명중 판정: AoE 원-보스 몸통 원 겹침 (`dist ≤ radius + boss_radius`). 파티원 프렌들리파이어 없음.
- 발동 시 이벤트 (Unity 조준점 폭발 VFX + 명중 여부 표시):
  `{"type":"player_skill_cast","uid":0,"skill":"skill"|"skill2","tx":..,"ty":..,"radius":..,"hit":bool,"amount":int}`
- 쿨다운 중 해당 액션 선택 → **STAY 처리(페널티 없음)**. 역할 불허 액션(W 를 딜러 외 사용 등) → invalid.
- 값은 `config.RaidConfig`(aim_q_*, aim_w_*, skill_cooldowns) 에서 조정 가능.

---

## 4. 관측 벡터 (128차원)

역할 공용 단일 벡터. `env._observe(uid)`. 블록 구성(`config.OBS_LAYOUT`):

| 블록 | 차원 | 내용 |
|------|------|------|
| Self | 16 | hp, mp, x/w, y/h, role(OH4), radius, cd_skill, cd_roleA, cd_roleB, step_progress, aggro_ratio, is_top_aggro, **guard_active** |
| Allies | 24 | 3명 × (dx/10, dy/10, hp, alive, role OH4) |
| Boss | 12 | dx/10, dy/10, dist/10, hp_ratio, phase(OH3), grog/stun, invuln, **facing_sin, facing_cos**, **counter_window** |
| PatternCh | 40 | 10패턴 × (active, turns_norm, am_I_target, in_danger_here) — 패턴 ID = 채널 인덱스 고정 |
| DangerSensor | 8 | 8방향 최근접 위험까지 거리 (1=안전) |
| Escape | 4 | in_danger, escape_dx, escape_dy, urgency |
| Coop | 8 | stagger(active, gauge) + seal_LOS(active, boss_dx, boss_dy, **hidden**) + counter(window, front_align) |
| Pillars | 12 | 4기둥 × (dx/10, dy/10, alive) |
| Player | 4 | dx/10, dy/10, dist/10, hp |

- 스킬 쿨다운 정규화값(cd_skill/roleA/roleB)이 Self 블록에 포함 → RL 이 액션 가용성 학습 가능(0 = 사용 가능).
  - cd_skill = ATTACK_SKILL(딜러 Q). 딜러의 cd_roleA/cd_roleB = W(SKILL_2)/E(COUNTER). 타 역할은 기존 역할 스킬 2종.
- facing 은 sin/cos 로 인코딩(불연속 방지).

---

## 5. 스냅샷 스키마 (Unity 작업자용)

`env.get_snapshot()` → JSON. 기존 boss 스키마 대비 **추가/변경점**:

### boss (추가 필드)
```
"facing": float(rad),          # NEW — 보스 몸 방향
"stun": int,                   # NEW — 가드/카운터 경직 턴
"counter_window": int,         # NEW — 카운터 창 남은 턴 (>0 이면 파란 발광)
"active_pattern": int,         # NEW — 현재 패턴 ID (-1=없음)
"active_mode": str,            # NEW — "steps"|"counter"|"stagger"|"seal"|""
```
기존 유지: x, y, hp, max_hp, phase, invuln, grog, stagger_active, stagger_gauge, radius, vx, vy.

### units[] (추가 필드)
```
"buff_guard": int,             # NEW
"cooldowns": { "<skillkey>": 남은턴, ... }   # NEW — 스킬바 UI 용. 역할 스킬만.
                               # 딜러: {"skill": Q, "skill2": W, "counter": E}
                               # 탱커: {"taunt","guard"} / 힐러: {"heal","cleanse"} / 서폿: {"buff_atk","buff_shield"}
                               # 스킬키 ↔ 액션ID 매핑은 3절 표 참고
```
`marked` 유지. `chained_with` 제거(체인 패턴 폐지).

### pillars[] (신규 배열)
```
{"x":float,"y":float,"radius":float,"alive":bool,"respawn_in":int}
```

### telegraphs[] (스텝 반영)
```
{
  "pattern": int, "step_index": int, "num_steps": int,   # NEW step_index/num_steps
  "anim": str,                                            # NEW — 애니 트리거
  "turns_remaining": int, "total_wind_up": int,
  "shapes": [ ... ],   # 월드 절대 좌표. kind: circle|donut|fan|line
  "target_uids": [int]
}
```
- **shape kind "donut" 추가**: `{"kind":"donut","cx","cy","r_in","r_out"}`.
- fan: `{"kind":"fan","cx","cy","angle"(월드 forward rad),"width"(full angle rad),"r"}`.
- line: `{"kind":"line","ax","ay","bx","by","hw"}`. circle: `{"kind":"circle","cx","cy","r"}`.
- counter/stagger/seal 모드는 telegraphs 비어있음(그릴 장판 없음) — `boss.active_mode` 와 events 로 연출.

### events[] (신규 이벤트 타입)
`cinematic_start` / `cinematic_end` / `guard_success` / `counter_success` / `counter_fail` /
`stagger_start` / `stagger_success` / `stagger_fail` / `stagger_contribute` / `rush_pillar_hit` /
`seal_holding`(hidden 플래그) / `seal_success` / `seal_fail` / `mechanic_success/fail`(brand) /
**`player_skill_cast`**(uid, skill, tx, ty, radius, hit, amount — 딜러 조준 스킬 VFX용) /
기존 `damage/damage_taken/heal/death/taunt/buff/cleanse/phase_clear/invalid_action`.

---

## 6. 시네마틱 프로토콜 (전멸기)

페이즈 전환은 HP 75%(P1→P2) / 50%(P2→P3) 두 지점. 전멸기는 **P3 진입(HP 50%) 시에만** 강제 발동(P1→P2 는 phase_clear 만). 상용 페이싱을 위해 전투 극초반 발동을 제거:
```
시작:  events += {"type":"cinematic_start","pattern":9,"duration_turns":30}
매턴:  events += {"type":"seal_holding","hidden":bool,"turns_left":int}   # 유닛별
종료:  events += {"type":"seal_success"} 또는 {"type":"seal_fail"}
       events += {"type":"cinematic_end","success":bool}
```
Unity: `cinematic_start` 로 시네마틱 카메라 재생(30턴 ~10초: 인트로 3.6초 + 파훼 이동 ~6.4초), `cinematic_end` 로 종료. 성공 → 전원 생존 + 보스 그로기 8턴(딜타임), 실패 → 전원 즉사(wipe).

---

## 7. 세션 제어 프로토콜 (`raid_streamer.py`)

Unity 가 Python 을 **자식 프로세스**로 실행. UDP 5005(상태 송신), TCP 5006(양방향 제어/입력).

### 메시지 흐름
```
[Python] 모델/env 로드 완료 → TCP 5006 리슨
[Unity ] TCP 접속
[Python] → {"type":"ready","mode":"fsm","obs_dim":128,"num_actions":19,"map":[20,20]}   (로딩바 70%)
[Python] (대기 — 에피소드 시작 안 함)
[Unity ] → {"cmd":"start"}
[Python]   env.reset() → 첫 스냅샷 UDP 송신
[Python] → {"type":"started"}    (턴 루프 시작, 피험자 1턴부터 플레이)
[Unity ] → {"action": <0..18>}   (매 턴 플레이어 입력; 최신값만 사용)
[Unity ] → {"action": 10|18, "tx": <float>, "ty": <float>}
           # 조준 스킬(Q/W) 입력. tx/ty = sim 좌표(0~20) 조준점 — 선택 필드.
           # tx/ty 없으면 보스 위치 자동 조준 (기존 {"action":n} 하위 호환).
           # 1회 소비형: 다음 턴 STAY/None 초기화.
   ... 턴 진행, 매 턴 UDP 스냅샷 ...
[Python] → {"type":"episode_end","result":"victory|wipe|timeout","steps":N,"duration_sec":F}
[Python] (다시 대기)
[Unity ] → {"cmd":"start"}  = 다시하기   |   {"cmd":"quit"} = 프로세스 종료
```
- `action` 입력과 `cmd` 제어는 같은 TCP 스트림에서 **키 유무**로 구분(`"cmd"` vs `"action"`).
- print stdout 은 주기적 flush(헤드리스 자식 프로세스 호환). 이모지 미사용(cp949).

### 세션 로그 (연구 데이터)
에피소드마다 `session_logs/session_<타임스탬프>.jsonl` 에 append:
```json
{"ts":ISO,"mode":"fsm","result":"wipe","steps":22,"duration_sec":0.02,
 "player_actions":12,"gimmicks":{"counter_success":1,"seal_fail":1,...}}
```

### CLI
```
python raid_streamer.py --mode fsm|rl|hybrid --ckpt <path> --turn-interval 0.3 [--no-player] [--max-steps N]
```
rl/hybrid 로드 실패 시 FSM 폴백(경고 출력).

---

## 8. 파일 구성

```
src/raid/
  __init__.py     패키지 export
  config.py       RaidConfig, 열거형, ROLE_STATS, SKILL_KEYS, OBS_LAYOUT
  shapes.py       RelShape(상대) → Shape(월드) bake, contains, 위험 센서, LOS
  patterns.py     PatternStep, PatternDef, 7 step-패턴 빌더 + 기믹 spec
  boss.py         Boss(facing, ActivePattern 스텝 머신, 기믹 상태)
  env.py          RaidEnv(스텝 구동, 임팩트, 기믹 틱, 관측, 스냅샷, Pillar)
  fsm_npc.py      비교군 FSM (탱커/힐러/서포터/딜러)
  rewards.py      보상 뼈대 (위험 시 양수 차단 원칙)
raid_streamer.py  Unity 브릿지 + 세션 제어 프로토콜
test_raid_env.py  스모크 테스트
boss/docs/RAID_V2_DESIGN.md  (본 문서)
```

## 9. 보상 설계 원칙 (rewards.py — 뼈대)
1. 위험(발동 임박, turns_remaining≤2) 영역 안 → 양수 보상 차단, 도망만.
2. 위험 밖 → 역할 수행(딜/힐/버프/어그로) 보상.
3. 기믹 파훼(가드 딜타임/카운터/무력화/돌진-기둥/전멸기 LOS)는 큰 보상.
4. 시간 패널티 + 비참여(보스에서 멀리) 패널티.
세부 튜닝은 후속.
