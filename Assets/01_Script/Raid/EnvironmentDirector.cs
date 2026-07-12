using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BossRaid
{
    /// <summary>
    /// 런타임 환경 연출 감독(에셋 불요·씬 배선 불요).
    /// BossArenaBuilder 가 구워 넣은 "Arena_Generated" 하위 오브젝트들을 이름으로 찾아
    /// 안개/앰비언트·떠다니는 입자·혈월 펄스·기둥 룬·조명 무드·바닥 브리딩을 코드로 연출한다.
    ///
    /// 부트스트랩: [RuntimeInitializeOnLoadMethod(AfterSceneLoad)] 로 스스로 GameObject 를 만든다.
    /// (다른 스크립트가 배선하지 않는다 / 하면 안 된다.) 보스 씬이 아니면 조용히 자체 소멸.
    ///
    /// 페이즈/전멸기 상태는 BossGameViewer.LatestSnapshot(읽기 전용)에서만 조회한다.
    ///   - phase: 0=P1, 1=P2, 2=P3
    ///   - 전멸기(seal): boss.active_pattern==9(SealWipe) 또는 boss.active_mode=="seal"
    ///
    /// 성능: 렌더러/라이트/파티클을 1회 캐시, 렌더러당 MaterialPropertyBlock 1개 재사용,
    ///       Update 는 sin/lerp 스칼라 연산 + 프로퍼티 세팅만(할당 없음).
    /// </summary>
    [DisallowMultipleComponent]
    public class EnvironmentDirector : MonoBehaviour
    {
        // ─────────────── 부트스트랩 ───────────────

        private static EnvironmentDirector _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            // 보스 아레나 씬에서만 동작(무관 씬 오염 방지).
            bool isArenaScene = GameObject.Find("Arena_Generated") != null
                                || Object.FindFirstObjectByType<BossGameViewer>() != null;
            if (!isArenaScene) return;

            var go = new GameObject("EnvironmentDirector");
            go.AddComponent<EnvironmentDirector>();
        }

        // ─────────────── 튜닝 상수 ───────────────

        // 1) 안개/앰비언트
        private const float FogDensity = 0.014f;
        private static readonly Color FogBase = new Color(0.09f, 0.04f, 0.08f);   // 어두운 자주
        private static readonly Color FogP3   = new Color(0.14f, 0.04f, 0.055f);  // P3 붉은기 +
        private static readonly Color AmbSky    = new Color(0.20f, 0.12f, 0.24f);
        private static readonly Color AmbEquator = new Color(0.14f, 0.09f, 0.17f);
        private static readonly Color AmbGround  = new Color(0.09f, 0.06f, 0.10f);
        private static readonly Color AmbP3Tint  = new Color(0.06f, -0.02f, -0.03f); // P3 지면 붉은 가산

        // 3) 혈월
        private static readonly Color MoonBaseHdr = new Color(2.2f, 0.5f, 0.4f);  // 빌더의 Mat_BloodMoon HDR
        private const float MoonGlowBaseIntensity = 5f;
        private const float MoonPulsePeriod = 4f;

        // 4) 기둥 룬
        private static readonly Color RuneIdle = new Color(0.55f, 0.14f, 0.10f);  // 은은한 핏빛 룬
        private static readonly Color RuneSeal = new Color(0.20f, 1.15f, 0.35f);  // 전멸기: 초록 강조("여기 숨어라")

        // 5) 조명 무드 (페이즈별 Directional Light 타겟)
        private static readonly Color SunP1 = new Color(0.95f, 0.78f, 0.72f);   // 중립 월광
        private static readonly Color SunP3 = new Color(0.98f, 0.42f, 0.34f);   // 붉은 월광
        private const float SunIntensityP1 = 0.62f;
        private const float SunIntensityP3 = 0.50f;
        private const float PointBaseIntensity = 45f;
        private const float FlickerAmp = 0.12f;

        // 6) 바닥 브리딩
        private static readonly Color FloorBase  = new Color(1.0f, 0.82f, 0.80f); // 빌더 Mat_ArenaFloor 기본
        private static readonly Color FloorBreath = new Color(0.78f, 0.66f, 0.82f); // 채도 낮은 자주
        private static readonly Color FloorSeal  = new Color(1.25f, 0.42f, 0.40f);  // 전멸기 붉은 고동
        private const float FloorPeriod = 8f;

        // ─────────────── 캐시 ───────────────

        private BossGameViewer _viewer;

        private Renderer _floor;
        private MaterialPropertyBlock _floorMpb;

        private Renderer _moon;
        private MaterialPropertyBlock _moonMpb;
        private Light _moonGlow;

        private readonly List<Renderer> _pillars = new List<Renderer>();
        private readonly List<MaterialPropertyBlock> _pillarMpb = new List<MaterialPropertyBlock>();
        private float[] _pillarPhase;

        private Light[] _pointLights;
        private float[] _pointBase;
        private float[] _pointSeed;

        private Light _directional;

        private Transform _cam;

        // 셰이더 프로퍼티 ID
        private static readonly int IdBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int IdColor     = Shader.PropertyToID("_Color");
        private static readonly int IdEmission  = Shader.PropertyToID("_EmissionColor");

        // 부드러운 상태 블렌드
        private float _phaseF;      // 0..2 (스무딩)
        private float _sealBlend;   // 0..1 (전멸기 진행)

        private bool _floorUsesBaseColor;
        private bool _moonUsesBaseColor;

        // ─────────────── 초기화 ───────────────

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
        }

        private void Start()
        {
            _viewer = Object.FindFirstObjectByType<BossGameViewer>();
            _cam = Camera.main != null ? Camera.main.transform : null;

            var missing = new List<string>();
            Transform arena = FindRoot("Arena_Generated");

            ResolveFloor(arena, missing);
            ResolveMoon(arena, missing);
            ResolvePillars(arena, missing);
            ResolveLights(arena, missing);

            // 안개/앰비언트 정적 세팅(밀도는 불변, 색만 Update 에서 페이즈 lerp)
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = FogDensity;
            RenderSettings.fogColor = FogBase;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientLight = AmbEquator;   // Flat 모드 폴백용(무해)

            BuildEmberParticles();
            BuildDustParticles();

            if (missing.Count > 0)
                Debug.LogWarning("[EnvironmentDirector] 일부 오브젝트를 찾지 못해 해당 연출은 생략합니다: "
                                 + string.Join(", ", missing));
        }

        private static Transform FindRoot(string name)
        {
            var go = GameObject.Find(name);
            return go != null ? go.transform : null;
        }

        /// <summary>arena 하위 우선 재귀 탐색, 실패 시 전역 Find 폴백.</summary>
        private static Transform Locate(Transform arena, string name)
        {
            if (arena != null)
            {
                var t = FindDeep(arena, name);
                if (t != null) return t;
            }
            var go = GameObject.Find(name);
            return go != null ? go.transform : null;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var f = FindDeep(root.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        private void ResolveFloor(Transform arena, List<string> missing)
        {
            var t = Locate(arena, "Arena_Floor");
            if (t == null) { missing.Add("Arena_Floor"); return; }
            _floor = t.GetComponent<Renderer>();
            if (_floor == null) { missing.Add("Arena_Floor(Renderer)"); return; }
            _floorMpb = new MaterialPropertyBlock();
            _floorUsesBaseColor = _floor.sharedMaterial != null && _floor.sharedMaterial.HasProperty(IdBaseColor);
        }

        private void ResolveMoon(Transform arena, List<string> missing)
        {
            var t = Locate(arena, "BloodMoonSphere");
            if (t != null)
            {
                _moon = t.GetComponent<Renderer>();
                if (_moon != null)
                {
                    _moonMpb = new MaterialPropertyBlock();
                    _moonUsesBaseColor = _moon.sharedMaterial != null && _moon.sharedMaterial.HasProperty(IdBaseColor);
                }
            }
            else missing.Add("BloodMoonSphere");

            var g = Locate(arena, "BloodMoonGlow");
            if (g != null) _moonGlow = g.GetComponent<Light>();
            else missing.Add("BloodMoonGlow");
        }

        private void ResolvePillars(Transform arena, List<string> missing)
        {
            var enabledMats = new HashSet<Material>();
            for (int i = 0; i < 4; i++)
            {
                var t = Locate(arena, "GameplayPillar_" + i);
                if (t == null) { missing.Add("GameplayPillar_" + i); continue; }
                var r = t.GetComponentInChildren<Renderer>();
                if (r == null) continue;

                // URP Lit 이미시브가 MPB _EmissionColor 로 발광하려면 _EMISSION 키워드가 필요.
                // 공유 머티리얼에 1회만 켠다(다른 사용처는 MPB 기본 이미션=검정이라 무영향).
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !enabledMats.Add(m)) continue;
                    if (m.HasProperty(IdEmission)) m.EnableKeyword("_EMISSION");
                }

                _pillars.Add(r);
                _pillarMpb.Add(new MaterialPropertyBlock());
            }
            _pillarPhase = new float[_pillars.Count];
            for (int i = 0; i < _pillarPhase.Length; i++)
                _pillarPhase[i] = i * (Mathf.PI * 2f / Mathf.Max(1, _pillars.Count));  // 위상 어긋남
        }

        private void ResolveLights(Transform arena, List<string> missing)
        {
            var pl = new List<Light>();
            var pb = new List<float>();
            for (int i = 0; i < 4; i++)
            {
                var t = Locate(arena, "PointLight_" + i);
                if (t == null) { missing.Add("PointLight_" + i); continue; }
                var l = t.GetComponent<Light>();
                if (l == null) continue;
                pl.Add(l);
                pb.Add(l.intensity > 0f ? l.intensity : PointBaseIntensity);
            }
            _pointLights = pl.ToArray();
            _pointBase = pb.ToArray();
            _pointSeed = new float[_pointLights.Length];
            for (int i = 0; i < _pointSeed.Length; i++) _pointSeed[i] = i * 13.37f;

            var d = Locate(arena, "Directional Light");
            if (d == null)
            {
                // 이름 폴백: 타입으로 스캔
                foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { d = l.transform; break; }
            }
            if (d != null) _directional = d.GetComponent<Light>();
            else missing.Add("Directional Light");
        }

        // ─────────────── 상태 조회(읽기 전용) ───────────────

        private BossData Boss()
        {
            var snap = _viewer != null ? _viewer.LatestSnapshot : null;
            return snap != null ? snap.boss : null;
        }

        private int RawPhase()
        {
            var b = Boss();
            return b != null ? Mathf.Clamp(b.phase, 0, 2) : 0;
        }

        private bool SealActive()
        {
            var b = Boss();
            if (b == null) return false;
            return b.active_pattern == 9 || (b.active_mode != null && b.active_mode == "seal");
        }

        // ─────────────── 프레임 연출 ───────────────

        private void Update()
        {
            float dt = Time.deltaTime;
            float t = Time.time;

            // 상태 스무딩
            _phaseF = Mathf.MoveTowards(_phaseF, RawPhase(), dt * 0.8f);
            _sealBlend = Mathf.MoveTowards(_sealBlend, SealActive() ? 1f : 0f, dt * 2.2f);
            float p3 = Mathf.Clamp01(_phaseF - 1f);   // P2→P3 사이 0..1

            UpdateFogAmbient(p3);
            UpdateMoon(t, p3);
            UpdatePillars(t);
            UpdatePointLights(t);
            UpdateDirectional(p3);
            UpdateFloor(t);
        }

        private void UpdateFogAmbient(float p3)
        {
            RenderSettings.fogColor = Color.Lerp(FogBase, FogP3, p3);

            RenderSettings.ambientSkyColor = AmbSky;
            RenderSettings.ambientEquatorColor = AmbEquator + AmbP3Tint * (0.6f * p3);
            RenderSettings.ambientGroundColor = AmbGround + AmbP3Tint * p3;
        }

        private void UpdateMoon(float t, float p3)
        {
            // 4s 주기 펄스, P3 에서 1.5배
            float pulse = 0.85f + 0.30f * (0.5f + 0.5f * Mathf.Sin(t * (Mathf.PI * 2f / MoonPulsePeriod)));
            float scale = pulse * Mathf.Lerp(1f, 1.5f, p3);

            if (_moon != null)
            {
                _moon.GetPropertyBlock(_moonMpb);
                Color c = MoonBaseHdr * scale;
                c.a = 1f;
                _moonMpb.SetColor(_moonUsesBaseColor ? IdBaseColor : IdColor, c);
                _moon.SetPropertyBlock(_moonMpb);
            }
            if (_moonGlow != null)
                _moonGlow.intensity = MoonGlowBaseIntensity * scale;
        }

        private void UpdatePillars(float t)
        {
            if (_pillars.Count == 0) return;
            // 전멸기 시 초록 강조(강도 상승), 평시 은은한 핏빛 룬
            for (int i = 0; i < _pillars.Count; i++)
            {
                var r = _pillars[i];
                if (r == null) continue;

                float ph = _pillarPhase.Length > i ? _pillarPhase[i] : 0f;
                float breathe = 0.5f + 0.5f * Mathf.Sin(t * 1.4f + ph);   // 0..1
                float idleMag = Mathf.Lerp(0.25f, 0.7f, breathe);
                float sealMag = Mathf.Lerp(0.9f, 1.8f, breathe);

                Color emis = Color.Lerp(RuneIdle * idleMag, RuneSeal * sealMag, _sealBlend);

                var mpb = _pillarMpb[i];
                r.GetPropertyBlock(mpb);
                mpb.SetColor(IdEmission, emis);
                r.SetPropertyBlock(mpb);
            }
        }

        private void UpdatePointLights(float t)
        {
            if (_pointLights == null) return;
            for (int i = 0; i < _pointLights.Length; i++)
            {
                var l = _pointLights[i];
                if (l == null) continue;
                float s = _pointSeed[i];
                // sin 노이즈 합성 → 횃불 플리커(진폭 12%)
                float flick = Mathf.Sin(t * 11.3f + s) * 0.6f
                            + Mathf.Sin(t * 19.7f + s * 1.7f) * 0.3f
                            + Mathf.Sin(t * 4.1f + s * 0.5f) * 0.1f;
                l.intensity = _pointBase[i] * (1f + FlickerAmp * flick);
            }
        }

        private void UpdateDirectional(float p3)
        {
            if (_directional == null) return;
            _directional.color = Color.Lerp(SunP1, SunP3, p3);
            _directional.intensity = Mathf.Lerp(SunIntensityP1, SunIntensityP3, p3);
        }

        private void UpdateFloor(float t)
        {
            if (_floor == null) return;

            // 8s 주기 채도 낮은 자주 브리딩
            float breathe = 0.5f + 0.5f * Mathf.Sin(t * (Mathf.PI * 2f / FloorPeriod));
            Color baseCol = Color.Lerp(FloorBase, FloorBreath, breathe * 0.5f);

            // 전멸기 시전 중엔 붉게 고동(빠른 펄스)
            if (_sealBlend > 0.001f)
            {
                float pound = 0.5f + 0.5f * Mathf.Sin(t * 6.0f);
                Color sealCol = Color.Lerp(FloorBase, FloorSeal, pound);
                baseCol = Color.Lerp(baseCol, sealCol, _sealBlend);
            }

            _floor.GetPropertyBlock(_floorMpb);
            _floorMpb.SetColor(_floorUsesBaseColor ? IdBaseColor : IdColor, baseCol);
            _floor.SetPropertyBlock(_floorMpb);
        }

        // ─────────────── 앰비언트 파티클(2계층) ───────────────

        /// <summary>(a) 느리게 떠오르는 붉은 불씨 — 아레나 전역, rate 6, 수명 6s, 약한 HDR.</summary>
        private void BuildEmberParticles()
        {
            var go = new GameObject("Ambient_Embers");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(10f, 1f, 10f);   // 아레나 중심(ContinuousToWorld(10,10))

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = 6f;
            main.startSpeed = 0.15f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
            main.startColor = new Color(1.1f, 0.42f, 0.16f, 0.85f);   // 약한 HDR 불씨
            main.gravityModifier = -0.02f;                            // 살짝 떠오름
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 80;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 6f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(30f, 1f, 30f);                  // 아레나 전역

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.y = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);     // 완만한 상승
            vel.x = new ParticleSystem.MinMaxCurve(-0.12f, 0.12f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.12f, 0.12f);

            ApplyFadeInOut(ps);

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.alignment = ParticleSystemRenderSpace.View;
            r.sortMode = ParticleSystemSortMode.None;
            r.material = ParticleMaterial(glow: true);

            ps.Play();
        }

        /// <summary>(b) 미세 먼지 — 회색, 카메라 주변(카메라에 부모 붙임, 로컬 시뮬).</summary>
        private void BuildDustParticles()
        {
            var go = new GameObject("Ambient_Dust");
            if (_cam != null)
            {
                go.transform.SetParent(_cam, false);
                go.transform.localPosition = new Vector3(0f, 0f, 10f);  // 카메라 앞
            }
            else
            {
                go.transform.SetParent(transform, false);
                go.transform.position = new Vector3(10f, 4f, 10f);
            }

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = 5f;
            main.startSpeed = 0.05f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.05f);
            main.startColor = new Color(0.55f, 0.53f, 0.58f, 0.35f);   // 미세 회색 먼지(비 HDR)
            main.gravityModifier = 0.005f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 60;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 8f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(14f, 10f, 8f);                   // 카메라 주변 볼륨

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;
            vel.x = new ParticleSystem.MinMaxCurve(-0.08f, 0.08f);
            vel.y = new ParticleSystem.MinMaxCurve(-0.04f, 0.06f);

            ApplyFadeInOut(ps);

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.alignment = ParticleSystemRenderSpace.View;
            r.sortMode = ParticleSystemSortMode.None;
            r.material = ParticleMaterial(glow: false);

            ps.Play();
        }

        /// <summary>루프 앰비언트용 알파 페이드 인/아웃.</summary>
        private static void ApplyFadeInOut(ParticleSystem ps)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f),
                    new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f)
                });
            col.color = g;
        }

        // 파티클 머티리얼 캐시(도형별 1개면 충분 — 색은 startColor 로 준다)
        private static Material _emberMat;
        private static Material _dustMat;

        private static Material ParticleMaterial(bool glow)
        {
            if (glow && _emberMat != null) return _emberMat;
            if (!glow && _dustMat != null) return _dustMat;

            var sh = Shader.Find("BossRaid/Particle");
            if (sh == null) sh = Shader.Find("Sprites/Default");   // 폴백
            var m = new Material(sh) { name = glow ? "EnvEmberMat" : "EnvDustMat" };
            if (m.HasProperty("_Shape")) m.SetInt("_Shape", 0);    // Circle
            if (m.HasProperty("_Glow")) m.SetFloat("_Glow", glow ? 1.2f : 0.0f);

            if (glow) _emberMat = m; else _dustMat = m;
            return m;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
