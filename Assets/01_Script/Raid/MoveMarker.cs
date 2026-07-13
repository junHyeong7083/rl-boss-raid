using System.Collections.Generic;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 로스트아크식 우클릭 이동 마커. 클릭 지점 지면에 청록 링을 표시하고, 도착/취소/새 지점
    /// 클릭 시 모두 "페이드아웃"(알파 서서히 0 + 살짝 스케일 축소)으로 사라진다.
    ///
    /// 재사용 풀(2개+): 페이드 중 새 지점을 클릭하면 기존 마커의 페이드는 그대로 두고 새 마커를
    /// 즉시 표시한다(두 마커가 잠깐 공존). MaterialPropertyBlock 으로 알파를 애니메이션한다
    /// (셰이더 추가 없음 — 기존 BossRaid/Telegraph circle 재활용).
    ///
    /// 프리팹 불요 — 링 Quad(콜라이더 없음)를 코드 생성한다.
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
        [Tooltip("등장 정착(스케일 안정) 시간(초).")]
        [SerializeField] private float introDuration = 0.15f;
        [Tooltip("페이드아웃 시간(초) — 도착/취소/새 지점 클릭 시. 0.25~0.35 권장.")]
        [SerializeField] private float fadeDuration = 0.3f;
        [Tooltip("등장 시 시작 스케일 배수(→ 1 로 정착).")]
        [SerializeField] private float startScaleMul = 1.35f;
        [Tooltip("페이드아웃 종료 스케일 배수(살짝 축소).")]
        [SerializeField] private float endScaleMul = 0.8f;

        private Transform _tr;
        private readonly List<Ping> _pings = new List<Ping>();
        private Ping _active;   // 현재 조작 대상(드래그 이동 / 페이드 대상)

        private void Awake() { _tr = transform; }

        // ─────────────── 공개 API ───────────────

        /// <summary>클릭 지점에 새 마커를 놓는다. 기존 활성 마커는 페이드아웃으로 보낸다.</summary>
        public void Show(Vector3 worldPoint)
        {
            FadeOut();                       // 기존 활성 마커 → 페이드아웃(연출 유지)
            worldPoint.y += groundLift;
            _active = Acquire();
            _active.Begin(worldPoint);
        }

        /// <summary>드래그 중 활성 마커 위치만 갱신(애니메이션 재생 없음).</summary>
        public void MoveTo(Vector3 worldPoint)
        {
            if (_active == null) return;
            worldPoint.y += groundLift;
            _active.SetPosition(worldPoint);
        }

        /// <summary>도착/취소/정지 시 활성 마커를 페이드아웃(즉시 사라짐 아님).</summary>
        public void FadeOut()
        {
            if (_active != null) { _active.BeginFade(); _active = null; }
        }

        /// <summary>전투 종료/워프 등 하드 리셋 — 모든 마커 즉시 숨김.</summary>
        public void HideImmediate()
        {
            for (int i = 0; i < _pings.Count; i++) _pings[i].HideImmediate();
            _active = null;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _pings.Count; i++) _pings[i].Tick(dt);
        }

        private Ping Acquire()
        {
            for (int i = 0; i < _pings.Count; i++)
                if (!_pings[i].InUse) return _pings[i];
            var ping = new Ping(_tr, markerSize, baseColor, outlineColor,
                                introDuration, fadeDuration, startScaleMul, endScaleMul);
            _pings.Add(ping);
            return ping;
        }

        // ─────────────── 개별 마커(핑) ───────────────

        private class Ping
        {
            private enum St { Idle, Intro, Hold, Fade }

            private readonly Transform _tr;
            private readonly Renderer _rend;
            private MaterialPropertyBlock _mpb;
            private readonly float _size, _intro, _fade, _startMul, _endMul;
            private readonly Color _base, _outline;
            private St _st = St.Idle;
            private float _t;

            public bool InUse => _st != St.Idle;

            public Ping(Transform parent, float size, Color baseCol, Color outlineCol,
                        float intro, float fade, float startMul, float endMul)
            {
                _size = size; _base = baseCol; _outline = outlineCol;
                _intro = intro; _fade = fade; _startMul = startMul; _endMul = endMul;
                var go = BuildQuad(parent, baseCol, outlineCol, out _rend);
                _tr = go.transform;
                go.SetActive(false);
            }

            /// <summary>등장: 정착 애니메이션 시작 후 도착/취소/새 클릭 전까지 상시 유지(Hold).</summary>
            public void Begin(Vector3 pos)
            {
                _tr.position = pos;
                _st = St.Intro; _t = 0f;
                _tr.gameObject.SetActive(true);
                Apply(_startMul, 1f);
            }

            public void SetPosition(Vector3 pos)
            {
                if (_st != St.Idle) _tr.position = pos;
            }

            public void BeginFade()
            {
                if (_st == St.Idle || _st == St.Fade) return;
                _st = St.Fade; _t = 0f;
            }

            public void HideImmediate()
            {
                _st = St.Idle;
                if (_tr != null) _tr.gameObject.SetActive(false);
            }

            public void Tick(float dt)
            {
                switch (_st)
                {
                    case St.Intro:
                        _t += dt;
                        float ki = _intro > 0f ? Mathf.Clamp01(_t / _intro) : 1f;
                        Apply(Mathf.Lerp(_startMul, 1f, ki), 1f);
                        if (ki >= 1f) _st = St.Hold;
                        break;
                    case St.Hold:
                        Apply(1f, 1f);   // 상시 유지
                        break;
                    case St.Fade:
                        _t += dt;
                        float kf = _fade > 0f ? Mathf.Clamp01(_t / _fade) : 1f;
                        Apply(Mathf.Lerp(1f, _endMul, kf), 1f - kf);   // 알파↓ + 살짝 스케일 축소
                        if (kf >= 1f) HideImmediate();
                        break;
                }
            }

            private void Apply(float scaleMul, float alpha)
            {
                float s = _size * scaleMul;
                _tr.localScale = new Vector3(s, s, s);
                if (_rend == null) return;
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                _rend.GetPropertyBlock(_mpb);
                var c = _base; c.a = _base.a * alpha;
                var o = _outline; o.a = _outline.a * alpha;
                _mpb.SetColor("_Color", c);
                _mpb.SetColor("_OutlineColor", o);
                _rend.SetPropertyBlock(_mpb);
            }

            /// <summary>바닥에 눕힌 1x1 링 Quad(BossRaid/Telegraph circle, _Fill=0 → 링). 미포함 시 Sprites/Default 폴백.</summary>
            private static GameObject BuildQuad(Transform parent, Color baseCol, Color outlineCol, out Renderer rend)
            {
                var go = new GameObject("MoveMarkerPing");
                go.transform.SetParent(parent, false);

                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();

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
                rend = mr;
                return go;
            }
        }
    }
}
