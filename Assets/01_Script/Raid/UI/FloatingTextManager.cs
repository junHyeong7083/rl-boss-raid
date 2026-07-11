using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 플로팅 데미지/힐 숫자 (로스트아크식 타격감).
    /// snapshot.events 에서 damage(보스 피격)/damage_taken(아군 피격)/heal(힐) 이벤트를 읽어
    /// 대상 월드 위치 위에서 떠오르며 페이드시킨다. 월드→Overlay Canvas 변환.
    ///
    /// 보스 피격(damage):
    ///  - 일반 : 흰색→연노랑, 폰트 ~24, 위로 40~60px, 0.7s 페이드. 콤마 포맷.
    ///  - 크리 : 폰트 ~38 Bold, 주황(1,0.6,0.1)→금색, 금색 아웃라인, 등장 펀치 스케일
    ///           1.6→1.0(0.15s 오버슈트), 미세 회전(-6~6도), 1.0s 유지 후 페이드, "12,345!".
    ///
    /// 간단 리스트 풀(24개)로 GC 스파이크를 방지한다. RaidHUD 가 Build/OnSnapshot 을 호출.
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        private const int PoolSize = 24;
        private const float SpawnHeight = 1.8f;   // 대상 월드 위 시작 높이(m)
        private const float SpreadPxMax = 34f;    // 좌우 랜덤 산포(px)

        // ── 일반 히트 ──
        private const float NormalLife = 0.7f;
        private const int   NormalFontSize = 24;
        private const float NormalRisePxMin = 40f;
        private const float NormalRisePxMax = 60f;

        // ── 크리티컬 ──
        private const float CritHold = 1.0f;       // 완전 불투명 유지(초)
        private const float CritFade = 0.35f;      // 이후 페이드(초)
        private const float CritPunchTime = 0.15f; // 펀치 스케일 지속(초)
        private const int   CritFontSize = 38;
        private const float CritRisePx = 26f;
        private const float CritRotMaxDeg = 6f;    // 미세 회전 범위(±도)
        private const float CritShakePx = 3f;      // 소멸 시 미세 흔들림(px)

        // ── 팔레트 ──
        private static readonly Color NormalStart     = new Color(1.00f, 1.00f, 1.00f, 1f); // 흰색
        private static readonly Color NormalEnd       = new Color(1.00f, 0.97f, 0.68f, 1f); // 연노랑
        private static readonly Color CritStart        = new Color(1.00f, 0.60f, 0.10f, 1f); // 주황
        private static readonly Color CritEnd          = new Color(1.00f, 0.84f, 0.35f, 1f); // 금색
        private static readonly Color CritOutline      = new Color(0.45f, 0.20f, 0.00f, 0.95f); // 진한 금갈색 외곽
        private static readonly Color ColDamageTaken   = new Color(0.95f, 0.20f, 0.18f, 1f); // 빨강(아군 피격)
        private static readonly Color ColHeal          = new Color(0.35f, 0.95f, 0.45f, 1f); // 초록(힐)

        private RectTransform _canvasRect;
        private BossGameViewer _viewer;
        private Font _font;

        private class Floater
        {
            public GameObject go;
            public RectTransform rect;
            public Text text;
            public Outline outline;      // 크리 전용 금색 외곽 (일반 시 비활성)
            public Vector3 worldStart;   // 시작 월드 위치(대상 위)
            public Color colStart;       // 시작 색
            public Color colEnd;         // 종료 색(그라데이션)
            public float life;           // 총 수명(초)
            public float risePx;         // 위로 떠오르는 총 거리(px)
            public float spreadPx;       // 좌우 산포(px, 부호 포함)
            public float rotDeg;         // 고정 회전(도, 크리만)
            public bool crit;            // 크리 연출 여부
            public float elapsed;
            public bool active;
        }
        private readonly Floater[] _pool = new Floater[PoolSize];

        public void Build(Canvas canvas, RectTransform canvasRect, Font font, BossGameViewer viewer)
        {
            _canvasRect = canvasRect;
            _viewer = viewer;
            _font = font;

            var root = RaidUIFactory.NewRect("FloatingText", canvas.transform);
            RaidUIFactory.Stretch(root, 0, 0, 0, 0);

            for (int i = 0; i < PoolSize; i++)
            {
                var t = RaidUIFactory.NewText($"Float{i}", root, _font, NormalFontSize, Color.white, TextAnchor.MiddleCenter);
                RaidUIFactory.Place(t.rectTransform,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(200f, 48f));
                t.fontStyle = FontStyle.Bold;

                // 크리 전용 금색 외곽 — 기본은 비활성(일반 히트는 외곽 없음).
                var outline = t.gameObject.AddComponent<Outline>();
                outline.effectColor = CritOutline;
                outline.effectDistance = new Vector2(2f, -2f);
                outline.enabled = false;

                t.gameObject.SetActive(false);
                _pool[i] = new Floater { go = t.gameObject, rect = t.rectTransform, text = t, outline = outline };
            }
        }

        // ─────────────── 이벤트 → 스폰 ───────────────

        public void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null || snap.events == null) return;

            foreach (var ev in snap.events)
            {
                if (ev == null || string.IsNullOrEmpty(ev.type)) continue;
                if (ev.type != "damage" && ev.type != "damage_taken" && ev.type != "heal") continue;

                if (!TryResolveWorld(ev, snap, out var world)) continue;

                SpawnFor(ev, world + Vector3.up * SpawnHeight);
            }
        }

        /// <summary>이벤트 종류·크리 여부에 따라 라벨/색/연출 파라미터를 구성해 스폰.</summary>
        private void SpawnFor(EventData ev, Vector3 world)
        {
            int amount = Mathf.Abs(ev.amount);
            string num = amount.ToString("N0");   // 콤마 포맷: 12,345

            // 보스 피격 크리티컬 — 크게·주황금색·펀치·회전.
            if (ev.type == "damage" && ev.crit)
            {
                var fc = new Floater
                {
                    colStart = CritStart,
                    colEnd = CritEnd,
                    life = CritHold + CritFade,
                    risePx = CritRisePx,
                    spreadPx = Random.Range(-SpreadPxMax, SpreadPxMax),
                    rotDeg = Random.Range(-CritRotMaxDeg, CritRotMaxDeg),
                    crit = true,
                };
                Spawn(fc, world, num + "!", CritFontSize);
                return;
            }

            // 일반 히트(보스 피격/아군 피격/힐).
            Color start;
            switch (ev.type)
            {
                case "damage_taken": start = ColDamageTaken; break;
                case "heal":         start = ColHeal; break;
                default:             start = NormalStart; break; // damage(비크리)
            }
            string label = (ev.type == "heal" ? "+" : "") + num;
            var f = new Floater
            {
                colStart = start,
                colEnd = (ev.type == "damage") ? NormalEnd : start, // 보스 피격만 흰→연노랑 그라데이션
                life = NormalLife,
                risePx = Random.Range(NormalRisePxMin, NormalRisePxMax),
                spreadPx = Random.Range(-SpreadPxMax, SpreadPxMax),
                rotDeg = 0f,
                crit = false,
            };
            Spawn(f, world, label, NormalFontSize);
        }

        /// <summary>이벤트 대상의 월드 위치 해석. damage=보스, heal=target 아군, 그 외=uid 아군.</summary>
        private bool TryResolveWorld(EventData ev, BossSnapshot snap, out Vector3 world)
        {
            world = Vector3.zero;
            if (_viewer == null) return false;

            // 힐: 대상(target) 아군 → 없으면 시전자(uid)
            if (ev.type == "heal")
            {
                if (TryUnitWorld(ev.target, snap, out world)) return true;
                if (TryUnitWorld(ev.uid, snap, out world)) return true;
                return false;
            }

            // 보스에 준 피해: 보스 머리 위
            if (ev.type == "damage")
            {
                if (snap.boss != null)
                {
                    world = _viewer.ContinuousToWorld(snap.boss.x, snap.boss.y);
                    return true;
                }
                // 폴백: uid 유닛
                return TryUnitWorld(ev.uid, snap, out world);
            }

            // 아군 피격: 해당 유닛
            return TryUnitWorld(ev.uid, snap, out world);
        }

        private bool TryUnitWorld(int uid, BossSnapshot snap, out Vector3 world)
        {
            world = Vector3.zero;
            if (uid < 0 || snap.units == null) return false;
            foreach (var u in snap.units)
            {
                if (u != null && u.uid == uid)
                {
                    world = _viewer.ContinuousToWorld(u.x, u.y);
                    return true;
                }
            }
            return false;
        }

        /// <summary>풀 슬롯을 확보해 스폰 파라미터를 적용.</summary>
        private void Spawn(Floater cfg, Vector3 world, string label, int fontSize)
        {
            var f = GetFree();
            if (f == null) return;

            f.worldStart = world;
            f.colStart = cfg.colStart;
            f.colEnd = cfg.colEnd;
            f.life = cfg.life;
            f.risePx = cfg.risePx;
            f.spreadPx = cfg.spreadPx;
            f.rotDeg = cfg.rotDeg;
            f.crit = cfg.crit;
            f.elapsed = 0f;
            f.active = true;

            f.text.text = label;
            f.text.fontSize = fontSize;
            f.text.color = cfg.colStart;
            f.outline.enabled = cfg.crit;   // 크리만 금색 외곽
            f.rect.localScale = Vector3.one;
            f.rect.localRotation = Quaternion.Euler(0f, 0f, cfg.rotDeg);

            f.go.SetActive(true);
            UpdateFloaterTransform(f); // 첫 프레임 위치 즉시 반영
        }

        /// <summary>빈 풀 슬롯 반환. 없으면 가장 오래된 것 재활용.</summary>
        private Floater GetFree()
        {
            Floater oldest = null;
            float max = -1f;
            for (int i = 0; i < _pool.Length; i++)
            {
                var f = _pool[i];
                if (f == null) continue;
                if (!f.active) return f;
                if (f.elapsed > max) { max = f.elapsed; oldest = f; }
            }
            return oldest;
        }

        // ─────────────── 애니메이션 ───────────────

        private void Update()
        {
            for (int i = 0; i < _pool.Length; i++)
            {
                var f = _pool[i];
                if (f == null || !f.active) continue;

                f.elapsed += Time.deltaTime;
                if (f.elapsed >= f.life)
                {
                    f.active = false;
                    f.outline.enabled = false;
                    f.go.SetActive(false);
                    continue;
                }
                UpdateFloaterTransform(f);
            }
        }

        private void UpdateFloaterTransform(Floater f)
        {
            float t = Mathf.Clamp01(f.elapsed / f.life);
            float ease = 1f - (1f - t) * (1f - t);   // easeOutQuad — 상승 감속

            if (!RaidUIFactory.WorldToCanvas(_canvasRect, Camera.main, f.worldStart, out var local))
            {
                // 카메라 뒤 → 숨김
                f.go.SetActive(false);
                return;
            }
            if (!f.go.activeSelf) f.go.SetActive(true);

            float x = f.spreadPx;
            float y = f.risePx * ease;
            float alpha;
            float scale = 1f;

            if (f.crit)
            {
                // 등장 펀치: 1.6 → 1.0, 살짝 오버슈트(중간에 1.0 아래로 딥).
                if (f.elapsed < CritPunchTime)
                {
                    float p = Mathf.Clamp01(f.elapsed / CritPunchTime);
                    float pe = 1f - Mathf.Pow(1f - p, 3f); // easeOutCubic
                    scale = Mathf.Lerp(1.6f, 1.0f, pe) - Mathf.Sin(p * Mathf.PI) * 0.08f;
                }
                // 유지 후 페이드.
                if (f.elapsed <= CritHold) alpha = 1f;
                else alpha = 1f - Mathf.Clamp01((f.elapsed - CritHold) / CritFade);
                // 소멸 구간 미세 흔들림.
                if (f.elapsed > CritHold)
                    x += Mathf.Sin(f.elapsed * 42f) * CritShakePx * (1f - alpha);
            }
            else
            {
                alpha = 1f - t;   // 선형 페이드아웃
            }

            f.rect.anchoredPosition = local + new Vector2(x, y);
            f.rect.localScale = Vector3.one * scale;

            // 색 그라데이션(시작→종료) + 알파.
            var c = Color.Lerp(f.colStart, f.colEnd, ease);
            c.a = alpha;
            f.text.color = c;
            if (f.outline.enabled)
            {
                var oc = CritOutline;
                oc.a = CritOutline.a * alpha;
                f.outline.effectColor = oc;
            }
        }
    }
}
