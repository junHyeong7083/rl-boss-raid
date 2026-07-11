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

        private void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
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
                _aliveKnown = true;
                _prevAlive = u.alive;
                if (deathEffect) deathEffect.SetActive(!u.alive);
                SafeSetBool(paramDead, !u.alive);
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
                case "damage_taken": SafeSetTrigger(trigHit); break;
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

        private void LateUpdate()
        {
            // 표식이 꺼져야 하면 여기서 제어 (스냅샷의 marked 필드로)
            if (_markInstance != null && _hasData && !_latest.marked)
                _markInstance.SetActive(false);
        }
    }
}
