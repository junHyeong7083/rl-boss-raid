using System.Collections;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 타격감용 히트스톱(순간 슬로우/정지) 싱글턴.
    /// Time.timeScale을 잠깐 낮췄다가 unscaled time으로 복원한다.
    /// - 중첩 호출 시 "더 늦게 끝나는" 쪽을 우선(긴 히트스톱이 짧은 걸 덮어씀)
    /// - Time.fixedDeltaTime도 timeScale에 비례 조정 후 원복(물리 스텝 왜곡 방지)
    /// </summary>
    [DisallowMultipleComponent]
    public class HitStopManager : MonoBehaviour
    {
        private static HitStopManager _instance;

        // 원래 값 백업 (첫 히트스톱 진입 시점 기준)
        private float _defaultTimeScale = 1f;
        private float _defaultFixedDelta = 0.02f;

        private Coroutine _routine;
        private float _restoreAtUnscaled = -1f;   // 이 unscaled 시각에 복원 (중첩 시 max로 연장)

        /// <summary>씬에 인스턴스가 없으면 자동 생성.</summary>
        private static HitStopManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("HitStopManager");
                    _instance = go.AddComponent<HitStopManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            _defaultFixedDelta = Time.fixedDeltaTime;
        }

        /// <summary>
        /// 히트스톱 실행.
        /// </summary>
        /// <param name="duration">멈춤 지속 시간(초, unscaled).</param>
        /// <param name="timeScale">멈춤 동안의 Time.timeScale (0.05 = 거의 정지).</param>
        public static void HitStop(float duration = 0.08f, float timeScale = 0.05f)
        {
            Instance.Begin(duration, timeScale);
        }

        private void Begin(float duration, float timeScale)
        {
            duration = Mathf.Max(0f, duration);
            timeScale = Mathf.Clamp01(timeScale);

            float newRestore = Time.unscaledTime + duration;

            if (_routine == null)
            {
                // 진행 중 히트스톱이 없을 때만 원래 값 백업 (중첩 중엔 이미 slow 상태이므로 백업 금지)
                _defaultTimeScale = Time.timeScale;
                _defaultFixedDelta = Time.fixedDeltaTime;
                _restoreAtUnscaled = newRestore;
                _routine = StartCoroutine(Run(timeScale));
            }
            else
            {
                // 중첩: 더 늦게 끝나는 쪽으로 연장하고, 더 강한(작은) slow를 적용
                _restoreAtUnscaled = Mathf.Max(_restoreAtUnscaled, newRestore);
                float target = _defaultTimeScale * timeScale;
                if (target < Time.timeScale)
                {
                    Time.timeScale = target;
                    Time.fixedDeltaTime = _defaultFixedDelta * Time.timeScale;
                }
            }
        }

        private IEnumerator Run(float timeScale)
        {
            Time.timeScale = _defaultTimeScale * timeScale;
            Time.fixedDeltaTime = _defaultFixedDelta * Time.timeScale;

            // 복원 시각까지 대기 (WaitForSecondsRealtime과 달리 중첩 연장에 반응)
            while (Time.unscaledTime < _restoreAtUnscaled)
                yield return null;

            Time.timeScale = _defaultTimeScale;
            Time.fixedDeltaTime = _defaultFixedDelta;
            _routine = null;
        }
    }
}
