using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 스냅샷 도착 간격을 실측(EMA)해 보간 시간을 적응적으로 맞추는 헬퍼.
    /// UDP 스냅샷이 0.3~0.4초로 흔들려도 멈춤/속도 튐 없이 부드럽게 이어지도록 한다.
    /// - EMA 계수 0.2, 초기 간격 0.3초
    /// - 보간 시간 = EMA 간격 × 1.05 (다음 스냅샷이 살짝 늦어도 멈추지 않게 마진)
    /// - 목표까지 거리 ≥ snapDistance면 순간이동(에피소드 리셋/리스폰)으로 보고 스냅
    /// - 선형 보간(easing 없음): 연속 이동 시 매 턴 가속-감속 맥동 제거
    /// UnitView / BossController 양쪽에서 공용으로 쓰는 값 타입.
    /// </summary>
    public struct SnapshotLerp
    {
        private const float EmaAlpha = 0.2f;      // EMA 계수
        private const float DurationMargin = 1.05f; // 보간 시간 마진
        private const float InitialInterval = 0.3f; // 초기 간격 추정치

        // 실측 도착 간격(EMA)
        private float _ema;
        private float _lastArrival;
        private bool _hasArrival;

        // 보간 상태
        private Vector3 _prevPos;
        private Vector3 _targetPos;
        private float _interpStart;
        private float _interpDuration;
        private bool _hasData;

        /// <summary>보간 데이터가 한 번이라도 들어왔는지.</summary>
        public bool HasData => _hasData;
        /// <summary>현재 목표 위치.</summary>
        public Vector3 TargetPos => _targetPos;
        /// <summary>현재 실측 간격(EMA). 디버그용.</summary>
        public float SmoothedInterval => _ema;

        /// <summary>
        /// 새 스냅샷 목표를 등록한다.
        /// current는 현재 transform.position(보간 중이면 그 위치) — 여기서부터 새 목표로 이어붙인다.
        /// 목표까지 거리가 snapDistance 이상이면 순간이동으로 간주(스냅).
        /// </summary>
        /// <returns>스냅(순간이동)으로 처리해야 하면 true.</returns>
        public bool OnSnapshot(Vector3 current, Vector3 target, float snapDistance)
        {
            float now = Time.time;

            // 도착 간격 EMA 갱신
            if (!_hasArrival)
            {
                _ema = InitialInterval;
                _hasArrival = true;
            }
            else
            {
                float dt = now - _lastArrival;
                if (dt > 0.0001f)
                    _ema = Mathf.Lerp(_ema, dt, EmaAlpha);
            }
            _lastArrival = now;

            // 첫 데이터거나 순간이동 거리면 스냅
            bool snap = !_hasData || (target - current).sqrMagnitude >= snapDistance * snapDistance;

            _prevPos = snap ? target : current;
            _targetPos = target;
            _interpStart = now;
            _interpDuration = Mathf.Max(0.01f, _ema * DurationMargin);
            _hasData = true;

            return snap;
        }

        /// <summary>이번 프레임 보간 위치를 계산한다. 진행도 t(0~1)를 out으로 반환.</summary>
        public Vector3 Evaluate(out float t)
        {
            if (!_hasData) { t = 1f; return _targetPos; }
            float elapsed = Time.time - _interpStart;
            t = Mathf.Clamp01(elapsed / _interpDuration);
            return Vector3.LerpUnclamped(_prevPos, _targetPos, t); // 선형 보간
        }

        /// <summary>보간 진행 중 &amp; 실제 이동 거리 있음 = 이동 중.</summary>
        public bool IsMoving(float t)
            => t < 0.999f && (_targetPos - _prevPos).sqrMagnitude > 0.0004f;
    }
}
