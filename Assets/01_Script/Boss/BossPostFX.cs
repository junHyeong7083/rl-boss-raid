using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BossRaid
{
    /// <summary>
    /// 로스트아크 톤 포스트 프로세싱 런타임 부트스트랩 (혈월 무드).
    /// 씬마다 VolumeProfile 에셋을 만들 필요 없이, 코드로 글로벌 Volume을 구성한다.
    /// 모든 오버라이드는 <b>1회 생성 후 캐시</b>하며 매 프레임 할당하지 않는다.
    ///
    /// 볼륨 스택:
    ///   Tonemapping(ACES) · Bloom · ColorAdjustments · WhiteBalance ·
    ///   SplitToning · LiftGammaGain · Vignette · FilmGrain · ChromaticAberration · DepthOfField
    ///
    /// 동적 연출:
    ///   - 페이즈 무드 그레이딩(boss.phase 구독 → 화이트밸런스/새추/노출 서서히 전환)
    ///   - 저체력 붉은 비네트(딜러 HP &lt; 임계) · 그로기 딜타임 금색 비네트 + 새추 부스트
    ///   - FlashScreen(색 플래시) · PunchAberration(임팩트 색수차 펄스) · SetCinematicDoF(원경 흐림)
    ///
    /// 외부 계약(다른 소유 스크립트에서 호출):
    ///   FlashScreen / PunchAberration / SetCinematicDoF — public API만 노출, 직접 배선하지 않음.
    /// [SerializeField] 값들은 OnValidate로 플레이 중 라이브 반영된다.
    /// </summary>
    [DisallowMultipleComponent]
    public class BossPostFX : MonoBehaviour
    {
        [Header("Bloom (HDR 이미시브만 피어나게)")]
        [SerializeField] private float bloomThreshold = 0.9f;
        [SerializeField] private float bloomIntensity = 0.65f;
        [SerializeField, Range(0f, 1f)] private float bloomScatter = 0.55f;

        [Header("Color Adjustments")]
        [Tooltip("기본 노출(베이스). 페이즈 무드가 이 위로 서서히 이동하고, FlashScreen이 잠깐 튀었다 돌아온다.")]
        [SerializeField] private float postExposure = 0.15f;
        [SerializeField, Range(-100f, 100f)] private float contrast = 20f;
        [SerializeField, Range(-100f, 100f)] private float saturation = 15f;

        [Header("Split Toning (섀도 블루 / 하이라이트 웜)")]
        [SerializeField] private Color splitShadowTint = new Color(0.35f, 0.45f, 0.75f);
        [SerializeField] private Color splitHighlightTint = new Color(1.0f, 0.85f, 0.7f);
        [SerializeField, Range(-100f, 100f)] private float splitBalance = -10f;

        [Header("Lift-Gamma-Gain (밤 혈월 무드)")]
        [Tooltip("섀도 살짝 블루로 리프트")]
        [SerializeField] private Vector3 liftShadowBias = new Vector3(-0.01f, -0.004f, 0.03f);
        [Tooltip("미드 콘트라스트(감마) 소폭")]
        [SerializeField] private Vector3 gammaMidBias = new Vector3(-0.01f, -0.01f, -0.015f);

        [Header("Vignette (기본 상시 + 상태 합성)")]
        [SerializeField, Range(0f, 1f)] private float vignetteBaseIntensity = 0.2f;
        [SerializeField, Range(0.01f, 1f)] private float vignetteSmoothness = 0.4f;
        [SerializeField] private Color vignetteBaseColor = Color.black;

        [Header("Film Grain (미세 노이즈)")]
        [SerializeField, Range(0f, 1f)] private float filmGrainIntensity = 0.15f;
        [SerializeField, Range(0f, 1f)] private float filmGrainResponse = 0.8f;

        [Header("저체력 / 딜타임 비네트")]
        [Tooltip("딜러(role 0) HP 비율이 이 값 미만이면 붉은 비네트")]
        [SerializeField, Range(0f, 1f)] private float lowHpThreshold = 0.3f;
        [SerializeField] private Color lowHpVignetteColor = new Color(0.7f, 0.05f, 0.05f);
        [SerializeField, Range(0f, 1f)] private float lowHpVignetteIntensity = 0.35f;
        [Tooltip("그로기 딜타임 금색 미광")]
        [SerializeField] private Color groggyVignetteColor = new Color(0.95f, 0.75f, 0.25f);
        [SerializeField, Range(0f, 1f)] private float groggyVignetteIntensity = 0.3f;
        [Tooltip("비네트/그레이딩 전환 속도(지수 감쇠 계수)")]
        [SerializeField] private float moodLerpSpeed = 4f;

        [Header("페이즈 무드 그레이딩 (P1 중립 / P2 청색 / P3 혈월 적색)")]
        [SerializeField] private float phase2Temperature = -8f;   // P2 차가운 청색
        [SerializeField] private float phase3Tint = 10f;          // P3 혈월 적색 tint
        [SerializeField] private float phase3SatBoost = 12f;      // P3 새추 부스트
        [SerializeField] private float phase3ExposureDrop = 0.05f;// P3 노출 살짝 다운
        [SerializeField] private float groggySatBoost = 15f;      // 딜타임 새추 부스트

        [Header("Depth of Field (데스캠/결과 화면 시네마틱)")]
        [SerializeField] private float dofGaussianStart = 8f;
        [SerializeField] private float dofGaussianEnd = 22f;

        [Header("Camera")]
        [Tooltip("Camera.main에 HDR + PostProcessing을 강제 활성화")]
        [SerializeField] private bool configureMainCamera = true;

        // 런타임 볼륨/오버라이드 핸들 (1회 생성 캐시)
        private Volume _volume;
        private VolumeProfile _profile;
        private Bloom _bloom;
        private Tonemapping _tonemapping;
        private ColorAdjustments _colorAdjustments;
        private WhiteBalance _whiteBalance;
        private SplitToning _splitToning;
        private LiftGammaGain _liftGammaGain;
        private Vignette _vignette;
        private FilmGrain _filmGrain;
        private ChromaticAberration _chromatic;
        private DepthOfField _dof;

        // 동적 상태
        private BossGameViewer _viewerRef;
        private bool _subscribed;
        private float _dealerHpRatio = 1f;
        private bool _bossGroggy;
        private int _bossPhase;
        private float _baseExposure;          // 페이즈 무드가 lerp하는 노출 베이스(FlashScreen 복원 기준)
        private float _aberrationValue;       // 현재 색수차 강도
        private float _aberrationDecay;       // 초당 감쇠량

        // FlashScreen 코루틴 중복 방지 + baseline colorFilter
        private Coroutine _flashCo;
        private Color _baseColorFilter = Color.white;

        private void Awake()
        {
            BuildVolume();
            if (configureMainCamera) ConfigureCamera();
            _baseExposure = postExposure;
        }

        private void OnEnable()
        {
            TrySubscribe();
        }

        private void OnDisable()
        {
            if (_subscribed && _viewerRef != null) _viewerRef.OnSnapshotApplied -= OnSnapshot;
            _subscribed = false;
        }

        private void OnDestroy()
        {
            // CreateInstance로 만든 프로파일은 씬 언로드로 자동 정리되지 않으므로 명시적 파기
            if (_profile != null) Destroy(_profile);
        }

        private void TrySubscribe()
        {
            if (_subscribed) return;
            if (_viewerRef == null) _viewerRef = FindFirstObjectByType<BossGameViewer>();
            if (_viewerRef != null)
            {
                _viewerRef.OnSnapshotApplied += OnSnapshot;
                _subscribed = true;
            }
        }

        // ─────────────── 볼륨 구성 ───────────────

        private void BuildVolume()
        {
            _volume = gameObject.GetComponent<Volume>();
            if (_volume == null) _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 10f;

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "BossPostFX_Runtime";
            _volume.sharedProfile = _profile;

            // Add<T>(true): 모든 파라미터 overrideState = true 로 추가 (1회)
            _bloom = _profile.Add<Bloom>(true);
            _tonemapping = _profile.Add<Tonemapping>(true);
            _colorAdjustments = _profile.Add<ColorAdjustments>(true);
            _whiteBalance = _profile.Add<WhiteBalance>(true);
            _splitToning = _profile.Add<SplitToning>(true);
            _liftGammaGain = _profile.Add<LiftGammaGain>(true);
            _vignette = _profile.Add<Vignette>(true);
            _filmGrain = _profile.Add<FilmGrain>(true);
            _chromatic = _profile.Add<ChromaticAberration>(true);
            _dof = _profile.Add<DepthOfField>(true);

            ApplyAll();

            // DoF 는 기본 Off (SetCinematicDoF 로만 켜짐)
            if (_dof != null)
            {
                _dof.mode.value = DepthOfFieldMode.Off;
                _dof.gaussianStart.value = dofGaussianStart;
                _dof.gaussianEnd.value = dofGaussianEnd;
                _dof.gaussianMaxRadius.value = 1.0f;
                _dof.highQualitySampling.value = false;
            }

            // 색수차는 펄스 때만(초기 0)
            if (_chromatic != null) _chromatic.intensity.value = 0f;
        }

        /// <summary>[SerializeField] 정적 값들을 볼륨 오버라이드에 반영. (동적 값은 Update가 담당)</summary>
        private void ApplyAll()
        {
            if (_bloom != null)
            {
                _bloom.threshold.value = bloomThreshold;
                _bloom.intensity.value = bloomIntensity;
                _bloom.scatter.value = bloomScatter;
            }
            if (_tonemapping != null)
                _tonemapping.mode.value = TonemappingMode.ACES;   // 저폴리 고급화 1순위

            if (_colorAdjustments != null)
            {
                _colorAdjustments.contrast.value = contrast;
                // saturation/postExposure 는 Update의 페이즈 무드가 구동(여기선 베이스만 확정)
                _baseColorFilter = Color.white;
                if (_flashCo == null)
                {
                    _colorAdjustments.colorFilter.value = _baseColorFilter;
                    _colorAdjustments.postExposure.value = _baseExposure;
                    _colorAdjustments.saturation.value = saturation;
                }
            }
            if (_whiteBalance != null)
            {
                // 초기 중립(페이즈 무드가 이동시킴)
            }
            if (_splitToning != null)
            {
                _splitToning.shadows.value = splitShadowTint;
                _splitToning.highlights.value = splitHighlightTint;
                _splitToning.balance.value = splitBalance;
            }
            if (_liftGammaGain != null)
            {
                _liftGammaGain.lift.value = new Vector4(liftShadowBias.x, liftShadowBias.y, liftShadowBias.z, 0f);
                _liftGammaGain.gamma.value = new Vector4(gammaMidBias.x, gammaMidBias.y, gammaMidBias.z, 0f);
            }
            if (_vignette != null)
            {
                _vignette.smoothness.value = vignetteSmoothness;
                _vignette.intensity.value = vignetteBaseIntensity;
                _vignette.color.value = vignetteBaseColor;
            }
            if (_filmGrain != null)
            {
                _filmGrain.type.value = FilmGrainLookup.Thin1;
                _filmGrain.intensity.value = filmGrainIntensity;
                _filmGrain.response.value = filmGrainResponse;
            }
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _profile != null) ApplyAll();
        }

        // ─────────────── 카메라 설정 ───────────────

        private void ConfigureCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            cam.allowHDR = true;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null) data.renderPostProcessing = true;
        }

        // ─────────────── 스냅샷 구독 ───────────────

        private void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null) return;
            if (snap.units != null)
            {
                foreach (var u in snap.units)
                {
                    if (u != null && u.role == 0)   // 딜러(플레이어)
                    {
                        _dealerHpRatio = Mathf.Clamp01((float)u.hp / Mathf.Max(1, u.max_hp));
                        break;
                    }
                }
            }
            if (snap.boss != null)
            {
                _bossPhase = snap.boss.phase;
                _bossGroggy = snap.boss.grog > 0 || snap.boss.stun > 0;
            }
        }

        // ─────────────── 동적 구동 (매 프레임, 할당 없음) ───────────────

        private void Update()
        {
            if (!_subscribed) TrySubscribe();
            if (_profile == null) return;

            float s = 1f - Mathf.Exp(-moodLerpSpeed * Time.unscaledDeltaTime);

            // ── 페이즈 무드 그레이딩 ──
            // P0/P1 중립, P2(index1) 차가운 청색, P3(index2) 혈월 적색
            float tgtTemp = _bossPhase == 1 ? phase2Temperature : 0f;
            float tgtTint = _bossPhase >= 2 ? phase3Tint : 0f;
            float satTarget = saturation
                            + (_bossPhase >= 2 ? phase3SatBoost : 0f)
                            + (_bossGroggy ? groggySatBoost : 0f);
            float expTarget = postExposure - (_bossPhase >= 2 ? phase3ExposureDrop : 0f);

            if (_whiteBalance != null)
            {
                _whiteBalance.temperature.value = Mathf.Lerp(_whiteBalance.temperature.value, tgtTemp, s);
                _whiteBalance.tint.value = Mathf.Lerp(_whiteBalance.tint.value, tgtTint, s);
            }
            if (_colorAdjustments != null)
            {
                _colorAdjustments.saturation.value = Mathf.Lerp(_colorAdjustments.saturation.value, satTarget, s);
                _baseExposure = Mathf.Lerp(_baseExposure, expTarget, s);
                if (_flashCo == null)   // 플래시 중이 아니면 무드 노출을 그대로 반영
                    _colorAdjustments.postExposure.value = _baseExposure;
            }

            // ── 비네트: 저체력(붉음) > 그로기 딜타임(금색) > 기본 ──
            float tgtVigI = vignetteBaseIntensity;
            Color tgtVigC = vignetteBaseColor;
            if (_dealerHpRatio < lowHpThreshold) { tgtVigI = lowHpVignetteIntensity; tgtVigC = lowHpVignetteColor; }
            else if (_bossGroggy) { tgtVigI = groggyVignetteIntensity; tgtVigC = groggyVignetteColor; }
            if (_vignette != null)
            {
                _vignette.intensity.value = Mathf.Lerp(_vignette.intensity.value, tgtVigI, s);
                _vignette.color.value = Color.Lerp(_vignette.color.value, tgtVigC, s);
            }

            // ── 색수차 펄스 감쇠 ──
            if (_aberrationValue > 0f && _chromatic != null)
            {
                _aberrationValue = Mathf.Max(0f, _aberrationValue - _aberrationDecay * Time.unscaledDeltaTime);
                _chromatic.intensity.value = _aberrationValue;
            }
        }

        // ─────────────── 공개 API (외부 소유 스크립트 계약) ───────────────

        /// <summary>
        /// 페이즈 전환 등 큰 연출용 화면 플래시.
        /// ColorAdjustments.postExposure를 순간 밝게 튀겼다가 무드 베이스(_baseExposure)로 감쇠.
        /// HitStop(Time.timeScale=0)과 무관하게 unscaled time 기반.
        /// </summary>
        /// <param name="color">colorFilter 색 편향.</param>
        /// <param name="duration">baseline 복귀까지 시간(초).</param>
        public void FlashScreen(Color color, float duration)
        {
            if (_colorAdjustments == null) return;
            if (_flashCo != null) StopCoroutine(_flashCo);
            _flashCo = StartCoroutine(FlashRoutine(color, Mathf.Max(0.01f, duration)));
        }

        private IEnumerator FlashRoutine(Color color, float duration)
        {
            const float peakBoost = 1.2f;   // baseline 위로 얹는 노출량(스톱)
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float k = 1f - Mathf.Clamp01(t / duration);
                float ease = k * k;
                _colorAdjustments.postExposure.value = _baseExposure + peakBoost * ease;
                _colorAdjustments.colorFilter.value = Color.Lerp(_baseColorFilter, color, ease * 0.6f);
                yield return null;
            }
            _colorAdjustments.postExposure.value = _baseExposure;
            _colorAdjustments.colorFilter.value = _baseColorFilter;
            _flashCo = null;
        }

        /// <summary>
        /// 임팩트 색수차 펄스. 크리/카운터/궁극기급 순간에 호출하면 색수차가 강하게 튀었다 0으로 감쇠한다.
        /// 외부(RaidVFXManager 등)에서 호출하는 계약용 public API — 이 컴포넌트는 직접 배선하지 않는다.
        /// </summary>
        /// <param name="strength">피크 색수차 강도(0~1 권장, 예: 0.4). 현재값보다 클 때만 갱신.</param>
        /// <param name="decay">초당 감쇠량(예: 1.5 = 약 0.27초에 0.4→0).</param>
        public void PunchAberration(float strength, float decay)
        {
            strength = Mathf.Clamp01(strength);
            _aberrationValue = Mathf.Max(_aberrationValue, strength);
            _aberrationDecay = Mathf.Max(0.01f, decay);
            if (_chromatic != null) _chromatic.intensity.value = _aberrationValue;
        }

        /// <summary>
        /// 시네마틱 피사계 심도(가우시안 원경 흐림) on/off. 데스캠/결과 화면용.
        /// GameFlowUI/카메라 쪽 에이전트가 소비하는 계약용 public API.
        /// </summary>
        /// <param name="on">true=가우시안 DoF 활성, false=Off.</param>
        public void SetCinematicDoF(bool on)
        {
            if (_dof == null) return;
            _dof.mode.value = on ? DepthOfFieldMode.Gaussian : DepthOfFieldMode.Off;
            if (on)
            {
                _dof.gaussianStart.value = dofGaussianStart;
                _dof.gaussianEnd.value = dofGaussianEnd;
            }
        }
    }
}
