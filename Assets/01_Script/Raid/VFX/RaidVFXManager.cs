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
        private static readonly Color CSilver    = new Color(0.90f, 0.94f, 1.00f);   // 평타 은백
        private static readonly Color CGreen     = new Color(0.35f, 1.00f, 0.45f);   // 힐 초록
        private static readonly Color CBuffAtk   = new Color(1.00f, 0.42f, 0.42f);   // 공버프 붉은기
        private static readonly Color CBuffShield = new Color(0.42f, 0.72f, 1.00f);  // 실드버프 파란기

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
            bool guardShown = false;   // guard_success 는 전 유닛 브로드캐스트 → 스냅샷당 1회(탱커 위치)만 연출

            foreach (var ev in snap.events)
            {
                if (ev == null || string.IsNullOrEmpty(ev.type)) continue;
                switch (ev.type)
                {
                    case "guard_success":
                    {
                        if (guardShown) break;
                        guardShown = true;
                        // 탱커 가드 딜타임 성공: 유닛을 감싸는 파란 반구형 실드 플래시(강하게).
                        // 이벤트 uid 는 브로드캐스트라 신뢰 불가 → 탱커(role==1) 유닛 위치로 특정.
                        int guardUid = ev.uid;
                        if (snap.units != null)
                            foreach (var u in snap.units)
                                if (u != null && u.role == 1) { guardUid = u.uid; break; }
                        Vector3 p = UnitPos(snap, guardUid, bossPos);
                        ProceduralVFX.ShieldFlash(p, CBlue);
                        ProceduralVFX.RingWave(p, CBlue, 2.8f, 0.42f);
                        ProceduralVFX.Burst(p + Vector3.up * 0.6f, CBlue, 24, 5.5f, 0.36f, 0.42f);
                        HitStopManager.HitStop(0.1f);
                        LostArkCamera.ShakeCamera(0.35f, 0.22f);
                        break;
                    }
                    case "taunt":
                    {
                        // 탱커 도발("나를 봐라"): 주황-금 링 확장 + 위로 솟는 분수.
                        Vector3 p = UnitPos(snap, ev.uid, bossPos);
                        ProceduralVFX.RingWave(p, COrange, 2.6f, 0.45f);
                        ProceduralVFX.RingWave(p, CGold, 1.5f, 0.3f);
                        ProceduralVFX.Fountain(p + Vector3.up * 0.2f, CGold);
                        break;
                    }
                    case "heal":
                    {
                        // 힐러 치유: 대상 유닛 상공에서 아래로 떨어지는 초록 스파클 + 부드러운 초록 링.
                        Vector3 p = UnitPos(snap, ev.target, UnitPos(snap, ev.uid, bossPos));
                        ProceduralVFX.SparkleFall(p, CGreen);
                        ProceduralVFX.RingWave(p, CGreen, 1.6f, 0.4f);
                        break;
                    }
                    case "buff":
                    {
                        // 서포터 버프: 대상 유닛 주위 회전 상승 나선(atk=붉은기 / shield=파란기).
                        Vector3 p = UnitPos(snap, ev.target, UnitPos(snap, ev.uid, bossPos));
                        Color c = ev.kind == "atk" ? CBuffAtk : CBuffShield;
                        ProceduralVFX.Spiral(p, c);
                        ProceduralVFX.RingWave(p, c, 1.3f, 0.35f);
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
        /// 설치기 스킬 시전 이펙트 (type=="player_skill_cast"). 로아식 "지점 발동" 가독성 —
        /// 핵심은 실제로 "날아가는" 투사체: 시전자/상공에서 임팩트 지점으로 투사체가 duration 동안 이동하고,
        /// 도착 "순간"에 임팩트(데칼/버스트/링/셰이크)를 동기 발동한다. 데미지 숫자(별도 damage 이벤트)는
        /// 지연 못 하므로 투사체를 짧게(≤0.35s) 유지해 괴리를 최소화.
        ///   basic : 0.12s 직선, 작은 은백 구체            → ImpactBasic
        ///   skill : 0.18s 직선+포물선(중간 +1.2), 진홍 창  → ImpactSkillQ
        ///   skill2: 0.35s 조준점 상공 8m → 수직 낙하, 대형 붉은 구체 → ImpactSkillW
        /// 반경 r 은 ev.radius(sim) × cellSize 로 월드 변환(실판정 일치).
        /// </summary>
        private void FirePlayerSkill(EventData ev, Vector3 bossPos)
        {
            float cell = CellSize();
            Vector3 p = ToWorld(ev.tx, ev.ty);
            float r = (ev.radius > 0f ? ev.radius : 1.8f) * cell;   // sim → 월드 반경(실판정 일치)

            if (ev.skill_id == "basic")
            {
                // 평타: 0.12s 직선, 작은 은백 구체 → 도착 시 절제된 임팩트.
                LaunchProjectile(DealerCastOrigin(p), p, CSilver, 0.12f, 0.28f, 0f, TrajKind.Straight,
                    () => ImpactBasic(ev, p, r, bossPos));
            }
            else if (ev.skill_id == "skill2")
            {
                // W 혈월: 조준점 상공 8m 에서 수직 낙하(가속), 크고 붉은 구체 → 도착 시 대형 슬램 임팩트.
                Vector3 from = p + Vector3.up * 8f;
                LaunchProjectile(from, p, CCrimson, 0.35f, 0.9f, 0f, TrajKind.Drop,
                    () => ImpactSkillW(ev, p, r, bossPos));
            }
            else
            {
                // Q 혈창: 0.18s 직선+살짝 포물선(중간 높이 +1.2), 길쭉한 진홍 창 → 도착 시 관통 임팩트.
                LaunchProjectile(DealerCastOrigin(p), p, CCrimson, 0.18f, 0.45f, 1.2f, TrajKind.Arc,
                    () => ImpactSkillQ(ev, p, r, bossPos));
            }
        }

        // ─────────────── 스킬 임팩트(투사체 도착 콜백에서 동기 실행) ───────────────

        /// <summary>평타 도착 임팩트: 절제하되 명중/크리/빗나감을 명확히 구분.</summary>
        private void ImpactBasic(EventData ev, Vector3 p, float r, Vector3 bossPos)
        {
            Color decalBase, decalOutline; float peakAlpha;
            if (ev.hit && ev.crit)  { decalBase = CGold;   decalOutline = CGold * 2.8f;   peakAlpha = 0.75f; }
            else if (ev.hit)        { decalBase = CSilver; decalOutline = CSilver * 1.9f; peakAlpha = 0.5f; }
            else                    { decalBase = CBrown;  decalOutline = CBrown * 1.2f;  peakAlpha = 0.22f; } // 빗나감 흙먼지
            FlashImpactDecal(p, r, decalBase, decalOutline, peakAlpha, 0.28f);   // 바닥 잔광(페이드아웃)

            if (ev.hit)
            {
                ProceduralVFX.Burst(p, CSilver, 16, 4.5f, 0.28f, 0.35f);          // 버스트 상향
                ProceduralVFX.RingWave(p, CSilver, r * 1.15f, 0.3f);             // 링 확대
                ProceduralVFX.Burst(bossPos + Vector3.up * 1.2f, CSilver, 10, 3.5f, 0.22f, 0.3f);
                LostArkCamera.ShakeCamera(0.06f * (ev.crit ? 1.5f : 1f), 0.13f);  // 소폭(크리 1.5배)
                if (ev.crit)
                {
                    ProceduralVFX.Burst(p, CGold, 30, 7f, 0.42f, 0.4f);          // 플래시성 대형 버스트
                    HitStopManager.HitStop(0.05f);
                }
            }
            else
            {
                ProceduralVFX.Burst(p, CBrown, 8, 2.5f, 0.2f, 0.35f);            // 빗나감: 약한 흙먼지
            }
        }

        /// <summary>Q 혈창 도착 임팩트: 금-진홍 관통 폭발 + 반경 링/잔광.</summary>
        private void ImpactSkillQ(EventData ev, Vector3 p, float r, Vector3 bossPos)
        {
            Color decalBase, decalOutline; float peakAlpha;
            if (ev.hit && ev.crit)  { decalBase = CGold;  decalOutline = CGold * 3.4f;  peakAlpha = 0.95f; }
            else if (ev.hit)        { decalBase = CGold;  decalOutline = CGold * 2.4f;  peakAlpha = 0.65f; }
            else                    { decalBase = CBrown; decalOutline = CBrown * 1.3f; peakAlpha = 0.28f; }
            FlashImpactDecal(p, r, decalBase, decalOutline, peakAlpha, 0.35f);   // 잔광 0.35s

            if (ev.hit)
            {
                ProceduralVFX.Burst(p, CGold, 34, 7.5f, 0.45f, 0.5f);            // 버스트 상향
                ProceduralVFX.Burst(p, CCrimson, 22, 5.5f, 0.4f, 0.45f);
                ProceduralVFX.RingWave(p, CGold, r * 1.25f, 0.4f);              // 반경 확대
                ProceduralVFX.Debris(p, CCrimson);
                ProceduralVFX.Burst(bossPos + Vector3.up * 1.2f, CGold, 22, 5.5f, 0.32f, 0.38f);
                LostArkCamera.ShakeCamera(0.12f * (ev.crit ? 1.5f : 1f), 0.16f);
                if (ev.crit)
                {
                    ProceduralVFX.Burst(p, CGold, 40, 9f, 0.55f, 0.45f);        // 크리 대형 플래시
                    HitStopManager.HitStop(0.09f);                              // 기존 크리 히트스톱 유지
                }
            }
            else
            {
                ProceduralVFX.Burst(p, CBrown, 14, 3.5f, 0.3f, 0.45f);          // 빗나감 흙먼지
                ProceduralVFX.RingWave(p, CBrown, r * 0.9f, 0.35f);
            }
        }

        /// <summary>W 혈월 도착 임팩트: 대형 슬램(강한 셰이크/히트스톱 + 이중 링 + 잔광).</summary>
        private void ImpactSkillW(EventData ev, Vector3 p, float r, Vector3 bossPos)
        {
            Color decalBase, decalOutline; float peakAlpha;
            if (ev.hit && ev.crit)  { decalBase = CCrimson; decalOutline = CRed * 3.2f;  peakAlpha = 0.95f; }
            else if (ev.hit)        { decalBase = CCrimson; decalOutline = CRed * 2.3f;  peakAlpha = 0.7f; }
            else                    { decalBase = CBrown;   decalOutline = CBrown * 1.3f; peakAlpha = 0.3f; }
            FlashImpactDecal(p, r, decalBase, decalOutline, peakAlpha, 0.35f);   // 잔광 0.35s

            // 슬램은 명중 여부와 무관하게 지면 충격(무게감).
            ProceduralVFX.Burst(p, CCrimson, 52, 9f, 0.6f, 0.6f);               // 버스트 대폭 상향
            ProceduralVFX.RingWave(p, CCrimson, r * 1.3f, 0.5f);               // 반경 확대(외곽)
            ProceduralVFX.RingWave(p, CRed, r * 0.7f, 0.35f);                  // 이중 링(내곽)
            ProceduralVFX.Debris(p, CBrown);
            LostArkCamera.ShakeCamera(0.25f * (ev.crit ? 1.5f : 1f), 0.28f);    // 강한 셰이크
            HitStopManager.HitStop(ev.crit ? 0.12f : 0.07f);                    // 무게감(크리 유지)

            if (ev.crit)
            {
                Flash(CRed, 0.35f);
                ProceduralVFX.Burst(p, CGold, 44, 10f, 0.65f, 0.5f);           // 크리 대형 플래시
            }
            if (ev.hit)
                ProceduralVFX.Burst(bossPos + Vector3.up * 1.2f, CCrimson, 24, 6f, 0.35f, 0.4f);
        }

        // ─────────────── 투사체 발사 + 풀링 ───────────────

        private enum TrajKind { Straight, Arc, Drop }

        // 재사용 가능한 투사체(발광 파티클 트레일 GO). 평타 스팸 대비 GO/머티리얼 재사용으로 GC 압박 해소.
        private class PooledProjectile { public GameObject go; public ParticleSystem ps; public bool inUse; }
        private readonly List<PooledProjectile> _projPool = new List<PooledProjectile>();
        private const float ProjectileFade = 0.3f;   // 도착 후 트레일 소멸 대기(초, unscaled)

        /// <summary>투사체를 from→to 로 duration 동안 실제 이동시키고, 도착 시 onArrive 임팩트를 발동.</summary>
        private void LaunchProjectile(Vector3 from, Vector3 to, Color color, float duration, float size,
            float arcHeight, TrajKind kind, System.Action onArrive)
        {
            var pp = GetProjectile(from, to, color, duration, size);
            StartCoroutine(MoveProjectile(pp, from, to, duration, arcHeight, kind, onArrive));
        }

        private PooledProjectile GetProjectile(Vector3 from, Vector3 to, Color color, float duration, float size)
        {
            PooledProjectile pp = null;
            for (int i = 0; i < _projPool.Count; i++)
                if (!_projPool[i].inUse) { pp = _projPool[i]; break; }

            if (pp == null)
            {
                // 신규 생성: ProceduralVFX 는 비주얼만 생성. 풀에 등록해 이후 재사용.
                var go = ProceduralVFX.Projectile(from, to, color, duration, size);
                go.transform.SetParent(transform, true);
                pp = new PooledProjectile { go = go, ps = go.GetComponent<ParticleSystem>() };
                _projPool.Add(pp);
            }
            else
            {
                // 재사용: 위치/색/크기/잔상 길이만 재설정 후 Clear/Play (새 GO·머티리얼 생성 없음).
                pp.go.SetActive(true);
                pp.go.transform.position = from;
                Vector3 dir = to - from;
                if (dir.sqrMagnitude > 1e-6f)
                    pp.go.transform.rotation = Quaternion.LookRotation(dir.normalized);
                if (pp.ps != null)
                {
                    var main = pp.ps.main;
                    main.startColor = HdrColor(color, 2.6f);
                    main.startSize = Mathf.Max(0.05f, size);
                    main.startLifetime = Mathf.Clamp(duration * 0.9f, 0.12f, 0.35f);
                    pp.ps.Clear();
                    pp.ps.Play();
                }
            }
            pp.inUse = true;
            return pp;
        }

        private System.Collections.IEnumerator MoveProjectile(PooledProjectile pp, Vector3 from, Vector3 to,
            float duration, float arcHeight, TrajKind kind, System.Action onArrive)
        {
            duration = Mathf.Max(0.02f, duration);
            float t = 0f;
            // 비행은 unscaled 로 구동 — 동시 히트스톱에도 실제 비행 시간이 늘지 않아 데미지 숫자와 괴리 최소.
            while (pp.go != null && t < duration)
            {
                float k = t / duration;
                Vector3 pos;
                switch (kind)
                {
                    case TrajKind.Arc:                     // 직선 + 포물선(중간 높이 arcHeight)
                        pos = Vector3.Lerp(from, to, k);
                        pos.y += arcHeight * 4f * k * (1f - k);
                        break;
                    case TrajKind.Drop:                    // 수직 낙하(가속감)
                        pos = Vector3.Lerp(from, to, k * k);
                        break;
                    default:                               // 직선
                        pos = Vector3.Lerp(from, to, k);
                        break;
                }
                pp.go.transform.position = pos;
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (pp.go != null) pp.go.transform.position = to;
            onArrive?.Invoke();   // 도착 "순간" 임팩트 동기 발동

            // 방출 정지 후 트레일이 자연 소멸하도록 잠시 대기 → 풀 반납.
            if (pp.ps != null) pp.ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            float fade = 0f;
            while (fade < ProjectileFade) { fade += Time.unscaledDeltaTime; yield return null; }
            if (pp.go != null) pp.go.SetActive(false);
            pp.inUse = false;
        }

        /// <summary>ProceduralVFX.Hdr 과 동일 규칙(재사용 시 startColor 재설정용).</summary>
        private static Color HdrColor(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

        /// <summary>혈창 투척의 투사체 시작점(딜러 손 높이). 딜러 미탐색 시 지점 상공으로 폴백.</summary>
        private Vector3 DealerCastOrigin(Vector3 fallbackPoint)
        {
            if (viewer != null && viewer.TryGetDealerTransform(out var t) && t != null)
                return t.position + Vector3.up * 1.0f;
            return fallbackPoint + Vector3.up * 1.5f;
        }

        // ─────────────── 임팩트 반경 데칼 플래시 ───────────────

        // 재사용 가능한 임팩트 데칼(GO + 전용 Material). 평타 스팸 시 생성/파괴 GC 를 없애기 위해 풀링.
        // 색/알파는 인스턴스별로 애니메이션하므로 머티리얼은 데칼당 1개(재사용, 재생성 안 함).
        private class PooledDecal { public GameObject go; public Material mat; public bool inUse; }
        private readonly List<PooledDecal> _decalPool = new List<PooledDecal>();

        /// <summary>
        /// 임팩트 지점에 실판정 반경 크기의 원 데칼을 dur 초간 밝게 플래시(페이드아웃 후 풀 반납).
        /// BossRaid/Telegraph 셰이더 circle(_Fill=1) 재활용. 셰이더 미포함 시 Sprites/Default 폴백.
        /// </summary>
        private void FlashImpactDecal(Vector3 pos, float worldRadius, Color baseCol, Color outlineCol,
            float peakAlpha, float dur)
        {
            var d = GetDecal();
            float diam = Mathf.Max(0.02f, worldRadius * 2f);
            d.go.transform.position = new Vector3(pos.x, 0.03f, pos.z);
            d.go.transform.localScale = new Vector3(diam, diam, diam);
            StartCoroutine(ImpactDecalRoutine(d, baseCol, outlineCol, peakAlpha, Mathf.Max(0.05f, dur)));
        }

        private PooledDecal GetDecal()
        {
            for (int i = 0; i < _decalPool.Count; i++)
                if (!_decalPool[i].inUse)
                {
                    _decalPool[i].inUse = true;
                    _decalPool[i].go.SetActive(true);
                    return _decalPool[i];
                }

            var pd = new PooledDecal { go = BuildCircleDecal(out var mat), mat = mat, inUse = true };
            _decalPool.Add(pd);
            return pd;
        }

        private System.Collections.IEnumerator ImpactDecalRoutine(PooledDecal d,
            Color baseCol, Color outlineCol, float peakAlpha, float dur)
        {
            float t = 0f;
            while (d.go != null && t < dur)
            {
                float k = t / dur;
                float a = peakAlpha * (1f - k * k);   // 빠른 감쇠
                SetDecalColor(d.mat, baseCol, outlineCol, a);
                t += Time.deltaTime;
                yield return null;
            }
            if (d.go != null) d.go.SetActive(false);
            d.inUse = false;
        }

        /// <summary>데칼 머티리얼 색/알파 갱신(Telegraph 셰이더 우선, Sprites/Default 폴백).</summary>
        private static void SetDecalColor(Material mat, Color baseCol, Color outlineCol, float a)
        {
            if (mat == null) return;
            Color b = baseCol; b.a = a;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", b);
            else mat.color = b;
            if (mat.HasProperty("_OutlineColor"))
            {
                Color o = outlineCol; o.a = a;
                mat.SetColor("_OutlineColor", o);
            }
        }

        /// <summary>바닥에 눕힌 원형 Quad 데칼 GO + 전용 Material 을 1회 생성(색은 이후 SetDecalColor 로 갱신).</summary>
        private GameObject BuildCircleDecal(out Material mat)
        {
            var go = new GameObject("VFX_ImpactDecal");
            go.transform.SetParent(transform, false);

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
