using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif
using Random = UnityEngine.Random;   // System.Random과의 모호성 방지

namespace BossRaid
{
    /// <summary>
    /// 로스트아크 스타일 쿼터뷰(아이소메트릭) 추적 카메라.
    /// Dealer(role==0, 플레이어) 유닛을 자동 추적하며 고정 pitch + 좁은 FOV로 부감을 만든다.
    /// 마우스 휠 줌, SmoothDamp 추적, Perlin 기반 셰이크를 제공.
    /// LateUpdate에서 동작하므로 UnitView의 Lerp 이동 이후에 카메라가 따라간다.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class LostArkCamera : MonoBehaviour
    {
        // ─────────────── 싱글턴 ───────────────
        public static LostArkCamera Instance { get; private set; }

        // ─────────────── 씬 참조 ───────────────
        [Header("Scene Refs")]
        [Tooltip("Dealer Transform 조회에 쓸 씬 조율자. 비워두면 자동 탐색.")]
        [SerializeField] private BossGameViewer viewer;

        // ─────────────── 카메라 앵글/렌즈 ───────────────
        [Header("Camera Angle / Lens")]
        [Tooltip("내려다보는 각도(도). 부감 상향 — 보스(아레나 중앙)까지 화면에 담기게 62° 부근.")]
        [SerializeField] private float pitch = 62f;
        [Tooltip("좌우 회전(요). 로스트아크는 0° 고정.")]
        [SerializeField] private float yaw = 0f;
        [Tooltip("넓은 시야 확보용 FOV(보스가 대부분 화면에 들어오도록 상향).")]
        [SerializeField] private float fov = 30f;

        // ─────────────── 거리 / 줌 ───────────────
        [Header("Distance / Zoom")]
        [Tooltip("기본 추적 거리(타깃 → 카메라). 더 높고 넓은 구도용으로 상향.")]
        [SerializeField] private float distance = 30f;
        [SerializeField] private float minDistance = 18f;
        [SerializeField] private float maxDistance = 44f;
        [Tooltip("마우스 휠 한 노치당 거리 변화량 배율.")]
        [SerializeField] private float zoomSpeed = 40f;
        [Tooltip("목표 거리까지 부드럽게 수렴하는 시간(초).")]
        [SerializeField] private float zoomSmoothing = 0.12f;

        // ─────────────── 추적 ───────────────
        [Header("Follow")]
        [Tooltip("Vector3.SmoothDamp 추적 시간. 작을수록 빠르게 따라감.")]
        [SerializeField] private float smoothTime = 0.18f;
        [Tooltip("타깃 발 위치에서 이만큼 위를 바라봄(가슴/허리 높이). 부감 상향에 맞춰 소폭 상향.")]
        [SerializeField] private float lookAtHeightOffset = 1.5f;

        // ─────────────── 셰이크 ───────────────
        [Header("Shake")]
        [Tooltip("Perlin 노이즈 샘플링 주파수(높을수록 빠르게 떨림).")]
        [SerializeField] private float shakeFrequency = 25f;

        // ─────────────── 내부 상태 ───────────────
        private Camera _cam;
        private Transform _target;              // 현재 추적 중인 Dealer Transform
        private Vector3 _focusPoint;            // 카메라가 바라보는 지점(마지막 위치 유지용)
        private bool _hasFocus;                 // 한 번이라도 타깃을 잡았는지
        private Vector3 _basePosition;          // 셰이크가 섞이지 않은 추적 기준 위치(SmoothDamp 출발점)
        private Vector3 _followVelocity;        // SmoothDamp 속도 캐시(추적용)
        private float _currentDistance;         // 현재 보간된 거리
        private float _distanceVelocity;        // SmoothDamp 속도 캐시(줌용)

        // 셰이크 상태(코루틴 없이 Update 감쇠)
        private float _shakeAmplitude;          // 이번 셰이크의 최초 진폭
        private float _shakeDuration;           // 이번 셰이크의 총 지속
        private float _shakeElapsed;            // 경과 시간
        private Vector2 _shakeSeed;             // Perlin 오프셋(호출마다 랜덤)

        private void Awake()
        {
            Instance = this;
            _cam = GetComponent<Camera>();
            if (_cam != null) _cam.fieldOfView = fov;   // FOV 적용(Awake)

            _currentDistance = Mathf.Clamp(distance, minDistance, maxDistance);
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
        }

        private void OnEnable()
        {
            // 보스 대형 임팩트(페이즈 상승/그로기 진입) → 셰이크 배선.
            BossController.OnBigImpact += HandleBigImpact;
        }

        private void OnDisable()
        {
            BossController.OnBigImpact -= HandleBigImpact;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>BossController.OnBigImpact(amplitude, duration) 구독 핸들러.</summary>
        private void HandleBigImpact(float amplitude, float duration) => Shake(amplitude, duration);

#if UNITY_EDITOR
        private void OnValidate()
        {
            // 에디터에서 값 조절 시 즉시 반영(플레이 중 아니어도 프리뷰).
            if (minDistance > maxDistance) minDistance = maxDistance;
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
            var cam = GetComponent<Camera>();
            if (cam != null) cam.fieldOfView = fov;
        }
#endif

        private void Update()
        {
            // 줌 입력 → 목표 거리 갱신 (입력만 Update, 카메라 이동은 LateUpdate)
            float scroll = ReadScrollDelta();
            if (Mathf.Abs(scroll) > Mathf.Epsilon)
            {
                distance = Mathf.Clamp(distance - scroll * zoomSpeed, minDistance, maxDistance);
            }
        }

        private void LateUpdate()
        {
            // 1) 타깃 확보(스폰 전이면 매 프레임 재시도). 이미 잡은 타깃이 파괴되면 마지막 위치 유지.
            AcquireTargetIfNeeded();

            // 2) 바라볼 지점 결정: 타깃이 살아있으면 갱신, 없으면 마지막 지점 유지.
            if (_target != null)
            {
                _focusPoint = _target.position + Vector3.up * lookAtHeightOffset;
                if (!_hasFocus)
                {
                    // 최초 타깃 확보: 셰이크 미포함 기준 위치를 현재 transform에서 초기화.
                    _basePosition = transform.position;
                    _hasFocus = true;
                }
            }
            if (!_hasFocus) return;   // 아직 한 번도 타깃을 못 잡았으면 카메라를 움직이지 않음

            // 3) 거리 부드럽게 보간(줌 스무딩).
            _currentDistance = Mathf.SmoothDamp(_currentDistance, distance, ref _distanceVelocity, zoomSmoothing);

            // 4) 고정 pitch/yaw로 카메라 back 방향 계산.
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 back = rot * Vector3.back;                 // 카메라가 놓일 방향(타깃 뒤/위)
            Vector3 desiredPos = _focusPoint + back * _currentDistance;

            // 5) SmoothDamp로 목표 위치까지 부드럽게 추적.
            //    출발점은 transform.position이 아닌 _basePosition — 셰이크 오프셋이
            //    다음 프레임 SmoothDamp 속도에 피드백되어 흔들림이 뭉개지는 것 방지.
            _basePosition = Vector3.SmoothDamp(_basePosition, desiredPos, ref _followVelocity, smoothTime);

            // 6) 셰이크 오프셋(감쇠)은 기준 위치 위에만 얹어서 최종 위치/회전 확정.
            Vector3 shake = EvaluateShake();
            transform.position = _basePosition + shake;
            transform.rotation = rot;
        }

        // ─────────────── 타깃 추적 ───────────────

        /// <summary>Dealer Transform을 아직 못 잡았거나 소실됐으면 재조회.</summary>
        private void AcquireTargetIfNeeded()
        {
            if (_target != null) return;
            if (viewer == null)
            {
                viewer = FindFirstObjectByType<BossGameViewer>();
                if (viewer == null) return;
            }
            if (viewer.TryGetDealerTransform(out var t))
                _target = t;
        }

        // ─────────────── 셰이크 ───────────────

        /// <summary>
        /// 카메라 셰이크 시작. 코루틴 없이 LateUpdate에서 선형 감쇠 처리.
        /// 중첩 호출 시 "현재 남은 실효 진폭"보다 큰 요청만 덮어써서 큰 흔들림이 우선한다.
        /// </summary>
        public void Shake(float amplitude, float duration)
        {
            if (amplitude <= 0f || duration <= 0f) return;

            // 현재 진행 중인 셰이크의 남은 실효 진폭(선형 감쇠 기준).
            float currentRemaining = 0f;
            if (_shakeElapsed < _shakeDuration && _shakeDuration > 0f)
                currentRemaining = _shakeAmplitude * (1f - _shakeElapsed / _shakeDuration);

            if (amplitude < currentRemaining) return;   // 더 약한 요청은 무시(큰 값 우선)

            _shakeAmplitude = amplitude;
            _shakeDuration = duration;
            _shakeElapsed = 0f;
            _shakeSeed = new Vector2(Random.value * 100f, Random.value * 100f);
        }

        /// <summary>싱글턴 편의 메서드. 어디서든 흔들기 트리거.</summary>
        public static void ShakeCamera(float amplitude, float duration)
        {
            if (Instance != null) Instance.Shake(amplitude, duration);
        }

        /// <summary>이번 프레임 셰이크 오프셋 계산 + 시간 진행/감쇠.</summary>
        private Vector3 EvaluateShake()
        {
            if (_shakeElapsed >= _shakeDuration || _shakeDuration <= 0f)
                return Vector3.zero;

            // 히트스톱(Time.timeScale ≈ 0.05) 중에도 셰이크는 계속 진동해야 하므로
            // 시간 진행/샘플링 모두 unscaled time 사용. (추적 SmoothDamp는 scaled 유지 — 의도된 연출)
            _shakeElapsed += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(1f - _shakeElapsed / _shakeDuration);   // 선형 감쇠 계수
            float amp = _shakeAmplitude * k;

            // Perlin noise: [-1,1] 범위로 매핑, 축마다 다른 시드 사용.
            float ft = Time.unscaledTime * shakeFrequency;
            float ox = (Mathf.PerlinNoise(_shakeSeed.x, ft) - 0.5f) * 2f;
            float oy = (Mathf.PerlinNoise(_shakeSeed.y, ft) - 0.5f) * 2f;

            // 카메라 로컬 축 기준으로 흔들어 화면 진동처럼 보이게 함.
            return (transform.right * ox + transform.up * oy) * amp;
        }

        // ─────────────── 입력(레거시 / 신규 Input System 양쪽 지원) ───────────────

        /// <summary>
        /// 마우스 휠 스크롤 델타를 정규화해 반환(+면 확대/가까이, -면 축소/멀리).
        /// Active Input Handling 설정에 따라 레거시/신규를 컴파일 분기.
        /// </summary>
        private float ReadScrollDelta()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            // 신규 Input System 전용 빌드: Mouse.current.scroll (노치당 대략 ±120).
            var mouse = Mouse.current;
            if (mouse == null) return 0f;
            return mouse.scroll.ReadValue().y * 0.01f;   // 레거시(±0.1) 스케일에 근사
#elif ENABLE_LEGACY_INPUT_MANAGER
            // 레거시 Input Manager: 노치당 대략 ±0.1.
            return Input.GetAxis("Mouse ScrollWheel");
#else
            return 0f;
#endif
        }
    }
}
