using System.Collections.Generic;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 이벤트 → 이펙트 디스패처. 스냅샷의 events / 텔레그래프 스텝 발동을 감지해
    /// ProceduralVFX 팩토리 + 타격감(HitStop/Shake/Flash)을 배선한다.
    ///
    /// 소비하는 B1(병행 구현) 계약 — 신뢰하고 호출:
    ///   BossGameViewer.LatestSnapshot                → BossSnapshot (최근 적용 스냅샷)
    ///   BossGameViewer.OnSnapshotApplied             → event Action&lt;BossSnapshot&gt; (스냅샷 적용 직후)
    ///   BossGameViewer.OnPillarDestroyed (static)    → event Action&lt;Vector3&gt; (기둥 파괴 지점)
    ///   BossData.counter_window (int)                → 카운터 창 남은 턴(>0 이면 파란 오라)
    ///   TelegraphData.anim (string) / step_index(int)→ 스텝 애니 키 / 스텝 인덱스
    /// (events 는 Python 스냅샷에서 딕셔너리로 직렬화 → BossJsonParser 가 EventData.type/uid 로 파싱.
    ///  cinematic_end 성공 여부처럼 여분 필드가 필요한 건 동반 이벤트(seal_success/fail)로 판별.)
    /// </summary>
    [DisallowMultipleComponent]
    public class RaidVFXManager : MonoBehaviour
    {
        [Header("Scene Refs")]
        [Tooltip("비워두면 씬에서 자동 탐색.")]
        [SerializeField] private BossGameViewer viewer;
        [Tooltip("붉은 광역 플래시용. 비어 있으면 자동 탐색.")]
        [SerializeField] private BossPostFX postFX;

        // ─── 팔레트(HDR 전 원색) ───
        private static readonly Color CBlue    = new Color(0.30f, 0.62f, 1.00f);
        private static readonly Color CPurple  = new Color(0.72f, 0.32f, 1.00f);
        private static readonly Color CRed      = new Color(1.00f, 0.16f, 0.16f);
        private static readonly Color CBrown    = new Color(0.55f, 0.40f, 0.25f);
        private static readonly Color CGray      = new Color(0.62f, 0.62f, 0.68f);
        private static readonly Color COrange    = new Color(1.00f, 0.55f, 0.16f);
        private static readonly Color CCrimson   = new Color(0.85f, 0.06f, 0.12f);
        private static readonly Color CGold      = new Color(1.00f, 0.82f, 0.25f);
        private static readonly Color CCyan      = new Color(0.25f, 0.95f, 0.90f);

        // ─── 텔레그래프 스텝 발동 감지 상태 ───
        // key = "pattern:step_index". turns_remaining<=1 일 때 무장(arm)하고,
        // 다음 스냅샷에서 사라지면 "발동"으로 간주해 임팩트를 낸다(1→0 전환 근사).
        private struct ArmedStep { public string anim; public Vector3 pos; public Vector3 a, b; public bool isLine; }
        private readonly Dictionary<string, ArmedStep> _armed = new Dictionary<string, ArmedStep>();

        // ─── 카운터 오라 상태 ───
        private Transform _auraHolder;   // 보스 위치를 매 프레임 추종하는 홀더
        private GameObject _counterAura; // 활성 오라 GO (없으면 null)

        // ─────────────── 라이프사이클 ───────────────

        private void Awake()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            if (postFX == null) postFX = FindFirstObjectByType<BossPostFX>();

            var holder = new GameObject("RaidVFX_AuraHolder");
            holder.transform.SetParent(transform, false);
            _auraHolder = holder.transform;
        }

        private void OnEnable()
        {
            if (viewer != null) viewer.OnSnapshotApplied += OnSnapshot;
            BossGameViewer.OnPillarDestroyed += OnPillarDestroyed;
        }

        private void OnDisable()
        {
            if (viewer != null) viewer.OnSnapshotApplied -= OnSnapshot;
            BossGameViewer.OnPillarDestroyed -= OnPillarDestroyed;
        }

        private void Update()
        {
            // 카운터 오라가 보스를 따라다니도록 홀더 위치 갱신.
            if (_auraHolder != null && viewer != null && viewer.TryGetBossPosition(out var bp))
                _auraHolder.position = bp;
        }

        // ─────────────── 스냅샷 처리 ───────────────

        private void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null) return;
            DispatchEvents(snap);
            DetectPatternSteps(snap);
            UpdateCounterAura(snap);
        }

        /// <summary>events 순회 → 기믹/피격 이펙트.</summary>
        private void DispatchEvents(BossSnapshot snap)
        {
            if (snap.events == null) return;

            Vector3 bossPos = BossPos();

            foreach (var ev in snap.events)
            {
                if (ev == null || string.IsNullOrEmpty(ev.type)) continue;
                switch (ev.type)
                {
                    case "guard_success":
                    {
                        // 탱커(가드한 유닛) 위치 파란 실드 파열.
                        Vector3 p = UnitPos(snap, ev.uid, bossPos);
                        ProceduralVFX.RingWave(p, CBlue, 2.2f, 0.35f);
                        ProceduralVFX.Burst(p, CBlue, 22, 5f, 0.35f, 0.4f);
                        HitStopManager.HitStop(0.1f);
                        LostArkCamera.ShakeCamera(0.3f, 0.2f);
                        break;
                    }
                    case "counter_success":
                    {
                        // 보스 위치 파란 대형 버스트 + 강 셰이크.
                        ProceduralVFX.Burst(bossPos, CBlue, 48, 9f, 0.5f, 0.55f);
                        ProceduralVFX.RingWave(bossPos, CBlue, 4f, 0.45f);
                        HitStopManager.HitStop(0.15f);
                        LostArkCamera.ShakeCamera(0.6f, 0.3f);
                        break;
                    }
                    case "rush_pillar_hit":
                    {
                        // 돌진-기둥 충돌: 보스 정지 지점에서 돌 파편.
                        ProceduralVFX.Debris(bossPos, CBrown);
                        HitStopManager.HitStop(0.12f);
                        LostArkCamera.ShakeCamera(0.7f, 0.3f);
                        break;
                    }
                    case "stagger_success":
                    {
                        ProceduralVFX.Burst(bossPos, CPurple, 40, 8f, 0.5f, 0.55f);
                        ProceduralVFX.RingWave(bossPos, CPurple, 3.5f, 0.45f);
                        HitStopManager.HitStop(0.12f);
                        LostArkCamera.ShakeCamera(0.5f, 0.3f);
                        break;
                    }
                    case "stagger_fail":
                    {
                        // 붉은 광역 플래시.
                        Flash(CRed, 0.5f);
                        ProceduralVFX.RingWave(bossPos, CRed, 9f, 0.5f);
                        LostArkCamera.ShakeCamera(0.6f, 0.35f);
                        break;
                    }
                    case "death":
                    {
                        // 유닛 소멸: 회색 느린 파티클.
                        Vector3 p = UnitPos(snap, ev.uid, bossPos);
                        ProceduralVFX.Burst(p, CGray, 18, 2.2f, 0.4f, 0.9f);
                        break;
                    }
                    case "player_skill_cast":
                    {
                        // 설치기 스킬(tx/ty = sim 좌표 발동 지점, radius = 스킬 반경).
                        FirePlayerSkill(ev, bossPos);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 설치기 스킬 시전 이펙트 (type=="player_skill_cast"). 로아식 "지점 발동" 가독성:
        ///   (a) 시전자(딜러)→지점 투사체 트레일   (skill2 는 하늘 낙하)
        ///   (b) 임팩트 지점에 실판정 반경 원 데칼 플래시(0.2s) — hit=true 골드/크리티컬 강조, 빗나감 옅게
        ///   (c) 임팩트 버스트 + 실반경 링
        /// 반경은 ev.radius(sim 단위)를 viewer.cellSize 로 월드 변환해 실제 판정과 일치시킨다.
        /// skill_id: "skill"(혈창 투척 r1.8) | "skill2"(혈월 낙하 r3.0). 데미지 숫자는 별도 damage 이벤트가 처리.
        /// </summary>
        private void FirePlayerSkill(EventData ev, Vector3 bossPos)
        {
            float cell = CellSize();
            Vector3 p = ToWorld(ev.tx, ev.ty);
            float r = (ev.radius > 0f ? ev.radius : 1.8f) * cell;   // sim → 월드 반경(실판정 일치)

            // ── 명중/크리티컬에 따른 임팩트 데칼 강조 배선 ──
            Color decalBase; Color decalOutline; float peakAlpha;
            if (ev.hit && ev.crit)  { decalBase = CGold; decalOutline = CGold * 3.2f;  peakAlpha = 0.9f; }
            else if (ev.hit)        { decalBase = CGold; decalOutline = CGold * 2.2f;  peakAlpha = 0.6f; }
            else                    { decalBase = CCyan; decalOutline = CCyan * 1.6f;  peakAlpha = 0.28f; } // 빗나감 옅게

            if (ev.skill_id == "skill2")
            {
                // 혈월 낙하: (a) 하늘에서 떨어지는 낙하 스트릭(y+3 → 지면).
                ProceduralVFX.Trail(p + Vector3.up * 3f, p, CCrimson);
                ProceduralVFX.Burst(p, CCrimson, 44, 8f, 0.5f, 0.55f);
                ProceduralVFX.RingWave(p, CCrimson, r, 0.45f);   // (c) 실반경 링
                ProceduralVFX.Debris(p, CBrown);
                LostArkCamera.ShakeCamera(0.35f, 0.25f);
            }
            else
            {
                // 혈창 투척: (a) 시전자(딜러)→지점 투사체 트레일 + 금색-진홍 중형 버스트.
                ProceduralVFX.Trail(DealerCastOrigin(p), p, CCrimson);
                ProceduralVFX.Burst(p, CGold, 26, 6f, 0.4f, 0.45f);
                ProceduralVFX.Burst(p, CCrimson, 16, 4.5f, 0.35f, 0.4f);
                ProceduralVFX.RingWave(p, CGold, r, 0.35f);       // (c) 실반경 링
            }

            // (b) 임팩트 실판정 반경 데칼 플래시(0.2s).
            FlashImpactDecal(p, r, decalBase, decalOutline, peakAlpha, 0.2f);

            // 보스 명중 강조: 스파크 + (크리티컬이면) 히트스톱/셰이크.
            if (ev.hit)
            {
                ProceduralVFX.Burst(bossPos + Vector3.up * 1.2f, CGold, 20, 5f, 0.3f, 0.35f);
                if (ev.crit)
                {
                    ProceduralVFX.Burst(p, CGold, 28, 7f, 0.4f, 0.4f);
                    HitStopManager.HitStop(0.09f);
                    LostArkCamera.ShakeCamera(0.3f, 0.2f);
                }
            }
        }

        /// <summary>혈창 투척의 투사체 시작점(딜러 손 높이). 딜러 미탐색 시 지점 상공으로 폴백.</summary>
        private Vector3 DealerCastOrigin(Vector3 fallbackPoint)
        {
            if (viewer != null && viewer.TryGetDealerTransform(out var t) && t != null)
                return t.position + Vector3.up * 1.0f;
            return fallbackPoint + Vector3.up * 1.5f;
        }

        // ─────────────── 임팩트 반경 데칼 플래시 ───────────────

        /// <summary>
        /// 임팩트 지점에 실판정 반경 크기의 원 데칼을 dur 초간 밝게 플래시(페이드아웃 후 자동 파괴).
        /// BossRaid/Telegraph 셰이더 circle(_Fill=1) 재활용. 셰이더 미포함 시 Sprites/Default 폴백.
        /// </summary>
        private void FlashImpactDecal(Vector3 pos, float worldRadius, Color baseCol, Color outlineCol,
            float peakAlpha, float dur)
        {
            var go = BuildCircleDecal(pos, worldRadius, baseCol, outlineCol, out var mat);
            StartCoroutine(ImpactDecalRoutine(go, mat, baseCol, outlineCol, peakAlpha, Mathf.Max(0.05f, dur)));
        }

        private System.Collections.IEnumerator ImpactDecalRoutine(GameObject go, Material mat,
            Color baseCol, Color outlineCol, float peakAlpha, float dur)
        {
            float t = 0f;
            while (go != null && t < dur)
            {
                float k = t / dur;
                float a = peakAlpha * (1f - k * k);   // 빠른 감쇠
                if (mat != null)
                {
                    Color b = baseCol; b.a = a;
                    Color o = outlineCol; o.a = a;
                    mat.SetColor("_Color", b);
                    mat.SetColor("_OutlineColor", o);
                }
                t += Time.deltaTime;
                yield return null;
            }
            if (go != null) Destroy(go);
        }

        /// <summary>바닥에 눕힌 원형 Quad 데칼 GO 생성(Telegraph 셰이더 circle, _Fill=1).</summary>
        private GameObject BuildCircleDecal(Vector3 pos, float worldRadius, Color baseCol, Color outlineCol,
            out Material mat)
        {
            var go = new GameObject("VFX_ImpactDecal");
            go.transform.SetParent(transform, false);
            float d = Mathf.Max(0.02f, worldRadius * 2f);
            go.transform.position = new Vector3(pos.x, 0.03f, pos.z);
            go.transform.localScale = new Vector3(d, d, d);

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = GroundCircleMesh();

            var sh = Shader.Find("BossRaid/Telegraph");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            mat = new Material(sh);
            if (sh != null && sh.name == "BossRaid/Telegraph")
            {
                mat.SetInt("_ShapeType", 0);       // circle
                mat.SetFloat("_Fill", 1f);
                mat.SetFloat("_Progress", 1f);
                mat.SetFloat("_Pulse", 0f);
                mat.SetFloat("_OutlineWidth", 0.10f);
                mat.SetFloat("_UnfilledAlpha", 0.8f);
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
            return go;
        }

        // 바닥(XZ)에 눕힌 1x1 Quad 메시(재사용 캐시). Telegraph 셰이더 UV 원과 호환.
        private static Mesh _groundCircleMesh;
        private static Mesh GroundCircleMesh()
        {
            if (_groundCircleMesh != null) return _groundCircleMesh;
            var mesh = new Mesh { name = "RaidVFX_GroundCircleQuad" };
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
            _groundCircleMesh = mesh;
            return mesh;
        }

        /// <summary>기둥 파괴 지점 돌 파편(정적 이벤트 구독).</summary>
        private void OnPillarDestroyed(Vector3 worldPos)
        {
            ProceduralVFX.Debris(worldPos, CBrown);
            LostArkCamera.ShakeCamera(0.45f, 0.25f);
        }

        // ─────────────── 텔레그래프 스텝 발동 → 패턴 임팩트 ───────────────

        private void DetectPatternSteps(BossSnapshot snap)
        {
            var current = new HashSet<string>();
            var armableNow = new Dictionary<string, ArmedStep>();

            if (snap.telegraphs != null)
            {
                foreach (var tg in snap.telegraphs)
                {
                    if (tg == null) continue;
                    string key = tg.pattern + ":" + tg.step_index;
                    current.Add(key);

                    if (tg.turns_remaining <= 1)
                    {
                        var step = BuildArmedStep(tg);
                        armableNow[key] = step;
                    }
                }
            }

            // 이전에 무장됐는데 이번 스냅샷에 사라진 키 = 발동됨.
            if (_armed.Count > 0)
            {
                var fired = new List<ArmedStep>();
                foreach (var kv in _armed)
                    if (!current.Contains(kv.Key)) fired.Add(kv.Value);
                foreach (var s in fired) FireStepImpact(s);
            }

            // 이번 스냅샷의 무장 세트로 교체(여전히 진행 중이면 유지, 갓 사라진 건 이미 발동 처리).
            _armed.Clear();
            foreach (var kv in armableNow) _armed[kv.Key] = kv.Value;
        }

        /// <summary>텔레그래프의 대표 위치/선분을 계산해 무장 스텝 생성.</summary>
        private ArmedStep BuildArmedStep(TelegraphData tg)
        {
            var s = new ArmedStep { anim = tg.anim, pos = BossPos(), isLine = false };
            if (tg.shapes != null && tg.shapes.Length > 0)
            {
                var sh = tg.shapes[0];
                if (sh != null)
                {
                    if (sh.kind == "line")
                    {
                        s.a = ToWorld(sh.ax, sh.ay);
                        s.b = ToWorld(sh.bx, sh.by);
                        s.pos = (s.a + s.b) * 0.5f;
                        s.isLine = true;
                    }
                    else
                    {
                        s.pos = ToWorld(sh.cx, sh.cy);
                    }
                }
            }
            return s;
        }

        /// <summary>anim 키별 패턴 스텝 임팩트.</summary>
        private void FireStepImpact(ArmedStep s)
        {
            switch (s.anim)
            {
                case "slash":                          // 주황 슬래시 스파크
                    ProceduralVFX.Burst(s.pos, COrange, 26, 7f, 0.4f, 0.4f);
                    LostArkCamera.ShakeCamera(0.2f, 0.15f);
                    break;
                case "smash":                          // 갈색 흙먼지 링
                case "shock":
                    ProceduralVFX.RingWave(s.pos, CBrown, 6f, 0.5f);
                    ProceduralVFX.Debris(s.pos, CBrown);
                    LostArkCamera.ShakeCamera(0.35f, 0.2f);
                    break;
                case "rush":                           // 붉은 트레일
                    if (s.isLine) ProceduralVFX.Trail(s.a, s.b, CRed);
                    else ProceduralVFX.Burst(s.pos, CRed, 30, 8f, 0.4f, 0.4f);
                    break;
                case "throw":                          // 낙하 폭발
                    ProceduralVFX.Burst(s.pos, COrange, 28, 6f, 0.45f, 0.45f);
                    ProceduralVFX.RingWave(s.pos, COrange, 3f, 0.4f);
                    ProceduralVFX.Debris(s.pos, CBrown);
                    break;
                case "spin":                           // 회전 스파크
                    ProceduralVFX.Burst(s.pos, COrange, 34, 7f, 0.35f, 0.45f);
                    break;
                case "roar":                           // 진홍 링 웨이브
                    ProceduralVFX.RingWave(s.pos, CCrimson, 9f, 0.6f);
                    LostArkCamera.ShakeCamera(0.4f, 0.3f);
                    break;
                case "brand":                          // 보라 폭발
                    ProceduralVFX.Burst(s.pos, CPurple, 32, 6f, 0.45f, 0.5f);
                    ProceduralVFX.RingWave(s.pos, CPurple, 3.5f, 0.45f);
                    break;
                // counter_glow / lift / blood_moon 은 카운터오라·시네마틱에서 별도 처리.
            }
        }

        // ─────────────── 카운터 오라 ───────────────

        private void UpdateCounterAura(BossSnapshot snap)
        {
            bool windowOpen = snap.boss != null && snap.boss.counter_window > 0;

            if (windowOpen && _counterAura == null)
            {
                if (viewer != null && viewer.TryGetBossPosition(out var bp))
                    _auraHolder.position = bp;
                _counterAura = ProceduralVFX.Aura(_auraHolder, CBlue);
            }
            else if (!windowOpen && _counterAura != null)
            {
                Destroy(_counterAura);
                _counterAura = null;
            }
        }

        // ─────────────── 위치 헬퍼 ───────────────

        private Vector3 BossPos()
        {
            if (viewer != null && viewer.TryGetBossPosition(out var p)) return p;
            return Vector3.zero;
        }

        /// <summary>uid 유닛의 월드 위치(스냅샷 sim 좌표 → 월드). 못 찾으면 fallback.</summary>
        private Vector3 UnitPos(BossSnapshot snap, int uid, Vector3 fallback)
        {
            if (snap.units != null)
            {
                foreach (var u in snap.units)
                    if (u != null && u.uid == uid) return ToWorld(u.x, u.y);
            }
            return fallback;
        }

        private Vector3 ToWorld(float x, float y)
            => viewer != null ? viewer.ContinuousToWorld(x, y) : new Vector3(x, 0f, y);

        /// <summary>sim 단위 → 월드 단위 스케일(반경 변환). viewer 없으면 1.</summary>
        private float CellSize() => viewer != null ? viewer.cellSize : 1f;

        private void Flash(Color c, float dur)
        {
            if (postFX == null) postFX = FindFirstObjectByType<BossPostFX>();
            if (postFX != null) postFX.FlashScreen(c, dur);
        }
    }
}
