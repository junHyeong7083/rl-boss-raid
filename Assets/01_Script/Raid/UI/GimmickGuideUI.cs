using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// "지금 뭘 해야 하는가" 한 줄 기믹 안내 배너.
    /// 화면 중앙 하단(스킬바 위쪽)에 큰 안내 문구 + 키/행동 힌트를 띄운다.
    ///
    /// 전멸기(SEAL_WIPE, pattern 9)는 SealAlertUI 가 전담하므로 이 배너는 침묵한다.
    /// 그 외 파훼가 필요한 패턴/상태를 스냅샷 필드로 매 스냅샷 판정한다(이벤트가 아니라 상태 위주).
    ///
    /// 판정 소스(BossFrameData / env.get_snapshot 확인):
    ///   - boss.counter_window (>0 이면 카운터 창 열림, active_mode=="counter")
    ///   - boss.stagger_active / active_pattern==STAGGER_LIFT(8)
    ///   - telegraphs[].pattern==FRENZY_RUSH(2) (steps 모드 텔레그래프 활성)
    ///   - telegraphs[].pattern∈{EARTH_CRUSH(1),BLOOD_ROAR(5)} 이고 shape.kind=="donut" (몸쪽 안전)
    ///   - units[uid==0(딜러)].marked (붉은 낙인 CRIMSON_BRAND(6) 표식이 나에게)
    ///   - active_pattern==SEAL_WIPE(9) or active_mode=="seal" → 침묵(SealAlertUI 담당)
    ///
    /// 우선순위: 카운터 > 표식(나) > 전멸기침묵 > 무력화 > 돌진 > 도넛. 조건 없으면 숨김.
    /// CounterAlertUI 가 상단 중앙에 "카운터!"를 이미 띄우므로, 이 배너(하단)는 겹치지 않도록
    /// 구체적 행동 지시("E — 저지하라")로 문구를 분담한다.
    ///
    /// RaidHUD 가 Build/OnSnapshot 을 호출한다(다른 하위 컴포넌트와 동일). 애니메이션은 자체 Update.
    /// </summary>
    public class GimmickGuideUI : MonoBehaviour
    {
        // 파훼 배너 상태(색/펄스 연출 분기)
        private enum Guide { None, Counter, Marked, Stagger, Rush, Donut }

        // 로아 톤 보조 색 (팩토리에 없는 것만 로컬 정의)
        private static readonly Color Blue  = new Color(0.35f, 0.70f, 1.00f, 1f); // 카운터
        private static readonly Color Green = new Color(0.35f, 0.95f, 0.45f, 1f); // 안전(도넛)

        private Guide _state = Guide.None;
        private GameObject _root;
        private Text _title;         // 큰 안내 문구
        private RectTransform _titleRect;
        private Text _hint;          // 작은 키/행동 힌트

        private Color _baseColor = Color.white;
        private bool _pulse;         // 알파 펄스(카운터/표식 강조)
        private float _elapsed;      // 상태 진입 후 경과(등장 펀치 + 펄스)

        public void Build(Canvas canvas, Font font)
        {
            _root = RaidUIFactory.NewRect("GimmickGuideRoot", canvas.transform).gameObject;
            var rootRect = (RectTransform)_root.transform;
            // 중앙 하단, 스킬바(bottomMargin 26 + 슬롯) 위쪽
            RaidUIFactory.Place(rootRect,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 150f), new Vector2(980f, 130f));

            // 큰 안내 문구
            _title = RaidUIFactory.NewText("GimmickTitle", _root.transform, font, 46,
                Color.white, TextAnchor.MiddleCenter);
            RaidUIFactory.Place(_title.rectTransform,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 56f), new Vector2(960f, 62f));
            _title.fontStyle = FontStyle.Bold;
            _titleRect = _title.rectTransform;
            var sh = _title.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
            sh.effectDistance = new Vector2(3f, -3f);

            // 키/행동 힌트
            _hint = RaidUIFactory.NewText("GimmickHint", _root.transform, font, 24,
                new Color(0.92f, 0.90f, 0.85f, 1f), TextAnchor.MiddleCenter);
            RaidUIFactory.Place(_hint.rectTransform,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 18f), new Vector2(960f, 34f));
            _hint.fontStyle = FontStyle.Bold;
            var hsh = _hint.gameObject.AddComponent<Shadow>();
            hsh.effectColor = new Color(0f, 0f, 0f, 0.8f);
            hsh.effectDistance = new Vector2(2f, -2f);

            _root.SetActive(false);
        }

        // ─────────────── 스냅샷 반영 (상태 판정) ───────────────

        public void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null || _root == null) return;

            Guide next = Evaluate(snap);
            if (next != _state) Apply(next);
        }

        /// <summary>스냅샷 필드로 현재 안내 상태를 판정. 우선순위 순으로 체크.</summary>
        private Guide Evaluate(BossSnapshot snap)
        {
            var b = snap.boss;
            if (b == null) return Guide.None;

            // 1) 카운터 창 열림 (최우선) — active_mode=="counter", counter_window>0
            if (b.counter_window > 0) return Guide.Counter;

            // 2) 붉은 낙인 표식이 나(딜러 uid 0)에게 — 파티에서 멀어져야 함
            if (DealerMarked(snap)) return Guide.Marked;

            // 3) 전멸기(SEAL_WIPE 9)는 SealAlertUI 담당 → 이 배너는 침묵
            if (b.active_pattern == (int)RaidPatternId.SealWipe || b.active_mode == "seal")
                return Guide.None;

            // 4) 무력화 — 스킬 폭딜 타이밍
            if (b.stagger_active || b.active_pattern == (int)RaidPatternId.StaggerLift)
                return Guide.Stagger;

            // 5) 폭주 돌진 텔레그래프 활성 — 기둥 뒤로 유도
            if (HasTelegraph(snap, RaidPatternId.FrenzyRush)) return Guide.Rush;

            // 6) 도넛 안전지대 (EARTH_CRUSH 도넛 스텝 / BLOOD_ROAR) — 몸쪽 안전
            if (HasDonutSafe(snap)) return Guide.Donut;

            return Guide.None;
        }

        /// <summary>딜러(uid 0, 폴백 role==Dealer)가 표식 대상인지.</summary>
        private static bool DealerMarked(BossSnapshot snap)
        {
            if (snap.units == null) return false;
            UnitData dealer = null;
            foreach (var u in snap.units)
            {
                if (u == null) continue;
                if (u.uid == 0) { dealer = u; break; }
                if (dealer == null && u.role == (int)PartyRole.Dealer) dealer = u;
            }
            return dealer != null && dealer.alive && dealer.marked;
        }

        /// <summary>해당 패턴의 steps 텔레그래프가 활성인지.</summary>
        private static bool HasTelegraph(BossSnapshot snap, RaidPatternId pattern)
        {
            if (snap.telegraphs == null) return false;
            foreach (var tg in snap.telegraphs)
                if (tg != null && tg.pattern == (int)pattern) return true;
            return false;
        }

        /// <summary>
        /// 몸쪽 안전(도넛) 상태 판정: EARTH_CRUSH(1) 또는 BLOOD_ROAR(5) 텔레그래프에
        /// donut 도형이 실려 있으면 안쪽이 안전. (EARTH_CRUSH 1스텝 중심 원 단계는 도넛 도형이
        /// 없어 제외됨 — 그 단계는 중심이 위험.)
        /// </summary>
        private static bool HasDonutSafe(BossSnapshot snap)
        {
            if (snap.telegraphs == null) return false;
            foreach (var tg in snap.telegraphs)
            {
                if (tg == null) continue;
                if (tg.pattern != (int)RaidPatternId.EarthCrush &&
                    tg.pattern != (int)RaidPatternId.BloodRoar) continue;
                if (tg.shapes == null) continue;
                foreach (var s in tg.shapes)
                    if (s != null && s.kind == "donut") return true;
            }
            return false;
        }

        /// <summary>상태 전환: 문구/색/펄스 세팅 + 등장 펀치 리셋.</summary>
        private void Apply(Guide next)
        {
            _state = next;
            _elapsed = 0f;

            if (next == Guide.None)
            {
                _root.SetActive(false);
                return;
            }

            string title, hint;
            switch (next)
            {
                case Guide.Counter:
                    title = "!! E — 저지하라 !!";
                    hint = "돌진을 반격으로 끊어라";
                    _baseColor = Blue;
                    _pulse = true;
                    break;
                case Guide.Marked:
                    title = "표식 — 파티에서 멀어져라!";
                    hint = "혼자 떨어져 폭발을 분산하라";
                    _baseColor = RaidUIFactory.Purple;
                    _pulse = true;
                    break;
                case Guide.Stagger:
                    title = "무력화 — 스킬을 퍼부어라!";
                    hint = "지금이 폭딜 타이밍";
                    _baseColor = RaidUIFactory.Purple;
                    _pulse = false;
                    break;
                case Guide.Rush:
                    title = "돌진 — 기둥 뒤로 유도하라!";
                    hint = "기둥에 충돌시켜 그로기를 만들어라";
                    _baseColor = RaidUIFactory.Gold;
                    _pulse = false;
                    break;
                default: // Donut
                    title = "보스 몸쪽이 안전하다!";
                    hint = "바깥 링을 피해 안쪽으로";
                    _baseColor = Green;
                    _pulse = false;
                    break;
            }

            if (_title != null) { _title.text = title; _title.color = _baseColor; }
            if (_hint != null) _hint.text = hint;
            if (_titleRect != null) _titleRect.localScale = Vector3.one;
            _root.SetActive(true);
        }

        // ─────────────── 애니메이션 (히트스톱 무관 unscaled) ───────────────

        private void Update()
        {
            if (_state == Guide.None || _root == null) return;

            _elapsed += Time.unscaledDeltaTime;

            // 등장 펀치: 큰 값에서 1로 감쇠(0.3s)
            float k = 1f - Mathf.Min(1f, _elapsed / 0.3f);
            float scale = 1f + 0.22f * k * k;
            if (_titleRect != null) _titleRect.localScale = Vector3.one * scale;

            if (_title != null)
            {
                var c = _baseColor;
                if (_pulse)
                {
                    float pulse = 0.5f + 0.5f * Mathf.Sin(_elapsed * 5f); // 0~1
                    c.a = 0.6f + 0.4f * pulse;
                }
                _title.color = c;
            }
        }
    }
}
