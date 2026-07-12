using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 파티 유닛(플레이어 포함) 시각화. 격자 Lerp + HP 바 + 상태 효과.
    ///
    /// 권위/표시 위치 분리:
    ///   클라이언트 사이드 예측(DealerMotionPredictor)이 LateUpdate 에서 transform.position 에
    ///   예측 오프셋을 더해 덮어쓰므로, transform.position 은 다음 프레임에 "예측 오염" 상태다.
    ///   이를 보간 시작점/회전 방향 계산에 다시 쓰면 시작점이 오프셋만큼 오염되고 회전 타깃이
    ///   후향으로 뒤집혀 떨림이 생긴다. → 예측 오염 없는 권위 보간 위치를 _authPos 필드로 분리.
    ///   _lerp 갱신과 회전 방향은 _authPos 기준으로만 계산하고, transform.position 은 렌더 결과로만 쓴다.
    ///   예측기는 AuthoritativePosition 프로퍼티로 오염 없는 권위 위치를 읽는다.
    /// </summary>
    public class UnitView : MonoBehaviour
    {
        [HideInInspector] public BossGameViewer viewer;
        [HideInInspector] public int uid;

        /// <summary>역할(0=Dealer/플레이어, 1=Tank, 2=Healer, 3=Support). 스냅샷 수신 전에는 -1.</summary>
        public int Role => _latest != null ? _latest.role : -1;

        [Header("Visual")]
        [Tooltip("한 턴(격자 1칸)을 몇 초에 이동할지 (Python TURN_INTERVAL과 맞추기). 적응형 보간의 초기 추정치 역할만 하며 실측 간격으로 대체됨")]
        public float turnDuration = 0.3f;
        [Tooltip("목표와 이 거리 이상 벌어지면 순간이동(리셋/리스폰)으로 간주해 보간 없이 스냅")]
        public float snapDistance = 3.0f;
        public float rotateLerpSpeed = 12f;
        public Animator animator;

        [Header("Animator Params")]
        public string paramIsMoving = "IsMoving";
        public string paramDead = "Dead";
        public string trigAttack = "TrigAttack";
        public string trigHeal = "TrigHeal";
        public string trigTaunt = "TrigTaunt";
        public string trigBuff = "TrigBuff";
        public string trigHit = "TrigHit";
        public GameObject hpBarRoot;
        public Transform hpBarFill;
        public GameObject deathEffect;
        public GameObject shieldEffect;
        public GameObject buffAtkEffect;
        public GameObject guardEffect;

        private SnapshotLerp _lerp;           // 적응형 스냅샷 보간 상태
        private Quaternion _targetRot = Quaternion.identity;
        private GameObject _markInstance;
        private UnitData _latest;
        private bool _hasData;
        private bool _prevAlive;              // 직전 생존 상태(사망/부활 전환 감지용)
        private bool _aliveKnown;             // 생존 상태를 한 번이라도 받았는지
        private Vector3 _authPos;             // 예측 오염 없는 권위 보간 위치(transform.position 과 분리)

        /// <summary>예측기 계약: 예측 오프셋이 섞이지 않은 권위 렌더 위치. 첫 데이터 전에는 transform 폴백.</summary>
        public Vector3 AuthoritativePosition => _hasData ? _authPos : transform.position;

        /// <summary>
        /// 이동 회전 소유권 위임 플래그. 딜러의 DealerMotionPredictor 가 intent 활성 동안 true 로 세팅한다.
        /// true 인 동안 UnitView.Update 의 회전 로직(이동 방향/보스 바라보기 Slerp)을 스킵해
        /// 예측기의 intent 회전과 이중 슬럽 경합(떨림/역회전)을 없앤다. NPC 유닛은 항상 false(기존 동작).
        /// </summary>
        public bool ExternalRotationOwner { get; set; }

        // ─────────────── 피격 플래시 / 사망 디졸브 (MPB, 머티리얼 신규 인스턴스 생성 금지) ───────────────
        [Header("Juice (피격/사망 연출)")]
        [Tooltip("피격 흰 플래시 지속(초, unscaled)")]
        public float hitFlashDuration = 0.08f;
        [Tooltip("피격 플래시 밝기 배수(1.5 = 흰색 1.5배)")]
        public float hitFlashIntensity = 1.5f;
        [Tooltip("사망 디졸브 시간(초)")]
        public float deathDissolveDuration = 1.2f;
        [Tooltip("사망 시 최종 스케일 배수")]
        public float deathShrink = 0.85f;
        [Tooltip("사망 시 가라앉는 깊이(월드 y). URP Lit 불투명이라 알파가 안 먹으면 이 침강+수축이 대체 연출")]
        public float deathSink = 0.6f;

        private Renderer[] _fxRenderers;            // 본체 렌더러 캐시(Awake 1회). 파티클/라인/HP바/이펙트 제외
        private MaterialPropertyBlock _mpb;
        private Color[] _fxBaseColors;              // 렌더러별 원본 베이스컬러(역할 틴트 반영) — lazy 캐시
        private bool _fxColorsReady;
        private Coroutine _flashCo;
        private Coroutine _dissolveCo;
        private Vector3 _baseScale;
        private bool _baseScaleCaptured;
        private float _deathSinkY;                  // LateUpdate 에서 적용하는 침강량
        private bool _isDissolved;                  // 디졸브 진행/완료(피격 플래시 억제용)

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId     = Shader.PropertyToID("_Color");
        private static readonly int EmissionId  = Shader.PropertyToID("_EmissionColor");

        private void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
            _mpb = new MaterialPropertyBlock();
            CacheFxRenderers();

            // 순간이동 근절: 프리팹에 직렬화된 snapDistance(3.0)가 코드 기본값을 덮어써,
            // 대시(2.5)+같은 턴 이동(≈1.0)의 서버 위치 점프(3.0+)가 임계 초과 → 텔레포트로 오판됐다.
            // 최소 4.6 으로 강제해 대시+이동 한 턴 점프(≈3.5)를 보간으로 수용한다.
            // 에피소드 리셋(유닛 재배치 거리 5+)은 여전히 초과 → 정상 스냅 유지.
            snapDistance = Mathf.Max(snapDistance, 4.6f);
        }

        /// <summary>본체 렌더러만 캐시. HP바/디스이펙트/버프이펙트/파티클/라인/트레일은 플래시 대상 제외.</summary>
        private void CacheFxRenderers()
        {
            var rs = GetComponentsInChildren<Renderer>(true);
            var list = new List<Renderer>(rs.Length);
            foreach (var r in rs)
            {
                if (r == null) continue;
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if (IsUnder(r.transform, hpBarRoot) || IsUnder(r.transform, deathEffect) ||
                    IsUnder(r.transform, shieldEffect) || IsUnder(r.transform, buffAtkEffect) ||
                    IsUnder(r.transform, guardEffect)) continue;
                list.Add(r);
            }
            _fxRenderers = list.ToArray();
        }

        private static bool IsUnder(Transform t, GameObject root)
            => root != null && t != null && t.IsChildOf(root.transform);

        /// <summary>역할 틴트가 머티리얼에 반영된 뒤(첫 피격/사망 시) 원본 컬러 캡처 + 이미시브 키워드 보장.</summary>
        private void EnsureFxColors()
        {
            if (_fxColorsReady || _fxRenderers == null) return;
            _fxColorsReady = true;
            _fxBaseColors = new Color[_fxRenderers.Length];
            for (int i = 0; i < _fxRenderers.Length; i++)
            {
                var r = _fxRenderers[i];
                Color c = Color.white;
                if (r != null)
                {
                    var m = r.sharedMaterial;   // 틴트로 이미 인스턴스화된 머티리얼
                    if (m != null)
                    {
                        if (m.HasProperty(BaseColorId)) c = m.GetColor(BaseColorId);
                        else if (m.HasProperty(ColorId)) c = m.GetColor(ColorId);
                        // 이미시브 플래시가 보이도록 키워드 활성(기존 인스턴스에만 적용 — 신규 인스턴스 없음)
                        if (m.HasProperty(EmissionId)) r.material.EnableKeyword("_EMISSION");
                    }
                }
                _fxBaseColors[i] = c;
            }
        }

        public void ApplySnapshot(UnitData u)
        {
            _latest = u;

            // 유클리드 float 좌표를 월드 좌표로 직접 변환 (중심 오프셋 불필요)
            var world = viewer.ContinuousToWorld(u.x, u.y);

            // 적응형 보간 갱신: 권위 위치(보간 중이면 그 위치)에서 새 목표로 이어붙임.
            // 예측 오염된 transform.position 대신 _authPos 를 시작점으로 써 오프셋 오염을 차단한다.
            // 첫 데이터 수신 전(!_hasData)에는 _authPos 가 아직 유효하지 않으므로 transform.position 폴백.
            Vector3 current = _hasData ? _authPos : transform.position;
            bool snapped = _lerp.OnSnapshot(current, world, snapDistance);
            if (snapped) { transform.position = world; _authPos = world; }
            _hasData = true;

            // HP 바
            if (hpBarFill != null)
            {
                float r = Mathf.Clamp01((float)u.hp / Mathf.Max(1, u.max_hp));
                hpBarFill.localScale = new Vector3(r, 1f, 1f);
            }
            if (hpBarRoot != null) hpBarRoot.SetActive(u.alive);

            // 상태 효과
            if (shieldEffect) shieldEffect.SetActive(u.buff_shield > 0);
            if (buffAtkEffect) buffAtkEffect.SetActive(u.buff_atk > 0);

            // 사망/부활 전환 처리 (다시하기로 부활 시 Dead/deathEffect 원복이 없던 버그 수정).
            // 매 스냅샷 SetBool 남발을 막기 위해 생존 상태가 "바뀔 때만" 적용한다.
            // Dead bool 은 여기서만 소유(Update 의 매프레임 세팅 제거) — paramDead 필드로 일관 처리.
            if (!_aliveKnown || u.alive != _prevAlive)
            {
                bool revived = _aliveKnown && u.alive && !_prevAlive;
                _aliveKnown = true;
                _prevAlive = u.alive;
                if (deathEffect) deathEffect.SetActive(!u.alive);
                SafeSetBool(paramDead, !u.alive);

                // 사망 진입: 비활성 대신 디졸브(수축+침강, 알파 베스트에포트). 부활 시 원복.
                if (!u.alive) StartDissolve();

                // 부활: Dead=false 만으로는 사망 상태에서 못 나온다(사망 스테이트에 출구 전이가
                // 없는 컨트롤러가 일반적) → 애니메이터를 초기 상태로 강제 리셋.
                if (revived && animator != null)
                {
                    animator.Rebind();
                    animator.Update(0f);
                    SafeSetBool(paramDead, false);   // Rebind 가 파라미터도 초기화하므로 재보증
                }

                // 부활: 디졸브 원복(스케일/침강/컬러 복원). 기존 Rebind 부활 로직과 공존.
                if (revived) RestoreFromDeath();
            }
        }

        // Animator 파라미터 캐시 (없는 파라미터 호출 시 경고 방지)
        private System.Collections.Generic.HashSet<string> _animParams;

        private bool HasParam(string name)
        {
            if (animator == null || string.IsNullOrEmpty(name)) return false;
            if (_animParams == null)
            {
                _animParams = new System.Collections.Generic.HashSet<string>();
                foreach (var p in animator.parameters) _animParams.Add(p.name);
            }
            return _animParams.Contains(name);
        }

        private void SafeSetTrigger(string name)
        {
            if (HasParam(name)) animator.SetTrigger(name);
        }

        private void SafeSetBool(string name, bool v)
        {
            if (HasParam(name)) animator.SetBool(name, v);
        }

        /// <summary>Python에서 발생한 이벤트에 맞춰 애니메이션 트리거.</summary>
        public void OnEvent(EventData ev)
        {
            if (animator == null || ev == null || string.IsNullOrEmpty(ev.type)) return;
            switch (ev.type)
            {
                case "damage":       SafeSetTrigger(trigAttack); break;
                case "heal":         SafeSetTrigger(trigHeal); break;
                case "taunt":        SafeSetTrigger(trigTaunt); break;
                case "buff":         SafeSetTrigger(trigBuff); break;
                case "damage_taken": SafeSetTrigger(trigHit); FlashHit(); break;
                case "death":        SafeSetBool(paramDead, true); break;
            }
        }

        public void ShowMark(GameObject markPrefab, int turnsRemaining)
        {
            if (_markInstance == null)
                _markInstance = Instantiate(markPrefab, transform);
            _markInstance.SetActive(true);
            // 턴이 0에 가까울수록 붉어지도록 자식 Renderer가 처리한다고 가정
        }

        private void Update()
        {
            if (!_hasData) return;

            // 적응형 선형 보간 (smoothstep 제거 → 연속 이동 시 맥동 없이 등속)
            Vector3 newPos = _lerp.Evaluate(out float t);

            // 회전 방향/렌더는 권위 위치(_authPos) 기준으로만 계산 → 예측 오프셋 오염 차단.
            var moveDir = newPos - _authPos;
            _authPos = newPos;
            transform.position = newPos;

            if (animator != null)
            {
                bool moving = _lerp.IsMoving(t);
                SafeSetBool(paramIsMoving, moving);
                // Dead bool 은 ApplySnapshot 의 생존 전환 처리에서만 세팅(중복/충돌 제거).
            }

            // 회전: 딜러 예측기가 소유권을 가져간 동안(ExternalRotationOwner)은 스킵 —
            // 예측기의 intent 회전과 이중 슬럽 경합(떨림/역회전)을 없앤다. NPC/비소유 시 기존 로직.
            if (!ExternalRotationOwner)
            {
                // 이동 중이면 이동 방향, 정지면 보스를 바라봄 (전투 중 자연스러움)
                Vector3 faceTarget;
                bool haveFace = false;
                if (moveDir.sqrMagnitude > 0.0004f)
                {
                    faceTarget = transform.position + moveDir.normalized;
                    haveFace = true;
                }
                else if (viewer != null && viewer.TryGetBossPosition(out var bossPos))
                {
                    faceTarget = bossPos;
                    haveFace = true;
                }
                else faceTarget = transform.position + transform.forward;

                if (haveFace)
                {
                    var flat = new Vector3(faceTarget.x - transform.position.x, 0, faceTarget.z - transform.position.z);
                    if (flat.sqrMagnitude > 0.0001f)
                        _targetRot = Quaternion.LookRotation(flat.normalized, Vector3.up);
                }
                transform.rotation = Quaternion.Slerp(transform.rotation, _targetRot, Time.deltaTime * rotateLerpSpeed);
            }
        }

        private void LateUpdate()
        {
            // 사망 침강: Update 가 transform.position(y=base)를 덮으므로 이후 LateUpdate 에서 오프셋 적용.
            if (_deathSinkY > 0f) transform.position -= Vector3.up * _deathSinkY;

            // 표식이 꺼져야 하면 여기서 제어 (스냅샷의 marked 필드로)
            if (_markInstance != null && _hasData && !_latest.marked)
                _markInstance.SetActive(false);
        }

        // ─────────────── 피격 흰 플래시 ───────────────

        /// <summary>damage_taken 이벤트 시 본체를 짧게 흰색(1.5배)으로 플래시 후 원복. 사망 중엔 무시.</summary>
        public void FlashHit()
        {
            if (_isDissolved || !isActiveAndEnabled) return;
            EnsureFxColors();
            if (_flashCo != null) StopCoroutine(_flashCo);
            _flashCo = StartCoroutine(FlashRoutine());
        }

        private IEnumerator FlashRoutine()
        {
            float t = 0f;
            float dur = Mathf.Max(0.01f, hitFlashDuration);
            Color flash = Color.white * hitFlashIntensity;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;      // 히트스톱(timeScale=0)과 무관하게 감쇠
                float k = 1f - Mathf.Clamp01(t / dur);
                for (int i = 0; i < _fxRenderers.Length; i++)
                {
                    var r = _fxRenderers[i];
                    if (r == null) continue;
                    r.GetPropertyBlock(_mpb);
                    Color baseC = _fxBaseColors != null ? _fxBaseColors[i] : Color.white;
                    Color lit = Color.Lerp(baseC, flash, k);
                    _mpb.SetColor(BaseColorId, lit);
                    _mpb.SetColor(ColorId, lit);
                    _mpb.SetColor(EmissionId, flash * k);
                    r.SetPropertyBlock(_mpb);
                }
                yield return null;
            }
            ClearMpb();   // MPB 제거 → 머티리얼(역할 틴트) 원복
            _flashCo = null;
        }

        private void ClearMpb()
        {
            if (_fxRenderers == null) return;
            foreach (var r in _fxRenderers)
                if (r != null) r.SetPropertyBlock(null);
        }

        // ─────────────── 사망 디졸브 ───────────────

        private void StartDissolve()
        {
            if (!_baseScaleCaptured) { _baseScale = transform.localScale; _baseScaleCaptured = true; }
            EnsureFxColors();
            if (_flashCo != null) { StopCoroutine(_flashCo); _flashCo = null; }
            if (_dissolveCo != null) StopCoroutine(_dissolveCo);
            _dissolveCo = StartCoroutine(DissolveRoutine());
        }

        private IEnumerator DissolveRoutine()
        {
            _isDissolved = true;
            float t = 0f;
            float dur = Mathf.Max(0.01f, deathDissolveDuration);
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / dur);
                transform.localScale = Vector3.Lerp(_baseScale, _baseScale * deathShrink, k);
                _deathSinkY = Mathf.Lerp(0f, deathSink, k);
                ApplyDissolveAlpha(1f - k);   // 알파 베스트에포트(투명 서피스면 페이드, 불투명이면 무시됨)
                yield return null;
            }
            transform.localScale = _baseScale * deathShrink;
            _deathSinkY = deathSink;
            ApplyDissolveAlpha(0f);
            _dissolveCo = null;   // 완료 후 상태 유지(비활성화하지 않음)
        }

        private void ApplyDissolveAlpha(float a)
        {
            if (_fxRenderers == null) return;
            for (int i = 0; i < _fxRenderers.Length; i++)
            {
                var r = _fxRenderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                Color c = _fxBaseColors != null ? _fxBaseColors[i] : Color.white;
                c.a = a;
                _mpb.SetColor(BaseColorId, c);
                _mpb.SetColor(ColorId, c);
                r.SetPropertyBlock(_mpb);
            }
        }

        private void RestoreFromDeath()
        {
            if (_dissolveCo != null) { StopCoroutine(_dissolveCo); _dissolveCo = null; }
            _isDissolved = false;
            _deathSinkY = 0f;
            if (_baseScaleCaptured) transform.localScale = _baseScale;
            ClearMpb();
        }
    }
}
