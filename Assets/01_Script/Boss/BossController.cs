using System;
using System.Collections;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 보스 3D 오브젝트 컨트롤러. 격자 좌표를 Lerp로 보간해 자연스럽게 이동.
    /// 2x2 타일을 점유하므로 중심 오프셋 (+0.5, 0, +0.5) 적용.
    /// 패턴 텔레그래프 시작을 감지해 Animator 트리거 발동.
    /// </summary>
    public class BossController : MonoBehaviour
    {
        [HideInInspector] public BossGameViewer viewer;

        [Header("Visual")]
        [Tooltip("한 턴(격자 1칸)을 몇 초에 이동할지 (Python TURN_INTERVAL과 맞추기). 적응형 보간의 초기 추정치 역할만 하며 실측 간격으로 대체됨")]
        public float turnDuration = 0.3f;
        [Tooltip("목표와 이 거리 이상 벌어지면 순간이동(리셋/리스폰)으로 간주해 보간 없이 스냅")]
        public float snapDistance = 3.0f;
        public float rotateLerpSpeed = 10f;
        public Animator animator;
        public Renderer[] bodyRenderers;
        public Color phase1Color = Color.white;
        public Color phase2Color = new Color(1f, 0.5f, 0.3f);
        public Color phase3Color = new Color(1f, 0.2f, 0.2f);

        [Header("Effects")]
        public GameObject invulnEffect;
        public GameObject groggyEffect;
        public GameObject staggerEffect;

        [Header("Juice (타격감)")]
        [Tooltip("페이즈 전환/그로기 연출용 포스트FX. 비어 있으면 씬에서 자동 탐색.")]
        public BossPostFX postFX;

        /// <summary>
        /// 큰 임팩트(페이즈 상승/그로기 진입) 시 (amplitude, duration)으로 발화.
        /// LostArkCamera.ShakeCamera 등 카메라 흔들림을 컴파일 의존성 없이 구독시키기 위한 이벤트.
        /// </summary>
        public static event Action<float, float> OnBigImpact;

        // 상태 전이 감지용 이전 값
        private int _prevPhase = -1;
        private bool _prevGroggy = false;
        private bool _stateInit = false;

        [Header("Animator Params")]
        public string paramPhase = "Phase";
        public string paramGroggy = "Groggy";
        public string paramDead = "Dead";
        public string paramIsMoving = "IsMoving";
        public string trigSlash = "TrigSlash";
        public string trigCharge = "TrigCharge";
        public string trigJump = "TrigJump";
        public string trigRoar = "TrigRoar";
        public string trigTail = "TrigTail";
        [Tooltip("Target까지 이 거리 이상이면 이동 중으로 판정")]
        public float movingThreshold = 0.05f;

        [Header("Juice - 피격 플래시/틴트 (MPB, 배칭 보존)")]
        [Tooltip("플레이어 딜 명중 시 흰 플래시 지속(초)")]
        public float hitFlashDuration = 0.06f;
        [Tooltip("플래시 이미시브 밝기 배수")]
        public float hitFlashIntensity = 2.0f;
        [Tooltip("플래시 스팸 방지 최소 간격(초)")]
        public float hitFlashMinInterval = 0.1f;
        [Tooltip("크리티컬 시 붉은 플래시 색")]
        public Color critFlashColor = new Color(1f, 0.3f, 0.25f);
        [Tooltip("그로기/스턴 중 파르스름 이미시브 틴트")]
        public Color groggyTint = new Color(0.45f, 0.7f, 1.15f);
        [Tooltip("카운터 창 동안 몸 전체 파란 이미시브 틴트(그로기보다 우선). HDR 강조.")]
        public Color counterGlowColor = new Color(0.2f, 0.55f, 1.2f);
        [Tooltip("카운터 파란 틴트 이미시브 배수(체감 강화용).")]
        public float counterGlowIntensity = 3.5f;
        [Tooltip("무력화(그로기 진입) 성공 순간 보라 플래시 색")]
        public Color staggerFlashColor = new Color(0.7f, 0.35f, 1f);

        private MaterialPropertyBlock _bossMpb;
        private Coroutine _bossFlashCo;
        private bool _bossEmissionReady;
        private bool _grogTintActive;
        private float _lastFlashTime = -999f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionId  = Shader.PropertyToID("_EmissionColor");

        private SnapshotLerp _lerp;           // 적응형 스냅샷 보간 상태
        private Quaternion _targetRot = Quaternion.identity;
        private BossData _latestData;
        private bool _hasData;

        private void Awake()
        {
            _bossMpb = new MaterialPropertyBlock();
        }

        // 이전 프레임 텔레그래프 상태 (신규 발동 감지용)
        private readonly System.Collections.Generic.HashSet<int> _prevTelegraphIds = new System.Collections.Generic.HashSet<int>();

        public void ApplySnapshot(BossData b)
        {
            _latestData = b;

            // 유클리드 float 좌표를 월드로 직접 변환 (보스 중심이 곧 (x, y))
            var world = viewer.ContinuousToWorld(b.x, b.y);

            // 적응형 보간 갱신: 현재 위치(보간 중이면 그 위치)에서 새 목표로 이어붙임.
            // 거리 snapDistance 이상이면 순간이동(리셋/리스폰)으로 보고 스냅.
            bool snapped = _lerp.OnSnapshot(transform.position, world, snapDistance);
            if (snapped) transform.position = world;
            _hasData = true;

            ApplyPhaseColor(b.phase);

            // stun(가드/카운터 경직)도 그로기처럼 취급 → 애니/이펙트 공유
            bool grogOrStun = b.grog > 0 || b.stun > 0;

            if (invulnEffect) invulnEffect.SetActive(b.invuln > 0);
            if (groggyEffect) groggyEffect.SetActive(grogOrStun);
            if (staggerEffect) staggerEffect.SetActive(b.stagger_active);

            if (animator != null)
            {
                animator.SetBool(paramGroggy, grogOrStun);
                animator.SetInteger(paramPhase, b.phase);
            }

            // V2 카운터 발광: counter_window > 0 이면 파란 이미시브 틴트, 끝나면 원복
            ApplyCounterGlow(b.counter_window > 0);

            // 플레이어 딜 명중 시 흰/붉은 플래시 (이번 스냅샷 events 에서 damage/uid 0 검사)
            DetectPlayerHits();

            // 그로기/스턴 파르스름 지속 틴트 갱신
            UpdateGroggyTint(grogOrStun);

            // 타격감 훅: 상태 전이 감지 → 포스트FX/히트스톱/카메라 흔들림
            DetectImpactTransitions(b);
        }

        /// <summary>
        /// 스냅샷 간 상태 전이를 감지해 연출을 트리거한다. (기존 로직 미변경, 감지+호출만)
        /// - 페이즈 상승  → 화면 플래시 + HitStop(0.12) + 큰 카메라 흔들림
        /// - 그로기 진입  → HitStop(0.15) + 카메라 흔들림 (스태거 성공 순간)
        /// </summary>
        private void DetectImpactTransitions(BossData b)
        {
            bool groggyNow = b.grog > 0;

            // 첫 스냅샷은 baseline만 기록하고 연출 생략(초기 진입을 임팩트로 오인 방지)
            if (!_stateInit)
            {
                _prevPhase = b.phase;
                _prevGroggy = groggyNow;
                _stateInit = true;
                return;
            }

            // 페이즈 상승
            if (b.phase > _prevPhase)
            {
                Color flash = b.phase == 1 ? phase2Color : b.phase >= 2 ? phase3Color : phase1Color;
                if (postFX == null) postFX = FindFirstObjectByType<BossPostFX>();
                if (postFX != null) postFX.FlashScreen(flash, 0.5f);

                HitStopManager.HitStop(0.12f);
                OnBigImpact?.Invoke(0.6f, 0.35f);   // (amplitude, duration)
            }

            // 그로기 진입 (스태거 성공) — 무력화 성공 보라 플래시
            if (groggyNow && !_prevGroggy)
            {
                HitStopManager.HitStop(0.15f);
                OnBigImpact?.Invoke(0.45f, 0.3f);
                BossFlash(staggerFlashColor, hitFlashIntensity, 0.18f);
            }

            _prevPhase = b.phase;
            _prevGroggy = groggyNow;
        }

        /// <summary>
        /// 스냅샷의 텔레그래프 리스트를 받아 "이번 프레임에 새로 시작된 것"만
        /// 트리거 발동. BossGameViewer에서 호출.
        /// </summary>
        public void OnTelegraphs(TelegraphData[] telegraphs)
        {
            if (animator == null || telegraphs == null) return;

            // V2: 스텝별 독립 텔레그래프. (pattern, step_index) 단위로 신규 스텝 감지 →
            //     telegraph.anim 문자열을 트리거 이름으로 SetTrigger.
            var current = new System.Collections.Generic.HashSet<int>();
            foreach (var tg in telegraphs)
            {
                int key = tg.pattern * 100 + tg.step_index;   // 스텝 고유 키
                current.Add(key);

                // 이번 프레임에 처음 관측된 스텝이면 애니 트리거 (한 스텝당 1회)
                if (!_prevTelegraphIds.Contains(key))
                    FireAnim(tg.anim);
            }
            _prevTelegraphIds.Clear();
            foreach (var id in current) _prevTelegraphIds.Add(id);
        }

        /// <summary>
        /// telegraph.anim 키(slash/smash/shock/rush/throw/spin/roar/brand/counter_glow/lift/blood_moon 등)를
        /// 그대로 Animator 트리거 이름으로 사용. 해당 파라미터가 없으면 안전하게 무시.
        /// </summary>
        private void FireAnim(string anim)
        {
            if (animator == null || string.IsNullOrEmpty(anim)) return;
            if (HasParameter(anim, AnimatorControllerParameterType.Trigger))
                animator.SetTrigger(anim);
        }

        /// <summary>Animator에 주어진 이름/타입의 파라미터가 존재하는지 확인.</summary>
        private bool HasParameter(string paramName, AnimatorControllerParameterType type)
        {
            if (animator == null || string.IsNullOrEmpty(paramName)) return false;
            var ps = animator.parameters;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].type == type && ps[i].name == paramName) return true;
            return false;
        }

        // ─────────────── V2 카운터 발광 ───────────────

        private bool _counterGlowOn;

        /// <summary>
        /// 카운터 창 동안 몸 전체 파란 이미시브 틴트. MPB 지속 상태 경로로 통합해
        /// 우선순위(카운터 파랑 > 그로기 > 기본)를 보장한다. 종료 시 원복.
        /// (기존 material 직접 쓰기 방식은 groggy MPB 에 덮여 파랑이 안 보이던 문제 수정)
        /// </summary>
        private void ApplyCounterGlow(bool on)
        {
            if (_counterGlowOn == on) return;   // 상태 변화 시에만 갱신
            _counterGlowOn = on;
            EnsureBossEmission();
            if (_bossFlashCo == null) RestoreBossPersistent();   // 플래시 중이면 종료 시 반영됨
        }

        // ─────────────── 피격 플래시 / 그로기 틴트 (MPB) ───────────────

        /// <summary>이번 스냅샷 events 에서 플레이어(uid 0) damage 를 찾아 플래시. 크리면 붉게.</summary>
        private void DetectPlayerHits()
        {
            var snap = viewer != null ? viewer.LatestSnapshot : null;
            if (snap == null || snap.events == null) return;

            bool hit = false, crit = false;
            foreach (var ev in snap.events)
            {
                if (ev == null) continue;
                if (ev.type == "damage" && ev.uid == 0)   // 딜러 → 보스 타격
                {
                    hit = true;
                    if (ev.crit) crit = true;
                }
            }
            if (!hit) return;
            if (Time.unscaledTime - _lastFlashTime < hitFlashMinInterval) return;   // 스팸 방지
            _lastFlashTime = Time.unscaledTime;
            BossFlash(crit ? critFlashColor : Color.white, hitFlashIntensity, hitFlashDuration);
        }

        /// <summary>그로기/스턴 지속 파르스름 틴트 on/off. 상태 변화 시에만 갱신.</summary>
        private void UpdateGroggyTint(bool grogOrStun)
        {
            if (grogOrStun == _grogTintActive) return;
            _grogTintActive = grogOrStun;
            EnsureBossEmission();
            if (_bossFlashCo == null) RestoreBossPersistent();   // 플래시 중이면 종료 시 반영됨
        }

        /// <summary>보스 본체 이미시브 플래시 (unscaled 감쇠). 종료 후 지속 상태(그로기 틴트/원복)로 복귀.</summary>
        private void BossFlash(Color color, float intensity, float duration)
        {
            if (bodyRenderers == null || bodyRenderers.Length == 0) return;
            EnsureBossEmission();
            if (_bossFlashCo != null) StopCoroutine(_bossFlashCo);
            _bossFlashCo = StartCoroutine(BossFlashRoutine(color, intensity, Mathf.Max(0.01f, duration)));
        }

        private IEnumerator BossFlashRoutine(Color color, float intensity, float duration)
        {
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float k = 1f - Mathf.Clamp01(t / duration);
                SetBossEmission(color * intensity * k);
                yield return null;
            }
            _bossFlashCo = null;
            RestoreBossPersistent();
        }

        /// <summary>플래시 종료/틴트 변경 시 지속 상태 반영. 우선순위: 카운터 파랑 > 그로기 파르스름 > 기본(MPB 제거).</summary>
        private void RestoreBossPersistent()
        {
            if (bodyRenderers == null) return;
            if (_counterGlowOn)
            {
                SetBossEmission(counterGlowColor * counterGlowIntensity);   // 카운터 최우선
            }
            else if (_grogTintActive)
            {
                SetBossEmission(groggyTint * 0.9f);
            }
            else
            {
                foreach (var r in bodyRenderers)
                    if (r != null) r.SetPropertyBlock(null);   // MPB 이미시브 override 제거 → 기본
            }
        }

        private void SetBossEmission(Color emission)
        {
            foreach (var r in bodyRenderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_bossMpb);
                _bossMpb.SetColor(EmissionId, emission);
                r.SetPropertyBlock(_bossMpb);
            }
        }

        /// <summary>MPB 이미시브가 보이도록 본체 머티리얼(이미 인스턴스)에 _EMISSION 키워드 1회 활성.</summary>
        private void EnsureBossEmission()
        {
            if (_bossEmissionReady || bodyRenderers == null) return;
            _bossEmissionReady = true;
            foreach (var r in bodyRenderers)
            {
                if (r == null) continue;
                var m = r.material;
                if (m != null && m.HasProperty(EmissionId)) m.EnableKeyword("_EMISSION");
            }
        }

        public void SetDead(bool dead)
        {
            if (animator != null) animator.SetBool(paramDead, dead);
        }

        private void Update()
        {
            if (!_hasData) return;

            // 적응형 선형 보간 (smoothstep easing 제거 → 연속 이동 시 가속-감속 맥동 없음)
            Vector3 newPos = _lerp.Evaluate(out float t);

            transform.position = newPos;

            // V2: 회전 목표는 이동 방향이 아니라 boss.facing(rad)으로 결정.
            // sim (x,y) → 월드 (x,z) 이므로 Unity yaw = 90° - facing*Rad2Deg
            // (모델 forward=+Z 기준. facing θ → 월드 (cosθ,0,sinθ) 를 향함)
            if (_latestData != null)
            {
                float yawDeg = 90f - _latestData.facing * Mathf.Rad2Deg;
                _targetRot = Quaternion.Euler(0f, yawDeg, 0f);
            }
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRot, Time.deltaTime * rotateLerpSpeed);

            if (animator != null)
            {
                // 보간 진행 중 & 이동 거리 있음 & 그로기 아님 = 이동 중
                bool moving = _lerp.IsMoving(t) && (_latestData == null || _latestData.grog <= 0);
                animator.SetBool(paramIsMoving, moving);
            }
        }

        // ─── 에디터 테스트용 ───
        [ContextMenu("Test Phase 0 (White)")] private void TestPhase0() { ApplyPhaseColor(0); }
        [ContextMenu("Test Phase 1 (Orange)")] private void TestPhase1() { ApplyPhaseColor(1); }
        [ContextMenu("Test Phase 2 (Red)")]    private void TestPhase2() { ApplyPhaseColor(2); }

        private void ApplyPhaseColor(int phase)
        {
            if (bodyRenderers == null || bodyRenderers.Length == 0) return;
            Color c = phase == 0 ? phase1Color : phase == 1 ? phase2Color : phase3Color;
            foreach (var r in bodyRenderers)
            {
                if (r != null && r.sharedMaterial != null && r.material.HasProperty("_BaseColor"))
                    r.material.SetColor("_BaseColor", c);
                else if (r != null && r.material != null)
                    r.material.color = c;
            }
        }
    }
}
