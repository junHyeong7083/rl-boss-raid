using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace BossRaid
{
    /// <summary>
    /// 로스트아크식 마우스+키보드 조작을 이산 액션으로 변환해 RaidSession으로 전송한다.
    /// "설치기 위주" 전투 개편: 조준형 스킬은 키 입력 → 조준 모드(사거리 링+AoE 레티클) →
    /// 좌클릭으로 지점 확정 → SendActionAimed(액션, tx, ty).
    ///
    /// 조작 매핑:
    ///   우클릭(누름/드래그) = 지면 클릭 지점으로 이동(8방향 양자화, MOVE 1~8). 도달 시 정지.
    ///   Q = 혈창 투척(10, 조준형: 사거리7/AoE r1.8, cd "skill")
    ///   W = 혈월 낙하(18, 조준형: 사거리9/AoE r3.0, cd "skill2")
    ///   R = 혈월 처형(20, 조준형: 사거리9/AoE r4.0, cd "ult")
    ///   E = 카운터(17, 즉시, cd "counter")
    ///   Shift = 대시(19, 즉발 방향기: 조준 모드 없이 마우스 지면 포인트 방향으로 즉시 2.5 이동,
    ///           서버가 거리 클램프. cd "dash" 17턴≈5s). 마우스 포인트 없으면 현재 이동 방향, 없으면 무시.
    ///   좌클릭(조준 모드가 아닐 때) 또는 C = 평타(9, 방향 스킬샷 — skills 배열과 별도 전용 핸들러).
    ///           마우스 지면 포인트를 그대로 SendActionAimed(9, tx, ty) 로 보내면 서버가 "방향"만 취해
    ///           고정 사거리 라인 공격을 낸다(조준 모드 불요, 쿨 없음). 연타는 0.12s 최소 간격 가드.
    ///   G = 패링(21, 즉시 — 조준/방향 불요, 서버가 facing 판정. cd "parry" 10턴).
    ///   조준 중: 좌클릭 = 발사(평타보다 우선), 같은 키 재입력 = 현재 레티클 위치로 즉시 발사(Q 조준→Q 발동),
    ///            Esc = 취소, 다른 조준 스킬 키 = 대상 전환, 우클릭 이동은 그대로 동작(무빙 조준).
    ///            대시/패링은 조준 중에도 조준을 유지한 채 발동.
    ///   입력 버퍼: 스킬/대시/패링 키를 눌렀을 때 유효 쿨이 소량(≤bufferWindowTurns)이면 거부 대신 버퍼링해
    ///            쿨이 풀리는 순간 자동 실행(조준형=조준 진입, 즉발형=발동). 평타는 스로틀에 막히면 1회 버퍼.
    ///            버퍼는 최신 1개만·유효시간 bufferValidTime, 조준 진입/취소 시 초기화. 쿨이 많이 남으면 흔들림.
    ///   Space = 평타에서 제거(재량 결정). 평타 전용 키는 basicAttackKey(기본 C) 하나 — 원하면 인스펙터에서
    ///           KeyCode.Space 로 바꾸거나 KeyCode.None 으로 비활성(좌클릭만 사용) 가능.
    ///
    /// 원칙:
    ///   - 스킬 발동은 즉시 전송(이동보다 우선). 그 턴의 이동 전송은 1회 스킵.
    ///   - 이동도 목표 지정/변경 순간 즉시 전송(다음 스냅샷 대기 없이) + 매 스냅샷 재계산·재전송.
    ///   - 클라이언트 사이드 예측(DealerMotionPredictor)으로 클릭 즉시 로컬 이동 시작 → 서버 보정.
    ///   - 쿨다운 중 스킬(서버 값 vs 클라 예측 쿨 중 큰 값)은 차단 + SkillBarUI 흔들림 피드백.
    ///   - RaidSession.InputEnabled 가 false면(전투 외) 입력 무시 + 조준 취소.
    ///
    /// 씬 부착: 빈 GameObject에 이 컴포넌트만 붙이면 self-contained. viewer/skillBar/moveMarker 는
    ///          비워두면 자동 탐색·생성한다.
    /// </summary>
    public class RaidPlayerController : MonoBehaviour
    {
        // ─────────────── 스킬 바인딩 (확장 가능한 배열) ───────────────

        /// <summary>키 하나 = 하나의 이산 액션. cooldownKey 는 스냅샷 units.cooldowns 의 키.</summary>
        [Serializable]
        public class SkillBinding
        {
            public string label;         // UI 약칭/설명용
            public KeyCode key;          // 입력 키
            public int actionId;         // Python 액션 ID
            public string cooldownKey;   // 스냅샷 cooldowns 키 (없으면 빈 문자열)
            public bool aimed;           // 조준형(좌클릭으로 지점 확정) 여부
            public float range;          // 사거리 (sim 단위, aimed 전용)
            public float aoeRadius;      // AoE 반경 (sim 단위, aimed 전용)
            public int maxCooldown;      // 쿨다운(턴) — 클라이언트 예측 쿨 시작값
            public Color aimColor;       // 조준 시 화면 외곽 발광 색(HDR). 씬 직렬화 누락(alpha 0) 시 팔레트 폴백.
        }

        // ─────────────── 조준 외곽 발광 팔레트 (HDR — 블룸 대응) ───────────────
        // Q 진홍 / W 붉은 대형(더 진하게) / R 궁극 금색 / 평타·폴백 은백.
        private static readonly Color AimCrimson = new Color(1.9f, 0.16f, 0.13f, 1f);  // Q 혈창: 진홍
        private static readonly Color AimDeepRed = new Color(2.4f, 0.06f, 0.05f, 1f);  // W 혈월 낙하: 더 진한 붉은
        private static readonly Color AimGold    = new Color(2.1f, 1.5f, 0.45f, 1f);   // R 처형: 금색
        private static readonly Color AimSilver  = new Color(1.4f, 1.5f, 1.7f, 1f);    // 폴백: 은백

        /// <summary>스킬 조준 외곽색 결정: aimColor.alpha 0(씬 직렬화 누락) 이면 cooldownKey 팔레트로 폴백.</summary>
        private static Color ResolveAimColor(SkillBinding sk)
        {
            if (sk.aimColor.a > 0.001f) return sk.aimColor;   // 코드/인스펙터 지정값 우선
            switch (sk.cooldownKey)                            // 씬 직렬화로 (0,0,0,0) 들어온 경우 폴백
            {
                case "skill":  return AimCrimson;   // Q
                case "skill2": return AimDeepRed;   // W
                case "ult":    return AimGold;      // R
                default:       return AimSilver;
            }
        }

        [Header("Scene Refs (비우면 자동 탐색/생성)")]
        [Tooltip("스냅샷/딜러 Transform 조회에 쓸 씬 조율자.")]
        [SerializeField] private BossGameViewer viewer;
        [Tooltip("하단 스킬바 UI. 쿨다운 흔들림 피드백 대상.")]
        [SerializeField] private SkillBarUI skillBar;
        [Tooltip("클릭 지점 마커. 비우면 런타임 생성.")]
        [SerializeField] private MoveMarker moveMarker;
        [Tooltip("조준 표시기(사거리 링 + AoE 레티클). 비우면 런타임 생성.")]
        [SerializeField] private SkillAimIndicator aimIndicator;

        // 평타(9)/패링(21)은 배열에서 제외 — 대시처럼 전용 즉발 핸들러로 처리(아래 헤더 참고).
        [Header("Skills (설치기 개편 딜러 킷 — 조준형 Q/W/R + 즉시 E)")]
        [SerializeField]
        private SkillBinding[] skills = new SkillBinding[]
        {
            new SkillBinding { label = "혈창 투척", key = KeyCode.Q, actionId = 10, cooldownKey = "skill",   aimed = true,  range = 7f, aoeRadius = 1.8f, maxCooldown = 20,  aimColor = AimCrimson },
            new SkillBinding { label = "혈월 낙하", key = KeyCode.W, actionId = 18, cooldownKey = "skill2",  aimed = true,  range = 9f, aoeRadius = 3.0f, maxCooldown = 40,  aimColor = AimDeepRed },
            new SkillBinding { label = "카운터",   key = KeyCode.E, actionId = 17, cooldownKey = "counter", aimed = false, maxCooldown = 25 },
            new SkillBinding { label = "혈월 처형", key = KeyCode.R, actionId = 20, cooldownKey = "ult",     aimed = true,  range = 9f, aoeRadius = 4.0f, maxCooldown = 200, aimColor = AimGold },
        };

        [Header("Dash (Shift, 즉발 방향기 — skills 배열과 별도 처리)")]
        [Tooltip("대시 입력 키. 기본 LeftShift.")]
        [SerializeField] private KeyCode dashKey = KeyCode.LeftShift;
        [Tooltip("대시 액션 ID (Python 계약: 19). SendActionAimed(19, tx, ty) 로 방향 지점 전달.")]
        [SerializeField] private int dashActionId = 19;
        [Tooltip("대시 쿨다운 스냅샷 키. 서버 cooldowns[\"dash\"].")]
        [SerializeField] private string dashCooldownKey = "dash";
        [Tooltip("대시 클라 예측 쿨다운(턴). 서버 17턴≈5s.")]
        [SerializeField] private int dashCooldown = 17;
        [Tooltip("대시 이동 거리(sim 단위 — 서버가 클램프). 마우스 폴백 조준점/체감 임펄스 산정용.")]
        [SerializeField] private float dashDistanceSim = 2.5f;

        [Header("Basic Attack (좌클릭/C, 방향 스킬샷 — skills 배열과 별도 처리)")]
        [Tooltip("평타 보조 키. 좌클릭(조준 모드가 아닐 때)과 함께 동작. 비활성화하려면 KeyCode.None.")]
        [SerializeField] private KeyCode basicAttackKey = KeyCode.C;
        [Tooltip("평타 액션 ID (Python 계약: ATTACK_BASIC=9). 마우스 지면 포인트를 방향 지시점으로 전송.")]
        [SerializeField] private int basicAttackActionId = 9;
        [Tooltip("연타 스팸 가드 — 클라 최소 전송 간격(초). 서버는 최신값만 쓰므로 중복은 무해하나 부하 절감.")]
        [SerializeField] private float basicAttackMinInterval = 0.12f;

        [Header("Parry (G, 즉발 — 조준/방향 불요, 서버가 facing 판정)")]
        [Tooltip("패링 입력 키. 기본 G.")]
        [SerializeField] private KeyCode parryKey = KeyCode.G;
        [Tooltip("패링 액션 ID (Python 계약: PARRY=21).")]
        [SerializeField] private int parryActionId = 21;
        [Tooltip("패링 쿨다운 스냅샷 키. 서버 cooldowns[\"parry\"].")]
        [SerializeField] private string parryCooldownKey = "parry";
        [Tooltip("패링 클라 예측 쿨다운(턴). 서버 10턴.")]
        [SerializeField] private int parryCooldown = 10;

        [Header("Input Buffering (씹힘 방지 — 격투/플랫포머식)")]
        [Tooltip("유효 쿨다운이 이 턴 이하로 남았을 때 키 입력을 거부/흔들림 대신 버퍼링(쿨 풀리는 순간 자동 실행). 기본 1턴≈0.3s.")]
        [SerializeField] private int bufferWindowTurns = 1;
        [Tooltip("버퍼 유효시간(초). 이 시간을 넘기면 버퍼 폐기(씹힘 대신 만료). 기본 0.35s.")]
        [SerializeField] private float bufferValidTime = 0.35f;

        [Header("Movement")]
        [Tooltip("지면 평면 높이(y). 스냅샷 렌더는 y=0 평면을 쓴다.")]
        [SerializeField] private float groundY = 0f;
        [Tooltip("이 거리(연속좌표 단위) 이내면 도달로 보고 정지.")]
        [SerializeField] private float reachThreshold = 0.6f;

        [Header("Client-side Prediction")]
        [Tooltip("클릭 즉시 로컬 예측 이동(서버 보정) 활성화. 딜러 UnitView에 DealerMotionPredictor 자동 부착.")]
        [SerializeField] private bool enablePrediction = true;

        // ─────────────── 8방향 액션 테이블 ───────────────
        // dir = 월드 XZ 평면에서 해당 액션이 딜러를 밀어내는 방향.
        // (기존 DealerPlayerController 의 W/S 반전 매핑과 렌더 좌표계 ContinuousToWorld 로부터 도출)
        //   MoveUp(1)  → 월드 -Z,  MoveDown(2)  → 월드 +Z
        //   MoveLeft(3)→ 월드 -X,  MoveRight(4) → 월드 +X, 대각은 합성.
        private const float Diag = 0.70710678f;
        private static readonly (int action, Vector2 dir)[] MoveDirs =
        {
            (1, new Vector2(0f, -1f)),          // Up
            (2, new Vector2(0f,  1f)),          // Down
            (3, new Vector2(-1f, 0f)),          // Left
            (4, new Vector2( 1f, 0f)),          // Right
            (5, new Vector2(-Diag, -Diag)),     // UpLeft
            (6, new Vector2( Diag, -Diag)),     // UpRight
            (7, new Vector2(-Diag,  Diag)),     // DownLeft
            (8, new Vector2( Diag,  Diag)),     // DownRight
        };

        // ─────────────── 내부 상태 ───────────────
        private bool _hasDestination;                 // 목표 지점 활성 여부
        private Vector3 _destination;                 // 월드 목표 지점
        private bool _skipMoveThisTurn;               // 스킬을 쐈으니 이번 턴 이동 전송 스킵
        private bool _awaitServerArrival;             // 표시 도착 후 서버 도착 대기(표시 위치 핀 고정 중)
        private bool _wasMoving;                       // 직전에 이동 명령을 보내고 있었는지(도달 STOP 판정용)
        private bool _snapshotSubscribed;
        private UnitData _dealerData;                 // 최신 스냅샷의 딜러 유닛(쿨다운 조회용)
        private DealerMotionPredictor _predictor;     // 딜러 클라이언트 사이드 예측기(딜러 스폰 시 부착)
        private DealerAnimationDriver _animDriver;     // 딜러 액션별 원샷 애니 재생기(딜러 스폰 시 부착)
        private int _lastSentMoveAction = -1;         // 즉시 전송 중복 방지(드래그 중 같은 방향이면 스킵)
        private SkillBinding _aiming;                 // 현재 조준 중인 스킬 (null = 조준 아님)
        private float _lastBasicAttackTime = -999f;    // 평타 연타 스팸 가드용 마지막 전송 시각(unscaled)

        // ── 입력 버퍼(씹힘 방지) ── 최신 1개만 유지(덮어쓰기). 조준 진입/취소 시 초기화.
        private enum BufferKind { None, Skill, Dash, Parry }
        private BufferKind _bufKind = BufferKind.None;   // 버퍼된 액션 종류
        private SkillBinding _bufSkill;                  // BufferKind.Skill 일 때 대상 스킬
        private float _bufTime;                          // 버퍼된 시각(unscaled) — bufferValidTime 초과 시 폐기
        private bool _bufBasicAttack;                    // 스로틀에 막힌 평타 1회 버퍼
        private float _bufBasicTime;                     // 평타 버퍼 시각(unscaled)

        private void Awake()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            EnsureMoveMarker();
            EnsureSkillBar();
            EnsureAimIndicator();
        }

        private void OnEnable()  => TrySubscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        private void EnsureMoveMarker()
        {
            if (moveMarker != null) return;
            var go = new GameObject("MoveMarker");
            moveMarker = go.AddComponent<MoveMarker>();
        }

        private void EnsureSkillBar()
        {
            if (skillBar != null) return;
            skillBar = FindFirstObjectByType<SkillBarUI>();
            if (skillBar != null) return;
            var go = new GameObject("SkillBarUI");
            skillBar = go.AddComponent<SkillBarUI>();
        }

        private void EnsureAimIndicator()
        {
            if (aimIndicator != null) return;
            var go = new GameObject("SkillAimIndicator");
            aimIndicator = go.AddComponent<SkillAimIndicator>();
        }

        // ─────────────── 스냅샷 구독 (턴 경계) ───────────────

        private void TrySubscribe()
        {
            if (_snapshotSubscribed) return;
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            if (viewer == null) return;
            viewer.OnSnapshotApplied += OnSnapshotApplied;   // B1 계약: Action<BossSnapshot> — 스냅샷 파라미터 수신
            _snapshotSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_snapshotSubscribed || viewer == null) return;
            viewer.OnSnapshotApplied -= OnSnapshotApplied;
            _snapshotSubscribed = false;
        }

        /// <summary>스냅샷(턴) 도착 시점: 딜러 데이터 캐시 + 예측기 서버 보정 + 이동 방향 양자화 전송.</summary>
        private void OnSnapshotApplied(BossSnapshot snap)
        {
            CacheDealerData(snap);

            // 예측기 서버 권위 보정: 새 서버 위치(월드)로 오프셋 재계산(서버 이동분 차감).
            if (_predictor != null && _dealerData != null && viewer != null)
                _predictor.OnServerSnapshot(
                    viewer.ContinuousToWorld(_dealerData.x, _dealerData.y), _dealerData.alive);

            if (!InputActive())
            {
                // 전투 외: 진행 중이던 이동 목표를 정리(다음 전투에서 잔상 방지).
                _skipMoveThisTurn = false;
                return;
            }

            // 스킬을 이번 턴에 이미 전송했다면: 스킬은 방금 스냅샷의 턴에서 소비됐으므로
            // "지금" 이동을 재전송하는 것은 안전하다(다음 턴 액션이 됨). 기존처럼 return 으로
            // 한 스냅샷을 더 쉬면 서버가 불필요하게 1턴 더 정지 → 예측과 어긋나 고무줄 체감.
            if (_skipMoveThisTurn)
                _skipMoveThisTurn = false;   // 소비만 하고 곧바로 이동 재전송으로 진행

            SendMoveIfNeeded();
        }

        private void CacheDealerData(BossSnapshot snap)
        {
            _dealerData = null;
            if (snap == null || snap.units == null) return;
            foreach (var u in snap.units)
            {
                if (u != null && u.role == (int)PartyRole.Dealer) { _dealerData = u; break; }
            }
        }

        // ─────────────── 매 프레임 입력 ───────────────

        private void Update()
        {
            if (!_snapshotSubscribed) TrySubscribe();
            EnsurePredictor();
            EnsureAnimDriver();

            if (!InputActive())
            {
                // 전투 외에는 마커/목표를 즉시 숨김 + 예측 의도/조준 해제.
                if (_hasDestination)
                {
                    _hasDestination = false; _wasMoving = false; _lastSentMoveAction = -1;
                    moveMarker?.HideImmediate();
                }
                _awaitServerArrival = false;
                _predictor?.ReleaseHold();
                _predictor?.ClearMoveIntent();
                if (_aiming != null) CancelAiming();
                ResetInputBuffers();
                return;
            }

            // 0) 입력 버퍼 소화: 쿨/스로틀이 풀리는 순간 버퍼된 액션 자동 실행(씹힘 방지).
            ProcessInputBuffer();

            // 1) 스킬 키: 조준형은 조준 진입/재입력 발동/전환, 즉시형은 즉시 전송. 쿨 소량이면 버퍼.
            HandleSkillInput();

            // 2) 평타(좌클릭/C): 조준 모드가 아닐 때만 좌클릭 소비. 조준 모드 중 좌클릭은 (3)의 발사 확정이 우선.
            //    → HandleAimingMode 보다 먼저 호출: 발사 확정 프레임에 CancelAiming 후 좌클릭이 평타로 새는 것을 방지.
            HandleBasicAttackInput();

            // 3) 조준 모드: 레티클 갱신(무빙 조준) + 좌클릭 발사 + Esc 취소.
            HandleAimingMode();

            // 4) Shift = 대시: 조준 모드와 무관하게 즉발(조준 중이면 조준 유지). 쿨다운 가드.
            HandleDashInput();

            // 5) G = 패링: 즉발(조준/방향 불요, 서버가 facing 판정). 쿨다운 가드.
            HandleParryInput();

            // 6) 우클릭(누름/드래그): 목표 지점 갱신 + 마커 표시 + 즉시 전송. (조준 중에도 동작)
            HandleMoveInput();

            // 7) 도달 판정(예측 표시 위치 기준, 매 프레임): 마커가 늦게 꺼지는 느낌 방지.
            if (_hasDestination && TryGetDealerDisplayPos(out var cur))
            {
                Vector2 delta = new Vector2(_destination.x - cur.x, _destination.z - cur.z);
                if (delta.magnitude <= ReachWorld()) HandleArrival();
            }
        }

        /// <summary>딜러 UnitView 를 찾은 시점에 예측기 부착(1회).</summary>
        private void EnsurePredictor()
        {
            if (!enablePrediction || _predictor != null) return;
            if (viewer == null || !viewer.TryGetDealerTransform(out var dealer) || dealer == null) return;
            _predictor = dealer.GetComponent<DealerMotionPredictor>();
            if (_predictor == null) _predictor = dealer.gameObject.AddComponent<DealerMotionPredictor>();
        }

        /// <summary>딜러 액션별 원샷 애니 재생기 부착(1회). 프리팹에 이미 있으면 그것을 재사용.
        /// 프리팹 인스턴스면 [SerializeField] 클립(예: Roll)이 이미 배선돼 있고, 런타임 AddComponent
        /// 폴백 시엔 컨트롤러 animationClips 이름 조회(전략 a)로 확보 가능한 클립만 재생한다.</summary>
        private void EnsureAnimDriver()
        {
            if (_animDriver != null) return;
            if (viewer == null || !viewer.TryGetDealerTransform(out var dealer) || dealer == null) return;
            _animDriver = dealer.GetComponent<DealerAnimationDriver>();
            if (_animDriver == null) _animDriver = dealer.gameObject.AddComponent<DealerAnimationDriver>();
        }

        private bool InputActive()
        {
            var s = RaidSession.Instance;
            return s != null && s.InputEnabled;
        }

        // ─────────────── 스킬 입력 (조준 상태 머신) ───────────────

        private void HandleSkillInput()
        {
            if (skills == null) return;
            foreach (var sk in skills)
            {
                if (sk == null) continue;
                if (!WasKeyPressedThisFrame(sk.key)) continue;

                // 같은 키 재입력 = 현재 레티클 위치로 즉시 발사 (Q 조준 → Q 발동). 취소는 Esc.
                if (sk.aimed && _aiming == sk)
                {
                    if (aimIndicator != null) FireAimedSkill(sk, aimIndicator.ClampedAimPoint);
                    break;
                }

                // 쿨다운 3분기: 0=즉시 실행 / 소량(≤버퍼창)=버퍼 / 많이 남음=거부(흔들림).
                int cd = GetEffectiveCooldown(sk);
                if (cd == 0)            ExecuteSkill(sk);
                else if (cd <= bufferWindowTurns) BufferSkill(sk);
                else                    skillBar?.Shake(sk.cooldownKey);
                break;                  // 한 프레임에 스킬 하나만
            }
        }

        /// <summary>스킬 실행(쿨 0 확인 후): 조준형은 조준 진입, 즉시형은 전송 + 예측 쿨.</summary>
        private void ExecuteSkill(SkillBinding sk)
        {
            if (sk.aimed)
            {
                EnterAiming(sk);   // 조준 진입 (다른 스킬 조준 중이었으면 대상 전환)
            }
            else
            {
                RaidSession.Instance?.SendAction(sk.actionId);
                skillBar?.StartPredictedCooldown(sk.cooldownKey, sk.maxCooldown);
                _skipMoveThisTurn = true;   // 이번 턴 이동 전송 스킵(스킬 우선)
                _predictor?.ClearMoveIntent();   // 정직한 예측: 서버가 이 턴 공격(정지)하므로 예측도 정지
            }
        }

        // ─────────────── 입력 버퍼(씹힘 방지) ───────────────

        private void BufferSkill(SkillBinding sk)
        {
            _bufKind = BufferKind.Skill;
            _bufSkill = sk;
            _bufTime = Time.unscaledTime;
        }

        private void BufferAction(BufferKind kind)
        {
            _bufKind = kind;
            _bufSkill = null;
            _bufTime = Time.unscaledTime;
        }

        /// <summary>조준 진입/취소/전투 종료 시 모든 버퍼 초기화.</summary>
        private void ResetInputBuffers()
        {
            _bufKind = BufferKind.None;
            _bufSkill = null;
            _bufBasicAttack = false;
        }

        /// <summary>
        /// 매 프레임 버퍼 소화: 유효시간 초과 폐기, 쿨/스로틀이 풀리는 순간 버퍼된 액션 자동 실행.
        /// 스킬(조준형이면 조준 진입/즉발이면 발동)·대시·패링·평타 스로틀 버퍼를 처리.
        /// </summary>
        private void ProcessInputBuffer()
        {
            float now = Time.unscaledTime;

            // 평타 스로틀 버퍼: 스로틀 해제 즉시 발사(유효창 내).
            if (_bufBasicAttack)
            {
                if (now - _bufBasicTime > bufferValidTime) _bufBasicAttack = false;
                else if (now - _lastBasicAttackTime >= basicAttackMinInterval)
                {
                    _bufBasicAttack = false;
                    FireBasicAttack();
                }
            }

            if (_bufKind == BufferKind.None) return;
            if (now - _bufTime > bufferValidTime) { _bufKind = BufferKind.None; _bufSkill = null; return; }   // 유효창 초과 폐기

            switch (_bufKind)
            {
                case BufferKind.Skill:
                    if (_bufSkill != null && GetEffectiveCooldown(_bufSkill) == 0)
                    {
                        var sk = _bufSkill; _bufKind = BufferKind.None; _bufSkill = null;
                        ExecuteSkill(sk);   // 조준형=조준 진입 / 즉발형=발동
                    }
                    break;
                case BufferKind.Dash:
                    if (GetDashCooldown() == 0) { _bufKind = BufferKind.None; DoDash(); }
                    break;
                case BufferKind.Parry:
                    if (GetParryCooldown() == 0) { _bufKind = BufferKind.None; DoParry(); }
                    break;
            }
        }

        /// <summary>조준 모드: 레티클 갱신(무빙 조준) + 좌클릭 발사 + Esc 취소.</summary>
        private void HandleAimingMode()
        {
            if (_aiming == null) return;

            if (WasKeyPressedThisFrame(KeyCode.Escape)) { CancelAiming(); return; }

            // 레티클 갱신: 딜러 표시 위치(예측 포함) 기준 사거리 클램프.
            if (aimIndicator != null &&
                TryGetDealerDisplayPos(out var dealerPos) && TryGetGroundPoint(out var mouse))
            {
                aimIndicator.UpdateAim(dealerPos, mouse);
            }

            // 좌클릭 = 발사.
            if (ReadLeftMouseDown() && aimIndicator != null)
                FireAimedSkill(_aiming, aimIndicator.ClampedAimPoint);
        }

        /// <summary>조준 모드 진입/전환: 레티클 표시 + 스킬바 하이라이트.</summary>
        private void EnterAiming(SkillBinding sk)
        {
            _aiming = sk;
            float cell = viewer != null ? viewer.cellSize : 1f;
            aimIndicator?.Show(sk.range * cell, sk.aoeRadius * cell, ResolveAimColor(sk));
            skillBar?.SetAimHighlight(sk.cooldownKey);
            ResetInputBuffers();   // 조준 진입 시 버퍼 초기화
        }

        /// <summary>조준 취소(Esc/전투 종료).</summary>
        private void CancelAiming()
        {
            _aiming = null;
            aimIndicator?.Hide();
            skillBar?.SetAimHighlight(null);
            ResetInputBuffers();   // 조준 취소 시 버퍼 초기화
        }

        /// <summary>조준 발사: 클램프 지점을 sim 좌표로 변환해 SendActionAimed + 예측 쿨 시작.</summary>
        private void FireAimedSkill(SkillBinding sk, Vector3 worldPoint)
        {
            Vector2 sim = WorldToSim(worldPoint);
            RaidSession.Instance?.SendActionAimed(sk.actionId, sim.x, sim.y);
            skillBar?.StartPredictedCooldown(sk.cooldownKey, sk.maxCooldown);
            _skipMoveThisTurn = true;   // 이번 턴 이동 전송 스킵(스킬 우선)
            _predictor?.ClearMoveIntent();   // 정직한 예측: 서버가 이 턴 공격(정지)하므로 예측도 정지

            // 시전 방향 즉시 바라보기(서버 facing 과 시각 일치 — "쓴 방향으로 즉시 전환").
            if (_predictor != null && TryGetDealerDisplayPos(out var castFrom))
                _predictor.FaceCast(worldPoint - castFrom);

            CancelAiming();
        }

        // ─────────────── 대시 (Shift, 즉발 방향기) ───────────────

        /// <summary>
        /// Shift 대시: 조준 모드 없이 즉발. 방향 지점(sim)을 SendActionAimed(19, tx, ty)로 전송하고
        /// 서버가 그 방향으로 거리 클램프해 이동한다. 방향 우선순위: 마우스 지면 포인트 → 현재 이동
        /// 방향 → (둘 다 없으면) 무시. 쿨다운 중이면 스킬바 흔들림. 조준 중이어도 조준을 유지.
        /// </summary>
        private void HandleDashInput()
        {
            if (!WasKeyPressedThisFrame(dashKey)) return;

            // 쿨다운 3분기: 0=즉시 대시 / 소량(≤버퍼창)=버퍼 / 많이 남음=흔들림.
            int cd = GetDashCooldown();
            if (cd == 0)                     DoDash();
            else if (cd <= bufferWindowTurns) BufferAction(BufferKind.Dash);
            else                             skillBar?.Shake(dashCooldownKey);
        }

        /// <summary>대시 실행(쿨 0 확인 후): 방향 지점 전송 + 예측 임펄스. 방향 없으면 무시(false).</summary>
        private bool DoDash()
        {
            if (!TryGetDashTarget(out Vector2 sim, out Vector3 worldDir)) return false;

            RaidSession.Instance?.SendActionAimed(dashActionId, sim.x, sim.y);
            skillBar?.StartPredictedCooldown(dashCooldownKey, dashCooldown);

            // 체감: 화면이 먼저 훅 나가도록 예측기에 즉시 임펄스(서버가 다음 스냅샷에 따라옴).
            float cell = viewer != null ? viewer.cellSize : 1f;
            _predictor?.DashImpulse(worldDir, dashDistanceSim * cell);

            _skipMoveThisTurn = true;   // 이번 턴 이동 전송 스킵(스킬류 우선). 조준은 유지.
            return true;
        }

        /// <summary>대시 목표(sim 좌표)와 월드 방향 산출. 마우스 지면 포인트 우선, 없으면 현재 이동 방향.</summary>
        private bool TryGetDashTarget(out Vector2 sim, out Vector3 worldDir)
        {
            sim = default;
            worldDir = Vector3.zero;
            if (!TryGetDealerDisplayPos(out var dealerPos)) return false;
            float cell = viewer != null ? viewer.cellSize : 1f;

            // 1순위: 마우스 지면 포인트 방향(실제 포인트를 sim 으로 — 거리 클램프는 서버가 함).
            if (TryGetGroundPoint(out var mouse))
            {
                Vector3 d = mouse - dealerPos; d.y = 0f;
                if (d.sqrMagnitude > 1e-6f)
                {
                    worldDir = d.normalized;
                    sim = WorldToSim(mouse);
                    return true;
                }
            }

            // 2순위: 현재 이동 방향(마지막 전송 이동 액션 1~8). 방향으로 dashDistanceSim 앞선 지점을 조준점으로.
            Vector3 moveDir = DirForAction(_lastSentMoveAction);
            if (moveDir.sqrMagnitude > 1e-6f)
            {
                worldDir = moveDir.normalized;
                sim = WorldToSim(dealerPos + worldDir * (dashDistanceSim * cell));
                return true;
            }

            return false;   // 방향 없음 → 무시
        }

        /// <summary>대시 유효 쿨다운 = max(서버 스냅샷 "dash", 스킬바 클라 예측).</summary>
        private int GetDashCooldown()
        {
            int server = GetCooldown(dashCooldownKey);
            int predicted = skillBar != null ? skillBar.GetEffectiveCooldown(dashCooldownKey) : 0;
            return Mathf.Max(server, predicted);
        }

        /// <summary>월드 좌표 → sim 좌표 (viewer.ContinuousToWorld 의 역변환: 월드 x→sim x, 월드 z→sim y).</summary>
        private Vector2 WorldToSim(Vector3 world)
        {
            float cell = viewer != null ? viewer.cellSize : 1f;
            Vector3 origin = viewer != null ? viewer.gridOrigin : Vector3.zero;
            return new Vector2((world.x - origin.x) / cell, (world.z - origin.z) / cell);
        }

        // ─────────────── 평타 (좌클릭/C, 방향 스킬샷) ───────────────

        /// <summary>
        /// 평타: 좌클릭(조준 모드가 아닐 때) 또는 C. 마우스 지면 포인트를 그대로 SendActionAimed(9, tx, ty)로
        /// 보내면 서버가 그 지점을 "방향 지시점"으로만 취해 고정 사거리 라인 공격을 낸다(조준 모드 불요, 쿨 없음).
        /// 조준 중 좌클릭은 발사 확정이 우선이므로 좌클릭은 조준 모드가 아닐 때만 소비한다(HandleAimingMode 앞에서 호출).
        /// 연타 스팸 가드로 최소 간격(basicAttackMinInterval)을 둔다 — 서버는 최신값만 쓰므로 안전성용.
        /// </summary>
        private void HandleBasicAttackInput()
        {
            bool byKey = basicAttackKey != KeyCode.None && WasKeyPressedThisFrame(basicAttackKey);
            bool byClick = _aiming == null && ReadLeftMouseDown();   // 조준 중 좌클릭은 발사 확정이 우선
            if (!byKey && !byClick) return;

            // 연타 스로틀에 막히면 1회 버퍼(스로틀 해제 즉시 발사) → 빠른 연타 씹힘 제거.
            if (Time.unscaledTime - _lastBasicAttackTime < basicAttackMinInterval)
            {
                _bufBasicAttack = true;
                _bufBasicTime = Time.unscaledTime;
                return;
            }
            FireBasicAttack();
        }

        /// <summary>평타 발사(스로틀 통과 후): 현재 마우스 지면 포인트를 방향 지시점으로 전송.</summary>
        private void FireBasicAttack()
        {
            if (!TryGetGroundPoint(out var pt)) return;

            Vector2 sim = WorldToSim(pt);
            RaidSession.Instance?.SendActionAimed(basicAttackActionId, sim.x, sim.y);
            _lastBasicAttackTime = Time.unscaledTime;
            _skipMoveThisTurn = true;   // 이번 턴 이동 전송 스킵(스킬류 우선). 평타는 쿨 없음 → 예측 쿨 미설정.
            _predictor?.ClearMoveIntent();   // 정직한 예측: 평타 연타(카이팅) 중 서버는 정지 — 예측도 정지(고무줄 방지)

            // 평타도 쏜 방향을 즉시 바라본다(시전 방향 즉시 전환).
            if (_predictor != null && TryGetDealerDisplayPos(out var castFrom))
                _predictor.FaceCast(pt - castFrom);
        }

        // ─────────────── 패링 (G, 즉발) ───────────────

        /// <summary>
        /// G 패링: 즉발. 조준/방향 불요 — 서버가 facing 조건을 판정한다. SendAction(21).
        /// 쿨다운 중이면 스킬바 흔들림 피드백(조준/이동 불변). 조준 중이어도 조준을 유지.
        /// </summary>
        private void HandleParryInput()
        {
            if (!WasKeyPressedThisFrame(parryKey)) return;

            // 쿨다운 3분기: 0=즉시 패링 / 소량(≤버퍼창)=버퍼 / 많이 남음=흔들림.
            int cd = GetParryCooldown();
            if (cd == 0)                     DoParry();
            else if (cd <= bufferWindowTurns) BufferAction(BufferKind.Parry);
            else                             skillBar?.Shake(parryCooldownKey);
        }

        /// <summary>패링 실행(쿨 0 확인 후): 즉발 전송 + 예측 쿨.</summary>
        private void DoParry()
        {
            RaidSession.Instance?.SendAction(parryActionId);
            skillBar?.StartPredictedCooldown(parryCooldownKey, parryCooldown);
            _skipMoveThisTurn = true;   // 이번 턴 이동 전송 스킵(스킬류 우선). 조준은 유지.
            _predictor?.ClearMoveIntent();   // 정직한 예측: 패링 턴 서버 정지 — 예측도 정지
        }

        /// <summary>패링 유효 쿨다운 = max(서버 스냅샷 "parry", 스킬바 클라 예측).</summary>
        private int GetParryCooldown()
        {
            int server = GetCooldown(parryCooldownKey);
            int predicted = skillBar != null ? skillBar.GetEffectiveCooldown(parryCooldownKey) : 0;
            return Mathf.Max(server, predicted);
        }

        /// <summary>유효 쿨다운 = max(서버 스냅샷 값, 스킬바 클라 예측 값).</summary>
        private int GetEffectiveCooldown(SkillBinding sk)
        {
            if (string.IsNullOrEmpty(sk.cooldownKey)) return 0;
            int server = GetCooldown(sk.cooldownKey);
            int predicted = skillBar != null ? skillBar.GetEffectiveCooldown(sk.cooldownKey) : 0;
            return Mathf.Max(server, predicted);
        }

        /// <summary>최신 스냅샷 딜러 cooldowns 에서 남은 턴 조회. 없으면 0.</summary>
        private int GetCooldown(string key)
        {
            if (_dealerData == null || _dealerData.cooldowns == null) return 0;
            return _dealerData.cooldowns.TryGetValue(key, out var v) ? v : 0;
        }

        // ─────────────── 이동 입력 ───────────────

        private void HandleMoveInput()
        {
            bool held = ReadRightMousePressed();
            bool pressedThisFrame = ReadRightMouseDown();
            if (!held) return;
            if (!TryGetGroundPoint(out var pt)) return;

            _destination = pt;
            _hasDestination = true;

            // 새 목적지 = 서버 도착 대기(핀) 해제하고 일반 이동 흐름으로 복귀.
            if (_awaitServerArrival) { _awaitServerArrival = false; _predictor?.ReleaseHold(); }

            // 최초 클릭이면 마커 애니메이션 재생, 드래그 중이면 위치만 갱신.
            if (pressedThisFrame) moveMarker?.Show(pt);
            else moveMarker?.MoveTo(pt);

            // 수정1: 목표 지정/변경 순간 즉시 양자화·전송(다음 스냅샷 대기 없이).
            // Python 수신부는 "최신값만 사용"이라 중복 전송 무해.
            TrySendMoveImmediate(pressedThisFrame);
        }

        /// <summary>클릭/드래그 순간 즉시 이동 전송. 드래그 중 같은 방향이면 스킵(스팸 방지).</summary>
        private void TrySendMoveImmediate(bool force)
        {
            if (_skipMoveThisTurn) return;   // 이번 턴은 스킬 우선(최신값 덮어쓰기 방지)
            if (!_hasDestination) return;
            if (!TryGetDealerDisplayPos(out var cur)) return;

            Vector2 delta = new Vector2(_destination.x - cur.x, _destination.z - cur.z);
            if (delta.magnitude <= ReachWorld()) { HandleArrival(); return; }

            int action = QuantizeToEightDir(delta);
            if (!force && action == _lastSentMoveAction) return;
            SendMove(action);
        }

        /// <summary>목표를 향한 이동 액션을 8방향으로 양자화해 전송. 도달 시 정지. (매 스냅샷 갱신용)</summary>
        private void SendMoveIfNeeded()
        {
            if (!_hasDestination) return;

            // 서버 도착 대기(핀 고정 중): 서버 스냅샷 위치 기준으로 목적지 도달을 판정하고,
            // 도달 전까지 이동을 계속 전송(예측 의도 없이 — 표시 위치는 핀에 고정돼 있음).
            if (_awaitServerArrival)
            {
                if (_dealerData == null) return;
                Vector2 destSim = WorldToSim(_destination);
                Vector2 sd = new Vector2(destSim.x - _dealerData.x, destSim.y - _dealerData.y);
                if (sd.magnitude <= reachThreshold) FinishArrival();
                else SendMoveRaw(QuantizeToEightDir(sd));
                return;
            }

            if (!TryGetDealerDisplayPos(out var cur)) return;

            Vector2 delta = new Vector2(_destination.x - cur.x, _destination.z - cur.z);
            if (delta.magnitude <= ReachWorld())
            {
                HandleArrival();
                return;
            }

            // 매 턴 재계산·재전송: 경로가 꺾이는 경우 방향 갱신.
            SendMove(QuantizeToEightDir(delta));
        }

        /// <summary>이동 액션 전송 + 예측기에 이동 의도 통지.</summary>
        private void SendMove(int action)
        {
            RaidSession.Instance?.SendAction(action);
            _lastSentMoveAction = action;
            _wasMoving = true;
            _predictor?.SetMoveIntent(DirForAction(action));
        }

        /// <summary>
        /// 표시(예측) 위치 도달: 마커 숨김 + 표시 위치를 목적지에 핀 고정하고 "서버 도착 대기"로 전환.
        /// 여기서 곧바로 STAY 를 보내면 서버가 ~1칸 못 미친 곳에 멈추고 예측 오프셋이 감쇠하며
        /// 캐릭터가 뒤로 끌려갔다 — 서버가 목적지에 실제로 도착할 때까지 이동을 계속 보낸다.
        /// </summary>
        private void HandleArrival()
        {
            if (_awaitServerArrival) return;
            _awaitServerArrival = true;
            moveMarker?.HideImmediate();
            _predictor?.HoldAtDestination(new Vector3(_destination.x, 0f, _destination.z));
        }

        /// <summary>서버 도착 확정: STAY 전송 + 목표/핀 해제.</summary>
        private void FinishArrival()
        {
            _awaitServerArrival = false;
            _hasDestination = false;
            _lastSentMoveAction = -1;
            _predictor?.ReleaseHold();
            if (_wasMoving)
            {
                RaidSession.Instance?.SendAction((int)BossActionId.Stay);
                _wasMoving = false;
            }
        }

        /// <summary>예측 의도 없이 서버만 유도하는 이동 전송(도착 핀 고정 중 사용).</summary>
        private void SendMoveRaw(int action)
        {
            RaidSession.Instance?.SendAction(action);
            _lastSentMoveAction = action;
            _wasMoving = true;
        }

        /// <summary>딜러 표시 위치(예측 활성 시 예측 포함, 아니면 서버 렌더 위치).</summary>
        private bool TryGetDealerDisplayPos(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (viewer == null || !viewer.TryGetDealerTransform(out var dealer) || dealer == null) return false;
            pos = _predictor != null ? _predictor.DisplayPosition : dealer.position;
            return true;
        }

        /// <summary>도달 판정 거리(월드 단위).</summary>
        private float ReachWorld()
            => reachThreshold * (viewer != null ? viewer.cellSize : 1f);

        /// <summary>이동 액션(1~8)의 월드 XZ 단위 방향(예측 의도용).</summary>
        private static Vector3 DirForAction(int action)
        {
            foreach (var md in MoveDirs)
                if (md.action == action) return new Vector3(md.dir.x, 0f, md.dir.y);
            return Vector3.zero;
        }

        /// <summary>월드 XZ 방향을 8방향 액션(1~8) 중 최근접으로 양자화(최대 내적).</summary>
        private static int QuantizeToEightDir(Vector2 worldDir)
        {
            Vector2 d = worldDir.normalized;
            int best = (int)BossActionId.Stay;
            float bestDot = float.NegativeInfinity;
            foreach (var md in MoveDirs)
            {
                float dot = Vector2.Dot(d, md.dir);
                if (dot > bestDot) { bestDot = dot; best = md.action; }
            }
            return best;
        }

        // ─────────────── 지면 레이캐스트 (콜라이더 불요) ───────────────

        private bool TryGetGroundPoint(out Vector3 point)
        {
            point = Vector3.zero;
            var cam = Camera.main;
            if (cam == null) return false;

            Ray ray = cam.ScreenPointToRay(ReadMousePosition());
            var plane = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));
            if (plane.Raycast(ray, out float enter))
            {
                point = ray.GetPoint(enter);
                point.y = groundY;
                return true;
            }
            return false;
        }

        // ─────────────── 입력 추상화 (레거시 / 신규 Input System 분기) ───────────────

        private static bool ReadRightMouseDown()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var m = Mouse.current;
            return m != null && m.rightButton.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(1);
