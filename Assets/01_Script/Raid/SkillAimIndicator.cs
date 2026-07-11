using System.Collections;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

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
        [Tooltip("확정 마커 베이스 컬러(청록 — 발사 순간 즉각 피드백).")]
        [SerializeField] private Color confirmColor = new Color(0.25f, 1f, 0.92f, 0.65f);
        [Tooltip("확정 마커 HDR 테두리 컬러.")]
        [SerializeField] private Color confirmOutline = new Color(0.5f, 2.8f, 2.6f, 1f);
        [Tooltip("확정 마커 표시 시간(초). 0.4~0.6 권장.")]
        [SerializeField] private float confirmDuration = 0.5f;

        private Transform _ring;       // 사거리 링 (딜러 중심)
        private Transform _reticle;    // AoE 레티클 (조준 지점)
        private float _rangeWorld;     // 사거리(월드 단위)
        private float _aoeWorld;       // AoE 실반경(월드 단위) — 확정 마커 크기 기준
        private bool _active;

        // ── 확정 마커(발사 순간 수축 애니메이션) 상태 ──
        private Transform _confirm;        // 확정 마커 Quad (조준과 독립적으로 잔존·애니메이션)
        private Material _confirmMat;      // 확정 마커 인스턴스 머티리얼(알파 애니메이션용)
        private Coroutine _confirmRoutine; // 진행 중인 수축 애니메이션

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

            // 확정 마커: 청록 채움 원(두꺼운 테두리). 조준 종료 후에도 독립적으로 잔존하며 수축.
            _confirm = BuildCircleQuad("ConfirmMarker", confirmColor, confirmOutline,
                fill: 1f, outlineWidth: 0.14f, unfilledAlpha: 0.55f);
            var cmr = _confirm.GetComponent<MeshRenderer>();
            if (cmr != null) _confirmMat = cmr.sharedMaterial;   // BuildCircleQuad가 GO별 고유 머티리얼 할당
            _confirm.gameObject.SetActive(false);

            Hide();
        }

        // ─────────────── 공개 API ───────────────

        /// <summary>조준 모드 진입: 사거리/AoE 반경(월드 단위) 설정 + 표시 시작.</summary>
        public void Show(float rangeWorld, float aoeRadiusWorld)
        {
            _rangeWorld = Mathf.Max(0.01f, rangeWorld);
            _aoeWorld = Mathf.Max(0.01f, aoeRadiusWorld);
            float ringD = _rangeWorld * 2f;
            float aoeD = _aoeWorld * 2f;
            _ring.localScale = new Vector3(ringD, ringD, ringD);
            _reticle.localScale = new Vector3(aoeD, aoeD, aoeD);
            _active = true;
            _ring.gameObject.SetActive(true);
            _reticle.gameObject.SetActive(true);
        }

        /// <summary>
        /// 조준 종료(발사/취소): 즉시 숨김.
        /// 좌클릭이 눌린 프레임에 활성 상태로 종료되면 "발사"로 간주해 확정 마커를 띄운다
        /// (RaidPlayerController.FireAimedSkill → CancelAiming → Hide 경로. Esc/같은 키 취소는 클릭이 없어 제외).
        /// 서버의 player_skill_cast 이벤트가 오기 전 즉각 피드백.
        /// </summary>
        public void Hide()
        {
            if (_active && ReadLeftMouseDown())
                ConfirmAt(ClampedAimPoint, _aoeWorld);

            _active = false;
            if (_ring != null) _ring.gameObject.SetActive(false);
            if (_reticle != null) _reticle.gameObject.SetActive(false);
        }

        /// <summary>
        /// 확정 마커 표시(공개 API): 조준 지점에 스킬 실반경 크기의 청록 원을
        /// confirmDuration 동안 수축·페이드로 그려 "여기서 발동" 을 즉시 읽히게 한다.
        /// </summary>
        public void ConfirmAt(Vector3 worldPoint, float radiusWorld)
        {
            if (_confirm == null) return;
            float r = radiusWorld > 0f ? radiusWorld : _aoeWorld;

            _confirm.position = new Vector3(worldPoint.x, groundLift + 0.01f, worldPoint.z);
            _confirm.gameObject.SetActive(true);

            if (_confirmRoutine != null) StopCoroutine(_confirmRoutine);
            _confirmRoutine = StartCoroutine(ConfirmAnim(Mathf.Max(0.01f, r)));
        }

        /// <summary>확정 마커 수축 애니메이션: 실반경 → 0.55배로 수축하며 알파 페이드아웃.</summary>
        private IEnumerator ConfirmAnim(float radiusWorld)
        {
            float dur = Mathf.Clamp(confirmDuration, 0.2f, 1.0f);
            float startD = radiusWorld * 2f;          // 시작: 실반경 전체
            float endD = radiusWorld * 2f * 0.55f;   // 끝: 안쪽으로 수축(지점 강조)
            float t = 0f;
            while (t < dur)
            {
                float k = t / dur;
                float ease = 1f - (1f - k) * (1f - k);       // ease-out
                float d = Mathf.Lerp(startD, endD, ease);
                _confirm.localScale = new Vector3(d, d, d);

                float a = 1f - k;                             // 선형 페이드아웃
                if (_confirmMat != null)
                {
                    Color b = confirmColor; b.a *= a;
                    Color o = confirmOutline; o.a *= a;
                    _confirmMat.SetColor("_Color", b);
                    _confirmMat.SetColor("_OutlineColor", o);
                }
                t += Time.deltaTime;
                yield return null;
            }
            _confirm.gameObject.SetActive(false);
            _confirmRoutine = null;
        }

        // 신규/레거시 입력 분기: 좌클릭이 이 프레임에 눌렸는가.
        private static bool ReadLeftMouseDown()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var m = Mouse.current;
            return m != null && m.leftButton.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(0);
#else
            return false;
#endif
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
