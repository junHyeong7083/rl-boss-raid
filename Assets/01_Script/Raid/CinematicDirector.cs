using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 전멸기('혈월 강림') 시네마틱 연출 감독 — '인트로'로 축소.
    /// 준비시간(30턴 ≈ 10초) 전체를 시네마틱이 점유하면 조작 불가 + 파훼 유도 불가이므로,
    /// 시네마틱은 도입부 3.6초만 재생하고 스스로 조작권을 반환한다. 파훼 안내는 SealAlertUI 가 맡는다.
    ///
    /// 타임라인:
    ///   ① 인트로 3.6초(= totalDuration): LostArkCamera 를 끄고 카메라 직접 제어 + 상하 레터박스.
    ///        - 보스 클로즈업 로우앵글(0~1.2초): 얼굴 높이에서 천천히 줌아웃, (45,22,65) 방향 붉은 달을 보스 위로.
    ///        - 아레나 부감 회전(1.2초~끝): 반경 18 · 고도 14 원호(반바퀴+).
    ///   ② 조작 복귀: 인트로 종료 시 CameraSequence 가 스스로 ReturnControl() 호출(카메라/레터박스 복귀).
    ///        cinematic_end 를 기다리지 않는다.
    ///   ③ 파훼(SealAlertUI 안내) → 판정 플래시: seal_success/seal_fail 수신 시점에 플래시·셰이크
    ///        (시네마틱 활성 여부와 무관하게 동작).
    ///
    /// 소비 계약(신뢰하고 호출):
    ///   BossGameViewer.OnSnapshotApplied → event Action&lt;BossSnapshot&gt;
    ///   - cinematic_start : 인트로 진입
    ///   - seal_success/seal_fail : 판정 플래시(성공=흰색 / 실패=붉은색 강)
    ///   - cinematic_end : 멱등 처리 — 아직 인트로 활성이면 조작권 반환, 이미 반환됐으면 무시
    /// 기존 API: LostArkCamera.Instance(enabled 토글) / BossPostFX.FlashScreen / LostArkCamera.ShakeCamera.
    /// HUD·스킬바는 건드리지 않는다(레터박스가 sortingOrder 30 으로 위에 덮음).
    /// </summary>
    [DisallowMultipleComponent]
    public class CinematicDirector : MonoBehaviour
    {
        [Header("Scene Refs")]
        [SerializeField] private BossGameViewer viewer;
        [SerializeField] private BossPostFX postFX;

        [Header("Timing (초)")]
        [SerializeField] private float totalDuration = 3.6f;   // 12턴 × 0.3초
        [SerializeField] private float closeUpDuration = 1.2f;
        [SerializeField] private float letterboxSlide = 0.4f;

        [Header("Cinematic Framing")]
        [SerializeField] private Vector3 moonWorldDir = new Vector3(45f, 22f, 65f); // 붉은 달 방향
        [SerializeField] private float orbitRadius = 18f;
        [SerializeField] private float orbitHeight = 14f;
        [SerializeField, Range(0f, 0.5f)] private float letterboxFrac = 0.12f;       // 화면 높이 대비 바 높이
        [SerializeField] private float cinematicFov = 34f;

        // ─── 상태 ───
        private bool _active;
        private Coroutine _seqCo;
        private Transform _camT;          // 시네마틱 동안 직접 제어할 카메라 Transform
        private Camera _cam;
        private float _savedFov;

        // 레터박스
        private Canvas _lbCanvas;
        private RectTransform _lbTop, _lbBottom;
        private Coroutine _lbCo;

        // ─────────────── 라이프사이클 ───────────────

        private void Awake()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            if (postFX == null) postFX = FindFirstObjectByType<BossPostFX>();
            BuildLetterbox();
        }

        private void OnEnable()
        {
            if (viewer != null) viewer.OnSnapshotApplied += OnSnapshot;
        }

        private void OnDisable()
        {
            if (viewer != null) viewer.OnSnapshotApplied -= OnSnapshot;
        }

        // ─────────────── 이벤트 판독 ───────────────

        private void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null || snap.events == null) return;

            bool sawStart = false, sawEnd = false, sawSealSuccess = false, sawSealFail = false;
            foreach (var ev in snap.events)
            {
                if (ev == null || string.IsNullOrEmpty(ev.type)) continue;
                switch (ev.type)
                {
                    case "cinematic_start": sawStart = true; break;
                    case "cinematic_end":   sawEnd = true; break;
                    case "seal_success":    sawSealSuccess = true; break;
                    case "seal_fail":       sawSealFail = true; break;
                }
            }

            // ① 인트로 진입
            if (sawStart) EnterCinematic();
            // ③ 판정 플래시 — 시네마틱 활성 여부와 무관하게(대부분 인트로는 이미 종료된 뒤 도착).
            if (sawSealSuccess) SealResultFlash(true);
            if (sawSealFail)    SealResultFlash(false);
            // cinematic_end 멱등: 아직 인트로 활성이면 조작권 반환, 이미 반환됐으면 무시(플래시 없음).
            if (sawEnd) ReturnControl();
        }

        // ─────────────── 진입 / 종료 ───────────────

        private void EnterCinematic()
        {
            if (_active) return;   // per-uid 중복 이벤트 방어
            _active = true;

            // LostArkCamera 비활성화 후 카메라 직접 제어권 획득.
            var lac = LostArkCamera.Instance;
            _camT = lac != null ? lac.transform : (Camera.main != null ? Camera.main.transform : null);
            _cam = _camT != null ? _camT.GetComponent<Camera>() : Camera.main;
            if (lac != null) lac.enabled = false;
            if (_cam != null) { _savedFov = _cam.fieldOfView; _cam.fieldOfView = cinematicFov; }

            StartLetterbox(true);
            if (_seqCo != null) StopCoroutine(_seqCo);
            // 카메라가 없어도 시퀀스는 돌려 totalDuration 타임아웃(조작권 반환)은 보장한다.
            _seqCo = StartCoroutine(CameraSequence());
        }

        // ② 조작권 반환 — 인트로 종료(타임아웃) 또는 cinematic_end 시 카메라/레터박스 복귀. 멱등.
        private void ReturnControl()
        {
            if (!_active) return;
            _active = false;

            if (_seqCo != null) { StopCoroutine(_seqCo); _seqCo = null; }

            StartLetterbox(false);

            // 카메라 복귀.
            if (_cam != null) _cam.fieldOfView = _savedFov;
            var lac = LostArkCamera.Instance;
            if (lac != null) lac.enabled = true;
        }

        // ③ 판정 플래시 — seal_success/seal_fail 수신 시점. 시네마틱 활성 여부와 무관하게 동작.
        private void SealResultFlash(bool success)
        {
            // 성공 → 흰 플래시 + 셰이크 / 실패 → 붉은 플래시 강 + 강 셰이크.
            if (success)
            {
                if (postFX != null) postFX.FlashScreen(Color.white, 0.4f);
                LostArkCamera.ShakeCamera(0.5f, 0.4f);
            }
            else
            {
                if (postFX != null) postFX.FlashScreen(new Color(1f, 0.1f, 0.1f), 0.7f);
                LostArkCamera.ShakeCamera(0.85f, 0.6f);
            }
        }

        // ─────────────── 카메라 시퀀스 (unscaled — 히트스톱/타임스케일 무관) ───────────────

        private IEnumerator CameraSequence()
        {
            float t = 0f;
            Vector3 center = viewer != null ? viewer.ContinuousToWorld(10f, 10f) : new Vector3(10f, 0f, 10f);
            Vector3 moonDir = moonWorldDir.sqrMagnitude > 1e-4f ? moonWorldDir.normalized : Vector3.up;
            Vector3 moonFlat = new Vector3(moonDir.x, 0f, moonDir.z);
            if (moonFlat.sqrMagnitude < 1e-4f) moonFlat = Vector3.forward;
            moonFlat.Normalize();

            // 인트로는 totalDuration(3.6초)만 재생 → 종료 시 스스로 조작권 반환.
            while (_active && t < totalDuration)
            {
                t += Time.unscaledDeltaTime;

                if (_camT != null)
                {
                    Vector3 bossPos = center;
                    if (viewer != null && viewer.TryGetBossPosition(out var bp)) bossPos = bp;

                    if (t <= closeUpDuration)
                    {
                        // ① 클로즈업 로우앵글 + 천천히 줌아웃.
                        float k = Mathf.Clamp01(t / Mathf.Max(0.01f, closeUpDuration));
                        Vector3 bossHead = bossPos + Vector3.up * 2.0f;
                        float dist = Mathf.Lerp(4f, 9f, k);           // 줌아웃
                        float lowY = Mathf.Lerp(0.5f, 1.6f, k);       // 로우앵글 상승
                        Vector3 camPos = bossHead - moonFlat * dist + Vector3.up * lowY;
                        // 시선을 보스에서 달 방향으로 살짝 들어 붉은 달이 보스 위로 걸리게.
                        Vector3 lookAt = Vector3.Lerp(bossHead, bossHead + moonDir * 20f, 0.35f);
                        _camT.position = camPos;
                        _camT.rotation = Quaternion.LookRotation((lookAt - camPos).normalized, Vector3.up);
                    }
                    else
                    {
                        // ② 아레나 부감 회전(반바퀴+). closeUp 이후 잔여 시간에 걸쳐 π 이상 회전.
                        float span = Mathf.Max(0.01f, totalDuration - closeUpDuration);
                        float p = (t - closeUpDuration) / span;        // 0→1
                        float ang = Mathf.PI * p;                       // 반바퀴
                        Vector3 camPos = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * orbitRadius
                                                + Vector3.up * orbitHeight;
                        _camT.position = camPos;
                        _camT.rotation = Quaternion.LookRotation((center - camPos).normalized, Vector3.up);
                    }
                }
                yield return null;
            }

            _seqCo = null;
            // 타임아웃으로 루프 종료 → 조작권 반환(외부 cinematic_end 로 이미 반환됐으면 _active=false 라 무시됨).
            if (_active) ReturnControl();
        }

        // ─────────────── 레터박스 ───────────────

        private void BuildLetterbox()
        {
            var go = new GameObject("CinematicLetterbox");
            go.transform.SetParent(transform, false);
            _lbCanvas = go.AddComponent<Canvas>();
            _lbCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _lbCanvas.sortingOrder = 30;   // HUD/스킬바 위에 덮음
            go.AddComponent<CanvasScaler>();

            _lbTop = MakeBar("BarTop", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), go.transform);
            _lbBottom = MakeBar("BarBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), go.transform);

            go.SetActive(false);   // 시네마틱 진입 전엔 숨김
        }

        private static RectTransform MakeBar(string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Transform parent)
        {
            var bar = new GameObject(name);
            bar.transform.SetParent(parent, false);
            var img = bar.AddComponent<Image>();
            img.color = Color.black;
            img.raycastTarget = false;   // 입력을 막지 않음(HUD 클릭 유지)
            var rt = img.rectTransform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, 0f);
            return rt;
        }

        private void StartLetterbox(bool slideIn)
        {
            if (_lbCanvas == null) return;
            _lbCanvas.gameObject.SetActive(true);
            if (_lbCo != null) StopCoroutine(_lbCo);
            _lbCo = StartCoroutine(LetterboxRoutine(slideIn));
        }

        private IEnumerator LetterboxRoutine(bool slideIn)
        {
            float targetH = Screen.height * letterboxFrac;
            float startFrac = slideIn ? 0f : 1f;
            float endFrac = slideIn ? 1f : 0f;

            float t = 0f;
            float dur = Mathf.Max(0.01f, letterboxSlide);
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float f = Mathf.Lerp(startFrac, endFrac, Mathf.Clamp01(t / dur));
                SetBarHeights(targetH * f);
                yield return null;
            }
            SetBarHeights(targetH * endFrac);

            if (!slideIn) _lbCanvas.gameObject.SetActive(false);   // 슬라이드 아웃 완료 후 숨김
            _lbCo = null;
        }

        private void SetBarHeights(float h)
        {
            if (_lbTop != null) _lbTop.sizeDelta = new Vector2(0f, h);
            if (_lbBottom != null) _lbBottom.sizeDelta = new Vector2(0f, h);
        }
    }
}
