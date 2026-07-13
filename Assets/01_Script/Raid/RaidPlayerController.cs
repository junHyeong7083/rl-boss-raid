using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace BossRaid
{
    /// <summary>
    /// 로스트아크식 마우스+키보드 조작 딜러 컨트롤러 — 클라이언트 권위 이동.
    ///
    /// 아키텍처(개편):
    ///   싱글플레이라 치팅 무관 → 플레이어(딜러) 위치는 클라이언트가 소유한다. 본 컨트롤러가
    ///   딜러 Transform 을 매 프레임 직접 이동시키고(진짜 무버), 위치를 20Hz 로 sim 좌표(px,py)
    ///   스트림(RaidSession.SendPos)으로 Python 에 보고한다. Python 은 전투 판정(피격/명중/쿨/
    ///   기믹/NPC/보스)만 담당하고 보고 위치를 새니티 클램프 후 딜러 위치로 채택한다.
    ///   기존 0.3초 턴 서버 이동 + 클라 예측(DealerMotionPredictor) 구조는 은퇴했다.
    ///
    /// 조작 매핑:
    ///   우클릭(누름/드래그) = 지면 클릭 지점으로 자유 방향 이동(양자화 없음). 도달 시 정지.
    ///   Q = 혈창 투척(10, 조준형: 사거리7/AoE r1.8, cd "skill")
    ///   W = 혈월 낙하(18, 조준형: 사거리9/AoE r3.0, cd "skill2")
    ///   R = 혈월 처형(20, 조준형: 사거리9/AoE r4.0, cd "ult")
    ///   E = 카운터(17, 즉시, cd "counter")
    ///   Shift = 대시(19, 즉발 방향기: 조준 없이 마우스 지면 포인트 방향으로 0.12s 활강(2.5×cell,
    ///           충돌 클램프) + 서버에 DASH 액션 전송(쿨/이벤트용). cd "dash" 17턴≈5s). 포인트 없으면
    ///           현재 이동 방향, 없으면 무시.
    ///   좌클릭(조준 모드가 아닐 때) 또는 C = 평타(9, 방향 스킬샷 — 서버가 "방향"만 취해 고정 사거리
    ///           라인 공격. 조준 모드 불요, 쿨 없음). 연타는 0.12s 최소 간격 가드.
    ///   G = 패링(21, 즉시 — 조준/방향 불요, 서버가 facing 판정. cd "parry" 10턴).
    ///   조준 중: 좌클릭 = 발사(평타보다 우선), 같은 키 재입력 = 즉시 발사, Esc = 취소, 다른 조준
    ///           스킬 키 = 대상 전환, 우클릭 이동은 그대로(무빙 조준). 대시/패링은 조준 유지한 채 발동.
    ///   입력 버퍼: 스킬/대시/패링 키를 눌렀을 때 유효 쿨이 소량(≤bufferWindowTurns)이면 거부 대신
    ///           버퍼링해 쿨이 풀리는 순간 자동 실행. 평타는 스로틀에 막히면 1회 버퍼.
    ///
    /// 원칙:
    ///   - 이동은 클라가 직접 수행(서버 대기·핀·양자화 없음). 위치는 20Hz 로 보고만 한다.
    ///   - 시전 방향 즉시 전환(FaceCast): 스킬/평타/대시 발동 순간 그 방향을 즉시 바라본다.
    ///   - 회전은 이동 방향으로 rotate-first(부스트 900°/s → 22 slerp). 정지 시 마지막 방향 유지.
    ///   - 쿨다운 중 스킬(서버 값 vs 클라 예측 쿨 중 큰 값)은 차단 + SkillBarUI 흔들림 피드백.
    ///   - RaidSession.InputEnabled 가 false면(전투 외) 입력 무시 + 조준 취소(위치는 유지).
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
        // 아군(플레이어) 스킬 = 청록/하늘/금 (적 보스 진홍과 구분). RaidPalette 단일 소스.
        // Q 청록 / W 하늘 / R 궁극 금색(유지) / 평타·폴백 백청.
        private static readonly Color AimTeal  = RaidPalette.AllyAimTeal;   // Q 혈창: 청록
        private static readonly Color AimSky    = RaidPalette.AllyAimSky;    // W 혈월 낙하: 하늘
        private static readonly Color AimGold   = RaidPalette.AllyAimGold;   // R 처형: 금색
        private static readonly Color AimBasic  = RaidPalette.AllyAimBasic;  // 평타/폴백: 백청

        /// <summary>
        /// 스킬 조준 외곽색 결정: 씬에 직렬화된 낡은 진홍 aimColor 는 무시하고 항상 코드 팔레트를
        /// 사용한다(아군=청록/하늘/금). 씬 파일 편집 없이 팔레트 교체가 바로 반영되도록 하기 위함.
        /// </summary>
        private static Color ResolveAimColor(SkillBinding sk)
        {
            switch (sk.cooldownKey)
            {
                case "skill":  return AimTeal;   // Q 혈창 투척 (청록)
                case "skill2": return AimSky;    // W 혈월 낙하 (하늘)
                case "ult":    return AimGold;   // R 혈월 처형 (금색)
                default:       return AimBasic;  // 평타/폴백 (백청)
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
            new SkillBinding { label = "혈창 투척", key = KeyCode.Q, actionId = 10, cooldownKey = "skill",   aimed = true,  range = 7f, aoeRadius = 1.8f, maxCooldown = 20,  aimColor = AimTeal },
            new SkillBinding { label = "혈월 낙하", key = KeyCode.W, actionId = 18, cooldownKey = "skill2",  aimed = true,  range = 9f, aoeRadius = 3.0f, maxCooldown = 40,  aimColor = AimSky },
            new SkillBinding { label = "카운터",   key = KeyCode.E, actionId = 17, cooldownKey = "counter", aimed = false, maxCooldown = 25 },
            new SkillBinding { label = "혈월 처형", key = KeyCode.R, actionId = 20, cooldownKey = "ult",     aimed = true,  range = 9f, aoeRadius = 4.0f, maxCooldown = 200, aimColor = AimGold },
        };

        [Header("Dash (Shift, 즉발 방향기 — skills 배열과 별도 처리)")]
        [Tooltip("대시 입력 키. 기본 LeftShift.")]
        [SerializeField] private KeyCode dashKey = KeyCode.LeftShift;
        [Tooltip("대시 액션 ID (Python 계약: 19). 서버엔 쿨/이벤트용으로만 전송(이동은 클라 소유).")]
        [SerializeField] private int dashActionId = 19;
        [Tooltip("대시 쿨다운 스냅샷 키. 서버 cooldowns[\"dash\"].")]
        [SerializeField] private string dashCooldownKey = "dash";
        [Tooltip("대시 클라 예측 쿨다운(턴). 서버 17턴≈5s.")]
        [SerializeField] private int dashCooldown = 17;
        [Tooltip("대시 이동 거리(sim 단위 — 클라가 직접 이동. 서버 dash_distance 와 일치).")]
        [SerializeField] private float dashDistanceSim = 2.5f;
        [Tooltip("대시 활강 시간(초). 임펄스를 한 프레임 점프가 아니라 이 시간에 걸쳐 고속 이동.")]
        [SerializeField] private float dashGlideTime = 0.12f;

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

        [Header("Movement (클라이언트 권위 무버)")]
        [Tooltip("지면 평면 높이(y). 스냅샷 렌더는 y=0 평면을 쓴다.")]
        [SerializeField] private float groundY = 0f;
        [Tooltip("이 거리(sim 단위) 이내면 도달로 보고 정지.")]
        [SerializeField] private float reachThreshold = 0.35f;
        [Tooltip("이동 속도(sim/초). 서버 딜러 move_speed 1.0/턴 ÷ 턴 0.3s ≈ 3.33.")]
        [SerializeField] private float moveSpeedSim = 3.33f;
        [Tooltip("즉시 정지 키(로스트아크/LoL 의 S). 이동 중 누르면 그 자리에 정지. None 이면 비활성.")]
        [SerializeField] private KeyCode stopKey = KeyCode.S;

        [Header("Rotation (rotate-first 부스트 감각)")]
        [Tooltip("회전 선점 이후 방향 추종 Slerp 속도.")]
        [SerializeField] private float rotateLerpSpeed = 22f;
        [Tooltip("클릭 직후 첫 회전 급가속 각속도(도/초) — '돌고 나서 간다' 체감.")]
        [SerializeField] private float rotateSnapDegPerSec = 900f;
        [Tooltip("새 이동 방향이 몸 방향과 이 각도(도) 이상 벌어지면 급회전 부스트 발동.")]
        [SerializeField] private float turnBoostAngleDeg = 5f;

        [Header("Cast Facing (시전 방향 즉시 전환)")]
        [Tooltip("스킬/평타/대시 발동 시 그 방향을 즉시 바라보고 이 시간 동안 고정(이동 회전보다 우선).")]
        [SerializeField] private float castFaceHoldTime = 0.25f;

        [Header("Position Reporting (클라 → Python)")]
        [Tooltip("위치 보고 주기(Hz). 20Hz = 0.05s 마다 SendPos(px,py).")]
        [SerializeField] private float posReportHz = 20f;

        // ─────────────── 내부 상태 (이동/무버) ───────────────
        private Transform _dealerTf;                  // 딜러 Transform (컨트롤러가 직접 이동시키는 대상)
        private UnitView _dealerView;                 // 딜러 UnitView (위치/회전 소유권 위임 + 리셋 warp)
        private DealerAnimationDriver _animDriver;     // 딜러 액션별 원샷 애니 재생기(딜러 스폰 시 부착)
        private bool _hasDestination;                 // 목표 지점 활성 여부
        private Vector3 _destination;                 // 월드 목표 지점
        private Vector3 _lastMoveDir;                 // 마지막 이동 월드 방향(대시 폴백/회전용)
        private bool _turnBoosting;                   // 급회전 부스트 진행 중
        private float _castFaceHold;                  // 시전 방향 고정 잔여(초)
        private float _reportTimer;                   // 위치 보고 누적 타이머
        // 대시 활강 상태(unscaled 진행 — 히트스톱 무관)
        private Vector3 _dashGlideDir;
        private float _dashGlideRemain;               // 잔여 활강 거리(월드)
        private float _dashGlideSpeed;                // 활강 속도(월드/초)

        // ─────────────── 내부 상태 (스냅샷/조준) ───────────────
        private bool _snapshotSubscribed;
        private UnitData _dealerData;                 // 최신 스냅샷의 딜러 유닛(쿨다운 조회용)
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

        // ─────────────── 스냅샷 구독 (쿨다운 캐시) ───────────────

        private void TrySubscribe()
        {
            if (_snapshotSubscribed) return;
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            if (viewer == null) return;
            viewer.OnSnapshotApplied += OnSnapshotApplied;   // B1 계약: Action<BossSnapshot>
            _snapshotSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_snapshotSubscribed || viewer == null) return;
            viewer.OnSnapshotApplied -= OnSnapshotApplied;
            _snapshotSubscribed = false;
        }

        /// <summary>스냅샷 도착: 딜러 데이터 캐시(쿨다운 조회용). 위치는 클라가 소유하므로 여기서 이동하지 않는다.</summary>
        private void OnSnapshotApplied(BossSnapshot snap)
        {
            CacheDealerData(snap);
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

        // ─────────────── 매 프레임 입력 + 이동 ───────────────

        private void Update()
        {
            if (!_snapshotSubscribed) TrySubscribe();
            EnsureDealerBinding();
            EnsureAnimDriver();

            if (!InputActive())
            {
                // 전투 외에는 마커/목표 숨김 + 조준 해제. 위치는 유지(다음 에피소드 리셋 warp 이 재배치).
                if (_hasDestination) { _hasDestination = false; moveMarker?.FadeOut(); }
                _dashGlideRemain = 0f;
                if (_aiming != null) CancelAiming();
                ResetInputBuffers();
                return;
            }

            // 0) 입력 버퍼 소화: 쿨/스로틀이 풀리는 순간 버퍼된 액션 자동 실행(씹힘 방지).
            ProcessInputBuffer();

            // 1) 스킬 키: 조준형은 조준 진입/재입력 발동/전환, 즉시형은 즉시 전송. 쿨 소량이면 버퍼.
            HandleSkillInput();

            // 2) 평타(좌클릭/C): 조준 모드가 아닐 때만 좌클릭 소비.
            HandleBasicAttackInput();

            // 3) 조준 모드: 레티클 갱신(무빙 조준) + 좌클릭 발사 + Esc 취소.
            HandleAimingMode();

            // 4) Shift = 대시: 조준 무관하게 즉발(조준 중이면 조준 유지). 쿨다운 가드.
            HandleDashInput();

            // 5) G = 패링: 즉발(조준/방향 불요). 쿨다운 가드.
            HandleParryInput();

            // 5.5) S = 즉시 정지: 이동 목표/경로 클리어(조준·대시는 유지).
            HandleStopInput();

            // 6) 우클릭(누름/드래그): 목표 지점 갱신 + 마커 표시.
            HandleMoveInput();

            // 7) 이동 수행(클라 권위): 목표로 직접 이동 + 대시 활강 + 충돌 클램프 + 회전 + 위치 보고.
            TickMovement();
        }

        /// <summary>딜러 UnitView 를 찾은 시점에 위치/회전 소유권을 위임받는다(1회).</summary>
        private void EnsureDealerBinding()
        {
            if (_dealerView != null) return;
            if (viewer == null || !viewer.TryGetDealerTransform(out var dealer) || dealer == null) return;
            _dealerTf = dealer;
            _dealerView = dealer.GetComponent<UnitView>();
            if (_dealerView != null)
            {
                _dealerView.PositionOwnedExternally = true;   // 서버 보간을 위치에 적용하지 않음(컨트롤러 소유)
                _dealerView.ExternalRotationOwner = true;     // 회전도 컨트롤러 소유
                _dealerView.OnResetWarp = WarpTo;             // 에피소드 리셋(부활/재배치) 시 서버 위치로 스냅
            }
            _lastMoveDir = dealer.forward;
        }

        /// <summary>딜러 액션별 원샷 애니 재생기 부착(1회). 프리팹에 이미 있으면 그것을 재사용.</summary>
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

            // 레티클 갱신: 딜러 위치 기준 사거리 클램프.
            if (aimIndicator != null &&
                TryGetDealerPos(out var dealerPos) && TryGetGroundPoint(out var mouse))
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

            // 시전 방향 즉시 바라보기(FaceCast 감각).
            if (TryGetDealerPos(out var castFrom)) FaceCast(worldPoint - castFrom);

            CancelAiming();
        }

        // ─────────────── 대시 (Shift, 즉발 방향기 — 클라 활강) ───────────────

        /// <summary>
        /// Shift 대시: 조준 모드 없이 즉발. 클라가 방향으로 0.12s 활강(충돌 클램프)하고, 서버엔 DASH
        /// 액션만 전송(쿨/이벤트용 — 이동은 클라 소유). 방향 우선순위: 마우스 지면 포인트 → 현재 이동
        /// 방향 → (둘 다 없으면) 무시. 쿨다운 중이면 스킬바 흔들림. 조준 중이어도 조준 유지.
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

        /// <summary>대시 실행(쿨 0 확인 후): 서버에 DASH 전송(쿨/이벤트) + 클라 활강 개시. 방향 없으면 무시(false).</summary>
        private bool DoDash()
        {
            if (!TryGetDashDir(out Vector3 worldDir)) return false;

            RaidSession.Instance?.SendAction(dashActionId);   // 쿨/이벤트용(서버는 이동 무시)
            skillBar?.StartPredictedCooldown(dashCooldownKey, dashCooldown);

            // 클라 활강 개시: dashDistanceSim×cell 을 dashGlideTime 에 걸쳐 고속 이동(충돌은 TickMovement 클램프).
            float cell = viewer != null ? viewer.cellSize : 1f;
            float distWorld = dashDistanceSim * cell;
            _dashGlideDir = worldDir;
            _dashGlideRemain = distWorld;
            _dashGlideSpeed = distWorld / Mathf.Max(0.02f, dashGlideTime);
            FaceCast(worldDir);   // 대시 방향 즉시 바라보기
            return true;
        }

        /// <summary>대시 방향 산출(단위 월드 벡터). 마우스 지면 포인트 우선, 없으면 현재 이동 방향.</summary>
        private bool TryGetDashDir(out Vector3 worldDir)
        {
            worldDir = Vector3.zero;
            if (!TryGetDealerPos(out var dealerPos)) return false;

            // 1순위: 마우스 지면 포인트 방향.
            if (TryGetGroundPoint(out var mouse))
            {
                Vector3 d = mouse - dealerPos; d.y = 0f;
                if (d.sqrMagnitude > 1e-6f) { worldDir = d.normalized; return true; }
            }
            // 2순위: 현재 이동 방향.
            if (_lastMoveDir.sqrMagnitude > 1e-6f) { worldDir = _lastMoveDir.normalized; return true; }
            return false;   // 방향 없음 → 무시
        }

        /// <summary>대시 유효 쿨다운 = max(서버 스냅샷 "dash", 스킬바 클라 예측).</summary>
        private int GetDashCooldown()
        {
            int server = GetCooldown(dashCooldownKey);
            int predicted = skillBar != null ? skillBar.GetEffectiveCooldown(dashCooldownKey) : 0;
            return Mathf.Max(server, predicted);
        }

        // ─────────────── 평타 (좌클릭/C, 방향 스킬샷) ───────────────

        /// <summary>
        /// 평타: 좌클릭(조준 모드가 아닐 때) 또는 C. 마우스 지면 포인트를 SendActionAimed(9, tx, ty)로
        /// 보내면 서버가 "방향 지시점"으로만 취해 고정 사거리 라인 공격을 낸다(조준 불요, 쿨 없음).
        /// </summary>
        private void HandleBasicAttackInput()
        {
            bool byKey = basicAttackKey != KeyCode.None && WasKeyPressedThisFrame(basicAttackKey);
            bool byClick = _aiming == null && ReadLeftMouseDown();   // 조준 중 좌클릭은 발사 확정이 우선
            if (!byKey && !byClick) return;

            // 연타 스로틀에 막히면 1회 버퍼(스로틀 해제 즉시 발사).
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

            // 평타도 쏜 방향을 즉시 바라본다(시전 방향 즉시 전환).
            if (TryGetDealerPos(out var castFrom)) FaceCast(pt - castFrom);
        }

        // ─────────────── 패링 (G, 즉발) ───────────────

        /// <summary>G 패링: 즉발. 조준/방향 불요 — 서버가 facing 조건을 판정한다. SendAction(21).</summary>
        private void HandleParryInput()
        {
            if (!WasKeyPressedThisFrame(parryKey)) return;

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

        // ─────────────── 즉시 정지 (S — 로스트아크/LoL) ───────────────

        /// <summary>
        /// S 즉시 정지: 이동 목표/경로를 그 자리에서 클리어하고 마커를 페이드아웃한다. 위치는 클라
        /// 권위라 목표만 지우면 다음 위치 보고가 정지 좌표를 보내 서버도 멈춘다(별도 액션 전송 불요).
        /// 이동 정지로 실제 속도가 0 이 되면 UnitView 가 즉시 idle 애니로 전환한다.
        /// - 조준 중(aim)에는 조준을 취소하지 않고 정지만 수행(조준 취소는 우클릭/ESC 유지).
        /// - 대시 활강 중에는 무시(대시는 커밋된 동작).
        /// </summary>
        private void HandleStopInput()
        {
            if (stopKey == KeyCode.None || !WasKeyPressedThisFrame(stopKey)) return;
            if (_dashGlideRemain > 0f) return;   // 대시 글라이드 중 — 무시(커밋된 동작)
            if (_hasDestination)
            {
                _hasDestination = false;   // 이동 의도 클리어
                moveMarker?.FadeOut();      // 즉시 사라짐이 아니라 페이드아웃
            }
            _turnBoosting = false;
        }

        // ─────────────── 이동 입력 ───────────────

        private void HandleMoveInput()
        {
            bool held = ReadRightMousePressed();
            bool pressedThisFrame = ReadRightMouseDown();
            if (!held) return;
            if (!TryGetGroundPoint(out var pt)) return;

            pt.y = groundY;
            _destination = pt;
            _hasDestination = true;

            // 최초 클릭이면 마커 애니메이션 재생, 드래그 중이면 위치만 갱신.
            if (pressedThisFrame) moveMarker?.Show(pt);
            else moveMarker?.MoveTo(pt);

            // 새 방향이 몸 방향과 크게 벌어지면 급회전 부스트(rotate-first).
            if (TryGetDealerPos(out var cur))
            {
                Vector3 dir = _destination - cur; dir.y = 0f;
                if (dir.sqrMagnitude > 1e-6f && _dealerTf != null &&
                    Vector3.Angle(_dealerTf.forward, dir) > turnBoostAngleDeg)
                    _turnBoosting = true;
            }
        }

        // ─────────────── 이동 수행 (클라이언트 권위 무버) ───────────────

        /// <summary>
        /// 목표로 직접 이동(자유 방향) + 대시 활강 + 충돌 클램프 + 회전 + 위치 보고. 매 프레임 호출.
        /// </summary>
        private void TickMovement()
        {
            if (_dealerTf == null) return;
            float dt = Time.deltaTime;
            Vector3 pos = _dealerTf.position;
            Vector3 moveDir = Vector3.zero;

            // 1) 목표 지점으로 등속 이동(자유 방향). 도달 시 정지.
            if (_hasDestination)
            {
                Vector3 to = _destination - pos; to.y = 0f;
                float cell = viewer != null ? viewer.cellSize : 1f;
                float reachWorld = reachThreshold * cell;
                if (to.magnitude <= reachWorld)
                {
                    _hasDestination = false;
                    moveMarker?.FadeOut();   // 도착 시 페이드아웃(즉시 사라짐 아님)
                }
                else
                {
                    moveDir = to.normalized;
                    float speedWorld = moveSpeedSim * cell;
                    float stepDist = Mathf.Min(speedWorld * dt, to.magnitude);
                    pos += moveDir * stepDist;
                    _lastMoveDir = moveDir;
                }
            }

            // 2) 대시 활강(unscaled — 히트스톱 무관). 목표 이동과 합성 가능.
            if (_dashGlideRemain > 0f)
            {
                float step = Mathf.Min(_dashGlideRemain, _dashGlideSpeed * Time.unscaledDeltaTime);
                pos += _dashGlideDir * step;
                _dashGlideRemain -= step;
            }

            // 3) 충돌 클램프(아레나 원/기둥/보스) — 서버 판정과 동일 기하 미러링.
            ClampToWorld(ref pos);
            pos.y = groundY;
            _dealerTf.position = pos;

            // 4) 회전(rotate-first 부스트) — 시전 방향 고정 우선.
            UpdateRotation(moveDir, dt);

            // 5) 위치 보고(20Hz): sim 좌표로 변환해 Python 에 전송.
            _reportTimer += dt;
            float interval = posReportHz > 0.01f ? 1f / posReportHz : 0.05f;
            if (_reportTimer >= interval)
            {
                _reportTimer = 0f;
                if (_dealerData == null || _dealerData.alive)   // 사망 중엔 위치 보고 무의미
                {
                    Vector2 sim = WorldToSim(pos);
                    RaidSession.Instance?.SendPos(sim.x, sim.y);
                }
            }
        }

        /// <summary>rotate-first 회전: 시전 방향 고정 중엔 유지, 이동 중엔 부스트→슬럽, 정지 시 마지막 방향 유지.</summary>
        private void UpdateRotation(Vector3 moveDir, float dt)
        {
            if (_castFaceHold > 0f) { _castFaceHold -= Time.unscaledDeltaTime; return; }   // FaceCast 유지
            if (moveDir.sqrMagnitude < 1e-6f) { _turnBoosting = false; return; }           // 정지: 마지막 방향 유지

            var rot = Quaternion.LookRotation(moveDir.normalized, Vector3.up);
            if (_turnBoosting)
            {
                // 클릭 직후 급회전: 900°/s 로 확 돌려 "돌고 나서 간다" 체감.
                _dealerTf.rotation = Quaternion.RotateTowards(_dealerTf.rotation, rot, rotateSnapDegPerSec * dt);
                if (Quaternion.Angle(_dealerTf.rotation, rot) < 1f) _turnBoosting = false;
            }
            else
            {
                _dealerTf.rotation = Quaternion.Slerp(_dealerTf.rotation, rot, dt * rotateLerpSpeed);
            }
        }

        /// <summary>시전 방향 즉시 전환: 그 방향을 즉시 바라보고 castFaceHoldTime 동안 고정.</summary>
        private void FaceCast(Vector3 worldDir)
        {
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 1e-6f || _dealerTf == null) return;
            _dealerTf.rotation = Quaternion.LookRotation(worldDir.normalized, Vector3.up);
            _castFaceHold = castFaceHoldTime;
            _turnBoosting = false;
        }

        /// <summary>
        /// 에피소드 리셋(부활/재배치) 시 UnitView 가 서버 위치로 스냅하며 호출(OnResetWarp 계약).
        /// 컨트롤러 이동 상태를 서버 위치에 동기화한다(목표/대시/부스트 해제).
        /// </summary>
        public void WarpTo(Vector3 worldPos)
        {
            worldPos.y = groundY;
            if (_dealerTf != null) _dealerTf.position = worldPos;
            _hasDestination = false;
            _dashGlideRemain = 0f;
            _turnBoosting = false;
            _castFaceHold = 0f;
            _reportTimer = 0f;
            moveMarker?.HideImmediate();
        }

        /// <summary>딜러 표시 위치(= 컨트롤러가 소유하는 transform.position).</summary>
        private bool TryGetDealerPos(out Vector3 pos)
        {
            if (_dealerTf != null) { pos = _dealerTf.position; return true; }
            if (viewer != null && viewer.TryGetDealerTransform(out var d) && d != null) { pos = d.position; return true; }
            pos = Vector3.zero;
            return false;
        }

        // ─────────────── 충돌 클램프 (아레나 원/기둥/보스 — 서버 기하 미러링) ───────────────

        /// <summary>
        /// 이동 위치를 씬 충돌 지오메트리(원형 아레나 경계/살아있는 기둥/보스 몸통)에 클램프.
        /// 서버 _clamp_arena/_resolve_player_collision 과 동일 기하를 클라에서 미러링한다.
        /// </summary>
        private void ClampToWorld(ref Vector3 pos)
        {
            var snap = viewer != null ? viewer.LatestSnapshot : null;
            if (snap == null || snap.boss == null) return;

            float cell = viewer.cellSize;
            const float unitR = 0.3f;   // 서버 딜러 radius

            // 1) 원형 아레나 경계(축소 포함)
            float arenaR = snap.boss.arena_radius;
            if (arenaR > 0.1f)
            {
                Vector3 center = viewer.ContinuousToWorld(viewer.arenaCenterSim.x, viewer.arenaCenterSim.y);
                Vector3 d = pos - center; d.y = 0f;
                float lim = (arenaR - unitR) * cell;
                if (d.magnitude > lim)
                {
                    Vector3 c = center + d.normalized * lim;
                    pos = new Vector3(c.x, pos.y, c.z);
                }
            }

            // 2) 살아있는 기둥 밖으로
            if (snap.pillars != null)
            {
                foreach (var p in snap.pillars)
                {
                    if (p == null || !p.alive) continue;
                    Vector3 pc = viewer.ContinuousToWorld(p.x, p.y);
                    Vector3 d = pos - pc; d.y = 0f;
                    float lim = (p.radius + unitR - 0.05f) * cell;
                    if (d.magnitude < lim)
                    {
                        Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.right;
                        Vector3 c = pc + dir * lim;
                        pos = new Vector3(c.x, pos.y, c.z);
                    }
                }
            }

            // 3) 보스 몸통 밖으로
            {
                Vector3 bc = viewer.ContinuousToWorld(snap.boss.x, snap.boss.y);
                Vector3 d = pos - bc; d.y = 0f;
                float lim = (snap.boss.radius + unitR - 0.05f) * cell;
                if (lim > 0f && d.magnitude < lim)
                {
                    Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.right;
                    Vector3 c = bc + dir * lim;
                    pos = new Vector3(c.x, pos.y, c.z);
                }
            }
        }

        /// <summary>월드 좌표 → sim 좌표 (viewer.ContinuousToWorld 의 역변환: 월드 x→sim x, 월드 z→sim y).</summary>
        private Vector2 WorldToSim(Vector3 world)
        {
            float cell = viewer != null ? viewer.cellSize : 1f;
            Vector3 origin = viewer != null ? viewer.gridOrigin : Vector3.zero;
            return new Vector2((world.x - origin.x) / cell, (world.z - origin.z) / cell);
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
