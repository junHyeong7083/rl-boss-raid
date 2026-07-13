using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 전멸기('혈월 강림', pattern==9) 파훼 안내 HUD.
    /// CinematicDirector 의 인트로(3.6초)가 끝나면 조작권이 돌아오므로, 남은 준비시간 동안
    /// "무엇을 해야 하는지"를 이 배너가 안내한다. cinematic_start ~ seal_success/seal_fail 사이 활성.
    ///
    /// 구성(상단 중앙, 보스바 아래):
    ///   ① 대형 배너 "!! 혈월 강림 !!"(진홍 Bold, 알파 펄스) + 부제 "기둥 뒤로 숨어라!"(금색)
    ///   ② 카운트다운 게이지(남은 턴/총 준비턴) + "N턴" 텍스트 — 스냅샷 간격(~0.33s) 사이를 매 프레임 보간
    ///   ③ 내 상태: 은신 중(초록) / 노출됨!(붉은 펄스)
    ///   ④ 판정: 성공 → 금색 "파훼 성공!" 1.5초 / 실패 → 진홍 "전멸..." 후 숨김
    ///
    /// hidden(은신) 판정 소스:
    ///   seal_holding 이벤트에는 hidden/turns_left 필드가 있으나, 클라 경량 파서(BossJsonParser)가
    ///   이 필드를 EventData 로 옮기지 않는다(스키마 고정, 본 작업 소유 밖). 따라서 은신 여부는
    ///   스냅샷의 pillars/boss/딜러 위치로 클라에서 직접 계산한다 — 딜러-보스 선분이 살아있는 기둥과
    ///   교차하면 은신(env._unit_hidden / segment_intersects_circle 와 동일 기하).
    ///   남은 턴 또한 파서에 없어, cinematic_start 이후 seal_holding 이 실린 스냅샷 수로 카운트한다.
    ///
    /// RaidHUD 가 Build/OnSnapshot 을 호출한다(다른 하위 컴포넌트와 동일). 애니메이션은 자체 Update.
    /// </summary>
    public class SealAlertUI : MonoBehaviour
    {
        // seal_wind_up_turns(=30, config.py) 와 일치. 준비시간 총 턴 수(카운트다운 분모).
        private const int DurationTurns = 30;
        private const float ResultTime = 1.5f;   // 판정 문구 표시 시간

        private static readonly Color Green = new Color(0.35f, 0.95f, 0.45f, 1f); // 은신 중
        private static readonly Color Red   = new Color(0.98f, 0.28f, 0.24f, 1f); // 노출됨

        private enum Mode { None, Active, ResultSuccess, ResultFail }
        private Mode _mode = Mode.None;

        private GameObject _root;
        private Text _title;         // "!! 혈월 강림 !!" / 판정 문구
        private RectTransform _titleRect;
        private Text _subtitle;      // "기둥 뒤로 숨어라!"
        private GameObject _barRoot;
        private RectTransform _barFill;
        private Text _turnsText;     // "N턴"
        private Text _statusText;    // 은신 중 / 노출됨!

        private float _elapsed;      // 현재 모드 경과(펄스/판정 타이머)
        private int _remaining;      // 남은 준비 턴(클라 카운트)
        private float _targetRatio;  // 남은 턴 비율(목표)
        private float _displayRatio; // 표시 비율(보간)
        private bool _hidden;        // 딜러 은신 여부(클라 계산)

        public void Build(Canvas canvas, Font font)
        {
            _root = RaidUIFactory.NewRect("SealAlertRoot", canvas.transform).gameObject;
            var rootRect = (RectTransform)_root.transform;
            RaidUIFactory.Place(rootRect,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -160f), new Vector2(840f, 300f));

            // ① 대형 배너
            _title = RaidUIFactory.NewText("SealTitle", _root.transform, font, 60,
                RaidUIFactory.CrimsonHot, TextAnchor.UpperCenter);
            RaidUIFactory.Place(_title.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, 0f), new Vector2(820f, 84f));
            _title.fontStyle = FontStyle.Bold;
            _titleRect = _title.rectTransform;
            var sh = _title.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
            sh.effectDistance = new Vector2(3f, -3f);

            // 부제
            _subtitle = RaidUIFactory.NewText("SealSubtitle", _root.transform, font, 30,
                RaidUIFactory.Gold, TextAnchor.UpperCenter);
            RaidUIFactory.Place(_subtitle.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -88f), new Vector2(820f, 40f));
            _subtitle.fontStyle = FontStyle.Bold;
            _subtitle.text = "초록 안전지대로 대피!";

            // ② 카운트다운 게이지
            RaidUIFactory.NewBar("SealCountdown", _root.transform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -138f), new Vector2(600f, 26f),
                RaidUIFactory.BorderWhite, RaidUIFactory.TrackDark, RaidUIFactory.Crimson,
                out _barFill);
            _barRoot = _barFill != null ? _barFill.parent.parent.gameObject : null; // Fill→Track→Frame

            _turnsText = RaidUIFactory.NewText("SealTurns", _root.transform, font, 18,
                Color.white, TextAnchor.MiddleCenter);
            RaidUIFactory.Place(_turnsText.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -138f), new Vector2(600f, 26f));
            _turnsText.fontStyle = FontStyle.Bold;

            // ③ 내 상태
            _statusText = RaidUIFactory.NewText("SealStatus", _root.transform, font, 32,
                Green, TextAnchor.UpperCenter);
            RaidUIFactory.Place(_statusText.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -178f), new Vector2(820f, 46f));
            _statusText.fontStyle = FontStyle.Bold;

            _root.SetActive(false);
        }

        // ─────────────── 스냅샷 반영 ───────────────

        public void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null || _root == null) return;

            bool sawStart = false, sawHold = false, sawSuccess = false, sawFail = false;
            if (snap.events != null)
            {
                foreach (var ev in snap.events)
                {
                    if (ev == null || string.IsNullOrEmpty(ev.type)) continue;
                    switch (ev.type)
                    {
                        case "cinematic_start": sawStart = true; break;   // pattern==9(전멸기)만 방출
                        case "seal_holding":    sawHold = true; break;
                        case "seal_success":    sawSuccess = true; break;
                        case "seal_fail":       sawFail = true; break;
                    }
                }
            }

            if (sawStart && _mode != Mode.Active) Activate();

            if (_mode == Mode.Active)
            {
                // 판정 우선
                if (sawSuccess) { ShowResult(true); return; }
                if (sawFail)    { ShowResult(false); return; }

                // 남은 턴 카운트: seal_holding 이 실린 스냅샷마다 1턴 감소(유닛별 다중 방출이므로 스냅샷 단위로)
                if (sawHold) _remaining = Mathf.Max(0, _remaining - 1);
                _targetRatio = DurationTurns > 0 ? (float)_remaining / DurationTurns : 0f;
                if (_turnsText != null) _turnsText.text = $"{_remaining}턴";

                // 내 상태(딜러 은신) 클라 계산
                _hidden = ComputeDealerHidden(snap);
            }
        }

        private void Activate()
        {
            _mode = Mode.Active;
            _elapsed = 0f;
            _remaining = DurationTurns;
            _targetRatio = 1f;
            _displayRatio = 1f;
            _hidden = false;

            _root.SetActive(true);
            if (_title != null)
            {
                _title.text = "!! 혈월 강림 !!";
                _title.color = RaidUIFactory.CrimsonHot;
            }
            if (_subtitle != null) _subtitle.gameObject.SetActive(true);
            if (_barRoot != null) _barRoot.SetActive(true);
            if (_turnsText != null) { _turnsText.gameObject.SetActive(true); _turnsText.text = $"{_remaining}턴"; }
            if (_statusText != null) _statusText.gameObject.SetActive(true);
            _titleRect.localScale = Vector3.one;
        }

        private void ShowResult(bool success)
        {
            _mode = success ? Mode.ResultSuccess : Mode.ResultFail;
            _elapsed = 0f;

            // 배너만 판정 문구로 전환, 나머지 요소는 숨김
            if (_subtitle != null) _subtitle.gameObject.SetActive(false);
            if (_barRoot != null) _barRoot.SetActive(false);
            if (_turnsText != null) _turnsText.gameObject.SetActive(false);
            if (_statusText != null) _statusText.gameObject.SetActive(false);

            if (_title != null)
            {
                _title.text = success ? "파훼 성공!" : "전멸...";
                _title.color = success ? RaidUIFactory.Gold : RaidUIFactory.Crimson;
            }
        }

        private void Hide()
        {
            _mode = Mode.None;
            if (_root != null) _root.SetActive(false);
        }

        // ─────────────── 애니메이션 (히트스톱 무관 unscaled) ───────────────

        private void Update()
        {
            if (_mode == Mode.None || _root == null) return;

            float dt = Time.unscaledDeltaTime;
            _elapsed += dt;

            if (_mode == Mode.Active)
            {
                // 배너 알파 펄스
                float pulse = 0.5f + 0.5f * Mathf.Sin(_elapsed * 5f); // 0~1
                if (_title != null)
                {
                    var c = RaidUIFactory.CrimsonHot;
                    c.a = 0.6f + 0.4f * pulse;
                    _title.color = c;
                }

                // 카운트다운 게이지 부드러운 보간
                float k = 1f - Mathf.Exp(-dt / 0.2f);
                _displayRatio = Mathf.Lerp(_displayRatio, _targetRatio, k);
                if (Mathf.Abs(_displayRatio - _targetRatio) < 0.002f) _displayRatio = _targetRatio;
                if (_barFill != null) RaidUIFactory.SetFill(_barFill, _displayRatio);

                // 내 상태 표시
                if (_statusText != null)
                {
                    if (_hidden)
                    {
                        _statusText.text = "은신 중";
                        _statusText.color = Green;
                    }
                    else
                    {
                        // 노출: 붉은색 강조 펄스
                        _statusText.text = "노출됨! 초록 원으로!";
                        var c = Red;
                        c.a = 0.55f + 0.45f * pulse;
                        _statusText.color = c;
                    }
                }
            }
            else // ResultSuccess / ResultFail
            {
                // 스케일 펀치 + 후반 페이드
                float k = 1f - Mathf.Min(1f, _elapsed / 0.4f);
                float scale = 1f + 0.5f * k * k;
                if (_titleRect != null) _titleRect.localScale = Vector3.one * scale;

                if (_title != null)
                {
                    var c = (_mode == Mode.ResultSuccess) ? RaidUIFactory.Gold : RaidUIFactory.Crimson;
                    float t = Mathf.Clamp01(_elapsed / ResultTime);
                    c.a = 1f - t * t; // 후반 가속 페이드
                    _title.color = c;
                }

                if (_elapsed >= ResultTime)
                {
                    if (_titleRect != null) _titleRect.localScale = Vector3.one;
                    Hide();
                }
            }
        }

        // ─────────────── 딜러 은신 클라 계산 (env._unit_hidden 미러) ───────────────

        private static bool ComputeDealerHidden(BossSnapshot snap)
        {
            if (snap.boss == null || snap.units == null || snap.pillars == null) return false;

            // 딜러 = player_slot(uid 0). 없으면 role==Dealer 폴백.
            UnitData dealer = null;
            foreach (var u in snap.units)
            {
                if (u == null) continue;
                if (u.uid == 0) { dealer = u; break; }
                if (dealer == null && u.role == (int)PartyRole.Dealer) dealer = u;
            }
            if (dealer == null || !dealer.alive) return false;

            float bx = snap.boss.x, by = snap.boss.y;
            foreach (var p in snap.pillars)
            {
                if (p == null || !p.alive) continue;
                if (SegmentHitsCircle(dealer.x, dealer.y, bx, by, p.x, p.y, p.radius))
                    return true; // 딜러-보스 시선이 기둥에 가려짐 = 은신
            }
            return false;
        }

        /// <summary>선분(a-b)과 원(c, r) 교차/접함 판정 — 점-선분 최단거리 ≤ r.</summary>
        private static bool SegmentHitsCircle(float ax, float ay, float bx, float by,
                                              float cx, float cy, float r)
        {
            float dx = bx - ax, dy = by - ay;
            float len2 = dx * dx + dy * dy;
            float t = len2 < 1e-8f ? 0f : ((cx - ax) * dx + (cy - ay) * dy) / len2;
            t = Mathf.Clamp01(t);
            float px = ax + t * dx, py = ay + t * dy;
            float ex = cx - px, ey = cy - py;
            return ex * ex + ey * ey <= r * r;
        }
    }
}
