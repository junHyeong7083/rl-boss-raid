using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 카운터 알림.
    /// boss.counter_window > 0 진입 순간: 화면 중앙 상단 "카운터!" 파랑 대형 텍스트
    /// 스케일 펀치(0.5초) + 창 지속 동안 은은한 펄스. 창 종료 시 숨김.
    /// events 에 counter_success 시 "저지 성공!" 초록 플래시.
    /// RaidHUD 가 Build/OnSnapshot 을 호출한다. 애니메이션은 자체 Update.
    /// </summary>
    public class CounterAlertUI : MonoBehaviour
    {
        private static readonly Color Blue  = new Color(0.35f, 0.70f, 1.00f, 1f); // 카운터 파랑
        private static readonly Color Green = new Color(0.35f, 0.95f, 0.45f, 1f); // 성공 초록

        private const float PunchTime = 0.5f;    // 스케일 펀치 지속
        private const float PunchScale = 0.7f;   // 추가 스케일량(1 + 0.7 → 1)
        private const float SuccessTime = 0.9f;  // 성공 플래시 지속

        private enum Mode { None, Window, Success }
        private Mode _mode = Mode.None;

        private Text _text;
        private RectTransform _rect;
        private float _elapsed;       // 현재 모드 경과 시간
        private int _prevWindow;      // 직전 counter_window (진입 감지용)

        public void Build(Canvas canvas, Font font)
        {
            _text = RaidUIFactory.NewText("CounterAlert", canvas.transform, font, 64, Blue, TextAnchor.MiddleCenter);
            RaidUIFactory.Place(_text.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -170f), new Vector2(700f, 90f));
            _text.fontStyle = FontStyle.Bold;

            // 가독성용 그림자
            var sh = _text.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
            sh.effectDistance = new Vector2(3f, -3f);

            _rect = _text.rectTransform;
            _text.gameObject.SetActive(false);
        }

        // ─────────────── 스냅샷 반영 ───────────────

        public void OnSnapshot(BossSnapshot snap)
        {
            // 1) counter_success 이벤트 → 성공 플래시 우선
            if (snap.events != null)
            {
                foreach (var ev in snap.events)
                {
                    if (ev != null && ev.type == "counter_success")
                    {
                        StartSuccess();
                        _prevWindow = 0;
                        return;
                    }
                }
            }

            int window = snap.boss != null ? snap.boss.counter_window : 0;

            // 2) 창 진입(0 → >0) 순간: 카운터 알림 시작
            if (window > 0 && _prevWindow <= 0 && _mode != Mode.Success)
            {
                StartWindow();
            }
            // 3) 창 종료: 성공 플래시 재생 중이 아니면 숨김
            else if (window <= 0 && _mode == Mode.Window)
            {
                Hide();
            }

            _prevWindow = window;
        }

        private void StartWindow()
        {
            _mode = Mode.Window;
            _elapsed = 0f;
            _text.text = "카운터!";
            _text.color = Blue;
            _text.gameObject.SetActive(true);
        }

        private void StartSuccess()
        {
            _mode = Mode.Success;
            _elapsed = 0f;
            _text.text = "저지 성공!";
            _text.color = Green;
            _text.gameObject.SetActive(true);
        }

        private void Hide()
        {
            _mode = Mode.None;
            if (_text != null) _text.gameObject.SetActive(false);
        }

        // ─────────────── 애니메이션 (히트스톱 무관 unscaled) ───────────────

        private void Update()
        {
            if (_mode == Mode.None || _text == null) return;

            _elapsed += Time.unscaledDeltaTime;

            if (_mode == Mode.Window)
            {
                // 펀치(0~0.5s): 큰 값에서 1로 수축, 이후 은은한 펄스
                float scale;
                var c = Blue;
                if (_elapsed < PunchTime)
                {
                    float k = 1f - _elapsed / PunchTime;          // 1→0
                    scale = 1f + PunchScale * k * k;              // 오버슈트 없이 감쇠
                    c.a = 1f;
                }
                else
                {
                    float pulse = 0.5f + 0.5f * Mathf.Sin(_elapsed * 6f); // 0~1
                    scale = 1f + 0.05f * pulse;                   // 미세 스케일 펄스
                    c.a = 0.65f + 0.35f * pulse;                  // 은은한 밝기 펄스
                }
                _rect.localScale = Vector3.one * scale;
                _text.color = c;
            }
            else if (_mode == Mode.Success)
            {
                float t = Mathf.Clamp01(_elapsed / SuccessTime);
                float k = 1f - Mathf.Min(1f, _elapsed / PunchTime);
                float scale = 1f + PunchScale * k * k;
                _rect.localScale = Vector3.one * scale;
                var c = Green;
                c.a = 1f - t * t;                                  // 후반 가속 페이드
                _text.color = c;
                if (_elapsed >= SuccessTime) Hide();
            }
        }
    }
}
