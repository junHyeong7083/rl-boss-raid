using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 로아식 스킬 조준 표시기(설치기 조준 모드용). 프리팹 불요 — 코드 생성.
    ///   - 사거리 링: 딜러 중심, 반경 = 스킬 사거리. 얇은 금색 반투명 링(딜러 따라다님).
    ///   - AoE 레티클: 마우스 지면 포인트에 스킬 AoE 반경 원(청록 톤 — 적 붉은 장판과 구분).
    ///     사거리 밖이면 사거리 경계로 클램프된 위치에 표시.
    ///
    /// 사용: 컨트롤러가 Show(range, aoe) → 매 프레임 UpdateAim(딜러 표시위치, 마우스 지면점)
    ///       → 발사 시 ClampedAimPoint 사용 → Hide().
    /// 렌더는 기존 BossRaid/Telegraph 셰이더(circle) 재활용: 링은 _Fill=0(테두리만),
    /// 레티클은 _Fill=1(채움). 셰이더 미포함 빌드면 Sprites/Default 폴백.
    /// </summary>
    public class SkillAimIndicator : MonoBehaviour
    {
        [Header("Look")]
        [Tooltip("지면 위로 살짝 띄우는 높이(Z-fighting 방지).")]
        [SerializeField] private float groundLift = 0.04f;
        [Tooltip("사거리 링 베이스 컬러(금색 반투명).")]
        [SerializeField] private Color ringColor = new Color(1f, 0.85f, 0.4f, 0.35f);
        [Tooltip("사거리 링 HDR 테두리 컬러.")]
        [SerializeField] private Color ringOutline = new Color(2.2f, 1.7f, 0.6f, 0.9f);
        [Tooltip("AoE 레티클 베이스 컬러(청록 반투명).")]
        [SerializeField] private Color reticleColor = new Color(0.2f, 0.95f, 0.9f, 0.4f);
        [Tooltip("AoE 레티클 HDR 테두리 컬러.")]
        [SerializeField] private Color reticleOutline = new Color(0.5f, 2.4f, 2.2f, 1f);

        private Transform _ring;       // 사거리 링 (딜러 중심)
        private Transform _reticle;    // AoE 레티클 (조준 지점)
        private float _rangeWorld;     // 사거리(월드 단위)
        private bool _active;

        /// <summary>사거리로 클램프된 최종 조준 지점(월드). 발사 좌표로 사용.</summary>
        public Vector3 ClampedAimPoint { get; private set; }

        /// <summary>조준 표시 활성 여부.</summary>
        public bool IsActive => _active;

        private void Awake()
        {
            _ring = BuildCircleQuad("RangeRing", ringColor, ringOutline,
                fill: 0f, outlineWidth: 0.02f, unfilledAlpha: 0.06f);
            _reticle = BuildCircleQuad("AoeReticle", reticleColor, reticleOutline,
                fill: 1f, outlineWidth: 0.08f, unfilledAlpha: 0.4f);
            Hide();
        }

        // ─────────────── 공개 API ───────────────

        /// <summary>조준 모드 진입: 사거리/AoE 반경(월드 단위) 설정 + 표시 시작.</summary>
        public void Show(float rangeWorld, float aoeRadiusWorld)
        {
            _rangeWorld = Mathf.Max(0.01f, rangeWorld);
            float ringD = _rangeWorld * 2f;
            float aoeD = Mathf.Max(0.01f, aoeRadiusWorld) * 2f;
            _ring.localScale = new Vector3(ringD, ringD, ringD);
            _reticle.localScale = new Vector3(aoeD, aoeD, aoeD);
            _active = true;
            _ring.gameObject.SetActive(true);
            _reticle.gameObject.SetActive(true);
        }

        /// <summary>조준 종료(발사/취소): 즉시 숨김.</summary>
        public void Hide()
        {
            _active = false;
            if (_ring != null) _ring.gameObject.SetActive(false);
            if (_reticle != null) _reticle.gameObject.SetActive(false);
        }

        /// <summary>
        /// 매 프레임 조준 갱신(무빙 조준). dealerPos = 딜러 표시 위치, aimPoint = 마우스 지면 포인트.
        /// 사거리 밖 조준은 사거리 경계로 클램프.
        /// </summary>
        public void UpdateAim(Vector3 dealerPos, Vector3 aimPoint)
        {
            if (!_active) return;

            _ring.position = new Vector3(dealerPos.x, groundLift, dealerPos.z);

            Vector3 d = aimPoint - dealerPos;
            d.y = 0f;
            if (d.magnitude > _rangeWorld) d = d.normalized * _rangeWorld;

            var p = new Vector3(dealerPos.x + d.x, groundLift, dealerPos.z + d.z);
            _reticle.position = p;
            ClampedAimPoint = new Vector3(p.x, 0f, p.z);   // 발사 좌표는 지면(y=0) 기준
        }

        // ─────────────── 메시/머티리얼 생성 ───────────────

        /// <summary>바닥에 눕힌 1x1 원형 Quad 자식 생성(Telegraph 셰이더 circle).</summary>
        private Transform BuildCircleQuad(string name, Color baseCol, Color outlineCol,
            float fill, float outlineWidth, float unfilledAlpha)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            var mesh = new Mesh { name = name + "Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3( 0.5f, 0f, -0.5f),
                new Vector3( 0.5f, 0f,  0.5f),
                new Vector3(-0.5f, 0f,  0.5f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;

            var sh = Shader.Find("BossRaid/Telegraph");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var mat = new Material(sh);
            if (sh != null && sh.name == "BossRaid/Telegraph")
            {
                mat.SetInt("_ShapeType", 0);          // circle
                mat.SetFloat("_Fill", fill);           // 0=링(테두리만), 1=채움
                mat.SetFloat("_Progress", 0f);         // 은은한 펄스 유지
                mat.SetFloat("_Pulse", 0f);
                mat.SetFloat("_OutlineWidth", outlineWidth);
                mat.SetFloat("_UnfilledAlpha", unfilledAlpha);
                mat.SetColor("_Color", baseCol);
                mat.SetColor("_OutlineColor", outlineCol);
            }
            else
            {
                mat.color = baseCol;
            }
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }
    }
}
