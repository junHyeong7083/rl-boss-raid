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
    ///     후방 점프가 사라진다. 단, 표시위치 − 새 서버 목표 오차가 snapErrorThreshold 이상
    ///     (서버가 이동 거부: 충돌/경직)이면 스냅 보정(오프셋 0).
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
        [Tooltip("스냅샷 보정 시 이 오차(유닛) 이상이면 스냅(오프셋 0). 서버 이동 거부 대응.")]
        public float snapErrorThreshold = 2.0f;
        [Tooltip("의도 해제 시 오프셋 지수 감쇠 시간 상수(초).")]
        public float decayTime = 0.15f;
        [Tooltip("예측 중 이동 의도 방향으로 몸 방향 보정(후향 달리기 아티팩트 방지).")]
        public bool alignFacingToIntent = true;
        [Tooltip("몸 방향 보정 Slerp 속도.")]
        public float facingLerpSpeed = 12f;

        // ─────────────── 내부 상태 ───────────────
        private Vector3 _offset;        // 권위 렌더 위치 대비 예측 오프셋
        private bool _hasIntent;        // 이동 의도 활성 여부
        private Vector3 _intentDir;     // 이동 의도 방향(단위 벡터, XZ)
        private bool _alive = true;     // 최신 스냅샷 딜러 생존 여부
        private Vector3 _displayPos;    // 마지막으로 표시한 위치(스냅샷 보정 기준)
        private bool _hasDisplay;
        private UnitView _unitView;     // 같은 GameObject 의 UnitView(권위 렌더 위치 조회 계약)

        private void Awake()
        {
            _unitView = GetComponent<UnitView>();   // 딜러 UnitView 와 동일 GameObject 에 부착됨
        }

        /// <summary>현재 표시(예측 포함) 위치. 마커 도달 판정 등에 사용.</summary>
        public Vector3 DisplayPosition => _hasDisplay ? _displayPos : transform.position;

        // ─────────────── 공개 API (RaidPlayerController 호출) ───────────────

        /// <summary>이동 의도 설정(8방향 양자화된 것과 동일한 월드 방향).</summary>
        public void SetMoveIntent(Vector3 worldDir)
        {
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 1e-6f) { ClearMoveIntent(); return; }
            _intentDir = worldDir.normalized;
            _hasIntent = true;
        }

        /// <summary>이동 의도 해제(목표 도달/정지). 오프셋은 지수 감쇠로 0 수렴.</summary>
        public void ClearMoveIntent() => _hasIntent = false;

        /// <summary>
        /// 스냅샷 도착(서버 권위 갱신) 통지. serverWorldPos = 이번 턴 서버 위치의 월드 변환값.
        /// 오프셋을 표시위치 − 권위 렌더 위치(보간 중 위치)로 재계산한다. 큰 오차는 스냅 보정.
        /// </summary>
        public void OnServerSnapshot(Vector3 serverWorldPos, bool alive)
        {
            _alive = alive;
            if (!alive || !InputOn()) { _offset = Vector3.zero; _hasIntent = false; return; }
            if (!_hasDisplay) return;

            // 큰 오차 판정은 새 서버 목표 기준(서버 이동 거부: 충돌/경직 대응).
            Vector3 snapErr = _displayPos - serverWorldPos;
            snapErr.y = 0f;
            if (snapErr.magnitude >= snapErrorThreshold)
            {
                _offset = Vector3.zero;                                   // 서버 거부/큰 오차 → 스냅 보정
                return;
            }

            // 오프셋은 '권위 렌더 위치'(보간 중이라 목표보다 뒤에 있음) 기준으로 재계산.
            // 목표 기준으로 잡으면 스냅샷 순간 표시 위치가 (목표−보간위치)만큼 후방 점프하지만,
            // 렌더 위치 기준이면 표시 위치가 이번 프레임에도 연속으로 유지된다.
            Vector3 auth = _unitView != null ? _unitView.AuthoritativePosition : serverWorldPos;
            Vector3 newOffset = _displayPos - auth;
            newOffset.y = 0f;
            _offset = Vector3.ClampMagnitude(newOffset, maxOffset);       // 상한 클램프
        }

        // ─────────────── 매 프레임 (UnitView.Update 이후, 카메라 이전) ───────────────

        private void LateUpdate()
        {
            // UnitView 가 이번 프레임 확정한 예측 오염 없는 권위 렌더 위치(명시 계약).
            Vector3 auth = _unitView != null ? _unitView.AuthoritativePosition : transform.position;

            bool active = _alive && InputOn();
            if (!active)
            {
                _offset = Vector3.zero;   // 전투 외/사망: 예측 비활성 + 즉시 0
            }
            else if (_hasIntent)
            {
                _offset += _intentDir * (moveSpeed * Time.deltaTime);
                _offset = Vector3.ClampMagnitude(_offset, maxOffset);
            }
            else if (_offset.sqrMagnitude > 1e-8f)
            {
                float k = decayTime > 0f ? Mathf.Exp(-Time.deltaTime / decayTime) : 0f;
                _offset *= k;
                if (_offset.sqrMagnitude < 1e-6f) _offset = Vector3.zero;
            }

            _displayPos = auth + _offset;
            _hasDisplay = true;
            transform.position = _displayPos;

            // 예측 중 몸 방향 보정: UnitView 는 (권위위치 − 표시위치) 기준으로 방향을 잡아
            // 예측 오프셋만큼 후향으로 보일 수 있음 → 의도 방향으로 부드럽게 재정렬.
            if (alignFacingToIntent && active && _hasIntent && _offset.sqrMagnitude > 0.01f)
            {
                var rot = Quaternion.LookRotation(_intentDir, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, rot, Time.deltaTime * facingLerpSpeed);
            }
        }

        private static bool InputOn()
        {
            var s = RaidSession.Instance;
            return s != null && s.InputEnabled;
        }
    }
}
