using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 로스트아크식 우클릭 이동 마커. 클릭 지점 지면에 초록/청록 링을 표시하고
    /// 0.5초 스케일 축소 후 페이드아웃한다. 도달(HideImmediate) 시 즉시 사라진다.
    ///
    /// 프리팹 불요 — Awake 에서 바닥 Quad 메시(콜라이더 없음)를 코드 생성하고
    /// 기존 BossRaid/Telegraph 셰이더(circle, _Fill=0 → 링 룩)를 재활용한다.
    /// </summary>
    public class MoveMarker : MonoBehaviour
    {
        [Header("Look")]
        [Tooltip("링 지름(월드 단위).")]
        [SerializeField] private float markerSize = 1.6f;
        [Tooltip("지면 위로 살짝 띄우는 높이(Z-fighting 방지).")]
        [SerializeField] private float groundLift = 0.03f;
        [Tooltip("청록 베이스 컬러.")]
        [SerializeField] private Color baseColor = new Color(0.15f, 1f, 0.6f, 0.55f);
        [Tooltip("HDR 링 아웃라인 컬러(초록).")]
        [SerializeField] private Color outlineColor = new Color(0.35f, 2.6f, 1.3f, 1f);

        [Header("Animation")]
        [Tooltip("스케일 축소 구간(초).")]
        [SerializeField] private float shrinkDuration = 0.5f;
        [Tooltip("축소 후 페이드 구간(초).")]
        [SerializeField] private float fadeDuration = 0.35f;
        [SerializeField] private float startScaleMul = 1.35f;
        [SerializeField] private float endScaleMul = 0.7f;

        private Renderer _renderer;
        private MaterialPropertyBlock _mpb;
        private Transform _tr;
        private bool _playing;
        private float _elapsed;

        private void Awake()
        {
            _tr = transform;
            BuildQuad();
            HideImmediate();
        }

        // ─────────────── 공개 API ───────────────

        /// <summary>클릭 지점에 마커를 놓고 축소+페이드 애니메이션을 재생.</summary>
        public void Show(Vector3 worldPoint)
        {
            MoveTo(worldPoint);
            _elapsed = 0f;
            _playing = true;
            gameObject.SetActive(true);
            ApplyVisual(startScaleMul, 1f);
        }

        /// <summary>애니메이션 재생 없이 마커 위치만 갱신(드래그 중).</summary>
        public void MoveTo(Vector3 worldPoint)
        {
            worldPoint.y = worldPoint.y + groundLift;
            if (_tr == null) _tr = transform;
            _tr.position = worldPoint;
        }

        /// <summary>도달/전투 종료 시 즉시 숨김.</summary>
        public void HideImmediate()
        {
            _playing = false;
            gameObject.SetActive(false);
        }

        // ─────────────── 애니메이션 ───────────────

        private void Update()
        {
            if (!_playing) return;
            _elapsed += Time.deltaTime;

            float total = shrinkDuration + fadeDuration;
            if (_elapsed >= total) { HideImmediate(); return; }

            if (_elapsed <= shrinkDuration)
            {
                // 축소 구간: 큰 링 → 작은 링, 알파 유지.
                float t = shrinkDuration > 0f ? _elapsed / shrinkDuration : 1f;
                float scaleMul = Mathf.Lerp(startScaleMul, endScaleMul, t);
                ApplyVisual(scaleMul, 1f);
            }
            else
            {
                // 페이드 구간: 스케일 고정, 알파 → 0.
                float t = fadeDuration > 0f ? (_elapsed - shrinkDuration) / fadeDuration : 1f;
                ApplyVisual(endScaleMul, Mathf.Lerp(1f, 0f, t));
            }
        }

        private void ApplyVisual(float scaleMul, float alpha)
        {
            if (_tr == null) _tr = transform;
            float s = markerSize * scaleMul;
            _tr.localScale = new Vector3(s, s, s);

            if (_renderer == null) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_mpb);
            var c = baseColor; c.a = baseColor.a * alpha;
            var o = outlineColor; o.a = outlineColor.a * alpha;
            _mpb.SetColor("_Color", c);
            _mpb.SetColor("_OutlineColor", o);
            _renderer.SetPropertyBlock(_mpb);
        }

        // ─────────────── 메시/머티리얼 생성 ───────────────

        /// <summary>바닥에 눕힌 1x1 Quad(노멀 +Y). 셰이더가 Cull Off 라 와인딩 무관.</summary>
        private void BuildQuad()
        {
            var mf = gameObject.GetComponent<MeshFilter>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
            var mr = gameObject.GetComponent<MeshRenderer>();
            if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();

            var mesh = new Mesh { name = "MoveMarkerQuad" };
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
                mat.SetInt("_ShapeType", 0);   // circle
                mat.SetFloat("_Fill", 0f);      // 내부 미충전 → 링(테두리)만 강조
                mat.SetFloat("_Progress", 1f);
                mat.SetFloat("_Pulse", 0f);
                mat.SetFloat("_OutlineWidth", 0.14f);
                mat.SetFloat("_UnfilledAlpha", 0.12f);
                mat.SetColor("_Color", baseColor);
                mat.SetColor("_OutlineColor", outlineColor);
            }
            else
            {
                mat.color = baseColor;
            }
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _renderer = mr;
        }
    }
}
