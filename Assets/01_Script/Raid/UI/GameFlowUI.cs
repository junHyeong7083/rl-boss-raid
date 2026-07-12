using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 사용자 평가용 게임 플로우 오버레이 (단일 씬 상태 머신, 씬 전환 없음).
    ///
    ///   Title → Loading → Countdown → Playing → Result → (다시하기/타이틀로)
    ///
    /// BossStage 씬 안에서 RaidSession 이벤트에 반응하여 전체화면 오버레이를 표시한다.
    /// 모든 UI는 런타임 uGUI 코드로 생성(Canvas sortingOrder 20, HUD보다 위).
    /// 로스트아크 톤: 어두운 패널 + 금색/진홍.
    ///
    /// 추가(연출):
    ///  - 전투 통계 수집: BossGameViewer.OnSnapshotApplied 를 구독해 에피소드 동안
    ///    플레이어(uid 0) 딜/크리/카운터/기믹/대시를 집계(started 시 리셋).
    ///  - 승리 순간 연출: 스냅샷에서 boss 처치(victory) 감지 → HitStop 슬로모 +
    ///    LostArkCamera.PlayDeathCam 클로즈업 → 결과 화면 페이드 인.
    ///  - 결과 화면: 승/패 타이틀(펀치 스케일) + 클리어/생존 시간 + 통계 리포트(슬라이드 인).
    ///
    /// RaidSession 이벤트(OnReady/OnStarted/OnEpisodeEnd)와 viewer.OnSnapshotApplied 는
    /// 모두 메인스레드에서 발화되므로 여기서 UI/코루틴을 직접 다뤄도 안전하다.
    /// </summary>
    public class GameFlowUI : MonoBehaviour
    {
        private enum FlowState { Title, Loading, Countdown, Playing, Result }

        // 로아 톤 색상
        private static readonly Color Crimson = new Color(0.72f, 0.09f, 0.11f, 1f);
        private static readonly Color Gold = new Color(0.85f, 0.68f, 0.32f, 1f);
        private static readonly Color PanelDark = new Color(0.03f, 0.02f, 0.03f, 0.96f);
        private static readonly Color PanelTrans = new Color(0.03f, 0.02f, 0.03f, 0.72f);
        private static readonly Color ReportPanelBg = new Color(0.05f, 0.04f, 0.06f, 0.92f);
        private static readonly Color BtnNormal = new Color(0.14f, 0.10f, 0.10f, 1f);
        private static readonly Color TextDim = new Color(0.75f, 0.72f, 0.68f, 1f);
        private static readonly Color OkGreen = new Color(0.42f, 0.92f, 0.5f, 1f);
        private static readonly Color FailDim = new Color(0.55f, 0.5f, 0.5f, 1f);

        private static readonly string[] LoadTips =
        {
            "TIP: 부채꼴(발톱) 공격은 보스의 측면·후방으로 피하세요.",
            "TIP: 도넛 장판(포효)은 오히려 보스에게 바짝 붙어야 안전합니다.",
            "TIP: 파란 발광이 보이면 카운터(딜러) 타이밍입니다.",
            "TIP: 돌진 경로에 기둥을 두면 보스가 그로기에 빠집니다.",
            "TIP: 전멸기 '혈월 강림'은 기둥 뒤로 은신해 시야를 차단하세요.",
        };

        private static readonly string[] LoadStages =
        {
            "환경 초기화 중...",
            "AI 모델 로딩 중...",
            "전투 준비 중...",
            "입장 중...",
        };

        private FlowState _state = FlowState.Title;
        private Font _font;

        // 상태별 그룹
        private GameObject _titleGroup, _loadingGroup, _countdownGroup, _resultGroup;

        // Loading 위젯
        private Image _loadBarFill;
        private Text _loadStageText, _loadTipText;
        private float _displayProgress;
        private float _tipTimer;
        private int _tipIndex;

        // Countdown
        private Text _countText;

        // Result
        private Text _resultBanner, _resultTime;
        private Text _rowDamage, _rowMaxHit, _rowCrit, _rowCounter;
        private Text _rowStagger, _rowSeal, _rowRush;
        private Text _causeHint;

        // 페이드 전환
        private Image _fadeImg;

        // 슬라이드 인 대상(위 → 아래 순차 등장)
        private readonly List<RectTransform> _revealItems = new List<RectTransform>();
        private readonly List<Vector2> _revealTargets = new List<Vector2>();
        private readonly List<CanvasGroup> _revealGroups = new List<CanvasGroup>();

        // ─── 전투 통계 (에피소드 단위, started 시 리셋) ───
        private bool _statsActive;
        private long _totalDamage;
        private int _maxHit;
        private int _critCount;
        private int _counterSuccess, _counterFail;
        private bool _gimStagger, _gimSeal, _gimRush;
        private int _dashCount;
        private int _lastHit;          // 딜러(uid 0)가 마지막으로 받은 피해량
        private bool _playerDied;      // 딜러 사망 여부(패배 원인 힌트)

        // ─── 승리 연출 상태 ───
        private BossGameViewer _viewer;
        private BossPostFX _postFx;
        private bool _bossDeadDetected;    // 이번 에피소드에서 데스캠을 이미 트리거했는지
        private float _cinematicRevealAt;  // 이 unscaled 시각 이후 결과 화면 공개

        private bool _subscribed;

        private void Start()
        {
            _font = ResolveFont();
            EnsureEventSystem();
            BuildCanvas();
            SubscribeSession();
            _viewer = FindFirstObjectByType<BossGameViewer>();
            if (_viewer != null) _viewer.OnSnapshotApplied += HandleSnapshot;
            _postFx = FindFirstObjectByType<BossPostFX>();
            SetState(FlowState.Title);
        }

        private void OnDestroy()
        {
            if (_subscribed && RaidSession.Instance != null)
            {
                RaidSession.Instance.OnReady -= HandleReady;
                RaidSession.Instance.OnStarted -= HandleStarted;
                RaidSession.Instance.OnEpisodeEnd -= HandleEpisodeEnd;
            }
            if (_viewer != null) _viewer.OnSnapshotApplied -= HandleSnapshot;
        }

        private void SubscribeSession()
        {
            var s = RaidSession.Instance;
            if (s == null)
            {
                Debug.LogWarning("[GameFlowUI] RaidSession.Instance 없음 — 이벤트 미구독");
                return;
            }
            s.OnReady += HandleReady;
            s.OnStarted += HandleStarted;
            s.OnEpisodeEnd += HandleEpisodeEnd;
            _subscribed = true;
        }

        // ───────────────────── 상태 전환 ─────────────────────

        private void SetState(FlowState s)
        {
            _state = s;
            _titleGroup.SetActive(s == FlowState.Title);
            _loadingGroup.SetActive(s == FlowState.Loading);
            _countdownGroup.SetActive(s == FlowState.Countdown);
            _resultGroup.SetActive(s == FlowState.Result);
            // Playing 은 오버레이 없음 (전부 비활성)
        }

        private void Update()
        {
            if (_state == FlowState.Loading)
                UpdateLoading();
        }

        // ── Title ──
        private void OnClickStart()
        {
            SetState(FlowState.Loading);
            _displayProgress = 0f;
            _tipTimer = 0f;
            _tipIndex = 0;
            _loadTipText.text = LoadTips[0];
            _loadStageText.text = LoadStages[0];
            if (RaidSession.Instance != null)
                RaidSession.Instance.LaunchAndConnect();
            else
                Debug.LogError("[GameFlowUI] RaidSession 없음 — 로딩 불가");
        }

        private void OnClickQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ── Loading ──
        private void UpdateLoading()
        {
            float target = RaidSession.Instance != null ? RaidSession.Instance.LoadingProgress : 0f;
            // 부드러운 보간
            _displayProgress = Mathf.MoveTowards(_displayProgress, target, Time.deltaTime * 0.8f);
            if (_loadBarFill != null)
                _loadBarFill.fillAmount = _displayProgress;

            // 단계 텍스트 (진행률 구간별)
            int stageIdx = _displayProgress < 0.24f ? 0 : _displayProgress < 0.68f ? 1 : _displayProgress < 0.99f ? 2 : 3;
            _loadStageText.text = LoadStages[stageIdx];

            // 팁 로테이션 (3.5초마다)
            _tipTimer += Time.deltaTime;
            if (_tipTimer >= 3.5f)
            {
                _tipTimer = 0f;
                _tipIndex = (_tipIndex + 1) % LoadTips.Length;
                _loadTipText.text = LoadTips[_tipIndex];
            }
        }

        // ── RaidSession 이벤트 (메인스레드) ──
        private void HandleReady()
        {
            // ready 수신 = Python 로드 완료(아직 스텝 안 돌림). 여기서 바로 start 를 보내지 않고
            // 카운트다운(3-2-1)부터 연출한 뒤, 카운트다운 종료 순간에 start 를 전송한다.
            Debug.Log("[GameFlowUI] ready 수신 → Countdown");
            StartCountdown();
        }

        private void HandleStarted()
        {
            // started 수신 → 전투 시작. 통계·연출 상태를 이 시점에 리셋.
            Debug.Log("[GameFlowUI] started 수신 → Playing");
            ResetStats();
            _bossDeadDetected = false;
            _cinematicRevealAt = 0f;
            SetState(FlowState.Playing);
        }

        private void HandleEpisodeEnd(string result, int steps, float duration)
        {
            Debug.Log($"[GameFlowUI] episode_end: {result} steps={steps} dur={duration}");
            _statsActive = false;   // 집계 종료
            StartCoroutine(ResultFlow(result, steps, duration));
        }

        // ── 스냅샷 통계 수집 + 승리 감지 ──
        private void HandleSnapshot(BossSnapshot snap)
        {
            if (snap == null) return;

            if (_statsActive) CollectStats(snap);

            // 보스 처치 순간(첫 victory 스냅샷) → 데스캠 즉시 트리거(에피소드 종료 메시지보다 빠른 반응).
            if (_state == FlowState.Playing && !_bossDeadDetected && snap.victory)
            {
                Vector3 pos = ResolveBossPos(snap);
                TriggerVictoryImpact(pos);
            }
        }

        private void CollectStats(BossSnapshot snap)
        {
            if (snap.events == null) return;
            foreach (var ev in snap.events)
            {
                if (ev == null) continue;
                switch (ev.type)
                {
                    case "damage":
                        if (ev.uid == 0)
                        {
                            _totalDamage += ev.amount;
                            if (ev.amount > _maxHit) _maxHit = ev.amount;
                            if (ev.crit) _critCount++;
                        }
                        break;
                    case "counter_success":
                        if (ev.uid == 0) _counterSuccess++;
                        break;
                    case "counter_miss":
                        if (ev.uid == 0) _counterFail++;
                        break;
                    case "dash":
                        if (ev.uid == 0) _dashCount++;
                        break;
                    case "stagger_success": _gimStagger = true; break;
                    case "seal_success":    _gimSeal = true; break;
                    case "rush_pillar_hit": _gimRush = true; break;
                    case "damage_taken":
                        if (ev.uid == 0) _lastHit = ev.amount;
                        break;
                    case "death":
                        if (ev.uid == 0) _playerDied = true;
                        break;
                }
            }
        }

        private void ResetStats()
        {
            _statsActive = true;
            _totalDamage = 0;
            _maxHit = 0;
            _critCount = 0;
            _counterSuccess = _counterFail = 0;
            _gimStagger = _gimSeal = _gimRush = false;
            _dashCount = 0;
            _lastHit = 0;
            _playerDied = false;
        }

        private Vector3 ResolveBossPos(BossSnapshot snap)
        {
            if (_viewer != null)
            {
                if (_viewer.TryGetBossPosition(out var p)) return p;
                if (snap.boss != null) return _viewer.ContinuousToWorld(snap.boss.x, snap.boss.y);
            }
            return Vector3.zero;
        }

        // ── 승리 임팩트(슬로모 + 데스캠) ──
        private void TriggerVictoryImpact(Vector3 bossPos)
        {
            _bossDeadDetected = true;
            // 0.5s 슬로모(거의 정지에 가깝게).
            HitStopManager.HitStop(0.5f, 0.15f);
            // 데스캠 클로즈업(자체 unscaled 코루틴).
            LostArkCamera.Instance?.PlayDeathCam(bossPos);
            // 금색 화면 플래시(있으면).
            _postFx?.FlashScreen(Gold, 0.6f);
            LostArkCamera.ShakeCamera(0.6f, 0.4f);
            // 데스캠(1.8s) + 여유 후 결과 공개.
            _cinematicRevealAt = Time.unscaledTime + 2.0f;
        }

        // ── Countdown ──
        private void StartCountdown()
        {
            StopAllCoroutines();
            SetState(FlowState.Countdown);
            StartCoroutine(CountdownRoutine());
        }

        private IEnumerator CountdownRoutine()
        {
            for (int n = 3; n >= 1; n--)
            {
                _countText.text = n.ToString();
                float t = 0f;
                while (t < 1f)
                {
                    t += Time.deltaTime / 1f;
                    float s = Mathf.Lerp(1.6f, 0.9f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 2f)));
                    _countText.rectTransform.localScale = Vector3.one * s;
                    var c = _countText.color; c.a = Mathf.Clamp01(1.4f - t); _countText.color = c;
                    yield return null;
                }
            }
            _countText.rectTransform.localScale = Vector3.one;
            _countText.text = "";

            Debug.Log("[GameFlowUI] 카운트다운 종료 → start 전송");
            RaidSession.Instance?.SendCmd("start");
        }

        // ── Result ──

        /// <summary>
        /// 결과 진입 오케스트레이션:
        ///  승리 → 데스캠(진행 중이면 종료까지 대기) → 검정 페이드 → 리포트 구성/공개 → 슬라이드 인.
        ///  패배/타임아웃 → 곧바로 페이드 → 리포트.
        /// </summary>
        private IEnumerator ResultFlow(string result, int steps, float duration)
        {
            string r = (result ?? "").ToLowerInvariant();

            if (r == "victory")
            {
                // 스냅샷에서 데스캠을 못 잡았으면(네트워크 순서상) 여기서라도 트리거.
                if (!_bossDeadDetected)
                {
                    Vector3 pos = _viewer != null && _viewer.LatestSnapshot != null
                        ? ResolveBossPos(_viewer.LatestSnapshot) : Vector3.zero;
                    TriggerVictoryImpact(pos);
                }
                // 데스캠이 끝날 때까지 대기(unscaled).
                while (Time.unscaledTime < _cinematicRevealAt)
                    yield return null;
            }

            // 검정으로 덮기.
            yield return Fade(0f, 1f, 0.4f);

            // 리포트 구성 + 결과 상태 진입(검정 아래에서).
            BuildResultContent(r, steps, duration);
            SetState(FlowState.Result);
            PrepareRevealHidden();

            // 검정 걷기.
            yield return Fade(1f, 0f, 0.4f);

            // 항목 순차 슬라이드 인.
            yield return SlideInReveal();

            // 배너 펀치 스케일.
            StartCoroutine(PunchScale(_resultBanner.rectTransform, 1.35f, 0.35f));
        }

        private void BuildResultContent(string r, int steps, float duration)
        {
            bool victory = r == "victory";
            bool wipe = r == "wipe";

            if (victory)
            {
                _resultBanner.text = "RAID CLEAR";
                _resultBanner.color = Gold;
                _resultTime.text = "클리어 타임  " + FormatTime(duration);
                _causeHint.gameObject.SetActive(false);
            }
            else if (wipe)
            {
                _resultBanner.text = "RAID FAILED";
                _resultBanner.color = Crimson;
                _resultTime.text = "생존 시간  " + FormatTime(duration);
                _causeHint.gameObject.SetActive(true);
                _causeHint.text = _playerDied
                    ? $"패배 원인 · 딜러 전사 (마지막 피해 {_lastHit:N0})"
                    : "패배 원인 · 파티 전멸";
            }
            else // timeout / 기타
            {
                _resultBanner.text = "TIME OVER";
                _resultBanner.color = TextDim;
                _resultTime.text = "전투 시간  " + FormatTime(duration);
                _causeHint.gameObject.SetActive(true);
                _causeHint.text = "패배 원인 · 제한 시간 내 보스 처치 실패";
            }

            _rowDamage.text = _totalDamage.ToString("N0", CultureInfo.InvariantCulture);
            _rowMaxHit.text = _maxHit.ToString("N0", CultureInfo.InvariantCulture);
            _rowCrit.text = $"{_critCount}회 (대시 {_dashCount})";
            int cTotal = _counterSuccess + _counterFail;
            _rowCounter.text = $"{_counterSuccess} / {cTotal}";
            SetGimmick(_rowStagger, _gimStagger);
            SetGimmick(_rowSeal, _gimSeal);
            SetGimmick(_rowRush, _gimRush);
        }

        private static void SetGimmick(Text t, bool ok)
        {
            t.text = ok ? "O  달성" : "X  미달성";
            t.color = ok ? OkGreen : FailDim;
        }

        private static string FormatTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        private void OnClickRetry()
        {
            // 다시하기: 진행 중 연출을 정리(StopAllCoroutines 먼저)한 뒤 검정 페이드 → 카운트다운.
            StopAllCoroutines();
            StartCoroutine(RetryFlow());
        }

        private IEnumerator RetryFlow()
        {
            // StartCountdown() 은 내부에서 StopAllCoroutines() 를 호출해 이 코루틴 자신을 죽이므로
            // 여기서는 상태 전환 + 카운트다운을 직접 인라인한다.
            yield return Fade(0f, 1f, 0.4f);
            SetState(FlowState.Countdown);
            yield return Fade(1f, 0f, 0.4f);
            yield return CountdownRoutine();
        }

        private void OnClickToTitle()
        {
            StopAllCoroutines();
            StartCoroutine(ToTitleFlow());
        }

        private IEnumerator ToTitleFlow()
        {
            yield return Fade(0f, 1f, 0.4f);
            SetState(FlowState.Title);
            yield return Fade(1f, 0f, 0.4f);
        }

        // ───────────────────── 페이드 / 애니메이션 ─────────────────────

        private IEnumerator Fade(float from, float to, float dur)
        {
            _fadeImg.raycastTarget = true;   // 전환 중 입력 차단
            float t = 0f;
            var c = _fadeImg.color;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                c.a = Mathf.Lerp(from, to, Mathf.Clamp01(t / dur));
                _fadeImg.color = c;
                yield return null;
            }
            c.a = to; _fadeImg.color = c;
            _fadeImg.raycastTarget = to > 0.01f;
        }

        private void PrepareRevealHidden()
        {
            for (int i = 0; i < _revealItems.Count; i++)
            {
                var rt = _revealItems[i];
                if (rt == null) continue;
                // 비활성(cause hint 등) 은 건너뜀 — SlideIn 에서도 skip.
                _revealGroups[i].alpha = 0f;
                rt.anchoredPosition = _revealTargets[i] + new Vector2(0f, 36f);
            }
        }

        private IEnumerator SlideInReveal()
        {
            const float stagger = 0.06f, dur = 0.28f;
            for (int i = 0; i < _revealItems.Count; i++)
            {
                var rt = _revealItems[i];
                if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                StartCoroutine(SlideOne(rt, _revealGroups[i], _revealTargets[i], dur));
                yield return WaitUnscaled(stagger);
            }
            // 마지막 항목이 다 들어올 시간 확보.
            yield return WaitUnscaled(dur);
        }

        private IEnumerator SlideOne(RectTransform rt, CanvasGroup cg, Vector2 target, float dur)
        {
            Vector2 start = target + new Vector2(0f, 36f);
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / dur));
                rt.anchoredPosition = Vector2.LerpUnclamped(start, target, k);
                cg.alpha = k;
                yield return null;
            }
            rt.anchoredPosition = target;
            cg.alpha = 1f;
        }

        private IEnumerator PunchScale(RectTransform rt, float peak, float dur)
        {
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / dur);
                // 1 → peak → 1 (빠르게 튀고 되돌아옴)
                float s = 1f + (peak - 1f) * Mathf.Sin(k * Mathf.PI);
                rt.localScale = Vector3.one * s;
                yield return null;
            }
            rt.localScale = Vector3.one;
        }

        private static IEnumerator WaitUnscaled(float seconds)
        {
            float t = 0f;
            while (t < seconds) { t += Time.unscaledDeltaTime; yield return null; }
        }

        // ───────────────────── UI 생성 ─────────────────────

        private void BuildCanvas()
        {
            var canvasGo = new GameObject("GameFlowCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20; // HUD보다 위
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            var root = canvasGo.transform;
            BuildTitle(root);
            BuildLoading(root);
            BuildCountdown(root);
            BuildResult(root);
            BuildFade(root);   // 최상단(마지막 형제) — 모든 그룹 위를 덮는다.
        }

        private void BuildTitle(Transform root)
        {
            _titleGroup = MakePanel("TitleGroup", root, PanelDark);
            MakeText("Title", _titleGroup.transform, "혈월의 마수 군주", 120, Crimson,
                new Vector2(0.5f, 0.66f), new Vector2(1400, 200), FontStyle.Bold);
            MakeText("Subtitle", _titleGroup.transform, "혈월 강림 — 마수 군주 토벌전", 34, Gold,
                new Vector2(0.5f, 0.55f), new Vector2(1200, 60), FontStyle.Normal);
            MakeButton("StartBtn", _titleGroup.transform, "게임 시작", Gold,
                new Vector2(0.5f, 0.36f), OnClickStart);
            MakeButton("QuitBtn", _titleGroup.transform, "종료", TextDim,
                new Vector2(0.5f, 0.26f), OnClickQuit);
        }

        private void BuildLoading(Transform root)
        {
            _loadingGroup = MakePanel("LoadingGroup", root, PanelDark);
            MakeText("LoadTitle", _loadingGroup.transform, "혈월의 마수 군주", 70, Crimson,
                new Vector2(0.5f, 0.7f), new Vector2(1200, 120), FontStyle.Bold);

            var barBg = MakeImage("LoadBarBg", _loadingGroup.transform,
                new Color(0.12f, 0.10f, 0.10f, 1f), new Vector2(0.5f, 0.42f), new Vector2(900, 26));
            var fillGo = MakeImage("LoadBarFill", barBg.transform, Gold, new Vector2(0.5f, 0.5f), new Vector2(900, 26));
            _loadBarFill = fillGo.GetComponent<Image>();
            _loadBarFill.type = Image.Type.Filled;
            _loadBarFill.fillMethod = Image.FillMethod.Horizontal;
            _loadBarFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _loadBarFill.fillAmount = 0f;
            var fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = Vector2.zero; fillRt.offsetMax = Vector2.zero;

            _loadStageText = MakeText("LoadStage", _loadingGroup.transform, LoadStages[0], 30, TextDim,
                new Vector2(0.5f, 0.36f), new Vector2(900, 50), FontStyle.Normal);
            _loadTipText = MakeText("LoadTip", _loadingGroup.transform, LoadTips[0], 26, Gold,
                new Vector2(0.5f, 0.16f), new Vector2(1400, 60), FontStyle.Italic);
        }

        private void BuildCountdown(Transform root)
        {
            _countdownGroup = MakePanel("CountdownGroup", root, new Color(0, 0, 0, 0)); // 투명
            _countText = MakeText("Count", _countdownGroup.transform, "3", 260, Gold,
                new Vector2(0.5f, 0.5f), new Vector2(600, 400), FontStyle.Bold);
        }

        private void BuildResult(Transform root)
        {
            _resultGroup = MakePanel("ResultGroup", root, PanelTrans);

            _resultBanner = MakeText("Banner", _resultGroup.transform, "RAID CLEAR", 150, Gold,
                new Vector2(0.5f, 0.82f), new Vector2(1500, 220), FontStyle.Bold);
            _resultTime = MakeText("Time", _resultGroup.transform, "", 40, TextDim,
                new Vector2(0.5f, 0.71f), new Vector2(1100, 60), FontStyle.Normal);

            // ── 통계 리포트 패널 ──
            var panel = MakeImage("ReportPanel", _resultGroup.transform, ReportPanelBg,
                new Vector2(0.5f, 0.42f), new Vector2(820, 470));
            var header = MakeText("ReportHeader", panel.transform, "전투 리포트", 30, Gold,
                new Vector2(0.5f, 1f), new Vector2(760, 44), FontStyle.Bold);
            // 헤더를 패널 상단 안쪽으로 내림.
            header.rectTransform.anchoredPosition = new Vector2(0f, -14f);

            float y = -70f; const float step = -52f;
            _rowDamage  = BuildReportRow(panel.transform, "총 피해량", ref y, step, Gold);
            _rowMaxHit  = BuildReportRow(panel.transform, "최대 한 방", ref y, step, Gold);
            _rowCrit    = BuildReportRow(panel.transform, "크리티컬", ref y, step, TextDim);
            _rowCounter = BuildReportRow(panel.transform, "카운터 저지 (성공/시도)", ref y, step, Gold);
            _rowStagger = BuildReportRow(panel.transform, "기믹 · 무력화", ref y, step, OkGreen);
            _rowSeal    = BuildReportRow(panel.transform, "기믹 · 전멸기 회피", ref y, step, OkGreen);
            _rowRush    = BuildReportRow(panel.transform, "기믹 · 돌진 유도", ref y, step, OkGreen);

            _causeHint = MakeText("CauseHint", _resultGroup.transform, "", 28, Crimson,
                new Vector2(0.5f, 0.22f), new Vector2(1200, 44), FontStyle.Italic);

            var retry = MakeButton("RetryBtn", _resultGroup.transform, "다시하기", Gold,
                new Vector2(0.5f, 0.135f), OnClickRetry);
            var title = MakeButton("ToTitleBtn", _resultGroup.transform, "타이틀로", TextDim,
                new Vector2(0.5f, 0.055f), OnClickToTitle);

            // ── 슬라이드 인 등록(위 → 아래 순서) ──
            RegisterReveal(_resultBanner.rectTransform);
            RegisterReveal(_resultTime.rectTransform);
            RegisterReveal(header.rectTransform);
            RegisterReveal(_rowDamage.transform.parent as RectTransform);
            RegisterReveal(_rowMaxHit.transform.parent as RectTransform);
            RegisterReveal(_rowCrit.transform.parent as RectTransform);
            RegisterReveal(_rowCounter.transform.parent as RectTransform);
            RegisterReveal(_rowStagger.transform.parent as RectTransform);
            RegisterReveal(_rowSeal.transform.parent as RectTransform);
            RegisterReveal(_rowRush.transform.parent as RectTransform);
            RegisterReveal(_causeHint.rectTransform);
            RegisterReveal(retry.GetComponent<RectTransform>());
            RegisterReveal(title.GetComponent<RectTransform>());
        }

        /// <summary>리포트 한 줄(라벨 좌/값 우). RaidUIFactory 재사용. 값 Text 반환.</summary>
        private Text BuildReportRow(Transform panel, string label, ref float y, float step, Color valueColor)
        {
            var row = RaidUIFactory.NewRect("Row_" + label, panel);
            row.anchorMin = row.anchorMax = new Vector2(0.5f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.sizeDelta = new Vector2(760f, 46f);
            row.anchoredPosition = new Vector2(0f, y);
            y += step;

            var lab = RaidUIFactory.NewText("Lab", row, _font, 26, TextDim, TextAnchor.MiddleLeft);
            RaidUIFactory.Stretch(lab.rectTransform, 24f, 0f, 24f, 0f);
            lab.text = label;

            var val = RaidUIFactory.NewText("Val", row, _font, 28, valueColor, TextAnchor.MiddleRight);
            RaidUIFactory.Stretch(val.rectTransform, 24f, 0f, 24f, 0f);
            return val;
        }

        private void RegisterReveal(RectTransform rt)
        {
            if (rt == null) return;
            var cg = rt.GetComponent<CanvasGroup>();
            if (cg == null) cg = rt.gameObject.AddComponent<CanvasGroup>();
            _revealItems.Add(rt);
            _revealTargets.Add(rt.anchoredPosition);
            _revealGroups.Add(cg);
        }

        private void BuildFade(Transform root)
        {
            var go = new GameObject("FadeOverlay", typeof(Image));
            go.transform.SetParent(root, false);
            _fadeImg = go.GetComponent<Image>();
            _fadeImg.color = new Color(0f, 0f, 0f, 0f);
            _fadeImg.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        // ───────────────────── UI 헬퍼 ─────────────────────

        private GameObject MakePanel(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = color.a > 0.01f; // 완전 투명이면 클릭 통과
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return go;
        }

        private GameObject MakeImage(string name, Transform parent, Color color, Vector2 anchor, Vector2 size)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = size;
            return go;
        }

        private Text MakeText(string name, Transform parent, string content, int size, Color color,
            Vector2 anchor, Vector2 boxSize, FontStyle style)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.text = content;
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = boxSize;
            return t;
        }

        private Button MakeButton(string name, Transform parent, string label, Color labelColor,
            Vector2 anchor, Action onClick)
        {
            var go = new GameObject(name, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = BtnNormal;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(360, 78);

            var btn = go.GetComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = BtnNormal;
            colors.highlightedColor = new Color(0.24f, 0.16f, 0.16f, 1f);
            colors.pressedColor = new Color(0.32f, 0.10f, 0.10f, 1f);
            colors.selectedColor = BtnNormal;
            btn.colors = colors;
            btn.onClick.AddListener(() => onClick());

            var txt = MakeText(name + "Label", go.transform, label, 34, labelColor,
                new Vector2(0.5f, 0.5f), new Vector2(360, 78), FontStyle.Bold);
            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            return btn;
        }

        private Font ResolveFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
            return f;
        }

        private void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                es.transform.SetParent(transform, false);
            }
        }
    }
}
