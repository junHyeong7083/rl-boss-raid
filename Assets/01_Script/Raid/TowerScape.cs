using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BossRaid
{
    /// <summary>
    /// "탑 최상단 결전장" 절차 연출 부트스트랩(에셋 불요·씬 배선 불요).
    ///
    /// 아레나(원판 바닥)를 거대한 탑의 꼭대기로 재해석한다:
    ///   1) 혈월 하늘(BloodMoonSky.shader)을 RenderSettings.skybox 로 적용
    ///   2) 바닥 아래로 내려가는 테이퍼 석재 탑 몸체(y&lt;0)
    ///   3) 탑 아래 대형 소프트 구름층 + 원거리 부유 바위 실루엣(깊이감)
    ///   4) 아레나 가장자리에서 잔해가 흘러 떨어지는 루프 파티클
    ///      + arena_radius 축소 순간 바닥 조각이 떨어져 나가는 낙사 연출 + 셰이크
    ///
    /// 부트스트랩: [RuntimeInitializeOnLoadMethod(AfterSceneLoad)] 로 스스로 GameObject 를 만든다
    ///   (EnvironmentDirector/VisualUpgrader 와 동일 패턴 — 씬/다른 스크립트 배선 불요).
    ///   보스 아레나 씬이 아니면 조용히 자체 소멸. 오브젝트를 못 찾거나 셰이더가 없으면 해당 파트만 스킵.
    ///
    /// 읽기 전용 참조: BossGameViewer.LatestSnapshot.boss.arena_radius / ContinuousToWorld / OnSnapshotApplied.
    /// 씬 파일은 절대 수정하지 않는다(전부 런타임 생성).
    /// </summary>
    [DisallowMultipleComponent]
    public class TowerScape : MonoBehaviour
    {
        // ─────────────── 부트스트랩 ───────────────

        private static TowerScape _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            bool isArenaScene = GameObject.Find("Arena_Generated") != null
                                || Object.FindFirstObjectByType<BossGameViewer>() != null;
            if (!isArenaScene) return;

            var go = new GameObject("TowerScape");
            go.AddComponent<TowerScape>();
        }

        // ─────────────── 인스펙터 토글 ───────────────

        [Header("Toggles (문제 시 즉시 끄기 — 빌드 시점 게이트)")]
        [Tooltip("혈월 절차 스카이박스 적용")]                public bool enableSky = true;
        [Tooltip("바닥 아래 테이퍼 석재 탑 몸체 생성")]        public bool enableTower = true;
        [Tooltip("탑 아래 구름층 + 원거리 부유 바위 생성")]    public bool enableClouds = true;
        [Tooltip("가장자리 낙석 루프 + 축소 시 바닥 붕괴 연출")] public bool enableFallEdge = true;

        // ─────────────── 튜닝 상수 ───────────────

        // 실제 아레나 바닥 반경(BossArenaBuilder.FloorRadius = 16). 탑 상단을 여기에 맞춰
        // 바닥 원판이 탑 테두리에 정확히 얹히게 한다("탑 꼭대기" 착시의 핵심).
        private const float FloorRadius = 16f;

        // 탑 몸체
        private const float TowerTopRadius = 16.5f;   // 바닥(16)보다 살짝 넓게 → 틈 없는 석재 테두리
        private const float TowerBotRadius = 10f;     // 아래로 갈수록 좁아짐
        private const int   TowerSegments  = 5;
        private const float TowerSegHeight = 7f;      // 총 깊이 ≈ 35
        private static readonly Color StoneColor     = new Color(0.085f, 0.065f, 0.10f); // 어두운 자주-회색 석재
        private static readonly Color StoneColorDeep = new Color(0.045f, 0.035f, 0.06f); // 원거리 바위(더 어둡게)

        // 구름층
        private const int   CloudLayers   = 3;
        private const float CloudTopY     = -14f;
        private const float CloudLayerGap = 8f;       // -14, -22, -30
        private const float CloudBaseSize = 90f;

        // 부유 바위
        private const int   RockCount    = 7;
        private const float RockRadiusMin = 25f;
        private const float RockRadiusMax = 35f;

        // 가장자리 낙사
        private const float ShrinkEpsilon = 0.1f;     // 이 이상 줄면 붕괴 연출
        private const float ShardLife     = 1.2f;

        // 스카이박스 프로퍼티
        private static readonly Color SkyHorizon = new Color(0.25f, 0.04f, 0.06f);
        private static readonly Color SkyMid     = new Color(0.09f, 0.03f, 0.10f);
        private static readonly Color SkyZenith  = new Color(0.015f, 0.012f, 0.04f);
        private static readonly Vector4 MoonDir  = new Vector4(45f, 22f, 65f, 0f);
        private static readonly Color MoonGlow   = new Color(1.8f, 0.28f, 0.20f);
        private const float StarDensity = 0.5f;

        // ─────────────── 캐시/상태 ───────────────

        private BossGameViewer _viewer;
        private float _cellSize = 1f;
        private Vector3 _center;                 // 아레나 중심(월드)
        private Transform _cam;

        private Material _stoneMat;
        private Material _cloudMat;
        private Material _skyMat;
        private Material _prevSkybox;            // 복원용

        private readonly List<Transform> _cloudLayers = new List<Transform>();
        private readonly List<float> _cloudSpin = new List<float>();
        private Transform _rockRoot;

        private ParticleSystem _edgeFall;
        private float _prevRadiusSim = -1f;
        private bool _subscribed;

        // ─────────────── 초기화 ───────────────

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
        }

        private void Start()
        {
            _viewer = Object.FindFirstObjectByType<BossGameViewer>();
            _cellSize = _viewer != null ? _viewer.cellSize : 1f;
            _cam = Camera.main != null ? Camera.main.transform : null;
            _center = ArenaCenterWorld();

            if (enableSky)    BuildSky();
            if (enableTower)  BuildTower();
            if (enableClouds) { BuildClouds(); BuildFloatingRocks(); }
            if (enableFallEdge)
            {
                BuildEdgeFall();
                if (_viewer != null) { _viewer.OnSnapshotApplied += OnSnapshotApplied; _subscribed = true; }
            }
        }

        private void OnDestroy()
        {
            if (_subscribed && _viewer != null) _viewer.OnSnapshotApplied -= OnSnapshotApplied;
            // 스카이박스 복원(우리가 바꿨을 때만)
            if (_skyMat != null && RenderSettings.skybox == _skyMat) RenderSettings.skybox = _prevSkybox;
            if (_instance == this) _instance = null;
        }

        private Vector3 ArenaCenterWorld()
        {
            if (_viewer != null)
            {
                Vector2 c = _viewer.arenaCenterSim;
                return _viewer.ContinuousToWorld(c.x, c.y);
            }
            return new Vector3(10f, 0f, 10f);
        }

        // ─────────────── 1) 혈월 스카이박스 ───────────────

        private void BuildSky()
        {
            var sh = Shader.Find("BossRaid/BloodMoonSky");
            if (sh == null)
            {
                Debug.LogWarning("[TowerScape] BloodMoonSky 셰이더를 찾지 못해 스카이박스를 생략합니다.");
                return;
            }
            _skyMat = new Material(sh) { name = "BloodMoonSky (runtime)" };
            _skyMat.SetColor("_HorizonColor", SkyHorizon);
            _skyMat.SetColor("_MidColor", SkyMid);
            _skyMat.SetColor("_ZenithColor", SkyZenith);
            _skyMat.SetVector("_MoonDir", MoonDir);
            _skyMat.SetColor("_MoonGlowColor", MoonGlow);
            _skyMat.SetFloat("_StarDensity", StarDensity);

            _prevSkybox = RenderSettings.skybox;
            RenderSettings.skybox = _skyMat;   // DynamicGI 갱신 불요(정적 라이팅에 영향 없음)

            // 카메라 클리어가 SolidColor(검정)면 스카이박스가 안 보인다 → Skybox 로 전환.
            var cam = Camera.main;
            if (cam != null && cam.clearFlags == CameraClearFlags.SolidColor)
                cam.clearFlags = CameraClearFlags.Skybox;
        }

        // ─────────────── 2) 탑 몸체 ───────────────

        private void BuildTower()
        {
            _stoneMat = BuildLitMaterial(StoneColor, 0.12f);

            var root = new GameObject("TowerScape_Body").transform;
            root.SetParent(transform, false);
            // 탑 상단을 바닥(y=0)보다 0.05 아래로 → 룬 바닥이 석재 캡을 덮고 Z-파이팅도 회피.
            root.position = new Vector3(_center.x, -0.05f, _center.z);

            float yTop = 0f;   // 세그먼트 상단(로컬)
            for (int i = 0; i < TowerSegments; i++)
            {
                float t = (i + 0.5f) / TowerSegments;
                float r = Mathf.Lerp(TowerTopRadius, TowerBotRadius, t);

                var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                cyl.name = "TowerSeg_" + i;
                cyl.transform.SetParent(root, false);
                StripCollider(cyl);

                // 반경 계단 이음새를 감추려 각 세그먼트를 상단 고정·하단으로만 0.6 겹치게 늘림.
                // (실린더 기본 높이 2 → scale.y = 실제높이/2)
                float h = TowerSegHeight + 0.6f;
                cyl.transform.localScale = new Vector3(r * 2f, h * 0.5f, r * 2f);
                cyl.transform.localPosition = new Vector3(0f, yTop - h * 0.5f, 0f);

                var mr = cyl.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _stoneMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;   // 대형 지오메트리 — 그림자 비용 절감
                mr.receiveShadows = true;

                yTop -= TowerSegHeight;
            }
        }

        // ─────────────── 3) 구름층 + 부유 바위 ───────────────

        private void BuildClouds()
        {
            var noise = BuildCloudTexture(128);
            _cloudMat = BuildCloudMaterial(noise);
            if (_cloudMat == null) return;

            var root = new GameObject("TowerScape_Clouds").transform;
            root.SetParent(transform, false);
            root.position = new Vector3(_center.x, 0f, _center.z);

            for (int i = 0; i < CloudLayers; i++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                q.name = "CloudLayer_" + i;
                q.transform.SetParent(root, false);
                StripCollider(q);

                float yy = CloudTopY - i * CloudLayerGap;         // -14, -22, -30
                float size = CloudBaseSize - i * 6f;
                q.transform.localPosition = new Vector3(0f, yy, 0f);
                q.transform.localRotation = Quaternion.Euler(-90f, i * 47f, 0f);  // 수평으로 눕힘
                q.transform.localScale = new Vector3(size, size, 1f);

                var mr = q.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _cloudMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;

                _cloudLayers.Add(q.transform);
                _cloudSpin.Add((i % 2 == 0 ? 1f : -1f) * (1.4f + i * 0.5f));   // 도/초, 교차 회전
            }
        }

        private void BuildFloatingRocks()
        {
            Mesh mesh = FindDressingMesh();
            Material rockMat = BuildLitMaterial(StoneColorDeep, 0.08f);

            _rockRoot = new GameObject("TowerScape_Rocks").transform;
            _rockRoot.SetParent(transform, false);
            _rockRoot.position = new Vector3(_center.x, 0f, _center.z);

            // 결정적 배치(랜덤 시드 고정 → 프레임 간 일관, 재현성).
            var rnd = new System.Random(20260713);
            for (int i = 0; i < RockCount; i++)
            {
                float ang = (float)i / RockCount * Mathf.PI * 2f + (float)rnd.NextDouble() * 0.6f;
                float rad = Mathf.Lerp(RockRadiusMin, RockRadiusMax, (float)rnd.NextDouble());
                float yy = -6f - (i % 4) * 2.5f;                 // -6 ~ -13.5

                var go = new GameObject("FloatingRock_" + i);
                go.transform.SetParent(_rockRoot, false);
                go.transform.localPosition = new Vector3(Mathf.Cos(ang) * rad, yy, Mathf.Sin(ang) * rad);
                go.transform.localRotation = Quaternion.Euler(
                    (float)rnd.NextDouble() * 360f, (float)rnd.NextDouble() * 360f, (float)rnd.NextDouble() * 360f);

                float s = 3f + (float)rnd.NextDouble() * 3f;      // 크게(3~6)
                go.transform.localScale = new Vector3(s, s * (0.6f + (float)rnd.NextDouble() * 0.6f), s);

                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh = mesh;
                mr.sharedMaterial = rockMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
        }

        /// <summary>씬의 Props/Pillars/GameplayPillars 메시를 재활용. 없으면 Cube 프리미티브 메시로 폴백.</summary>
        private Mesh FindDressingMesh()
        {
            foreach (var name in new[] { "Props", "Pillars", "GameplayPillars" })
            {
                var go = GameObject.Find(name);
                if (go == null) continue;
                var mf = go.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) return mf.sharedMesh;
            }
            // 폴백: Cube 메시 1회 확보 후 파괴(메시만 재사용).
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var m = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
            return m;
        }

        // ─────────────── 4) 가장자리 낙사 ───────────────

        private void BuildEdgeFall()
        {
            var go = new GameObject("TowerEdgeFall");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(_center.x, 0f, _center.z);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);   // 살짝 바깥으로 떨어져 나감
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startColor = new Color(0.30f, 0.22f, 0.24f, 0.9f);        // 어두운 돌조각
            main.gravityModifier = 0.8f;                                   // 아래로 가속
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 140;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 10f;                                   // 절제된 rate

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radiusThickness = 0f;                                    // 원주(가장자리)에서만 방출
            shape.arc = 360f;
            shape.rotation = new Vector3(90f, 0f, 0f);                     // 원을 XZ 평면(수평)으로
            shape.radius = CurrentRadiusWorld();

            // 텀블링 회전(낙하 돌조각 느낌).
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-90f * Mathf.Deg2Rad, 90f * Mathf.Deg2Rad);

            // 수명 말미 알파 페이드.
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.9f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.alignment = ParticleSystemRenderSpace.View;
            r.sortMode = ParticleSystemSortMode.None;
            r.material = EdgeParticleMaterial();

            _edgeFall = ps;
            ps.Play();
        }

        private void OnSnapshotApplied(BossSnapshot snap)
        {
            float rSim = (snap != null && snap.boss != null) ? snap.boss.arena_radius : 0f;
            if (rSim <= 0.01f) { _prevRadiusSim = rSim; return; }

            if (_prevRadiusSim > 0f && (_prevRadiusSim - rSim) > ShrinkEpsilon)
            {
                float prevW = _prevRadiusSim * _cellSize;
                float newW = rSim * _cellSize;
                SpawnShrinkShards(newW, prevW);
                LostArkCamera.ShakeCamera(0.25f, 0.4f);
            }
            _prevRadiusSim = rSim;
        }

        /// <summary>이전~새 반경 사이 링 구간의 바닥 조각이 떨어져 나가 낙하+회전하며 스케일 페이드.</summary>
        private void SpawnShrinkShards(float newRWorld, float prevRWorld)
        {
            int n = Random.Range(6, 11);
            for (int i = 0; i < n; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float rad = Random.Range(newRWorld, prevRWorld);
                Vector3 outward = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                Vector3 pos = new Vector3(_center.x, 0f, _center.z) + outward * rad;

                var prim = (i % 2 == 0) ? PrimitiveType.Cube : PrimitiveType.Cylinder;
                var go = GameObject.CreatePrimitive(prim);
                go.name = "FallShard";
                StripCollider(go);
                go.transform.position = pos;
                go.transform.rotation = Random.rotation;
                float sx = Random.Range(0.4f, 1.0f);
                go.transform.localScale = new Vector3(sx, Random.Range(0.25f, 0.6f), sx);

                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _stoneMat != null ? _stoneMat : BuildLitMaterial(StoneColor, 0.12f);
                mr.shadowCastingMode = ShadowCastingMode.Off;

                var f = go.AddComponent<FallingShard>();
                f.velocity = outward * Random.Range(1.0f, 3.0f) + Vector3.up * Random.Range(0.5f, 2.5f);
                f.angularVelocity = new Vector3(
                    Random.Range(-220f, 220f), Random.Range(-220f, 220f), Random.Range(-220f, 220f));
                f.life = ShardLife;
            }
        }

        private float CurrentRadiusWorld()
        {
            var snap = _viewer != null ? _viewer.LatestSnapshot : null;
            float rs = (snap != null && snap.boss != null) ? snap.boss.arena_radius : 0f;
            return rs > 0.01f ? rs * _cellSize : FloorRadius;   // 데이터 없으면 바닥 반경으로 폴백
        }

        // ─────────────── 프레임 루프 ───────────────

        private void Update()
        {
            float dt = Time.deltaTime;

            // 구름 드리프트(천천히 회전).
            for (int i = 0; i < _cloudLayers.Count; i++)
            {
                var t = _cloudLayers[i];
                if (t != null) t.Rotate(0f, _cloudSpin[i] * dt, 0f, Space.World);
            }
            // 부유 바위 아주 느린 공전(깊이감).
            if (_rockRoot != null) _rockRoot.Rotate(0f, 0.6f * dt, 0f, Space.World);

            // 가장자리 낙석 링을 현재 아레나 반경에 맞춤(정지 시에도 값만 세팅 — 저비용).
            if (_edgeFall != null)
            {
                var shape = _edgeFall.shape;
                shape.radius = CurrentRadiusWorld();
            }
        }

        // ─────────────── 유틸 ───────────────

        private static void StripCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) Destroy(c);
        }

        /// <summary>URP Lit(폴백 Standard) 불투명 머티리얼 — 어두운 석재.</summary>
        private static Material BuildLitMaterial(Color baseColor, float smoothness)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            var m = new Material(sh) { name = "TowerStone (runtime)" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseColor);
            if (m.HasProperty("_Color")) m.SetColor("_Color", baseColor);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            return m;
        }

        /// <summary>반투명 구름 머티리얼(URP Unlit 알파 블렌드, 폴백 Sprites/Default).</summary>
        private static Material BuildCloudMaterial(Texture2D tex)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null)
            {
                var m = new Material(sh) { name = "TowerCloud (runtime)" };
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(0.14f, 0.07f, 0.16f, 0.55f));
                // 알파 블렌드 투명 설정
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.DisableKeyword("_ALPHATEST_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
                return m;
            }
            var fb = Shader.Find("Sprites/Default");
            if (fb == null) return null;
            var mm = new Material(fb) { name = "TowerCloud (runtime)" };
            mm.mainTexture = tex;
            mm.color = new Color(0.14f, 0.07f, 0.16f, 0.55f);
            return mm;
        }

        private static Material _edgeMat;
        private static Material EdgeParticleMaterial()
        {
            if (_edgeMat != null) return _edgeMat;
            var sh = Shader.Find("BossRaid/Particle");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var m = new Material(sh) { name = "TowerEdgeParticle (runtime)" };
            if (m.HasProperty("_Shape")) m.SetInt("_Shape", 0);   // Circle
            if (m.HasProperty("_Glow")) m.SetFloat("_Glow", 0f);  // 발광 없음(돌조각)
            _edgeMat = m;
            return m;
        }

        // ─────────────── 절차 구름 노이즈 텍스처 ───────────────

        private static Texture2D BuildCloudTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "TowerCloudNoise" };
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;
                    float n = Fbm(u * 4f, v * 4f);                 // 0..1 프랙탈 노이즈
                    float dx = u - 0.5f, dy = v - 0.5f;
                    float rr = Mathf.Sqrt(dx * dx + dy * dy) * 2f; // 0(중심)~1(가장자리)
                    float disc = Mathf.SmoothStep(1f, 0.2f, rr);   // 가장자리 투명(소프트 원반)
                    float a = Mathf.Clamp01(n * disc);
                    a *= a;                                        // 부드럽게
                    Color c = Color.Lerp(new Color(0.10f, 0.05f, 0.12f),
                                         new Color(0.18f, 0.08f, 0.16f), n);
                    px[y * size + x] = new Color(c.r, c.g, c.b, a * 0.9f);
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        private static float Hash2(int x, int y)
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFu) / 65535f;
        }

        private static float ValueNoise(float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float u = xf * xf * (3f - 2f * xf);
            float v = yf * yf * (3f - 2f * yf);
            float a = Hash2(xi, yi),     b = Hash2(xi + 1, yi);
            float c = Hash2(xi, yi + 1), d = Hash2(xi + 1, yi + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        private static float Fbm(float x, float y)
        {
            float s = 0f, amp = 0.5f, f = 1f;
            for (int i = 0; i < 4; i++) { s += ValueNoise(x * f, y * f) * amp; f *= 2f; amp *= 0.5f; }
            return Mathf.Clamp01(s);
        }

        // ─────────────── 낙하 조각(축소 붕괴 연출) ───────────────

        /// <summary>바닥 조각이 낙하+회전하며 스케일 아웃(불투명 석재라 알파 대신 축소로 "사라짐" 표현) 후 자멸.</summary>
        private class FallingShard : MonoBehaviour
        {
            public Vector3 velocity;
            public Vector3 angularVelocity;   // 도/초
            public float life = 1.2f;

            private float _t;
            private Vector3 _baseScale;
            private const float Gravity = 18f;

            private void Start() => _baseScale = transform.localScale;

            private void Update()
            {
                float dt = Time.deltaTime;
                _t += dt;
                velocity += Vector3.down * (Gravity * dt);
                transform.position += velocity * dt;
                transform.Rotate(angularVelocity * dt, Space.Self);

                float k = Mathf.Clamp01(_t / life);
                transform.localScale = _baseScale * (1f - k * 0.85f);   // 축소 페이드

                if (_t >= life) Destroy(gameObject);
            }
        }
    }
}
