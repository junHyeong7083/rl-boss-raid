using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 돌진 표식 뷰 (self-contained). BossGameViewer 를 자동 탐색하고 OnSnapshotApplied 를 구독해
    /// snapshot.boss.rush_target(조준 대상 uid)/rush_left(남은 windup 턴)에 따라
    /// 대상 유닛 발밑에 "BossRaid/RushMark" 지면 마커 1개를 재사용해 표시한다.
    ///
    /// - rush_target >= 0 : 대상 위치를 매 프레임 추종(스냅샷 sim 좌표 보간), _Progress 갱신.
    ///     _Progress = 1 - rush_left / (첫 관측 rush_left = 총 windup).
    ///     대상이 플레이어(uid 0)면 마커 색 강조 + 마커 위 "!" 쿼드 펄스. (화면 비네트는 금지)
    /// - rush_target == -1 : 숨김.
    ///
    /// 읽기 전용 계약: viewer.LatestSnapshot / ContinuousToWorld / OnSnapshotApplied / cellSize.
    /// </summary>
    [DisallowMultipleComponent]
    public class RushMarkView : MonoBehaviour
    {
        [Tooltip("비워두면 씬에서 자동 탐색.")]
        [SerializeField] private BossGameViewer viewer;

        [Tooltip("마커 반경 배율(대상 발밑을 감싸는 원, cellSize 배수).")]
        [SerializeField] private float radiusCells = 1.1f;

        [Tooltip("대상 추종 보간 속도(클수록 즉각적).")]
        [SerializeField] private float followLerp = 14f;

        // 표식 HDR 색(일반 / 플레이어 강조).
        private static readonly Color CMark        = new Color(2.2f, 0.24f, 0.16f, 1f);
        private static readonly Color CMarkPlayer  = new Color(3.4f, 0.55f, 0.30f, 1f);   // 더 밝고 하이라이트

        private GameObject _marker;
        private Material _mat;
        private Transform _bang;          // "!" 쿼드(플레이어 조준 시만 활성)

        private int _activeTarget = -1;   // 현재 조준 uid (-1=없음)
        private int _totalWindup = 1;     // 활성화 시 첫 rush_left 를 총값으로 캐시
        private Vector3 _followPos;       // 추종 목표 월드 위치
        private bool _hasPos;

        // ─────────────── 라이프사이클 ───────────────

        private void Awake()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            BuildMarker();
            SetVisible(false);
        }

        private void OnEnable()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            if (viewer != null) viewer.OnSnapshotApplied += OnSnapshot;
        }

        private void OnDisable()
        {
            if (viewer != null) viewer.OnSnapshotApplied -= OnSnapshot;
        }

        // ─────────────── 스냅샷 처리 ───────────────

        private void OnSnapshot(BossSnapshot snap)
        {
            int rt = (snap != null && snap.boss != null) ? snap.boss.rush_target : -1;
            if (rt < 0)
            {
                _activeTarget = -1;
                SetVisible(false);
                return;
            }

            int rl = snap.boss.rush_left;
            if (rt != _activeTarget)
            {
                // 새 조준 개시 → 첫 관측 rush_left 를 총 windup 으로 캐시.
                _activeTarget = rt;
                _totalWindup = Mathf.Max(1, rl);
            }
            else
            {
                // 안전장치: 총값보다 큰 값이 들어오면 총값을 늘려 진행도 음수 방지.
                _totalWindup = Mathf.Max(_totalWindup, rl);
            }

            float progress = 1f - (float)rl / Mathf.Max(1, _totalWindup);
            progress = Mathf.Clamp01(progress);

            bool isPlayer = rt == 0;
            if (_mat != null)
            {
                _mat.SetFloat("_Progress", progress);
                _mat.SetColor("_Color", isPlayer ? CMarkPlayer : CMark);
                _mat.SetFloat("_PulseSpeed", isPlayer ? 4.5f : 3.0f);   // 플레이어 조준 시 더 빠른 펄스
            }

            // 대상 유닛 월드 위치(스냅샷 sim 좌표 → 월드).
            if (TryTargetWorld(snap, rt, out var wp))
            {
                _followPos = wp;
                if (!_hasPos) { PlaceMarkerImmediate(wp); _hasPos = true; }   // 첫 프레임 튐 방지
            }

            SetVisible(true);
            if (_bang != null) _bang.gameObject.SetActive(isPlayer);
        }

        private bool TryTargetWorld(BossSnapshot snap, int uid, out Vector3 world)
        {
            world = Vector3.zero;
            if (viewer == null || snap.units == null) return false;
            foreach (var u in snap.units)
            {
                if (u != null && u.uid == uid)
                {
                    world = viewer.ContinuousToWorld(u.x, u.y);
                    return true;
                }
            }
            return false;
        }

        // ─────────────── 추종/펄스 ───────────────

        private void Update()
        {
            if (_marker == null || !_marker.activeSelf) return;

            // 대상 위치 부드럽게 추종(스냅샷 간 보간).
            if (_hasPos)
            {
                float k = 1f - Mathf.Exp(-followLerp * Time.deltaTime);
                Vector3 cur = _marker.transform.position;
                Vector3 tgt = new Vector3(_followPos.x, MarkerY(), _followPos.z);
                _marker.transform.position = Vector3.Lerp(cur, tgt, k);
            }

            // "!" 쿼드: 마커 위(월드 1.6m)에 배치 + 카메라 빌보드 + 스케일 펄스.
            if (_bang != null && _bang.gameObject.activeSelf)
            {
                _bang.position = _marker.transform.position + Vector3.up * 1.6f;
                var cam = Camera.main;
                if (cam != null)
                    _bang.rotation = Quaternion.LookRotation(_bang.position - cam.transform.position, Vector3.up);
                float s = 1f + 0.25f * Mathf.Sin(Time.unscaledTime * 9f);
                _bang.localScale = Vector3.one * (0.6f * s);
            }
        }

        // ─────────────── 마커 빌드 ───────────────

        private float MarkerY() => (viewer != null ? viewer.gridOrigin.y : 0f) + 0.05f;

        private void PlaceMarkerImmediate(Vector3 wp)
        {
            if (_marker != null)
                _marker.transform.position = new Vector3(wp.x, MarkerY(), wp.z);
        }

        private void SetVisible(bool v)
        {
            if (_marker != null && _marker.activeSelf != v) _marker.SetActive(v);
            // "!" 는 마커와 별도 부모라 함께 숨겨준다(표시 여부는 OnSnapshot 이 플레이어 조준일 때만 켬).
            if (!v && _bang != null && _bang.gameObject.activeSelf) _bang.gameObject.SetActive(false);
            if (!v) _hasPos = false;
        }

        private void BuildMarker()
        {
            float cell = viewer != null ? viewer.cellSize : 1f;
            float diam = 2f * radiusCells * cell;

            _marker = new GameObject("RushMark");
            _marker.transform.SetParent(transform, false);
            _marker.transform.localScale = new Vector3(diam, diam, diam);

            var mf = _marker.AddComponent<MeshFilter>();
            var mr = _marker.AddComponent<MeshRenderer>();
            mf.sharedMesh = GroundQuad();

            var sh = Shader.Find("BossRaid/RushMark");
            if (sh == null) sh = Shader.Find("Sprites/Default");   // 폴백
            _mat = new Material(sh) { name = "RushMark_Mat" };
            if (sh != null && sh.name == "BossRaid/RushMark")
            {
                _mat.SetColor("_Color", CMark);
                _mat.SetFloat("_Progress", 0f);
                _mat.SetFloat("_SpinSpeed", 1.4f);
                _mat.SetFloat("_PulseSpeed", 3.0f);
            }
            else _mat.color = CMark;
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            BuildBang();
        }

        /// <summary>대상이 플레이어일 때 마커 위에 뜨는 "!" 쿼드(3D 텍스트). 화면 비네트 대체.
        /// 스케일 상속을 피하려고 스케일된 마커의 자식이 아니라 (비스케일) 컴포넌트 GO 자식으로 두고
        /// 월드 위치는 Update 에서 마커 위로 매 프레임 배치한다.</summary>
        private void BuildBang()
        {
            var go = new GameObject("RushMark_Bang");
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * 0.6f;

            var tm = go.AddComponent<TextMesh>();
            // 코드 생성 TextMesh 는 폰트/머티리얼을 명시해야 렌더됨(빌트인 폰트 사용).
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null) tm.font = font;
            tm.text = "!";
            tm.characterSize = 0.5f;
            tm.fontSize = 96;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontStyle = FontStyle.Bold;
            tm.color = new Color(1f, 0.85f, 0.3f, 1f);
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (font != null) mr.sharedMaterial = font.material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            _bang = go.transform;
            go.SetActive(false);
        }

        // 바닥(XZ)에 눕힌 1x1 Quad 메시(재사용 캐시).
        private static Mesh _quad;
        private static Mesh GroundQuad()
        {
            if (_quad != null) return _quad;
            var mesh = new Mesh { name = "RushMark_GroundQuad" };
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
            _quad = mesh;
            return mesh;
        }
    }
}
