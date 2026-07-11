using System.Collections.Generic;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 절차적 파티클 VFX 팩토리 (에셋 불요).
    /// 코드만으로 ParticleSystem 을 구성해 순간 이펙트를 생성한다.
    /// - 머티리얼: "BossRaid/Particle" 셰이더(HDR 발광 + UV 도형). 못 찾으면 "Sprites/Default" 폴백.
    /// - 색은 HDR(강도 2~3)로 부풀려 URP Bloom 에 강하게 반응시킨다.
    /// - 렌더러는 빌보드. 풀링 없이 완료 시 자동 파괴(main.stopAction = Destroy)로 단순 정리.
    /// - ParticleSystem 모듈은 값 타입(struct)이므로 반드시 "var m = ps.main; m.x = ...;" 패턴 사용.
    ///
    /// 제공 팩토리:
    ///   Burst(pos,color,count,speed,size,lifetime) — 방사형 스파크/폭발
    ///   RingWave(pos,color,radius,duration)         — 확장 링 웨이브
    ///   Debris(pos,baseColor)                       — 중력 있는 돌 파편
    ///   Aura(parent,color)                          — 루프 오라(반환 GO 를 Destroy 해 정지)
    ///   Trail(from,to,color)                        — 선분 트레일 스트릭
    /// </summary>
    public static class ProceduralVFX
    {
        // 파티클 셰이더의 _Shape 값(BossParticle.shader): 0=Circle 1=Star 2=Diamond 3=Ring
        private const int SHAPE_CIRCLE = 0;
        private const int SHAPE_STAR = 1;
        private const int SHAPE_DIAMOND = 2;
        private const int SHAPE_RING = 3;

        // _Shape 별 머티리얼 캐시(공유). 색은 파티클 startColor(정점 색)로 주므로 도형당 1개면 충분.
        private static readonly Dictionary<int, Material> _matCache = new Dictionary<int, Material>();

        // ─────────────── 공개 팩토리 ───────────────

        /// <summary>중심에서 방사되는 스파크/폭발 버스트.</summary>
        public static GameObject Burst(Vector3 pos, Color color, int count, float speed, float size, float lifetime)
        {
            var ps = NewSystem("VFX_Burst", pos, SHAPE_CIRCLE, out var go);

            var main = ps.main;
            main.duration = 0.15f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = lifetime;
            main.startSpeed = speed;
            main.startSize = size;
            main.startColor = Hdr(color, 2.5f);
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = Mathf.Max(count, 64);
            main.stopAction = ParticleSystemStopAction.Destroy;

            SetBurstCount(ps, count);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.15f;

            ApplyFadeAndShrink(ps, 0.55f, 0.2f);

            ps.Play();
            return go;
        }

        /// <summary>중심에서 바깥으로 퍼지는 링 웨이브(충격파/포효).</summary>
        public static GameObject RingWave(Vector3 pos, Color color, float radius, float duration)
        {
            var ps = NewSystem("VFX_RingWave", pos, SHAPE_CIRCLE, out var go);
            duration = Mathf.Max(0.05f, duration);

            var main = ps.main;
            main.duration = 0.1f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = duration;
            main.startSpeed = radius / duration;          // duration 안에 반경까지 도달
            main.startSize = Mathf.Max(0.12f, radius * 0.12f);
            main.startColor = Hdr(color, 2.5f);
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 96;
            main.stopAction = ParticleSystemStopAction.Destroy;

            SetBurstCount(ps, 56);

            // Circle 쉐이프 = 원 둘레에서 방사(속도 방향이 바깥). 링이 확장.
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.3f;
            shape.radiusThickness = 1f;
            shape.arc = 360f;
            shape.rotation = new Vector3(90f, 0f, 0f);    // 바닥 평면(XZ)에 눕힘

            ApplyFadeAndShrink(ps, 0.4f, 0.6f);

            ps.Play();
            return go;
        }

        /// <summary>중력을 받아 튀어오르고 떨어지는 돌 파편.</summary>
        public static GameObject Debris(Vector3 pos, Color baseColor)
        {
            var ps = NewSystem("VFX_Debris", pos, SHAPE_DIAMOND, out var go);

            var main = ps.main;
            main.duration = 0.15f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.38f);
            main.startColor = Hdr(baseColor, 1.4f);       // 파편은 발광 약하게
            main.gravityModifier = 1.6f;                  // 중력 낙하
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 48;
            main.stopAction = ParticleSystemStopAction.Destroy;

            SetBurstCount(ps, 22);

            // 위쪽 반구로 튀어오르게
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.25f;

            // 텀블링 회전
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-180f * Mathf.Deg2Rad, 180f * Mathf.Deg2Rad);

            ApplyFadeAndShrink(ps, 0.7f, 0.7f);

            ps.Play();
            return go;
        }

        /// <summary>parent 에 붙는 루프 오라. 반환된 GO 를 Destroy 하면 정지.</summary>
        public static GameObject Aura(Transform parent, Color color)
        {
            var ps = NewSystem("VFX_Aura", parent != null ? parent.position : Vector3.zero, SHAPE_CIRCLE, out var go);
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
                go.transform.localPosition = Vector3.zero;
            }

            var main = ps.main;
            main.duration = 1.2f;
            main.loop = true;
            main.playOnAwake = true;
            main.startLifetime = 1.2f;
            main.startSpeed = 0.4f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.2f, 0.42f);
            main.startColor = Hdr(color, 2.0f);
            main.gravityModifier = -0.12f;                // 살짝 떠오름
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 96;
            // 루프이므로 stopAction 지정 안 함 (caller 가 Destroy)

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 18f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.7f;
            shape.radiusThickness = 0.4f;                 // 껍질 형태

            ApplyFadeInOut(ps);

            ps.Play();
            return go;
        }

        /// <summary>from → to 선분을 따라 흩뿌려지는 트레일 스트릭.</summary>
        public static GameObject Trail(Vector3 from, Vector3 to, Color color)
        {
            Vector3 dir = to - from;
            float len = dir.magnitude;
            Vector3 mid = (from + to) * 0.5f;

            var ps = NewSystem("VFX_Trail", mid, SHAPE_CIRCLE, out var go);
            if (len > 1e-4f)
                go.transform.rotation = Quaternion.FromToRotation(Vector3.right, dir / len);

            var main = ps.main;
            main.duration = 0.1f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.55f);
            main.startSpeed = 0.6f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.32f);
            main.startColor = Hdr(color, 2.5f);
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 140;
            main.stopAction = ParticleSystemStopAction.Destroy;

            int count = Mathf.Clamp(Mathf.RoundToInt(len * 8f), 24, 140);
            SetBurstCount(ps, count);

            // Edge 쉐이프 = 로컬 X축 선분(길이 2*radius)을 따라 방출
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.SingleSidedEdge;
            shape.radius = Mathf.Max(0.1f, len * 0.5f);

            ApplyFadeAndShrink(ps, 0.5f, 0.4f);

            ps.Play();
            return go;
        }

        // ─────────────── 내부 헬퍼 ───────────────

        /// <summary>새 GameObject + ParticleSystem 생성 + 렌더러(빌보드/머티리얼) 세팅.</summary>
        private static ParticleSystem NewSystem(string name, Vector3 pos, int shapeIndex, out GameObject go)
        {
            go = new GameObject(name);
            go.transform.position = pos;

            var ps = go.AddComponent<ParticleSystem>();

            // AddComponent 직후엔 playOnAwake 기본값(true)으로 이미 재생 중이라
            // duration 설정이 거부된다("Setting the duration while system is still playing").
            // 완전 정지 후 각 팩토리가 구성하고 마지막에 Play() 한다.
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            // 기본 방출을 즉시 끄고(스트레이 파티클 방지) 이후 각 팩토리가 재구성.
            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.alignment = ParticleSystemRenderSpace.View;
            r.sortMode = ParticleSystemSortMode.None;
            r.material = MaterialFor(shapeIndex);

            return ps;
        }

        /// <summary>0초 시점 단발 버스트 count 설정.</summary>
        private static void SetBurstCount(ParticleSystem ps, int count)
        {
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            short c = (short)Mathf.Clamp(count, 1, 10000);
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, c) });
        }

        /// <summary>수명 동안 알파 페이드아웃 + 크기 축소.</summary>
        private static void ApplyFadeAndShrink(ParticleSystem ps, float holdFrac, float endSize)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, Mathf.Clamp01(holdFrac)), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            var curve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, endSize));
            sol.size = new ParticleSystem.MinMaxCurve(1f, curve);
        }

        /// <summary>루프 오라용 알파 페이드 인/아웃.</summary>
        private static void ApplyFadeInOut(ParticleSystem ps)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
        }

        /// <summary>_Shape 별 머티리얼 조회/생성(캐시). 셰이더 폴백 처리 포함.</summary>
        private static Material MaterialFor(int shapeIndex)
        {
            if (_matCache.TryGetValue(shapeIndex, out var m) && m != null) return m;

            var sh = Shader.Find("BossRaid/Particle");
            if (sh == null) sh = Shader.Find("Sprites/Default");   // 폴백
            m = new Material(sh) { name = $"ProceduralVFX_Mat_{shapeIndex}" };
            if (m.HasProperty("_Shape")) m.SetInt("_Shape", shapeIndex);
            if (m.HasProperty("_Glow")) m.SetFloat("_Glow", 1.6f);  // 색 HDR + 이 값으로 순 발광 ~3
            _matCache[shapeIndex] = m;
            return m;
        }

        /// <summary>RGB 를 intensity 배 부풀린 HDR 색(알파 유지) → Bloom 반응.</summary>
        private static Color Hdr(Color c, float intensity)
            => new Color(c.r * intensity, c.g * intensity, c.b * intensity, c.a);
    }
}
