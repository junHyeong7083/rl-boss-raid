using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 딜러 클라이언트 사이드 예측(표준 넷코드 방식).
    /// 클릭 즉시 로컬에서 이동을 시작하고, 매 스냅샷 서버 권위 위치로 부드럽게 보정한다.
    ///
    /// 동작 원리:
    ///   UnitView 는 예측 오염 없는 "권위 렌더 위치"를 AuthoritativePosition 으로 노출한다.
    ///   본 컴포넌트는 LateUpdate 에서 그 값을 읽어 예측 오프셋을 더한 "표시 위치"로 transform.position
    ///   을 덮어쓴다(UnitView.Update 이후 실행). UnitView 는 이 오염된 transform 을 회전/보간에 쓰지 않고
    ///   내부 _authPos 로 계산하므로 이중 쓰기 충돌(떨림)이 사라진다.
    ///   LostArkCamera 도 LateUpdate 에서 딜러를 읽으므로 DefaultExecutionOrder(-50)로
    ///   예측기가 카메라보다 먼저 실행되게 한다.
    ///
    /// 보정 규칙:
    ///   - 이동 의도(SetMoveIntent) 동안 오프셋을 의도 방향으로 서버 속도만큼 전진(상한 maxOffset).
    ///   - 스냅샷 도착 시 오프셋 = 표시위치 − 권위 렌더 위치(AuthoritativePosition) 로 재계산 + 상한 클램프.
    ///     권위 위치는 보간이 진행되며 새 목표로 이어지므로, 오프셋이 자연히 소화되어 스냅샷 순간의
    ///     후방 점프가 사라진다. 단, 표시위치 − 새 서버 목표 오차가 크면 오차 크기에 따라 두 단계로 처리:
    ///       · snapErrorThreshold(2.6) 이상 & hardSnapThreshold(3.0) 미만(서버가 이동 거부: 충돌/경직):
    ///         '보정 모드'(_reconcile) — 즉시 오프셋 0 스냅(=순간이동 체감)이 아니라 LateUpdate 에서
    ///         오프셋을 0 으로 지수 수렴(시간상수 reconcileTime 0.12s)해 부드럽게 제자리로 당긴다.
    ///       · hardSnapThreshold(3.0) 이상(에피소드 리셋/텔레포트: 한 턴에 큰 점프): 진짜 스냅(오프셋 0).
    ///   - 의도 해제 시 지수 감쇠(decayTime)로 오프셋 0 수렴.
    ///   - InputEnabled false / 딜러 사망 시 예측 비활성 + 오프셋 즉시 0.
    ///
    /// 부착: RaidPlayerController 가 딜러 UnitView GameObject 를 찾은 시점에 AddComponent.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class DealerMotionPredictor : MonoBehaviour
    {
        [Header("Prediction Params")]
        [Tooltip("예측 이동 속도(유닛/초). 서버 기본: cellSize 1.0/턴 ÷ 턴간격 0.33초 ≈ 3.0")]
        public float moveSpeed = 3.0f;
        [Tooltip("예측 오프셋 상한(유닛). 서버보다 1턴 이상 앞서가지 않게.")]
        public float maxOffset = 1.2f;
        [Tooltip("스냅샷 보정 시 이 오차(유닛) 이상이면 '보정 모드'(즉시 스냅 대신 지수 수렴). 서버 이동 거부 대응. "
               + "순간이동 근절: 대시+이동 한 턴 점프(≈3.5)를 스냅 아닌 보정으로 수용하도록 2.6→3.2 상향.")]
        public float snapErrorThreshold = 3.2f;
        [Tooltip("이 오차(유닛) 이상이면 진짜 순간이동(에피소드 리셋/텔레포트)으로 보고 즉시 스냅(오프셋 0). "
               + "순간이동 근절: 진짜 리셋(거리 5+)만 스냅하도록 3.0→4.2 상향.")]
        public float hardSnapThreshold = 4.2f;
        [Tooltip("보정 모드 오프셋→0 지수 수렴 시간 상수(초). 순간이동 체감 제거용.")]
        public float reconcileTime = 0.12f;
        [Tooltip("의도 해제 시 오프셋 지수 감쇠 시간 상수(초).")]
        public float decayTime = 0.15f;
        [Tooltip("예측 중 이동 의도 방향으로 몸 방향 보정(후향 달리기 아티팩트 방지 + 회전 우선 이동).")]
        public bool alignFacingToIntent = true;
        [Tooltip("몸 방향 보정 정상 Slerp 속도(회전 선점 이후 추종). 12→22 상향으로 회전 우선 체감 강화.")]
        public float facingLerpSpeed = 22f;
        [Tooltip("intent 설정 직후 첫 회전 급가속 각속도(도/초). '클릭하면 먼저 돌고 나서 간다' 체감 — "
               + "부스트 동안 Slerp 대신 이 각속도로 RotateTowards 하여 목표 방향에 즉시 도달.")]
        public float facingSnapDegPerSec = 900f;

        [Header("Dash 특례")]
        [Tooltip("대시 임펄스 후 오프셋 상한 특례 지속(초). 이 창 동안 오프셋 상한이 대시 거리까지 확대되고, "
               + "큰 오차 스냅/감쇠가 억제되어 화면이 먼저 훅 나간 뒤 서버가 스냅샷으로 자연히 따라온다.")]
        public float dashOffsetWindow = 0.5f;
        [Tooltip("대시 활강 시간(초). 임펄스를 한 프레임 점프(텔레포트 체감)가 아니라 이 시간에 걸쳐 고속 이동.")]
        public float dashGlideTime = 0.12f;

        [Header("시전 방향")]
        [Tooltip("스킬/평타/대시 발동 시 그 방향을 즉시 바라보고 이 시간 동안 고정(이동 회전보다 우선).")]
        public float castFaceHoldTime = 0.25f;

        [Header("방향 전환")]
        [Tooltip("이동 의도 방향이 이전과 이 각도(도) 이상 달라지면 기존 오프셋의 '새 방향 수직 성분'을 즉시 감쇠(미끄러지는 관성 제거).")]
        public float turnSharpAngleDeg = 60f;
        [Tooltip("방향 급전환 시 수직 성분 즉시 감쇠율(0.6=60% 제거, 잔여 40%는 기존 감쇠 경로).")]
        public float turnPerpDamp = 0.6f;

        // ─────────────── 내부 상태 ───────────────
        private Vector3 _offset;        // 권위 렌더 위치 대비 예측 오프셋
        private bool _hasIntent;        // 이동 의도 활성 여부
        private Vector3 _intentDir;     // 이동 의도 방향(단위 벡터, XZ)
        private bool _alive = true;     // 최신 스냅샷 딜러 생존 여부
        private Vector3 _displayPos;    // 마지막으로 표시한 위치(스냅샷 보정 기준)
        private bool _hasDisplay;
        private UnitView _unitView;     // 같은 GameObject 의 UnitView(권위 렌더 위치 조회 계약)
        private float _dashMaxOffset;   // 대시 특례 오프셋 상한(월드) — 창 동안만 유효
        private float _dashWindowRemain;// 대시 특례 남은 시간(초, >0 이면 특례 적용 중)
        private bool _reconcile;        // 보정 모드: 큰 오차 시 즉시 스냅 대신 LateUpdate 에서 오프셋을 0 으로 지수 수렴
        private bool _turnBoosting;     // intent 설정 직후 급회전 부스트 진행 중(목표 도달 시 해제)
        private Vector3 _dashGlideDir;  // 대시 활강 방향(단위)
        private float _dashGlideRemain; // 대시 활강 잔여 거리(월드)
        private float _dashGlideSpeed;  // 대시 활강 속도(월드/초)
        private float _castFaceHold;    // 시전 방향 고정 잔여(초)
        private BossGameViewer _viewer; // 예측 표시 위치의 경계/기둥 클램프용(스냅샷 조회)

        private void Awake()
        {
            _unitView = GetComponent<UnitView>();   // 딜러 UnitView 와 동일 GameObject 에 부착됨
            _viewer = FindFirstObjectByType<BossGameViewer>();
        }

        /// <summary>현재 표시(예측 포함) 위치. 마커 도달 판정 등에 사용.</summary>
        public Vector3 DisplayPosition => _hasDisplay ? _displayPos : transform.position;

        // ─────────────── 공개 API (RaidPlayerController 호출) ───────────────

        /// <summary>이동 의도 설정(8방향 양자화된 것과 동일한 월드 방향).</summary>
        public void SetMoveIntent(Vector3 worldDir)
        {
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 1e-6f) { ClearMoveIntent(); return; }
            Vector3 newDir = worldDir.normalized;

            // 방향 전환 관성 제거: 새 의도 방향이 이전과 turnSharpAngleDeg 이상 달라지면
            // 기존 오프셋 중 '새 방향에 수직인 성분'을 즉시 turnPerpDamp 만큼 감쇠한다
            // (평행 성분과 잔여 수직분은 기존 누적/감쇠 경로 유지 → 꺾임이 즉답으로 느껴짐).
            // 대시 창 중에는 선행 임펄스를 훼손하지 않도록 미적용.
            if (_dashWindowRemain <= 0f && _intentDir.sqrMagnitude > 1e-6f && _offset.sqrMagnitude > 1e-6f)
            {
                float dot = Vector3.Dot(_intentDir, newDir);              // 단위벡터 내적 = cos(전환각)
                if (dot < Mathf.Cos(turnSharpAngleDeg * Mathf.Deg2Rad))   // ≥ 임계각이면 급전환
                {
                    Vector3 parallel = Vector3.Dot(_offset, newDir) * newDir;
                    Vector3 perp = _offset - parallel;                    // 새 방향 수직 성분
                    _offset -= perp * Mathf.Clamp01(turnPerpDamp);        // 즉시 60% 제거
                }
            }

            // 회전 우선 이동: 새 의도가 현재 몸 방향과 벌어져 있으면 급회전 부스트를 켠다
            // (LateUpdate 에서 facingSnapDegPerSec 각속도로 즉시 돌린 뒤 이동 추종).
            // 각도가 이미 작으면(직진 유지) 부스트가 첫 프레임에 자연히 종료되어 무해하다.
            if (alignFacingToIntent && Vector3.Angle(transform.forward, newDir) > 5f)
                _turnBoosting = true;

            _castFaceHold = 0f;          // 새 이동 클릭 = 시전 방향 고정 해제(이동 회전이 되찾음)
            _intentDir = newDir;
            _hasIntent = true;
        }

        /// <summary>이동 의도 해제(목표 도달/정지). 오프셋은 지수 감쇠로 0 수렴.</summary>
        public void ClearMoveIntent() => _hasIntent = false;

        /// <summary>
        /// 대시 즉발 임펄스: 오프셋에 방향×거리를 즉시 가산해 화면이 먼저 훅 나가게 한다.
        /// 대시 창(dashOffsetWindow) 동안 오프셋 상한을 distanceWorld 까지 특례로 확대하고,
        /// 그 창 동안엔 큰 오차 스냅/의도 없음 감쇠를 억제해 서버 스냅샷이 따라오며 자연히 소화된다.
        /// RaidPlayerController 가 대시 전송 직후 호출.
        /// </summary>
        public void DashImpulse(Vector3 worldDir, float distanceWorld)
        {
            worldDir.y = 0f;
            if (!_alive || !InputOn()) return;
            if (worldDir.sqrMagnitude < 1e-6f || distanceWorld <= 0f) return;

            _dashMaxOffset = Mathf.Max(maxOffset, distanceWorld);   // 특례 상한(대시 거리까지 허용)
            _dashWindowRemain = dashOffsetWindow;
            _reconcile = false;                                     // 대시 특례가 보정 모드보다 우선

            // 즉시 가산(한 프레임 점프 = 텔레포트 체감)이 아니라 dashGlideTime 에 걸친 고속 활강.
            _dashGlideDir = worldDir.normalized;
            _dashGlideRemain = distanceWorld;
            _dashGlideSpeed = distanceWorld / Mathf.Max(0.02f, dashGlideTime);
            FaceCast(_dashGlideDir);                                 // 대시 방향 즉시 바라보기
            _hasDisplay = true;
        }

        /// <summary>
        /// 시전 방향 즉시 전환: 스킬/평타/대시 발동 순간 캐릭터가 그 방향을 즉시 본다.
        /// castFaceHoldTime 동안 회전을 고정(이동 의도 회전보다 우선). 새 이동 클릭이 오면 해제.
        /// </summary>
        public void FaceCast(Vector3 worldDir)
        {
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.LookRotation(worldDir.normalized, Vector3.up);
            _castFaceHold = castFaceHoldTime;
            _turnBoosting = false;
            if (_unitView != null) _unitView.ExternalRotationOwner = true;
        }

        /// <summary>현재 유효 오프셋 상한: 대시 창 중이면 특례 상한, 아니면 기본 maxOffset.</summary>
        private float EffectiveMaxOffset()
            => _dashWindowRemain > 0f ? Mathf.Max(maxOffset, _dashMaxOffset) : maxOffset;

        /// <summary>
        /// 스냅샷 도착(서버 권위 갱신) 통지. serverWorldPos = 이번 턴 서버 위치의 월드 변환값.
        /// 오프셋을 표시위치 − 권위 렌더 위치(보간 중 위치)로 재계산한다. 큰 오차는 스냅 보정.
        /// </summary>
        public void OnServerSnapshot(Vector3 serverWorldPos, bool alive)
        {
            _alive = alive;
            if (!alive || !InputOn()) { _offset = Vector3.zero; _hasIntent = false; _dashWindowRemain = 0f; _reconcile = false; return; }
            if (!_hasDisplay) return;

            // 대시 특례 창에는 큰 오차 스냅을 억제한다. 대시는 의도적으로 큰 오프셋(≈distanceWorld)을
            // 만들며, 서버는 다음 스냅샷에 그 방향으로 이동해 오차를 소화한다. 창 밖에서만 스냅 보정.
            if (_dashWindowRemain <= 0f)
            {
                // 큰 오차 판정은 새 서버 목표 기준(서버 이동 거부: 충돌/경직 / 또는 텔레포트 대응).
                Vector3 snapErr = _displayPos - serverWorldPos;
                snapErr.y = 0f;
                float err = snapErr.magnitude;
                if (err >= hardSnapThreshold)
                {
                    // 진짜 순간이동(에피소드 리셋/텔레포트, 한 턴 큰 점프) → 즉시 스냅.
                    _offset = Vector3.zero;
                    _reconcile = false;
                    return;
                }
                if (err >= snapErrorThreshold)
                {
                    // 서버 이동 거부(충돌/경직) → 즉시 0 스냅(순간이동 체감) 대신 보정 모드로 전환.
                    // 현재 오프셋을 유지한 채 LateUpdate 에서 reconcileTime 시간상수로 0 에 지수 수렴.
                    _reconcile = true;
                    return;
                }
            }

            // 정상 보정: 오프셋은 '권위 렌더 위치'(보간 중이라 목표보다 뒤에 있음) 기준으로 재계산.
            // 목표 기준으로 잡으면 스냅샷 순간 표시 위치가 (목표−보간위치)만큼 후방 점프하지만,
            // 렌더 위치 기준이면 표시 위치가 이번 프레임에도 연속으로 유지된다.
            _reconcile = false;   // 오차가 정상 범위로 복귀 → 보정 모드 해제
            Vector3 auth = _unitView != null ? _unitView.AuthoritativePosition : serverWorldPos;
            Vector3 newOffset = _displayPos - auth;
            newOffset.y = 0f;
            _offset = Vector3.ClampMagnitude(newOffset, EffectiveMaxOffset());   // 상한 클램프(대시 창 특례)
        }

        // ─────────────── 매 프레임 (UnitView.Update 이후, 카메라 이전) ───────────────

        private void LateUpdate()
        {
            // UnitView 가 이번 프레임 확정한 예측 오염 없는 권위 렌더 위치(명시 계약).
            Vector3 auth = _unitView != null ? _unitView.AuthoritativePosition : transform.position;

            if (_dashWindowRemain > 0f) _dashWindowRemain -= Time.deltaTime;
            float maxOff = EffectiveMaxOffset();

            bool active = _alive && InputOn();
            if (!active)
            {
                _offset = Vector3.zero;    // 전투 외/사망: 예측 비활성 + 즉시 0
                _dashWindowRemain = 0f;
                _reconcile = false;
            }
            else if (_reconcile)
            {
                // 보정 모드: 즉시 스냅 대신 오프셋을 0 으로 지수 수렴(순간이동 체감 제거).
                float k = reconcileTime > 0f ? Mathf.Exp(-Time.deltaTime / reconcileTime) : 0f;
                _offset *= k;
                if (_offset.sqrMagnitude < 1e-6f) { _offset = Vector3.zero; _reconcile = false; }   // 수렴 완료 → 해제
            }
            else if (_dashGlideRemain > 0f)
            {
                // 대시 활강: 잔여 거리를 dashGlideSpeed 로 소화(한 프레임 점프 금지 — 텔레포트 체감 제거).
                // 히트스톱과 무관하게 실시간 진행(unscaled).
                float step = Mathf.Min(_dashGlideRemain, _dashGlideSpeed * Time.unscaledDeltaTime);
                _offset += _dashGlideDir * step;
                _dashGlideRemain -= step;
                _offset = Vector3.ClampMagnitude(_offset, maxOff);
            }
            else if (_hasIntent)
            {
                _offset += _intentDir * (moveSpeed * Time.deltaTime);
                _offset = Vector3.ClampMagnitude(_offset, maxOff);
            }
            else if (_dashWindowRemain > 0f)
            {
                // 대시 특례 창: 감쇠를 억제해 선행 오프셋을 유지(서버가 스냅샷으로 따라와 소화).
                _offset = Vector3.ClampMagnitude(_offset, maxOff);
            }
            else if (_offset.sqrMagnitude > 1e-8f)
            {
                float k = decayTime > 0f ? Mathf.Exp(-Time.deltaTime / decayTime) : 0f;
                _offset *= k;
                if (_offset.sqrMagnitude < 1e-6f) _offset = Vector3.zero;
            }

            _displayPos = auth + _offset;

            // 예측 표시 위치를 아레나 원형 경계/기둥/보스에 클램프 — 예측이 벽을 뚫고 들어갔다가
            // 서버 거부로 고무줄처럼 당겨지는 '벽 근처 순간이동' 체감 제거. 클램프 후 오프셋 재동기화.
            if (ClampDisplayToWorld(ref _displayPos))
            {
                _offset = _displayPos - auth;
                _offset.y = 0f;
            }

            _hasDisplay = true;
            transform.position = _displayPos;

            // ─── 회전 우선 이동 ───
            // intent 활성 동안 회전 소유권을 예측기가 가진다(UnitView 의 이동/보스 바라보기 Slerp 스킵)
            // → 이중 슬럽 경합 제거. 기존 오프셋 게이트(_offset.sqrMagnitude>0.01)를 제거해,
            // 오프셋이 아직 쌓이기 전(클릭 직후)에도 즉시 회전이 선점되도록 한다.
            if (_castFaceHold > 0f) _castFaceHold -= Time.unscaledDeltaTime;

            bool ownRotation = alignFacingToIntent && active && (_hasIntent || _castFaceHold > 0f);
            if (_unitView != null) _unitView.ExternalRotationOwner = ownRotation;
            if (ownRotation)
            {
                if (_castFaceHold > 0f)
                {
                    // 시전 방향 고정 중: FaceCast 가 이미 즉시 회전시켰다 — 유지만 한다.
                }
                else
                {
                    var rot = Quaternion.LookRotation(_intentDir, Vector3.up);
                    if (_turnBoosting)
                    {
                        // 클릭 직후 첫 프레임: 900°/s 급으로 확 돌려 "돌고 나서 간다" 체감.
                        transform.rotation = Quaternion.RotateTowards(transform.rotation, rot, facingSnapDegPerSec * Time.deltaTime);
                        if (Quaternion.Angle(transform.rotation, rot) < 1f) _turnBoosting = false;   // 도달 → 부스트 종료
                    }
                    else
                    {
                        // 회전 선점 이후: 강슬럽으로 방향 추종(방향 급전환에도 빠르게 재정렬).
                        transform.rotation = Quaternion.Slerp(transform.rotation, rot, Time.deltaTime * facingLerpSpeed);
                    }
                }
            }
            else _turnBoosting = false;
        }

        /// <summary>
        /// 예측 표시 위치를 씬 충돌 지오메트리(원형 아레나 경계/살아있는 기둥/보스 몸통)에 클램프.
        /// 서버 판정과 동일 기하를 클라에서 미러링해 예측 과주행(rubber-band)을 원천 차단한다.
        /// </summary>
        /// <returns>클램프로 위치가 바뀌었으면 true.</returns>
        private bool ClampDisplayToWorld(ref Vector3 pos)
        {
            var snap = _viewer != null ? _viewer.LatestSnapshot : null;
            if (snap == null || snap.boss == null) return false;

            float cell = _viewer.cellSize;
            bool changed = false;
            const float unitR = 0.3f;   // 서버 딜러 radius

            // 1) 원형 아레나 경계(축소 포함)
            float arenaR = snap.boss.arena_radius;
            if (arenaR > 0.1f)
            {
                Vector3 center = _viewer.ContinuousToWorld(_viewer.arenaCenterSim.x, _viewer.arenaCenterSim.y);
                Vector3 d = pos - center; d.y = 0f;
                float lim = (arenaR - unitR) * cell;
                if (d.magnitude > lim)
                {
                    Vector3 c = center + d.normalized * lim;
                    pos = new Vector3(c.x, pos.y, c.z);
                    changed = true;
                }
            }

            // 2) 살아있는 기둥 밖으로
            if (snap.pillars != null)
            {
                foreach (var p in snap.pillars)
                {
                    if (p == null || !p.alive) continue;
                    Vector3 pc = _viewer.ContinuousToWorld(p.x, p.y);
                    Vector3 d = pos - pc; d.y = 0f;
                    float lim = (p.radius + unitR - 0.05f) * cell;
                    if (d.magnitude < lim)
                    {
                        Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.right;
                        Vector3 c = pc + dir * lim;
                        pos = new Vector3(c.x, pos.y, c.z);
                        changed = true;
                    }
                }
            }

            // 3) 보스 몸통 밖으로
            {
                Vector3 bc = _viewer.ContinuousToWorld(snap.boss.x, snap.boss.y);
                Vector3 d = pos - bc; d.y = 0f;
                float lim = (snap.boss.radius + unitR - 0.05f) * cell;
                if (lim > 0f && d.magnitude < lim)
                {
                    Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.right;
                    Vector3 c = bc + dir * lim;
                    pos = new Vector3(c.x, pos.y, c.z);
                    changed = true;
                }
            }
            return changed;
        }

        // 예측기 비활성/파괴 시 회전 소유권을 UnitView 에 되돌려준다(플래그 고착 방지).
        private void OnDisable()
        {
            if (_unitView != null) _unitView.ExternalRotationOwner = false;
            _turnBoosting = false;
        }

        private static bool InputOn()
        {
            var s = RaidSession.Instance;
            return s != null && s.InputEnabled;
        }
    }
}