#else
            return false;
#endif
        }

        private static bool ReadLeftMouseDown()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var m = Mouse.current;
            return m != null && m.leftButton.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(0);
#else
            return false;
#endif
        }

        private static bool ReadRightMousePressed()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var m = Mouse.current;
            return m != null && m.rightButton.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButton(1);
#else
            return false;
#endif
        }

        private static Vector2 ReadMousePosition()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var m = Mouse.current;
            return m != null ? m.position.ReadValue() : Vector2.zero;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.mousePosition;
#else
            return Vector2.zero;
#endif
        }

        private static bool WasKeyPressedThisFrame(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var kb = Keyboard.current;
            if (kb == null) return false;
            var k = MapKeyCode(key);
            return k != Key.None && kb[k].wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(key);
#else
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        /// <summary>레거시 KeyCode → 신규 Input System Key 매핑(스킬바 확장 대비 문자/숫자 포함).</summary>
        private static Key MapKeyCode(KeyCode kc)
        {
            switch (kc)
            {
                case KeyCode.A: return Key.A; case KeyCode.B: return Key.B; case KeyCode.C: return Key.C;
                case KeyCode.D: return Key.D; case KeyCode.E: return Key.E; case KeyCode.F: return Key.F;
                case KeyCode.G: return Key.G; case KeyCode.H: return Key.H; case KeyCode.I: return Key.I;
                case KeyCode.J: return Key.J; case KeyCode.K: return Key.K; case KeyCode.L: return Key.L;
                case KeyCode.M: return Key.M; case KeyCode.N: return Key.N; case KeyCode.O: return Key.O;
                case KeyCode.P: return Key.P; case KeyCode.Q: return Key.Q; case KeyCode.R: return Key.R;
                case KeyCode.S: return Key.S; case KeyCode.T: return Key.T; case KeyCode.U: return Key.U;
                case KeyCode.V: return Key.V; case KeyCode.W: return Key.W; case KeyCode.X: return Key.X;
                case KeyCode.Y: return Key.Y; case KeyCode.Z: return Key.Z;
                case KeyCode.Alpha1: return Key.Digit1; case KeyCode.Alpha2: return Key.Digit2;
                case KeyCode.Alpha3: return Key.Digit3; case KeyCode.Alpha4: return Key.Digit4;
                case KeyCode.Space: return Key.Space;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.LeftShift: return Key.LeftShift;
                case KeyCode.RightShift: return Key.RightShift;
                default: return Key.None;
            }
        }
#endif
    }
}
