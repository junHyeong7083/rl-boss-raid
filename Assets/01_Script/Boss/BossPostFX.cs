using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BossRaid
{
    /// <summary>
    /// 로스트아크 톤 포스트 프로세싱 런타임 부트스트랩.
    /// 씬마다 VolumeProfile 에셋을 만들 필요 없이, 코드로 글로벌 Volume을 구성한다.
    /// - Bloom / Tonemapping(ACES) / ColorAdjustments / Vignette 자동 세팅
    /// - Camera.main HDR + URP renderPostProcessing 활성화
    /// - 페이즈 전환 연출용 FlashScreen() 제공 (풀스크린 UI 없이 볼륨만으로)
    /// [SerializeField] 값들은 OnValidate로 플레이 중 라이브 반영된다.
    /// </summary>
    [DisallowMultipleComponent]
    public class BossPostFX : MonoBehaviour
    {
        [Header("Bloom")]
        [SerializeField] private float bloomThreshold = 1.0f;
        [SerializeField] private float bloomIntensity = 0.8f;
        [SerializeField, Range(0f, 1f)] private float bloomScatter = 0.7f;

        [Header("Color Adjustments")]
        [Tooltip("기본 노출(베이스). FlashScreen이 이 값 위로 잠깐 튀었다 돌아온다.")]
        [SerializeField] private float postExposure = 0.15f;
        [SerializeField, Range(-100f, 100f)] private float contrast = 15f;
        [SerializeField, Range(-100f, 100f)] private float saturation = 10f;

        [Header("Vignette")]
        [SerializeField, Range(0f, 1f)] private float vignetteIntensity = 0.25f;
        [SerializeField, Range(0.01f, 1f)] private float vignetteSmoothness = 0.4f;

        [Header("Camera")]
        [Tooltip("Camera.main에 HDR + PostProcessing을 강제 활성화")]
        [SerializeField] private bool configureMainCamera = true;

        // 런타임 볼륨/오버라이드 핸들
        private Volume _volume;
        private VolumeProfile _profile;
        private Bloom _bloom;
        private Tonemapping _tonemapping;
        private ColorAdjustments _colorAdjustments;
        private Vignette _vignette;

        // FlashScreen 코루틴 중복 방지
        private Coroutine _flashCo;

        // 진짜 baseline colorFilter. 플래시 중 재호출 시 코루틴이 복원 없이 죽어도
        // "물든 값"이 baseline으로 오염되지 않도록 필드로 고정한다.
        private Color _baseColorFilter = Color.white;

        private void Awake()
        {
            BuildVolume();
            if (configureMainCamera) ConfigureCamera();
        }

        private void OnDestroy()
        {
            // CreateInstance로 만든 프로파일은 씬 언로드로 자동 정리되지 않으므로 명시적 파기
            if (_profile != null) Destroy(_profile);
        }

        // ─────────────── 볼륨 구성 ───────────────

        private void BuildVolume()
        {
            _volume = gameObject.GetComponent<Volume>();
            if (_volume == null) _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 10f;

            // 씬 에셋 없이 코드로 프로파일 생성
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "BossPostFX_Runtime";
            _volume.sharedProfile = _profile;

            // Add<T>(true): 모든 파라미터 overrideState = true 로 추가
            _bloom = _profile.Add<Bloom>(true);
            _tonemapping = _profile.Add<Tonemapping>(true);
            _colorAdjustments = _profile.Add<ColorAdjustments>(true);
            _vignette = _profile.Add<Vignette>(true);

            ApplyAll();
        }

        /// <summary>[SerializeField] 값들을 볼륨 오버라이드에 반영.</summary>
        private void ApplyAll()
        {
            if (_bloom != null)
            {
                _bloom.threshold.value = bloomThreshold;
                _bloom.intensity.value = bloomIntensity;
                _bloom.scatter.value = bloomScatter;
            }
            if (_tonemapping != null)
            {
                _tonemapping.mode.value = TonemappingMode.ACES;
            }
            if (_colorAdjustments != null)
            {
                _colorAdjustments.postExposure.value = postExposure;
                _colorAdjustments.contrast.value = contrast;
                _colorAdjustments.saturation.value = saturation;
                // baseline colorFilter 확정. 플래시 진행 중이 아니면 즉시 반영.
                _baseColorFilter = Color.white;
                if (_flashCo == null)
                    _colorAdjustments.colorFilter.value = _baseColorFilter;
            }
            if (_vignette != null)
            {
                _vignette.intensity.value = vignetteIntensity;
                _vignette.smoothness.value = vignetteSmoothness;
            }
        }

        private void OnValidate()
        {
            // 플레이 중 인스펙터 값 변경 시 라이브 반영 (에디터 정지 상태에서는 오버라이드 미생성)
            if (Application.isPlaying && _profile != null) ApplyAll();
        }

        // ─────────────── 카메라 설정 ───────────────

        private void ConfigureCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;

            cam.allowHDR = true;

            // URP additional camera data: 포스트 프로세싱 렌더링 활성
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null) data.renderPostProcessing = true;
        }

        // ─────────────── 페이즈 전환 연출 API ───────────────

        /// <summary>
        /// 페이즈 전환 등 큰 연출용 화면 플래시.
        /// ColorAdjustments.postExposure를 순간적으로 밝게 튀겼다가 baseline으로 감쇠.
        /// 풀스크린 UI 없이 볼륨만으로 처리하며, HitStop(Time.timeScale=0)과 무관하게
        /// unscaled time 기반으로 감쇠한다.
        /// </summary>
        /// <param name="color">사용하지 않는 볼륨 구성에서도 시그니처 유지를 위해 받되,
        /// 색 편향은 colorFilter로 반영한다.</param>
        /// <param name="duration">플래시가 baseline으로 돌아오기까지의 시간(초).</param>
        public void FlashScreen(Color color, float duration)
        {
            if (_colorAdjustments == null) return;
            if (_flashCo != null) StopCoroutine(_flashCo);
            _flashCo = StartCoroutine(FlashRoutine(color, Mathf.Max(0.01f, duration)));
        }

        private IEnumerator FlashRoutine(Color color, float duration)
        {
            const float peakBoost = 1.2f;                 // baseline 위로 얹는 노출량(스톱)
            // 주의: 현재 colorFilter 값을 캡처하지 않는다. 이전 플래시 도중 재호출되면
            // "물든 값"이 baseline으로 오염되므로, 항상 _baseColorFilter 필드 기준으로 처리.

            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float k = 1f - Mathf.Clamp01(t / duration);   // 1 → 0 감쇠
                float ease = k * k;                            // 끝부분 부드럽게

                _colorAdjustments.postExposure.value = postExposure + peakBoost * ease;
                // 색 편향: baseline(흰색) → 지정 색으로 살짝 물들였다 복원
                _colorAdjustments.colorFilter.value = Color.Lerp(_baseColorFilter, color, ease * 0.6f);
                yield return null;
            }

            // baseline 복원
            _colorAdjustments.postExposure.value = postExposure;
            _colorAdjustments.colorFilter.value = _baseColorFilter;
            _flashCo = null;
        }
    }
}
