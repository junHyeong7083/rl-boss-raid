using System.Collections.Generic;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// "어디로 가야 하는가" 안전 유도 마커.
    /// 매 스냅샷마다 텔레그래프 위험 도형(shapes)을 수집하고, 딜러(플레이어)가
    /// 위험 안에 있으면 가장 가까운 안전 지점을 찾아 SafeGuide 쉐이더 마커로 표시한다.
    /// 딜러→지점이 멀면 얇은 초록 방향선도 그린다.
    ///
    /// self-contained: 씬 배치 불요. RaidHUD 가 CreateChild 하거나, 스스로 viewer 를 탐색해
    /// viewer.OnSnapshotApplied 를 구독한다. 마커/방향선은 1회 생성 후 재사용한다.
    ///
    /// 위험 판정 기하는 RL_Game_NPC/src/raid/shapes.py 를 미러링:
    ///   circle: dist ≤ r / donut: r_in ≤ dist ≤ r_out /
    ///   fan: dist ≤ r ∧ |angleDiff| ≤ width/2 / line: 점-선분거리 ≤ hw
    /// </summary>
    public class SafeGuideMarker : MonoBehaviour
    {
        [Header("Refs (비워두면 자동 탐색)")]
        public BossGameViewer viewer;

        [Header("샘플링")]
        [Tooltip("극좌표 샘플 각도 수")]
        public int angleSamples = 16;
        [Tooltip("샘플 반경 최소~최대(sim 단위)")]
        public float minRadius = 1.0f;
        public float maxRadius = 8.0f;
        public float radiusStep = 0.5f;
        [Tooltip("아레나 경계(sim). 이 밖은 안전점으로 채택하지 않음")]
        public float mapWidth = 20f;
        public float mapHeight = 20f;
        public float boundMargin = 1.0f;

        [Header("표시")]
        [Tooltip("마커 반경(sim). 실제 스케일 = 반경×2×cellSize")]
        public float markerRadiusSim = 1.2f;
        [Tooltip("이 거리(sim) 이상 떨어져 있을 때만 방향선 표시")]
        public float lineShowDist = 1.5f;
        [Tooltip("마커 깜빡임 방지 히스테리시스(초)")]
        public float hysteresis = 0.2f;

        [Header("색")]
        public Color guideColor = new Color(0.25f, 1.6f, 0.6f, 0.9f);   // HDR 초록(발광)
        public Color lineColor = new Color(0.35f, 1.0f, 0.5f, 0.85f);

        private GameObject _marker;
        private LineRenderer _line;
        private bool _subscribed;

        // 히스테리시스 상태
        private bool _visible;              // 실제 마커 표시 여부
        private bool _desired;              // 이번 스냅샷이 원하는 표시 여부
        private Vector3 _targetWorld;       // 안전 지점(월드)
        private Vector3 _dealerWorld;       // 딜러 위치(월드)
        private float _sinceDesireChange;

        private void Start()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            BuildMarker();
            BuildLine();
            TrySubscribe();
        }

        private void OnEnable() { TrySubscribe(); }

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
            if (viewer.LatestSnapshot != null) HandleSnapshot(viewer.LatestSnapshot);
        }

        // ─────────────── 마커/방향선 생성(1회) ───────────────

        private void BuildMarker()
        {
            _marker = new GameObject("SafeGuideMarker_Quad");
            _marker.transform.SetParent(transform, false);

            var mf = _marker.AddComponent<MeshFilter>();
            var mr = _marker.AddComponent<MeshRenderer>();

            var mesh = new Mesh { name = "SafeGuideQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3( 0.5f, 0f,  0.5f), new Vector3(-0.5f, 0f,  0.5f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;

            var sh = Shader.Find("BossRaid/SafeGuide");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var mat = new Material(sh);
            if (sh != null && sh.name == "BossRaid/SafeGuide")
                mat.SetColor("_Color", guideColor);
            else
                mat.color = guideColor;
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            _marker.SetActive(false);
        }

        private void BuildLine()
        {
            var go = new GameObject("SafeGuideLine");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.numCapVertices = 2;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;

            float w = 0.15f * (viewer != null ? viewer.cellSize : 1f);
            _line.startWidth = w;
            _line.endWidth = w;

            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            var mat = new Material(sh);
            mat.color = lineColor;
            _line.material = mat;
            _line.startColor = lineColor;
            _line.endColor = new Color(lineColor.r, lineColor.g, lineColor.b, 0.15f);
            go.SetActive(false);
        }

        // ─────────────── 스냅샷 → 안전점 계산 ───────────────

        private void HandleSnapshot(BossSnapshot snap)
        {
            if (viewer == null || snap == null) { _desired = false; return; }

            // 위험 도형 수집
            var shapes = new List<ShapeData>();
            if (snap.telegraphs != null)
            {
                foreach (var tg in snap.telegraphs)
                {
                    if (tg == null || tg.shapes == null) continue;
                    foreach (var s in tg.shapes)
                        if (s != null) shapes.Add(s);
                }
            }

            // 딜러(uid 0, 폴백 role==Dealer) 위치
            if (!TryGetDealer(snap, out float dx, out float dy)) { _desired = false; return; }

            // 위험이 없거나 딜러가 이미 안전 → 숨김
            if (shapes.Count == 0 || !AnyContains(shapes, dx, dy)) { _desired = false; return; }

            // 가장 가까운 안전 지점 탐색
            if (TryFindSafePoint(shapes, dx, dy, out float sx, out float sy))
            {
                _targetWorld = viewer.ContinuousToWorld(sx, sy) + Vector3.up * 0.04f;
                _dealerWorld = viewer.ContinuousToWorld(dx, dy) + Vector3.up * 0.04f;
                _desired = true;
            }
            else
            {
                _desired = false;
            }
        }

        private bool TryGetDealer(BossSnapshot snap, out float x, out float y)
        {
            x = 0f; y = 0f;
            if (snap.units == null) return false;
            UnitData dealer = null;
            foreach (var u in snap.units)
            {
                if (u == null || !u.alive) continue;
                if (u.uid == 0) { dealer = u; break; }
                if (dealer == null && u.role == (int)PartyRole.Dealer) dealer = u;
            }
            if (dealer == null) return false;
            x = dealer.x; y = dealer.y;
            return true;
        }

        /// <summary>도넛이면 중심 안쪽(r_in) 우선, 그 외에는 극좌표 샘플링으로 최근접 안전점.</summary>
        private bool TryFindSafePoint(List<ShapeData> shapes, float dx, float dy,
                                      out float bx, out float by)
        {
            bx = dx; by = dy;
            float best = float.MaxValue;
            bool found = false;

            // (1) 딜러를 품은 도넛의 내부 구멍(중심 방향 r_in 안쪽) 후보
            foreach (var s in shapes)
            {
                if (s.kind != "donut") continue;
                if (!PointInDonut(dx, dy, s.cx, s.cy, s.r_in, s.r_out)) continue;
                float vx = dx - s.cx, vy = dy - s.cy;
                float d = Mathf.Sqrt(vx * vx + vy * vy);
                float inner = Mathf.Max(0f, s.r_in * 0.6f);
                float cx2, cy2;
                if (d < 1e-4f) { cx2 = s.cx; cy2 = s.cy; }
                else { cx2 = s.cx + vx / d * inner; cy2 = s.cy + vy / d * inner; }
                if (!AnyContains(shapes, cx2, cy2) && InBounds(cx2, cy2))
                {
                    float dist = Dist(dx, dy, cx2, cy2);
                    if (dist < best) { best = dist; bx = cx2; by = cy2; found = true; }
                }
            }

            // (2) 극좌표 샘플링(각도 angleSamples × 반경 minRadius..maxRadius)
            for (float rad = minRadius; rad <= maxRadius + 1e-4f; rad += radiusStep)
            {
                for (int i = 0; i < angleSamples; i++)
                {
                    float th = i * (2f * Mathf.PI / angleSamples);
                    float cx2 = dx + Mathf.Cos(th) * rad;
                    float cy2 = dy + Mathf.Sin(th) * rad;
                    if (AnyContains(shapes, cx2, cy2) || !InBounds(cx2, cy2)) continue;
                    if (rad < best) { best = rad; bx = cx2; by = cy2; found = true; }
                }
                // 이 반경에서 안전점을 찾았으면 더 먼 반경은 볼 필요 없음(최근접)
                if (found && best <= rad + 1e-4f) break;
            }

            return found;
        }

        // ─────────────── shapes.py 미러 기하 판정 ───────────────

        private static bool AnyContains(List<ShapeData> shapes, float px, float py)
        {
            for (int i = 0; i < shapes.Count; i++)
                if (ShapeContains(shapes[i], px, py)) return true;
            return false;
        }

        private static bool ShapeContains(ShapeData s, float px, float py)
        {
            switch (s.kind)
            {
                case "circle": return Dist(px, py, s.cx, s.cy) <= s.r;
                case "donut":  return PointInDonut(px, py, s.cx, s.cy, s.r_in, s.r_out);
                case "fan":    return PointInFan(px, py, s.cx, s.cy, s.angle, s.width, s.r);
                case "line":   return PointInSegment(px, py, s.ax, s.ay, s.bx, s.by, s.hw);
                default:       return false;
            }
        }

        private static bool PointInDonut(float px, float py, float cx, float cy, float rIn, float rOut)
        {
            float d = Dist(px, py, cx, cy);
            return d >= rIn && d <= rOut;
        }

        private static bool PointInFan(float px, float py, float cx, float cy,
                                       float angle, float width, float r)
        {
            float dx = px - cx, dy = py - cy;
            float d2 = dx * dx + dy * dy;
            if (d2 > r * r) return false;
            if (d2 < 1e-8f) return true;
            float ang = Mathf.Atan2(dy, dx);
            float diff = Mathf.Abs(Mathf.DeltaAngle(ang * Mathf.Rad2Deg, angle * Mathf.Rad2Deg) * Mathf.Deg2Rad);
            return diff <= width * 0.5f;
        }

        private static bool PointInSegment(float px, float py, float ax, float ay,
                                           float bx, float by, float hw)
        {
            float dx = bx - ax, dy = by - ay;
            float len2 = dx * dx + dy * dy;
            float t = len2 < 1e-8f ? 0f : ((px - ax) * dx + (py - ay) * dy) / len2;
            t = Mathf.Clamp01(t);
            float qx = ax + t * dx, qy = ay + t * dy;
            return Dist(px, py, qx, qy) <= hw;
        }

        private static float Dist(float ax, float ay, float bx, float by)
        {
            float dx = ax - bx, dy = ay - by;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private bool InBounds(float x, float y)
        {
            return x >= boundMargin && x <= mapWidth - boundMargin
                && y >= boundMargin && y <= mapHeight - boundMargin;
        }

        // ─────────────── 히스테리시스 + 렌더 ───────────────

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            // 원하는 표시 상태가 현재와 다르면 hysteresis 시간 경과 후 전환
            if (_desired != _visible)
            {
                _sinceDesireChange += dt;
                if (_sinceDesireChange >= hysteresis)
                {
                    _visible = _desired;
                    _sinceDesireChange = 0f;
                }
            }
            else _sinceDesireChange = 0f;

            if (!_visible)
            {
                if (_marker != null && _marker.activeSelf) _marker.SetActive(false);
                if (_line != null && _line.gameObject.activeSelf) _line.gameObject.SetActive(false);
                return;
            }

            // 마커 배치
            if (_marker != null)
            {
                if (!_marker.activeSelf) _marker.SetActive(true);
                float cell = viewer != null ? viewer.cellSize : 1f;
                float d = markerRadiusSim * 2f * cell;
                _marker.transform.position = _targetWorld;
                _marker.transform.rotation = Quaternion.identity;   // XZ 바닥 평면 메시
                _marker.transform.localScale = new Vector3(d, d, d);
            }

            // 방향선: 딜러→지점이 멀 때만
            if (_line != null)
            {
                float distWorld = Vector3.Distance(_dealerWorld, _targetWorld);
                float thresh = lineShowDist * (viewer != null ? viewer.cellSize : 1f);
                if (distWorld > thresh)
                {
                    if (!_line.gameObject.activeSelf) _line.gameObject.SetActive(true);
                    _line.SetPosition(0, _dealerWorld);
                    _line.SetPosition(1, _targetWorld);
                }
                else if (_line.gameObject.activeSelf)
                {
                    _line.gameObject.SetActive(false);
                }
            }
        }
    }
}
