using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    // ─────────────────────────────────────────────────────────────
    // 공용 uGUI 팩토리 — 프리팹 없이 런타임 코드로 UI를 조립하기 위한 헬퍼.
    // (RaidHUD / PartyFrameUI / FloatingTextManager / CounterAlertUI 공용)
    // 로스트아크 톤: 어두운 반투명 패널 + 금색/진홍 포인트, 기본 폰트.
    // ─────────────────────────────────────────────────────────────
    public static class RaidUIFactory
    {
        // 로아 톤 팔레트
        public static readonly Color PanelDark   = new Color(0.05f, 0.04f, 0.06f, 0.72f); // 어두운 반투명 패널
        public static readonly Color BorderWhite = new Color(0.92f, 0.90f, 0.85f, 0.90f); // 흰 테두리
        public static readonly Color Gold        = new Color(0.85f, 0.68f, 0.30f, 1f);    // 금색 포인트
        public static readonly Color Crimson     = new Color(0.62f, 0.06f, 0.09f, 1f);    // 진홍(어두운 쪽)
        public static readonly Color CrimsonHot  = new Color(0.96f, 0.20f, 0.18f, 1f);    // 진홍(밝은 쪽)
        public static readonly Color Purple      = new Color(0.58f, 0.28f, 0.85f, 1f);    // 무력화 보라
        public static readonly Color TrackDark   = new Color(0.10f, 0.09f, 0.11f, 0.95f); // 바 내부 트랙

        /// <summary>기본 내장 폰트 조회(LegacyRuntime.ttf, 구버전 폴백 Arial.ttf).</summary>
        public static Font GetFont()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }

        /// <summary>RectTransform 하나짜리 빈 노드 생성.</summary>
        public static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>단색 Image 생성(레이캐스트 비활성 — HUD는 입력을 먹지 않음).</summary>
        public static Image NewImage(string name, Transform parent, Color color)
        {
            var rt = NewRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>기본 폰트 Text 생성.</summary>
        public static Text NewText(string name, Transform parent, Font font, int size, Color color, TextAnchor anchor)
        {
            var rt = NewRect(name, parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>부모에 4방향 스트레치(패딩 지정).</summary>
        public static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>단일 앵커/피벗/위치/크기 지정.</summary>
        public static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        /// <summary>
        /// 로아식 게이지 바 생성: 흰 테두리(frame) → 어두운 트랙(bg) → 채움(fill).
        /// fillRect의 anchorMax.x 로 채움 비율을 표현(스프라이트 불요).
        /// </summary>
        public static Image NewBar(string name, Transform parent,
                                   Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size,
                                   Color border, Color track, Color fill, out RectTransform fillRect)
        {
            var frame = NewImage(name, parent, border);
            Place(frame.rectTransform, anchor, pivot, pos, size);

            var bg = NewImage("Track", frame.transform, track);
            Stretch(bg.rectTransform, 2f, 2f, 2f, 2f);

            var f = NewImage("Fill", bg.transform, fill);
            SetFill(f.rectTransform, 1f);
            fillRect = f.rectTransform;
            return f;
        }

        /// <summary>채움 비율(0~1) 적용 — 좌측 고정, 우측 anchor 이동.</summary>
        public static void SetFill(RectTransform fill, float amount)
        {
            amount = Mathf.Clamp01(amount);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(amount, 1f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// 월드 좌표 → 오버레이 Canvas 로컬 좌표 변환.
        /// visible=false 면 카메라 뒤(그리지 말 것).
        /// </summary>
        public static bool WorldToCanvas(RectTransform canvasRect, Camera cam, Vector3 world, out Vector2 local)
        {
            local = Vector2.zero;
            if (cam == null) return false;
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return false;   // 카메라 뒤
            // Screen Space Overlay → cam 인자는 null
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, sp, null, out local);
        }
    }

    /// <summary>
    /// 로스트아크식 레이드 HUD 조율자 + 상단 보스 바.
    /// 자체 Canvas(Screen Space Overlay, sortingOrder 10)를 만들고
    /// PartyFrameUI / FloatingTextManager / CounterAlertUI 를 하위에 조립한다.
    /// 씬에는 이 컴포넌트 하나만 부착하면 전부 구성된다.
    /// 매 스냅샷(viewer.OnSnapshotApplied) 마다 viewer.LatestSnapshot 을 읽어 갱신.
    /// </summary>
    public class RaidHUD : MonoBehaviour
    {
        [Header("Refs (비워두면 자동 탐색)")]
        [SerializeField] private BossGameViewer viewer;

        [Header("Boss")]
        [Tooltip("상단 보스 이름 표기")]
        public string bossName = "혈월의 마수 군주";
        [Tooltip("무력화 게이지 최대치 (stagger_gauge/max)")]
        public float staggerMax = 200f;

        [Header("Boss HP 멀티 레이어 (로스트아크식 x100 줄 바)")]
        [Tooltip("전체 HP를 몇 줄로 나눌지 (x100 표기 기준)")]
        [SerializeField] private int hpBarCount = 100;
        [Tooltip("줄마다 순환하는 색 팔레트 — 현재 줄 = palette[(barsRemaining-1)%len]")]
        [SerializeField] private Color[] hpLayerPalette =
        {
            new Color(0.96f, 0.20f, 0.18f, 1f), // 빨강 (진홍 밝은 톤)
            new Color(0.58f, 0.28f, 0.85f, 1f), // 보라
            new Color(0.24f, 0.46f, 0.95f, 1f), // 파랑
            new Color(0.20f, 0.80f, 0.82f, 1f), // 청록
            new Color(0.32f, 0.80f, 0.38f, 1f), // 초록
            new Color(0.85f, 0.68f, 0.30f, 1f), // 금색
        };
        [Tooltip("표시 HP를 실제 HP로 지수 보간하는 시간상수(초) — 작을수록 빠르게 따라감")]
        [SerializeField] private float hpSmoothTau = 0.25f;
        [Tooltip("잔상(ghost) 바가 실제 fill로 감쇠하는 지연 시간상수(초)")]
        [SerializeField] private float ghostLagTau = 0.5f;
        [Tooltip("잔상 바 색 — 방금 깎인 양을 밝게 번쩍임")]
        [SerializeField] private Color ghostColor = new Color(1f, 0.95f, 0.85f, 0.9f);

        private const int PhaseCount = 3;

        // 런타임 생성 UI 참조
        private Canvas _canvas;
        private RectTransform _canvasRect;
        private Font _font;

        // 멀티 레이어 HP 바 참조
        private Image _hpTrackImg;      // 트랙(바로 아래 줄 색이 드러남, 마지막 줄이면 TrackDark)
        private RectTransform _hpGhost; // 잔상 fill (밝은 색)
        private Image _hpGhostImg;
        private RectTransform _hpFill;  // 현재 줄 fill
        private Image _hpFillImg;
        private Text _countText;        // "xNN" 남은 줄 수 표기
        private Text _hpText;           // 중앙 hp / max (pct)
        private Image[] _phasePips;

        // HP 보간 상태 (스냅샷 ~0.3s 간격 → Update에서 매 프레임 부드럽게)
        private float _maxHp = 1f;      // 실제 max_hp
        private float _targetHp;        // 실제 hp (스냅샷)
        private float _displayedHp;     // 표시 HP (지수 보간)
        private float _ghost = 1f;      // 현재 줄 기준 잔상 폭(0~1)
        private int _prevBars = -1;     // 직전 프레임 남은 줄 수(줄 넘김 감지)
        private bool _hpReady;          // 첫 스냅샷 수신 여부
        private bool _resetSnap;        // 리셋/회복 감지 → 잔상 즉시 스냅

        private GameObject _staggerRoot;
        private RectTransform _staggerFill;
        private Text _staggerText;

        // 하위 컴포넌트
        private PartyFrameUI _party;
        private FloatingTextManager _floating;
        private CounterAlertUI _counter;
        private SealAlertUI _seal;

        private bool _subscribed;

        private void Awake()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            _font = RaidUIFactory.GetFont();

            BuildCanvas();
            BuildBossBar();

            // 하위 UI 컴포넌트 조립 (각자 별도 GameObject)
            _party    = CreateChild<PartyFrameUI>("PartyFrame");
            _floating = CreateChild<FloatingTextManager>("FloatingText");
            _counter  = CreateChild<CounterAlertUI>("CounterAlert");
            _seal     = CreateChild<SealAlertUI>("SealAlert");

            _party.Build(_canvas, _font);
            _floating.Build(_canvas, _canvasRect, _font, viewer);
            _counter.Build(_canvas, _font);
            _seal.Build(_canvas, _font);
        }

        private void OnEnable()
        {
            TrySubscribe();
        }

        private void Start()
        {
            // Awake 순서 문제로 viewer 가 늦게 준비될 수 있어 재시도
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            TrySubscribe();
        }

        private void OnDisable()
        {
            if (_subscribed && viewer != null)
            {
                viewer.OnSnapshotApplied -= HandleSnapshot;
                _subscribed = false;
            }
        }

        private void TrySubscribe()
        {
            if (_subscribed || viewer == null) return;
            viewer.OnSnapshotApplied += HandleSnapshot;
            _subscribed = true;
            // 이미 스냅샷이 있으면 즉시 1회 반영
            if (viewer.LatestSnapshot != null) HandleSnapshot(viewer.LatestSnapshot);
        }

        // ─────────────── Canvas / 보스 바 구성 ───────────────

        private void BuildCanvas()
        {
            var go = new GameObject("RaidHUD_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);

            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 10;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _canvasRect = (RectTransform)_canvas.transform;
        }

        private T CreateChild<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            return go.AddComponent<T>();
        }

        private void BuildBossBar()
        {
            // 상단 중앙 컨테이너 (앵커: top-center)
            var panel = RaidUIFactory.NewImage("BossBar", _canvas.transform, RaidUIFactory.PanelDark);
            RaidUIFactory.Place(panel.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -24f), new Vector2(760f, 118f));

            // 보스 이름
            var name = RaidUIFactory.NewText("BossName", panel.transform, _font, 26, RaidUIFactory.Gold, TextAnchor.UpperCenter);
            RaidUIFactory.Place(name.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -8f), new Vector2(720f, 34f));
            name.text = bossName;
            name.fontStyle = FontStyle.Bold;

            // 페이즈 표시(P1/P2/P3 다이아 3개) — 이름 우측 상단
            _phasePips = new Image[PhaseCount];
            for (int i = 0; i < PhaseCount; i++)
            {
                var pip = RaidUIFactory.NewImage($"Phase{i + 1}", panel.transform, RaidUIFactory.TrackDark);
                RaidUIFactory.Place(pip.rectTransform,
                    new Vector2(1f, 1f), new Vector2(1f, 1f),
                    new Vector2(-12f - i * 26f, -14f), new Vector2(18f, 18f));
                pip.rectTransform.localRotation = Quaternion.Euler(0, 0, 45f); // 다이아
                _phasePips[i] = pip;
            }

            // ── 로스트아크식 멀티 레이어 HP 바 ──
            // 단일 바 영역에 N줄(hpBarCount)을 색으로 표현한다.
            // 레이어(뒤→앞): 테두리 → 트랙(아래 줄 색) → 잔상 ghost(밝은 색) → 현재 줄 fill.
            // 앞줄 fill이 다 닳으면 그 아래 트랙 색(다음 줄)이 드러나는 구조.
            var frame = RaidUIFactory.NewImage("BossHpBar", panel.transform, RaidUIFactory.BorderWhite);
            RaidUIFactory.Place(frame.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -50f), new Vector2(720f, 34f));

            // 트랙: 바로 아래 줄 색이 드러나는 배경(색은 매 프레임 갱신)
            _hpTrackImg = RaidUIFactory.NewImage("Track", frame.transform, RaidUIFactory.TrackDark);
            RaidUIFactory.Stretch(_hpTrackImg.rectTransform, 2f, 2f, 2f, 2f);

            // 잔상 ghost: 방금 깎인 양을 밝게 번쩍이며 늦게 따라 내려옴(fill보다 넓음)
            _hpGhostImg = RaidUIFactory.NewImage("GhostFill", _hpTrackImg.transform, ghostColor);
            RaidUIFactory.SetFill(_hpGhostImg.rectTransform, 1f);
            _hpGhost = _hpGhostImg.rectTransform;

            // 현재 줄 fill: 현재 줄 색(palette 순환) — ghost 위에 얹혀 좌측을 덮는다
            _hpFillImg = RaidUIFactory.NewImage("Fill", _hpTrackImg.transform, RaidUIFactory.CrimsonHot);
            RaidUIFactory.SetFill(_hpFillImg.rectTransform, 1f);
            _hpFill = _hpFillImg.rectTransform;

            // 큰 "xNN" 줄 수 표기 — 바 좌측(금색, Bold)
            _countText = RaidUIFactory.NewText("BossHpCount", _hpTrackImg.transform, _font, 28, RaidUIFactory.Gold, TextAnchor.MiddleLeft);
            RaidUIFactory.Place(_countText.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(12f, 0f), new Vector2(160f, 32f));
            _countText.fontStyle = FontStyle.Bold;

            // HP 수치/퍼센트 (바 중앙에 겹침) — 기존 유지
            _hpText = RaidUIFactory.NewText("BossHpText", _hpTrackImg.transform, _font, 18, Color.white, TextAnchor.MiddleCenter);
            RaidUIFactory.Stretch(_hpText.rectTransform, 0f, 0f, 0f, 0f);
            _hpText.fontStyle = FontStyle.Bold;

            // 무력화(스태거) 게이지 — 보스 바 아래, 기본 숨김
            var stg = RaidUIFactory.NewImage("StaggerRoot", panel.transform, new Color(0, 0, 0, 0));
            RaidUIFactory.Place(stg.rectTransform,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
                new Vector2(0f, -4f), new Vector2(560f, 20f));
            _staggerRoot = stg.gameObject;

            RaidUIFactory.NewBar("StaggerBar", stg.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(560f, 18f),
                RaidUIFactory.BorderWhite, RaidUIFactory.TrackDark, RaidUIFactory.Purple,
                out _staggerFill);

            _staggerText = RaidUIFactory.NewText("StaggerText", stg.transform, _font, 13, Color.white, TextAnchor.MiddleCenter);
            RaidUIFactory.Stretch(_staggerText.rectTransform, 0f, 0f, 0f, 0f);

            _staggerRoot.SetActive(false);
        }

        // ─────────────── 스냅샷 반영 ───────────────

        private void HandleSnapshot(BossSnapshot snap)
        {
            if (snap == null) return;

            UpdateBossBar(snap);

            if (_party != null)    _party.Refresh(snap);
            if (_floating != null) _floating.OnSnapshot(snap);
            if (_counter != null)  _counter.OnSnapshot(snap);
            if (_seal != null)     _seal.OnSnapshot(snap);
        }

        private void UpdateBossBar(BossSnapshot snap)
        {
            var b = snap.boss;
            if (b == null) return;

            // ── HP 바 목표값 갱신(실제 렌더링은 Update에서 매 프레임 보간) ──
            float newTarget = b.hp;
            _maxHp = Mathf.Max(1f, b.max_hp);
            // 에피소드 리셋/회복: 실제 HP가 표시 HP보다 커짐 → 표시/잔상 즉시 스냅
            if (!_hpReady || newTarget > _displayedHp + 0.5f)
            {
                _displayedHp = newTarget;
                _resetSnap = true;
            }
            _targetHp = newTarget;
            _hpReady = true;

            // 페이즈 pip: 현재 페이즈까지 금색
            if (_phasePips != null)
            {
                for (int i = 0; i < _phasePips.Length; i++)
                {
                    if (_phasePips[i] == null) continue;
                    _phasePips[i].color = (i <= b.phase) ? RaidUIFactory.Gold : RaidUIFactory.TrackDark;
                }
            }

            // 무력화 게이지
            bool stagger = b.stagger_active;
            if (_staggerRoot != null) _staggerRoot.SetActive(stagger);
            if (stagger)
            {
                float g = Mathf.Clamp01(b.stagger_gauge / Mathf.Max(1f, staggerMax));
                RaidUIFactory.SetFill(_staggerFill, g);
                if (_staggerText != null)
                    _staggerText.text = $"무력화  {b.stagger_gauge:0} / {staggerMax:0}";
            }
        }

        // ─────────────── 매 프레임 HP 바 보간/렌더 ───────────────
        // 스냅샷은 ~0.3s 간격으로만 오므로, 부드러운 감소·잔상은 여기서 처리한다.

        private void Update()
        {
            if (!_hpReady) return;

            float dt = Time.deltaTime;

            // (a) 표시 HP → 실제 HP 지수 보간(시간상수 hpSmoothTau)
            float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, hpSmoothTau));
            _displayedHp = Mathf.Lerp(_displayedHp, _targetHp, k);
            if (Mathf.Abs(_displayedHp - _targetHp) < 0.5f) _displayedHp = _targetHp;

            RenderBossBar(dt);
        }

        private void RenderBossBar(float dt)
        {
            float totalRatio = Mathf.Clamp01(_displayedHp / _maxHp);

            // 남은 줄 수와 현재 줄 fill 비율
            int barsRemaining = (_displayedHp > 0f)
                ? Mathf.Max(1, Mathf.CeilToInt(totalRatio * hpBarCount))
                : 0;
            float lineFill = (barsRemaining > 0)
                ? Mathf.Clamp01(totalRatio * hpBarCount - (barsRemaining - 1))
                : 0f;

            int len = (hpLayerPalette != null && hpLayerPalette.Length > 0) ? hpLayerPalette.Length : 1;

            // 색 결정: 현재 줄 fill 색 / 그 아래(다음) 줄 트랙 색
            Color lineColor = (barsRemaining > 0 && hpLayerPalette != null && len > 0)
                ? hpLayerPalette[(barsRemaining - 1) % len]
                : RaidUIFactory.TrackDark;
            Color trackColor = (barsRemaining >= 2 && hpLayerPalette != null && len > 0)
                ? hpLayerPalette[(barsRemaining - 2) % len]  // 바로 아래 줄
                : RaidUIFactory.TrackDark;                   // 마지막 줄이면 어두운 트랙

            // 잔상 처리: 리셋 스냅 or 줄 넘김 or 지연 감쇠
            if (_resetSnap)
            {
                // 리셋/회복 직후엔 번쩍임 없이 즉시 맞춤
                _prevBars = barsRemaining;
                _ghost = lineFill;
                _resetSnap = false;
            }
            else if (barsRemaining != _prevBars)
            {
                // 줄 넘김 순간: ghost를 1f로 리셋 → 새 줄 전체가 번쩍이며 lineFill로 감쇠
                _ghost = 1f;
                _prevBars = barsRemaining;
            }

            // ghost는 항상 lineFill 이상 유지, 지연 감쇠로 늦게 따라옴
            if (lineFill >= _ghost)
            {
                _ghost = lineFill; // 회복 등으로 fill이 더 크면 즉시 맞춤
            }
            else
            {
                float gk = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, ghostLagTau));
                _ghost = Mathf.Lerp(_ghost, lineFill, gk);
            }

            // 레이어 적용
            if (_hpTrackImg != null) _hpTrackImg.color = trackColor;
            if (_hpFillImg != null) _hpFillImg.color = lineColor;
            if (_hpFill != null) RaidUIFactory.SetFill(_hpFill, lineFill);
            if (_hpGhost != null) RaidUIFactory.SetFill(_hpGhost, _ghost);

            // 텍스트: 좌측 xNN 줄 수, 중앙 hp / max (pct)
            if (_countText != null) _countText.text = $"x{barsRemaining}";
            if (_hpText != null)
                _hpText.text = $"{_targetHp:N0} / {_maxHp:N0}  ({totalRatio * 100f:0}%)";
        }
    }
}
