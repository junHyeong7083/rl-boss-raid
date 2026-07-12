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
    ///   Projectile(from,to,color,duration,size)     — 실제로 "날아가는" 발광 투사체(비주얼 생성만; 이동은 caller 코루틴)
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

        /// <summary>
        /// 실제로 "날아가는" 발광 투사체(비주얼 생성만 — 이동은 caller 코루틴이 transform 을 매 프레임 갱신).
        /// simulationSpace=World + 연속 방출로, 헤드가 이동하면 방출된 파티클이 제자리에 남아 자연스러운 트레일이 됨.
        /// caller 가 lifecycle 을 관리하므로 stopAction=Destroy 를 지정하지 않는다(RaidVFXManager 풀링과 호환).
        /// from 위치에서 즉시 Play. 재사용 시 caller 가 startColor/startSize/position 을 재설정 후 Clear/Play.
        /// duration 은 트레일 잔상 길이(startLifetime)에 반영. to 는 헤드 진행 방향 정렬(비주얼 힌트)에만 사용.
        /// </summary>
        public static GameObject Projectile(Vector3 from, Vector3 to, Color color, float duration, float size)
        {
            var ps = NewSystem("VFX_Projectile", from, SHAPE_CIRCLE, out var go);

            Vector3 dir = to - from;
            if (dir.sqrMagnitude > 1e-6f)
                go.transform.rotation = Quaternion.LookRotation(dir.normalized);

            var main = ps.main;
            main.duration = 1f;
            main.loop = true;                              // 비행 동안 계속 방출(트레일 유지) — caller 가 정지
            main.playOnAwake = false;
            main.startLifetime = Mathf.Clamp(duration * 0.9f, 0.12f, 0.35f);  // 트레일 잔상 길이
            main.startSpeed = 0.25f;                       // 거의 정지 → 헤드 이동으로 트레일 형성
            main.startSize = Mathf.Max(0.05f, size);
            main.startColor = Hdr(color, 2.6f);
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;   // 핵심: 방출 후 제자리 → 트레일
            main.maxParticles = 220;
            // stopAction 미지정 (풀링 lifecycle 은 caller 소유)

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 150f;                  // 촘촘한 헤드/트레일

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.06f;

            ApplyFadeAndShrink(ps, 0.2f, 0.1f);            // 뒤로 갈수록 흐려지고 작아짐

            ps.Play();
            return go;
        }

        /// <summary>위로 솟구치는 원뿔 분수(도발 "나를 봐라" 강조). 살짝 중력으로 되떨어짐.</summary>
        public static GameObject Fountain(Vector3 pos, Color color)
        {
            var ps = NewSystem("VFX_Fountain", pos, SHAPE_STAR, out var go);

            var main = ps.main;
            main.duration = 0.15f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.3f);
            main.startColor = Hdr(color, 2.4f);
            main.gravityModifier = 0.6f;                   // 솟았다가 살짝 낙하
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 48;
            main.stopAction = ParticleSystemStopAction.Destroy;

            SetBurstCount(ps, 22);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 16f;
            shape.radius = 0.15f;
            shape.rotation = new Vector3(-90f, 0f, 0f);    // 원뿔 축을 +Y(위)로

            ApplyFadeAndShrink(ps, 0.6f, 0.4f);

            ps.Play();
            return go;
        }

        /// <summary>대상 상공에서 아래로 떨어지는 스파클(힐 강림). 중력 양수로 낙하.</summary>
        public static GameObject SparkleFall(Vector3 pos, Color color)
        {
            var ps = NewSystem("VFX_SparkleFall", pos + Vector3.up * 2.4f, SHAPE_STAR, out var go);

            var main = ps.main;
            main.duration = 0.15f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.95f);
            main.startSpeed = 0.2f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.28f);
            main.startColor = Hdr(color, 2.2f);
            main.gravityModifier = 0.9f;                   // 아래로 떨어짐
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 48;
            main.stopAction = ParticleSystemStopAction.Destroy;

            SetBurstCount(ps, 20);

            // 대상 상공의 원반에서 흩뿌리듯 방출.
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.85f;
            shape.radiusThickness = 1f;
            shape.rotation = new Vector3(90f, 0f, 0f);     // XZ 평면(수평 원반)

            ApplyFadeAndShrink(ps, 0.6f, 0.5f);

            ps.Play();
            return go;
        }

        /// <summary>유닛을 감싸는 반구형 실드 플래시(가드). 링 파티클이 돔처럼 바깥으로 퍼짐.</summary>
        public static GameObject ShieldFlash(Vector3 pos, Color color)
        {
            var ps = NewSystem("VFX_ShieldFlash", pos + Vector3.up * 0.6f, SHAPE_RING, out var go);

            var main = ps.main;
            main.duration = 0.1f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.32f, 0.45f);
            main.startSpeed = 3.6f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.28f, 0.55f);
            main.startColor = Hdr(color, 2.7f);
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 72;
            main.stopAction = ParticleSystemStopAction.Destroy;

            SetBurstCount(ps, 44);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;   // 반구 껍질 → 돔
            shape.radius = 0.6f;
            shape.radiusThickness = 0.2f;

            ApplyFadeAndShrink(ps, 0.4f, 0.4f);

            ps.Play();
            return go;
        }

        /// <summary>유닛 주위를 회전하며 상승하는 나선 오라(버프). 궤도 속도 + 음의 중력으로 솟구침.</summary>
        public static GameObject Spiral(Vector3 pos, Color color)
        {
            var ps = NewSystem("VFX_Spiral", pos, SHAPE_DIAMOND, out var go);

            var main = ps.main;
            main.duration = 0.4f;                          // 0.4s 동안 계속 방출 → 상승 나선
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.8f);
            main.startSpeed = 0.15f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.14f, 0.3f);
            main.startColor = Hdr(color, 2.3f);
            main.gravityModifier = -0.45f;                 // 상승
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 64;
            main.stopAction = ParticleSystemStopAction.Destroy;

            // 단발 버스트가 아니라 시간에 걸쳐 방출(나선이 위로 이어지게).
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 55f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.7f;
            shape.radiusThickness = 0.15f;                 // 링 둘레에서
            shape.rotation = new Vector3(90f, 0f, 0f);
            shape.arc = 360f;

            // 궤도 속도 → 수직축 회전(나선).
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.orbitalY = new ParticleSystem.MinMaxCurve(3.2f);

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
