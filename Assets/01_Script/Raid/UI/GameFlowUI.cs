using System;
using System.Collections;
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
    /// RaidSession 이벤트(OnReady/OnStarted/OnEpisodeEnd)는 메인스레드에서 발화되므로
    /// 여기서 UI를 직접 조작해도 안전하다.
    /// </summary>
    public class GameFlowUI : MonoBehaviour
    {
        private enum FlowState { Title, Loading, Countdown, Playing, Result }

        // 로아 톤 색상
        private static readonly Color Crimson = new Color(0.72f, 0.09f, 0.11f, 1f);
        private static readonly Color Gold = new Color(0.85f, 0.68f, 0.32f, 1f);
        private static readonly Color PanelDark = new Color(0.03f, 0.02f, 0.03f, 0.96f);
        private static readonly Color PanelTrans = new Color(0.03f, 0.02f, 0.03f, 0.72f);
        private static readonly Color BtnNormal = new Color(0.14f, 0.10f, 0.10f, 1f);
        private static readonly Color TextDim = new Color(0.75f, 0.72f, 0.68f, 1f);

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
        private Text _resultBanner, _resultStats;

        private bool _subscribed;

        private void Start()
        {
            _font = ResolveFont();
            EnsureEventSystem();
            BuildCanvas();
            SubscribeSession();
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
            // → 전투/입력이 카운트다운 이전에 시작되던 문제 해결.
            Debug.Log("[GameFlowUI] ready 수신 → Countdown");
            StartCountdown();
        }

        private void HandleStarted()
        {
            // started 수신 → 이제부터 전투(InputEnabled 는 RaidSession 이 started 에서 true 로 세팅).
            // 카운트다운은 이미 끝난 상태이므로 오버레이만 걷어내고 Playing 진입.
            Debug.Log("[GameFlowUI] started 수신 → Playing");
            SetState(FlowState.Playing);
        }

        private void HandleEpisodeEnd(string result, int steps, float duration)
        {
            Debug.Log($"[GameFlowUI] episode_end: {result} steps={steps} dur={duration}");
            ShowResult(result, steps, duration);
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
                // 스케일 펀치
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
            _countText.text = ""; // 카운트다운 종료 — 숫자 지움(오버레이는 투명)

            // 카운트다운이 "끝난 뒤"에 전투 시작 요청.
            // Python 은 start 를 받기 전까지 스텝을 돌지 않으므로, 이 순간부터 실제 전투가 시작된다.
            // started 수신 시 HandleStarted 에서 Playing 진입 + InputEnabled=true.
            Debug.Log("[GameFlowUI] 카운트다운 종료 → start 전송");
            RaidSession.Instance?.SendCmd("start");
        }

        // ── Result ──
        private void ShowResult(string result, int steps, float duration)
        {
            SetState(FlowState.Result);
            string r = (result ?? "").ToLowerInvariant();
            if (r == "victory")
            {
                _resultBanner.text = "VICTORY";
                _resultBanner.color = Gold;
            }
            else if (r == "wipe")
            {
                _resultBanner.text = "WIPE";
                _resultBanner.color = Crimson;
            }
            else // timeout / 기타
            {
                _resultBanner.text = "TIME OVER";
                _resultBanner.color = TextDim;
            }
            _resultStats.text = $"전투 시간  {duration:0.0}s      스텝  {steps}";
        }

        private void OnClickRetry()
        {
            // 다시하기: 최초 시작과 동일한 순서 보장 —
            // 카운트다운(3-2-1) → 종료 순간 start 전송 → started 수신 시 Playing + InputEnabled=true.
            StartCountdown();
        }

        private void OnClickToTitle()
        {
            SetState(FlowState.Title);
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

            // 로딩바 (배경 + fill)
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
            _countdownGroup = MakePanel("CountdownGroup", root, new Color(0, 0, 0, 0)); // 투명 (패널 걷힘)
            _countText = MakeText("Count", _countdownGroup.transform, "3", 260, Gold,
                new Vector2(0.5f, 0.5f), new Vector2(600, 400), FontStyle.Bold);
        }

        private void BuildResult(Transform root)
        {
            _resultGroup = MakePanel("ResultGroup", root, PanelTrans);
            _resultBanner = MakeText("Banner", _resultGroup.transform, "VICTORY", 150, Gold,
                new Vector2(0.5f, 0.62f), new Vector2(1400, 220), FontStyle.Bold);
            _resultStats = MakeText("Stats", _resultGroup.transform, "", 34, TextDim,
                new Vector2(0.5f, 0.48f), new Vector2(1200, 60), FontStyle.Normal);
            MakeButton("RetryBtn", _resultGroup.transform, "다시하기", Gold,
                new Vector2(0.5f, 0.32f), OnClickRetry);
            MakeButton("ToTitleBtn", _resultGroup.transform, "타이틀로", TextDim,
                new Vector2(0.5f, 0.22f), OnClickToTitle);
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
            // Unity 6: LegacyRuntime.ttf. 폴백: Arial.ttf
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
