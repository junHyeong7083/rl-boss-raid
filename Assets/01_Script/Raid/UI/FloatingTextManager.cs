using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 플로팅 데미지/힐 숫자.
    /// snapshot.events 에서 damage(주황)/damage_taken(빨강)/heal(초록) 이벤트를 읽어
    /// 대상 유닛 월드 위치 위 1.8m 에서 떠오르며 페이드(1초, 위로 1m).
    /// 월드→스크린 변환하여 Overlay Canvas 에 표시. 오브젝트 풀 20개.
    /// RaidHUD 가 Build/OnSnapshot 을 호출한다.
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        private const int PoolSize = 20;
        private const float LifeTime = 1.0f;    // 지속(초)
        private const float RiseWorld = 1.0f;   // 위로 이동(m)
        private const float SpawnHeight = 1.8f; // 유닛 위 시작 높이(m)

        private static readonly Color ColDamage      = new Color(1.00f, 0.55f, 0.10f, 1f); // 주황(보스 피해)
        private static readonly Color ColDamageTaken = new Color(0.95f, 0.20f, 0.18f, 1f); // 빨강(아군 피격)
        private static readonly Color ColHeal        = new Color(0.35f, 0.95f, 0.45f, 1f); // 초록(힐)

        private RectTransform _canvasRect;
        private BossGameViewer _viewer;
        private Font _font;

        private class Floater
        {
            public GameObject go;
            public RectTransform rect;
            public Text text;
            public Vector3 worldStart;   // 시작 월드 위치(유닛 위)
            public Color baseColor;
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
                var t = RaidUIFactory.NewText($"Float{i}", root, _font, 26, Color.white, TextAnchor.MiddleCenter);
                RaidUIFactory.Place(t.rectTransform,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(160f, 40f));
                t.fontStyle = FontStyle.Bold;
                t.gameObject.SetActive(false);
                _pool[i] = new Floater { go = t.gameObject, rect = t.rectTransform, text = t };
            }
        }

        // ─────────────── 이벤트 → 스폰 ───────────────

        public void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null || snap.events == null) return;

            foreach (var ev in snap.events)
            {
                if (ev == null || string.IsNullOrEmpty(ev.type)) continue;

                Color col;
                switch (ev.type)
                {
                    case "damage":        col = ColDamage; break;
                    case "damage_taken":  col = ColDamageTaken; break;
                    case "heal":          col = ColHeal; break;
                    default: continue;
                }

                if (!TryResolveWorld(ev, snap, out var world)) continue;

                string label = (ev.type == "heal" ? "+" : "") + Mathf.Abs(ev.amount).ToString();
                Spawn(world + Vector3.up * SpawnHeight, label, col);
            }
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

        private void Spawn(Vector3 world, string label, Color color)
        {
            var f = GetFree();
            if (f == null) return;
            f.active = true;
            f.elapsed = 0f;
            f.worldStart = world;
            f.baseColor = color;
            f.text.text = label;
            f.text.color = color;
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
                if (f.elapsed >= LifeTime)
                {
                    f.active = false;
                    f.go.SetActive(false);
                    continue;
                }
                UpdateFloaterTransform(f);
            }
        }

        private void UpdateFloaterTransform(Floater f)
        {
            float t = Mathf.Clamp01(f.elapsed / LifeTime);
            Vector3 world = f.worldStart + Vector3.up * (RiseWorld * t);

            if (!RaidUIFactory.WorldToCanvas(_canvasRect, Camera.main, world, out var local))
            {
                // 카메라 뒤 → 숨김 상태로 위치만 유지
                f.go.SetActive(false);
                return;
            }
            if (!f.go.activeSelf) f.go.SetActive(true);
            f.rect.anchoredPosition = local;

            var c = f.baseColor;
            c.a = 1f - t;   // 선형 페이드아웃
            f.text.color = c;
        }
    }
}
