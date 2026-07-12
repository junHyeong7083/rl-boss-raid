using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 로스트아크식 하단 중앙 스킬바. 런타임 uGUI 코드 생성(프리팹 불요).
    /// 슬롯 = 배경 사각 + 금색 테두리 + 스킬 약칭 + 키 라벨 + 방사형 쿨다운 필 + 남은 턴.
    ///
    /// 매 스냅샷 딜러 units.cooldowns 를 읽어 필/텍스트 갱신(self-subscribe).
    /// 컨트롤러가 쿨다운 차단 시 Shake(cooldownKey) 로 흔들림 피드백을 요청한다.
    /// 조준 모드 중 SetAimHighlight(key) 로 해당 슬롯 금색 테두리 펄스.
    /// 발동 즉시 StartPredictedCooldown 으로 클라 예측 쿨 표시 → 스냅샷 값과 큰 값 우선 동기화.
    ///
    /// 씬 부착: 빈 GameObject 에 이 컴포넌트만 붙이면 Awake 에서 Canvas 까지 자체 생성.
    /// </summary>
    public class SkillBarUI : MonoBehaviour
    {
        /// <summary>슬롯 정의(확장 가능한 배열 구조).</summary>
        [Serializable]
        public class SkillSlotDef
        {
            public string label;         // 약칭 (예: "강딜")
            public string keyLabel;      // 키 라벨 (예: "W")
            public string cooldownKey;   // 스냅샷 cooldowns 키 (없으면 빈 문자열 = 쿨다운 없음)
            public int maxCooldown;      // 방사형 필 정규화용 최대 턴
        }

        [Header("Refs (비우면 자동 탐색)")]
        [Tooltip("스냅샷 조회용 조율자.")]
        [SerializeField] private BossGameViewer viewer;
        [Tooltip("직접 지정할 Canvas(비우면 Screen Space Overlay 자체 생성).")]
        [SerializeField] private Canvas canvas;

        [Header("Slots (조작 개편 딜러 킷 7종, 확장 가능)")]
        [SerializeField]
        private SkillSlotDef[] slotDefs = new SkillSlotDef[]
        {
            new SkillSlotDef { label = "혈창",   keyLabel = "Q",     cooldownKey = "skill",   maxCooldown = 20 },
            new SkillSlotDef { label = "혈월",   keyLabel = "W",     cooldownKey = "skill2",  maxCooldown = 40 },
            new SkillSlotDef { label = "카운터", keyLabel = "E",     cooldownKey = "counter", maxCooldown = 25 },
            new SkillSlotDef { label = "혈월처형", keyLabel = "R",   cooldownKey = "ult",     maxCooldown = 200 },
            new SkillSlotDef { label = "대시",   keyLabel = "Shift", cooldownKey = "dash",    maxCooldown = 17 },
            new SkillSlotDef { label = "가드",   keyLabel = "G",     cooldownKey = "parry",   maxCooldown = 10 },
            new SkillSlotDef { label = "평타",   keyLabel = "C",     cooldownKey = "",        maxCooldown = 0 },
        };

        [Header("Style (로아 톤)")]
        [SerializeField] private float slotSize = 88f;
        [SerializeField] private float slotGap = 12f;
        [SerializeField] private float bottomMargin = 26f;
        [SerializeField] private Color slotBg = new Color32(0x1a, 0x1a, 0x22, 0xcc);   // #1a1a22cc
        [SerializeField] private Color slotBorder = new Color32(0xC8, 0xA2, 0x4B, 0xff); // 금색
        [SerializeField] private Color cdOverlay = new Color(0f, 0f, 0f, 0.7f);          // 검정 70%
        [SerializeField] private float shakeDuration = 0.35f;
        [SerializeField] private float shakeMagnitude = 8f;
        [Tooltip("조준 모드 하이라이트 펄스 색(밝은 금색).")]
        [SerializeField] private Color aimPulseColor = new Color32(0xFF, 0xE2, 0x8A, 0xff);
        [SerializeField] private float aimPulseSpeed = 6f;
        [Tooltip("쿨다운 숫자를 남은 '초'로 표시할 때 턴→초 환산 계수(서버 turn-interval=0.3s). "
               + "20/40턴급 긴 쿨은 턴수보다 초(소수1)가 직관적.")]
        [SerializeField] private float secondsPerTurn = 0.3f;

        // ─────────────── 런타임 슬롯 상태 ───────────────
        private class Slot
        {
            public SkillSlotDef def;
            public RectTransform shakeRoot;   // 흔들림 대상(레이아웃과 분리)
            public Image border;              // 금색 테두리(조준 하이라이트 펄스 대상)
            public Image cdFill;              // 방사형 쿨다운 필
            public Text cdText;               // 남은 턴
            public float shakeElapsed = float.MaxValue;
        }

        private readonly List<Slot> _slots = new List<Slot>();
        private Sprite _whiteSprite;
        private Font _font;
        private bool _subscribed;
        private bool _built;
        private string _aimKey;                                                        // 조준 중 슬롯의 cooldownKey (null = 없음)
        private readonly Dictionary<string, int> _serverCds = new Dictionary<string, int>();     // 최신 서버 쿨다운
        private readonly Dictionary<string, int> _predictedCds = new Dictionary<string, int>();  // 클라 예측 쿨다운

        private void Awake()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            _font = LoadFont();
            _whiteSprite = MakeWhiteSprite();
            Build();
        }

        private void OnEnable()  => TrySubscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        // ─────────────── 스냅샷 구독 ───────────────

        private void TrySubscribe()
        {
            if (_subscribed) return;
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            if (viewer == null) return;
            viewer.OnSnapshotApplied += OnSnapshotApplied;   // B1 계약: Action<BossSnapshot> — 스냅샷 파라미터 수신
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || viewer == null) return;
            viewer.OnSnapshotApplied -= OnSnapshotApplied;
            _subscribed = false;
        }

        private void OnSnapshotApplied(BossSnapshot snap)
        {
            if (snap == null || snap.units == null) return;
            UnitData dealer = null;
            foreach (var u in snap.units)
            {
                if (u != null && u.role == (int)PartyRole.Dealer) { dealer = u; break; }
            }
            ApplyCooldowns(dealer);
        }

        /// <summary>
        /// 딜러 cooldowns 로 각 슬롯 방사형 필/남은턴 갱신.
        /// 클라 예측 쿨은 스냅샷(턴)마다 1 감소, 서버 값이 오면 큰 값 우선으로 동기화.
        /// </summary>
        public void ApplyCooldowns(UnitData dealer)
        {
            var cds = dealer != null ? dealer.cooldowns : null;
            foreach (var slot in _slots)
            {
                string key = slot.def.cooldownKey;
                if (string.IsNullOrEmpty(key))
                {
                    SetSlotCooldown(slot, 0, 0);
                    continue;
                }

                int server = 0;
                if (cds != null) cds.TryGetValue(key, out server);
                _serverCds[key] = server;

                // 예측 쿨 턴 경과 처리: 스냅샷 = 1턴. 서버가 예측 이상이면 서버 확정(예측 종료).
                if (_predictedCds.TryGetValue(key, out int pred) && pred > 0)
                {
                    pred -= 1;
                    if (server >= pred) pred = 0;
                    _predictedCds[key] = pred;
                }
                else pred = 0;

                SetSlotCooldown(slot, Mathf.Max(server, pred), slot.def.maxCooldown);
            }
        }

        /// <summary>
        /// 클라이언트 예측 쿨다운 시작(발동 즉시 호출). 스냅샷 cooldowns 가 확정하면 큰 값 우선 동기화.
        /// </summary>
        public void StartPredictedCooldown(string cooldownKey, int turns)
        {
            if (string.IsNullOrEmpty(cooldownKey) || turns <= 0) return;
            _predictedCds[cooldownKey] = turns;

            // 즉시 UI 반영 (스냅샷 대기 없이).
            foreach (var slot in _slots)
            {
                if (slot.def.cooldownKey != cooldownKey) continue;
                SetSlotCooldown(slot, GetEffectiveCooldown(cooldownKey), slot.def.maxCooldown);
                break;
            }
        }

        /// <summary>유효 쿨다운 = max(서버, 클라 예측). 컨트롤러 쿨다운 가드용.</summary>
        public int GetEffectiveCooldown(string cooldownKey)
        {
            if (string.IsNullOrEmpty(cooldownKey)) return 0;
            int s = _serverCds.TryGetValue(cooldownKey, out var sv) ? sv : 0;
            int p = _predictedCds.TryGetValue(cooldownKey, out var pv) ? pv : 0;
            return Mathf.Max(s, p);
        }

        /// <summary>조준 모드 하이라이트 대상 설정(null/"" = 해제). 해당 슬롯 테두리 금색 펄스.</summary>
        public void SetAimHighlight(string cooldownKey)
        {
            _aimKey = string.IsNullOrEmpty(cooldownKey) ? null : cooldownKey;
            // 해제 시 테두리 색 즉시 복원.
            if (_aimKey == null)
            {
                foreach (var slot in _slots)
                    if (slot.border != null) slot.border.color = slotBorder;
            }
        }

        private void SetSlotCooldown(Slot slot, int remaining, int max)
        {
            float fill = max > 0 ? Mathf.Clamp01((float)remaining / max) : 0f;
            if (slot.cdFill != null) slot.cdFill.fillAmount = fill;
            if (slot.cdText != null)
            {
                bool show = remaining > 0;
                slot.cdText.enabled = show;
                slot.cdText.text = show ? FormatCooldown(remaining) : "";
            }
        }

        /// <summary>
        /// 쿨다운 숫자 포맷: 남은 턴 → 초(턴×secondsPerTurn) 환산. 20/40턴급 긴 쿨은 초가 직관적.
        /// 10초 이상은 정수, 미만은 소수1(예: 12 / 5.1 / 0.3)로 슬롯 폭에 맞춰 표시.
        /// </summary>
        private string FormatCooldown(int remainingTurns)
        {
            if (secondsPerTurn <= 0f) return remainingTurns.ToString();
            float sec = remainingTurns * secondsPerTurn;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return sec >= 10f ? Mathf.RoundToInt(sec).ToString(ci) : sec.ToString("0.0", ci);
        }

        /// <summary>쿨다운 차단 피드백: 해당 쿨다운 키 슬롯을 흔든다.</summary>
        public void Shake(string cooldownKey)
        {
            foreach (var slot in _slots)
            {
                if (slot.def.cooldownKey == cooldownKey) { slot.shakeElapsed = 0f; break; }
            }
        }

        private void Update()
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * aimPulseSpeed);
            foreach (var slot in _slots)
            {
                // 조준 하이라이트: 테두리 금색 펄스.
                if (slot.border != null)
                {
                    bool highlighted = _aimKey != null && slot.def.cooldownKey == _aimKey;
                    slot.border.color = highlighted
                        ? Color.Lerp(slotBorder, aimPulseColor, pulse)
                        : slotBorder;
                }

                // 쿨다운 차단 흔들림.
                if (slot.shakeElapsed >= shakeDuration) continue;
                slot.shakeElapsed += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(1f - slot.shakeElapsed / shakeDuration);
                float off = Mathf.Sin(slot.shakeElapsed * 55f) * shakeMagnitude * k;
                if (slot.shakeRoot != null) slot.shakeRoot.localPosition = new Vector3(off, 0f, 0f);
                if (slot.shakeElapsed >= shakeDuration && slot.shakeRoot != null)
                    slot.shakeRoot.localPosition = Vector3.zero;
            }
        }

        // ─────────────── UI 생성 ───────────────

        private void Build()
        {
            if (_built) return;
            _built = true;

            EnsureCanvas();

            int n = slotDefs != null ? slotDefs.Length : 0;

            // 하단 중앙 컨테이너.
            var bar = NewUI("SkillBar", canvas.transform);
            bar.anchorMin = new Vector2(0.5f, 0f);
            bar.anchorMax = new Vector2(0.5f, 0f);
            bar.pivot = new Vector2(0.5f, 0f);
            bar.anchoredPosition = new Vector2(0f, bottomMargin);
            float totalW = n * slotSize + Mathf.Max(0, n - 1) * slotGap;
            bar.sizeDelta = new Vector2(totalW, slotSize);

            for (int i = 0; i < n; i++)
            {
                var def = slotDefs[i] ?? new SkillSlotDef();
                var slot = BuildSlot(bar, def, i, n, totalW);
                _slots.Add(slot);
            }
        }

        private Slot BuildSlot(RectTransform bar, SkillSlotDef def, int index, int count, float totalW)
        {
            // 슬롯 루트(레이아웃 위치 고정) — 흔들림은 하위 shakeRoot 에서만.
            var slotRoot = NewUI($"Slot_{index}", bar);
            slotRoot.sizeDelta = new Vector2(slotSize, slotSize);
            slotRoot.anchorMin = new Vector2(0.5f, 0.5f);
            slotRoot.anchorMax = new Vector2(0.5f, 0.5f);
            slotRoot.pivot = new Vector2(0.5f, 0.5f);
            float x = -totalW * 0.5f + slotSize * 0.5f + index * (slotSize + slotGap);
            slotRoot.anchoredPosition = new Vector2(x, 0f);

            var shakeRoot = NewUI("Shake", slotRoot);
            Stretch(shakeRoot);

            // 금색 테두리(뒤 판) + 어두운 배경(앞 판, 살짝 인셋).
            var border = NewImage("Border", shakeRoot, slotBorder);
            Stretch(border);
            var bg = NewImage("Bg", shakeRoot, slotBg);
            Stretch(bg, 3f);

            // 스킬 약칭(중앙 하단).
            var name = NewText("Name", shakeRoot, def.label, 18, new Color(0.95f, 0.92f, 0.8f, 1f));
            Stretch(name);
            name.alignment = TextAnchor.LowerCenter;
            name.rectTransform.offsetMin = new Vector2(2f, 6f);
            name.rectTransform.offsetMax = new Vector2(-2f, -2f);

            // 키 라벨(좌상단).
            var keyLbl = NewText("Key", shakeRoot, def.keyLabel, 20, new Color(1f, 0.85f, 0.4f, 1f));
            Stretch(keyLbl);
            keyLbl.alignment = TextAnchor.UpperLeft;
            keyLbl.fontStyle = FontStyle.Bold;
            keyLbl.rectTransform.offsetMin = new Vector2(6f, 2f);
            keyLbl.rectTransform.offsetMax = new Vector2(-2f, -4f);

            // 방사형 쿨다운 오버레이(검정 70%, radial360, 위→시계방향).
            var cdFill = NewImage("CdFill", shakeRoot, cdOverlay);
            Stretch(cdFill);
            cdFill.type = Image.Type.Filled;
            cdFill.fillMethod = Image.FillMethod.Radial360;
            cdFill.fillOrigin = (int)Image.Origin360.Top;
            cdFill.fillClockwise = true;
            cdFill.fillAmount = 0f;
            cdFill.raycastTarget = false;

            // 남은 턴(중앙, 큰 글씨).
            var cdText = NewText("CdText", shakeRoot, "", 30, Color.white);
            Stretch(cdText);
            cdText.alignment = TextAnchor.MiddleCenter;
            cdText.fontStyle = FontStyle.Bold;
            cdText.enabled = false;

            return new Slot { def = def, shakeRoot = shakeRoot, border = border, cdFill = cdFill, cdText = cdText };
        }

        private void EnsureCanvas()
        {
            if (canvas != null) return;
            var go = new GameObject("SkillBarCanvas");
            go.transform.SetParent(transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
        }

        // ─────────────── uGUI 헬퍼 ───────────────

        private static RectTransform NewUI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private Image NewImage(string name, Transform parent, Color color)
        {
            var rt = NewUI(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _whiteSprite;
            img.color = color;
            return img;
        }

        private Text NewText(string name, Transform parent, string content, int size, Color color)
        {
            var rt = NewUI(name, parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = _font;
            t.text = content;
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>부모에 꽉 채우기(+ 균일 인셋).</summary>
        private static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        private static void Stretch(Image img, float inset = 0f) => Stretch(img.rectTransform, inset);
        private static void Stretch(Text txt, float inset = 0f) => Stretch(txt.rectTransform, inset);

        private static Font LoadFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
            return f;
        }

        private static Sprite MakeWhiteSprite()
        {
            var tex = Texture2D.whiteTexture;
            return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
