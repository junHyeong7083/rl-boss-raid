using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 상단 패턴명 어나운스 배너. (보스바 바로 아래, 상단 중앙)
    /// 스냅샷의 boss.active_pattern(폴백: telegraphs[].pattern) 변화를 감지해
    /// 패턴 한글명을 진홍/금 톤 Bold + Shadow 로 어나운스한다.
    ///   - 등장: 펀치 스케일(1.4→1.0) + 빠른 페이드인, 1.2초 밝게 강조
    ///   - 유지: 이후 은은한 알파(_HoldAlpha)로 패턴이 살아있는 동안 유지
    ///   - 종료: active_pattern 이 -1(또는 다른 값)로 바뀌면 페이드아웃
    ///
    /// RaidHUD 가 Build/OnSnapshot 을 호출한다(다른 하위 컴포넌트와 동일 계약).
    /// 애니메이션은 자체 Update(히트스톱 무관 unscaled).
    /// </summary>
    public class PatternAnnounceUI : MonoBehaviour
    {
        private const float PunchTime = 0.28f;   // 등장 펀치 지속(초)
        private const float BrightHold = 1.2f;   // 완전 강조 유지(초)
        private const float DimTime = 0.5f;      // 강조→은은 전이(초)
        private const float FadeTime = 0.45f;    // 종료 페이드아웃(초)
        private const float HoldAlpha = 0.72f;   // 유지 구간 은은한 알파

        private static readonly Color WarmHot  = new Color(1.00f, 0.86f, 0.42f, 1f); // 금색 강조
        private static readonly Color WarmBase = new Color(0.98f, 0.42f, 0.30f, 1f); // 진홍기 유지색

        // RaidPatternId → 한글 어나운스명 (설계 §2 카탈로그)
        private static readonly string[] PatternNames =
        {
            "삼연 발톱",                    // 0 TripleClaw
            "대지 분쇄",                    // 1 EarthCrush
            "폭주 돌진",                    // 2 FrenzyRush
            "기둥 투척",                    // 3 PillarThrow
            "회전 휩쓸기",                  // 4 SpinSweep
            "혈흔의 포효",                  // 5 BloodRoar
            "붉은 낙인",                    // 6 CrimsonBrand
            "카운터 돌진",                  // 7 CounterRush
            "무력화 — 기둥 들어올리기",     // 8 StaggerLift
            "혈월 강림",                    // 9 SealWipe
        };

        private GameObject _root;
        private Text _label;
        private RectTransform _labelRect;

        private enum Mode { Idle, In, Hold, Out }
        private Mode _mode = Mode.Idle;
        private float _elapsed;
        private int _shownPattern = -1;   // 현재 표시 중인 패턴 ID(-1=없음)

        public void Build(Canvas canvas, Font font)
        {
            _root = RaidUIFactory.NewRect("PatternAnnounceRoot", canvas.transform).gameObject;
            var rootRect = (RectTransform)_root.transform;
            // 보스바(높이 118, top -24) 바로 아래 상단 중앙
            RaidUIFactory.Place(rootRect,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -150f), new Vector2(760f, 56f));

            _label = RaidUIFactory.NewText("PatternName", _root.transform, font, 40,
                WarmHot, TextAnchor.MiddleCenter);
            RaidUIFactory.Stretch(_label.rectTransform, 0f, 0f, 0f, 0f);
            _label.fontStyle = FontStyle.Bold;
            _labelRect = _label.rectTransform;

            var sh = _label.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0.12f, 0.02f, 0.03f, 0.9f);   // 어두운 진홍 그림자
            sh.effectDistance = new Vector2(2.5f, -2.5f);

            _root.SetActive(false);
        }

        // ─────────────── 스냅샷 반영 ───────────────

        public void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null || _root == null) return;

            int pat = ResolvePattern(snap);

            if (pat != _shownPattern)
            {
                if (pat >= 0 && pat < PatternNames.Length)
                {
                    // 새 패턴 등장 어나운스
                    _shownPattern = pat;
                    _label.text = PatternNames[pat];
                    _mode = Mode.In;
                    _elapsed = 0f;
                    _root.SetActive(true);
                }
                else
                {
                    // 패턴 종료 → 페이드아웃
                    _shownPattern = -1;
                    if (_mode != Mode.Idle) { _mode = Mode.Out; _elapsed = 0f; }
                }
            }
        }

        /// <summary>active_pattern 우선, 없으면 telegraphs[0].pattern 폴백.</summary>
        private static int ResolvePattern(BossSnapshot snap)
        {
            if (snap.boss != null && snap.boss.active_pattern >= 0)
                return snap.boss.active_pattern;
            if (snap.telegraphs != null && snap.telegraphs.Length > 0 && snap.telegraphs[0] != null)
                return snap.telegraphs[0].pattern;
            return -1;
        }

        // ─────────────── 애니메이션 ───────────────

        private void Update()
        {
            if (_mode == Mode.Idle || _root == null) return;

            float dt = Time.unscaledDeltaTime;
            _elapsed += dt;

            float scale = 1f;
            float alpha = 1f;
            Color col = WarmHot;

            switch (_mode)
            {
                case Mode.In:
                {
                    // 등장 펀치: 1.4→1.0 easeOutCubic, 알파 빠르게 상승
                    float p = Mathf.Clamp01(_elapsed / PunchTime);
                    float pe = 1f - Mathf.Pow(1f - p, 3f);
                    scale = Mathf.Lerp(1.4f, 1.0f, pe);
                    alpha = Mathf.Clamp01(_elapsed / (PunchTime * 0.6f));
                    col = WarmHot;
                    if (_elapsed >= BrightHold) { _mode = Mode.Hold; _elapsed = 0f; }
                    break;
                }
                case Mode.Hold:
                {
                    // 강조 → 은은 유지로 색/알파 전이
                    float t = Mathf.Clamp01(_elapsed / DimTime);
                    scale = 1f;
                    alpha = Mathf.Lerp(1f, HoldAlpha, t);
                    col = Color.Lerp(WarmHot, WarmBase, t);
                    break;
                }
                case Mode.Out:
                {
                    // 종료 페이드아웃
                    float t = Mathf.Clamp01(_elapsed / FadeTime);
                    scale = 1f;
                    alpha = (1f - t) * HoldAlpha;
                    col = WarmBase;
                    if (_elapsed >= FadeTime) { _root.SetActive(false); _mode = Mode.Idle; return; }
                    break;
                }
            }

            _labelRect.localScale = Vector3.one * scale;
            col.a = alpha;
            _label.color = col;
        }
    }
}
