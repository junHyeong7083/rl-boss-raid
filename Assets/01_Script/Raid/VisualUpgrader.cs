using System.Collections.Generic;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 절차적 그래픽 업그레이드 런타임 적용기 (에셋/씬 수정 불요).
    ///
    /// (a) Arena_Floor 렌더러 → BloodRuneFloor 머티리얼(혈월 마법진 바닥)로 교체.
    ///     기존 머티리얼의 _BaseColor 를 새 머티리얼 초기값으로 승계.
    ///     EnvironmentDirector 가 MPB 로 _BaseColor 브리딩을 계속하므로 그와 공존한다.
    ///
    /// (b) 유닛/보스 본체 렌더러 → RimLit 머티리얼(림라이트)로 교체.
    ///     기존 머티리얼의 _BaseMap/_BaseColor(역할 틴트 반영)를 승계. 림 색은 역할 틴트 기반으로
    ///     약간 밝게, 보스는 진홍 림. 스폰이 늦으므로 폴링 + OnSnapshotApplied 구독으로 신규 감지.
    ///     교체는 유닛/보스당 1회(HashSet 가드). HP바/이펙트/파티클/라인/트레일 렌더러는 제외
    ///     (UnitView 의 피격 대상 제외 규칙과 동일 기준).
    ///
    /// 히트플래시(UnitView/BossController)·역할틴트(BossGameViewer)와 완전 호환:
    ///   RimLit 은 _BaseColor/_Color/_EmissionColor/_BaseMap + _EMISSION 키워드를 그대로 지원한다.
    ///
    /// 부트스트랩: [RuntimeInitializeOnLoadMethod(AfterSceneLoad)] 로 스스로 GameObject 를 만든다
    ///   (EnvironmentDirector 와 동일 패턴 — 씬/다른 스크립트 배선 불요). 보스 씬이 아니면 자체 소멸.
    /// 셰이더를 못 찾으면(빌드 미포함 등) 해당 기능을 조용히 스킵한다.
    /// </summary>
    [DisallowMultipleComponent]
    public class VisualUpgrader : MonoBehaviour
    {
        // ─────────────── 부트스트랩 ───────────────

        private static VisualUpgrader _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            bool isArenaScene = GameObject.Find("Arena_Generated") != null
                                || Object.FindFirstObjectByType<BossGameViewer>() != null;
            if (!isArenaScene) return;

            var go = new GameObject("VisualUpgrader");
            go.AddComponent<VisualUpgrader>();
        }

        // ─────────────── 인스펙터 토글(문제 시 즉시 끄기) ───────────────

        [Header("Toggles")]
        [Tooltip("혈월 마법진 바닥 적용")] public bool enableFloor = true;
        [Tooltip("캐릭터 림라이트 적용 — 기본 OFF. 미검증 셰이더로 캐릭터 전체 머티리얼을 교체하다 " +
                 "컴파일 실패 시 캐릭터가 전부 사라지는 사고가 있었음(2026-07-13). " +
                 "에디터에서 RimLit.shader 정상 컴파일 확인 후 켤 것.")]
        public bool enableRim = false;

        [Header("Tuning")]
        [Tooltip("신규 유닛/보스 감지 폴링 간격(초)")]
        public float pollInterval = 1.5f;
        [Tooltip("림 강도")]                public float rimIntensity = 1.4f;
        [Tooltip("림 프레넬 지수")]          public float rimPower = 3.0f;

        private static readonly Color BossRim = new Color(2.2f, 0.35f, 0.28f); // 진홍 HDR

        // ─────────────── 셰이더/프로퍼티 ID ───────────────

        private Shader _floorShader;
        private Shader _rimShader;

        private static readonly int IdBaseMap    = Shader.PropertyToID("_BaseMap");
        private static readonly int IdMainTex    = Shader.PropertyToID("_MainTex");
        private static readonly int IdBaseColor  = Shader.PropertyToID("_BaseColor");
        private static readonly int IdColor      = Shader.PropertyToID("_Color");
        private static readonly int IdRimColor   = Shader.PropertyToID("_RimColor");
        private static readonly int IdRimPower   = Shader.PropertyToID("_RimPower");
        private static readonly int IdRimInten   = Shader.PropertyToID("_RimIntensity");

        // ─────────────── 상태 ───────────────

        private BossGameViewer _viewer;

        // 바닥
        private Renderer _floorRenderer;
        private Material _floorOriginal;
        private Material _runeMaterial;
        private bool _floorApplied;

        // 림: 교체 기록(되돌리기용) + 중복 가드
        private struct RimRecord { public Renderer renderer; public Material original; public Material applied; }
        private readonly List<RimRecord> _rimRecords = new List<RimRecord>();
        private readonly HashSet<int> _processed = new HashSet<int>();  // UnitView/BossController instanceID
        private bool _rimApplied;

        private float _pollTimer;

        // ─────────────── 초기화 ───────────────

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
        }

        private void Start()
        {
            _viewer = Object.FindFirstObjectByType<BossGameViewer>();

            _floorShader = Shader.Find("BossRaid/BloodRuneFloor");
            _rimShader   = Shader.Find("BossRaid/RimLit");

            if (_floorShader == null)
                Debug.LogWarning("[VisualUpgrader] BloodRuneFloor 셰이더를 찾지 못해 바닥 연출을 생략합니다.");
            if (_rimShader == null)
                Debug.LogWarning("[VisualUpgrader] RimLit 셰이더를 찾지 못해 림라이트를 생략합니다.");

            ResolveFloor();

            // 스폰된 유닛/보스 즉시 감지용 구독(있으면). 폴링은 폴백/보험.
            if (_viewer != null) _viewer.OnSnapshotApplied += OnSnapshotApplied;

            // 최초 1회 즉시 스캔
            _pollTimer = pollInterval;
        }

        private void OnDestroy()
        {
            if (_viewer != null) _viewer.OnSnapshotApplied -= OnSnapshotApplied;
            if (_instance == this) _instance = null;
        }

        private void OnSnapshotApplied(BossSnapshot snap)
        {
            if (enableRim && _rimShader != null) ScanAndApplyRim();
        }

        private void ResolveFloor()
        {
            var go = GameObject.Find("Arena_Floor");
            if (go == null) return;
            _floorRenderer = go.GetComponent<Renderer>();
            if (_floorRenderer != null) _floorOriginal = _floorRenderer.sharedMaterial;
        }

        // ─────────────── 프레임 루프 ───────────────

        private void Update()
        {
            // 바닥 토글 반영
            if (_floorShader != null && _floorRenderer != null)
            {
                if (enableFloor && !_floorApplied) ApplyFloor();
                else if (!enableFloor && _floorApplied) RevertFloor();
            }

            // 림 토글 반영
            if (_rimShader != null)
            {
                if (enableRim)
                {
                    _pollTimer += Time.deltaTime;
                    if (_pollTimer >= pollInterval)
                    {
                        _pollTimer = 0f;
                        ScanAndApplyRim();
                    }
                }
                else if (_rimApplied)
                {
                    RevertRim();
                }
            }
        }

        // ─────────────── 바닥 적용/복원 ───────────────

        private void ApplyFloor()
        {
            if (_runeMaterial == null)
            {
                _runeMaterial = new Material(_floorShader) { name = "BloodRuneFloor (runtime)" };
                // 기존 바닥 컬러를 _BaseColor 초기값으로 승계
                if (_floorOriginal != null)
                {
                    Color baseC;
                    if (_floorOriginal.HasProperty(IdBaseColor)) baseC = _floorOriginal.GetColor(IdBaseColor);
                    else if (_floorOriginal.HasProperty(IdColor)) baseC = _floorOriginal.GetColor(IdColor);
                    else baseC = _runeMaterial.GetColor(IdBaseColor);
                    _runeMaterial.SetColor(IdBaseColor, baseC);
                }
            }
            _floorRenderer.sharedMaterial = _runeMaterial;
            _floorApplied = true;
        }

        private void RevertFloor()
        {
            if (_floorRenderer != null && _floorOriginal != null)
                _floorRenderer.sharedMaterial = _floorOriginal;
            _floorApplied = false;
        }

        // ─────────────── 림 스캔/적용/복원 ───────────────

        private void ScanAndApplyRim()
        {
            // 유닛
            var units = Object.FindObjectsByType<UnitView>(FindObjectsSortMode.None);
            foreach (var uv in units)
            {
                if (uv == null) continue;
                int id = uv.GetInstanceID();
                if (!_processed.Add(id)) continue;   // 이미 처리

                Color rim = RimForRole(uv.Role);
                foreach (var r in CollectUnitRenderers(uv))
                    SwapToRim(r, rim);
            }

            // 보스
            var boss = Object.FindFirstObjectByType<BossController>();
            if (boss != null)
            {
                int id = boss.GetInstanceID();
                if (_processed.Add(id) && boss.bodyRenderers != null)
                {
                    foreach (var r in boss.bodyRenderers)
                        SwapToRim(r, BossRim);
                }
            }

            _rimApplied = _rimRecords.Count > 0;
        }

        /// <summary>UnitView 의 피격 대상 렌더러와 동일 기준으로 본체 렌더러만 수집(HP바/이펙트/파티클 제외).</summary>
        private static List<Renderer> CollectUnitRenderers(UnitView uv)
        {
            var result = new List<Renderer>();
            var rs = uv.GetComponentsInChildren<Renderer>(true);
            foreach (var r in rs)
            {
                if (r == null) continue;
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if (IsUnder(r.transform, uv.hpBarRoot) || IsUnder(r.transform, uv.deathEffect) ||
                    IsUnder(r.transform, uv.shieldEffect) || IsUnder(r.transform, uv.buffAtkEffect) ||
                    IsUnder(r.transform, uv.guardEffect)) continue;
                result.Add(r);
            }
            return result;
        }

        private static bool IsUnder(Transform t, GameObject root)
            => root != null && t != null && t.IsChildOf(root.transform);

        /// <summary>역할 틴트를 승계해 살짝 밝힌 HDR 림 색. 역할별로 미세 차이만.</summary>
        private Color RimForRole(int role)
        {
            // 역할별 기본 색조(역할 틴트 성향과 유사). 미상(-1)은 중립.
            Color seed;
            switch (role)
            {
                case 1:  seed = new Color(0.55f, 0.75f, 1.0f); break; // Tank - 청색
                case 2:  seed = new Color(0.5f, 1.0f, 0.6f);  break; // Healer - 녹색
                case 3:  seed = new Color(1.0f, 0.85f, 0.5f); break; // Support - 황금
                default: seed = new Color(1.0f, 0.55f, 0.5f); break; // Dealer/미상 - 온난
            }
            return seed * rimIntensity;
        }

        private void SwapToRim(Renderer r, Color rim)
        {
            if (r == null) return;
            var src = r.sharedMaterial;
            if (src != null && src.shader == _rimShader) return;  // 이미 적용

            var m = new Material(_rimShader) { name = "RimLit (runtime)" };

            if (src != null)
            {
                // _BaseMap 승계 (_MainTex 폴백)
                Texture bm = null;
                if (src.HasProperty(IdBaseMap)) bm = src.GetTexture(IdBaseMap);
                if (bm == null && src.HasProperty(IdMainTex)) bm = src.GetTexture(IdMainTex);
                if (bm != null)
                {
                    m.SetTexture(IdBaseMap, bm);
                    if (src.HasProperty(IdBaseMap))
                    {
                        m.SetTextureScale(IdBaseMap, src.GetTextureScale(IdBaseMap));
                        m.SetTextureOffset(IdBaseMap, src.GetTextureOffset(IdBaseMap));
                    }
                }

                // _BaseColor(역할 틴트 반영) 승계 — _Color 별칭도 동일값 세팅
                Color baseC = Color.white;
                if (src.HasProperty(IdBaseColor)) baseC = src.GetColor(IdBaseColor);
                else if (src.HasProperty(IdColor)) baseC = src.GetColor(IdColor);
                m.SetColor(IdBaseColor, baseC);
                m.SetColor(IdColor, baseC);
            }

            m.SetColor(IdRimColor, rim);
            m.SetFloat(IdRimPower, rimPower);
            m.SetFloat(IdRimInten, rimIntensity);

            _rimRecords.Add(new RimRecord { renderer = r, original = src, applied = m });
            r.sharedMaterial = m;
        }

        private void RevertRim()
        {
            foreach (var rec in _rimRecords)
            {
                if (rec.renderer != null) rec.renderer.sharedMaterial = rec.original;
                if (rec.applied != null) Destroy(rec.applied);
            }
            _rimRecords.Clear();
            _processed.Clear();      // 재활성화 시 깨끗하게 재적용
            _rimApplied = false;
        }
    }
}
