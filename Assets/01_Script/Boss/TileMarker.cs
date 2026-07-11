using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 패턴 텔레그래프 Quad에 붙는 컴포넌트.
    /// BossRaid/Telegraph 쉐이더 파라미터를 제어해 원/부채꼴/빔/십자를 그린다.
    ///
    /// BossGameViewer.RenderShape()가 다음 순서로 호출:
    ///   1) ApplyShape(ShapeData)   — shape 종류, 크기, 회전을 세팅
    ///   2) SetTelegraph(...)       — 색·펄스·진행도
    /// </summary>
    public class TileMarker : MonoBehaviour
    {
        [Header("Renderer")]
        public Renderer rend;

        [Header("Shape Colors — V2 10패턴 (설계 문서 §2)")]
        public Color tripleClawColor   = new Color(1.0f,  0.55f, 0.1f,  0.9f);   // 0 TRIPLE_CLAW: 주황
        public Color earthCrushColor   = new Color(1.0f,  0.35f, 0.05f, 0.9f);   // 1 EARTH_CRUSH: 용암
        public Color frenzyRushColor   = new Color(1.0f,  0.15f, 0.15f, 0.9f);   // 2 FRENZY_RUSH: 빨강
        public Color pillarThrowColor  = new Color(0.85f, 0.45f, 0.15f, 0.9f);   // 3 PILLAR_THROW: 갈주황
        public Color spinSweepColor    = new Color(1.0f,  0.1f,  0.5f,  0.95f);  // 4 SPIN_SWEEP: 핫핑크
        public Color bloodRoarColor    = new Color(0.75f, 0.05f, 0.1f,  0.92f);  // 5 BLOOD_ROAR: 진홍
        public Color crimsonBrandColor = new Color(0.7f,  0.25f, 1.0f,  0.9f);   // 6 CRIMSON_BRAND: 보라
        public Color counterRushColor  = new Color(0.2f,  0.5f,  1.0f,  0.9f);   // 7 COUNTER_RUSH: 파랑
        public Color staggerLiftColor  = new Color(0.2f,  0.85f, 1.0f,  0.85f);  // 8 STAGGER_LIFT: 시안
        public Color sealWipeColor     = new Color(0.6f,  0.0f,  0.15f, 0.95f);  // 9 SEAL_WIPE: 혈월 암적
        public Color rimColor          = new Color(1.0f,  1.0f,  0.7f,  1.0f);   // 림 글로우: 밝은 노랑 (legacy)

        [Header("Fill / Outline — 로스트아크 장판")]
        [Tooltip("테두리 HDR 강도 (블룸 반응). 패턴 색 × 이 값이 _OutlineColor")]
        public float outlineIntensity = 2.5f;
        [Tooltip("한 턴 재생 시간(초) — Fill 보간 속도 기준")]
        public float turnSeconds = 0.3f;

        // URP SRP Batcher는 MaterialPropertyBlock을 UnityPerMaterial CBUFFER에 대해 무시함.
        // → 인스턴스 머티리얼(.material)을 직접 수정하는 방식 사용.
        [Tooltip("Debug 로그로 색 세팅 확인")]
        public bool debugLog = false;

        private Material _mat;

        // ── Fill 부드러운 보간 상태 ──
        private float _targetFill = 1f;   // 목표 채움 (1 - remaining/total)
        private float _currentFill = 1f;  // 현재 화면 채움 (MoveTowards 보간)
        private float _fillSpeed = 3f;    // 초당 Fill 증가량 (턴당 증분 / turnSeconds)
        private int _lastPattern = -1;    // 풀 재활용 시 패턴 변경 감지 → 보간 없이 스냅

        // 전체턴 정보가 스냅샷에 없을 때 fallback: V2 패턴별 스텝 telegraph 대략값(설계 문서 §2).
        // 실제 값은 스냅샷 total_wind_up 가 스텝별로 오므로 이 표는 근사 fallback 용.
        private static readonly int[] PatternWindUp =
        {
            2,   // 0 TRIPLE_CLAW  (스텝 2·2·2)
            3,   // 1 EARTH_CRUSH  (3·2)
            4,   // 2 FRENZY_RUSH  (4)
            3,   // 3 PILLAR_THROW (3·1·1)
            2,   // 4 SPIN_SWEEP   (2·2)
            5,   // 5 BLOOD_ROAR   (5)
            6,   // 6 CRIMSON_BRAND(6)
            3,   // 7 COUNTER_RUSH (창 3)
            6,   // 8 STAGGER_LIFT (창 6)
            12,  // 9 SEAL_WIPE    (wind_up 12)
        };
        // 표/스냅샷 모두 없을 때: 처음 관측된 잔여턴을 total로 캐싱
        private int _cachedPattern = -1;
        private int _cachedTotal = 0;

        private Material EnsureMat()
        {
            if (rend == null) rend = GetComponentInChildren<Renderer>();
            if (rend == null)
            {
                if (debugLog) Debug.LogWarning($"[TileMarker] {name}: Renderer not found!");
                return null;
            }
            // rend.material은 필요 시 인스턴스 생성 (sharedMaterial 복제).
            // 이후 호출은 동일 인스턴스 반환.
            if (_mat == null) _mat = rend.material;
            return _mat;
        }

        private void Awake()
        {
            EnsureMat();
        }

        // 프레임 간 Fill이 계단식으로 튀지 않게 MoveTowards로 부드럽게 보간.
        private void Update()
        {
            if (_mat == null) return;
            if (_currentFill != _targetFill)
            {
                _currentFill = Mathf.MoveTowards(_currentFill, _targetFill, _fillSpeed * Time.deltaTime);
                _mat.SetFloat("_Fill", _currentFill);
            }
        }

        public void ApplyShape(ShapeData shape)
        {
            var m = EnsureMat();
            if (m == null) return;

            int shapeType = 0;
            float fanHalf = 0.785f;
            float safeMask = 0f;
            float donutInner = 0.4f;

            switch (shape.kind)
            {
                case "circle": shapeType = 0; break;
                case "fan":    shapeType = 1; fanHalf = Mathf.Max(0.01f, shape.width * 0.5f); break;
                case "line":   shapeType = 2; break;
                case "cross":  shapeType = 3; safeMask = shape.safe_mask; break;
                case "donut":  // V2: 링 위험. Quad 크기=r_out*2 (Viewer), 내부비율=r_in/r_out
                    shapeType = 4;
                    donutInner = shape.r_out > 1e-4f ? Mathf.Clamp01(shape.r_in / shape.r_out) : 0.4f;
                    break;
            }

            m.SetInt("_ShapeType", shapeType);
            m.SetFloat("_FanWidthRad", fanHalf);
            m.SetFloat("_SafeMask", safeMask);
            m.SetFloat("_DonutInnerRatio", donutInner);

            if (debugLog)
                Debug.Log($"[TileMarker] ApplyShape kind={shape.kind} type={shapeType}");
        }

        public void SetTelegraph(int pattern, int turnsRemaining, int totalWindUp)
        {
            var m = EnsureMat();
            if (m == null) return;

            Color baseCol = ColorFor(pattern);

            // 전체턴 결정: 스냅샷 → 패턴 상수표 → 첫 관측 잔여턴 캐싱 순으로 fallback
            int total = ResolveTotal(pattern, turnsRemaining, totalWindUp);
            // 잔여 k턴 = k턴 후 발동. 이번 턴 동안 (total-k)/total → (total-k+1)/total 로 차오르므로
            // 목표를 (total-remaining+1)/total 로 두면 마지막 턴(remaining=1) 종료 시 정확히 100% 도달.
            // (기존 1-remaining/total 은 최대 (total-1)/total 에서 발동돼 "덜 차고 사라짐")
            float fill = total <= 0 ? 1f : (total - turnsRemaining + 1f) / total;
            fill = Mathf.Clamp01(fill);

            // 패턴이 바뀌었거나(풀 재활용), 같은 패턴 재시전으로 Fill 목표가 현재보다 낮으면
            // (채움은 시전 중 단조 증가만 유효) 역방향 쓸림 방지를 위해 보간 없이 스냅
            if (pattern != _lastPattern || fill < _currentFill)
            {
                _currentFill = fill;
                _lastPattern = pattern;
            }
            _targetFill = fill;
            // 턴당 증분(1/total)을 turnSeconds 동안 이동하도록 속도 설정
            _fillSpeed = (1f / Mathf.Max(1, total)) / Mathf.Max(0.01f, turnSeconds);

            // 테두리 HDR 색 = 패턴 색 × 강도 (블룸용). 알파는 1로 고정.
            Color outCol = baseCol * outlineIntensity;
            outCol.a = 1f;

            m.SetColor("_Color", baseCol);
            m.SetColor("_RimColor", rimColor);
            m.SetColor("_OutlineColor", outCol);
            m.SetFloat("_Progress", fill);                                   // 진행도 = 채움과 동일
            m.SetFloat("_Pulse", turnsRemaining <= 1 ? 1f : 0f);            // 잔여 1턴 이하 깜빡임
            m.SetFloat("_Fill", _currentFill);                              // Update에서 목표까지 보간

            if (debugLog)
                Debug.Log($"[TileMarker] SetTelegraph pattern={pattern} color={baseCol} fill={fill:F2} total={total} remain={turnsRemaining}");
        }

        /// <summary>전체 wind-up 턴 수 결정: 스냅샷 값 → 패턴 상수표 → 첫 관측 잔여턴 캐싱.</summary>
        private int ResolveTotal(int pattern, int remaining, int snapTotal)
        {
            if (snapTotal > 0) return snapTotal;
            if (pattern >= 0 && pattern < PatternWindUp.Length && PatternWindUp[pattern] > 0)
                return PatternWindUp[pattern];

            // 표에도 없으면: 처음 본 잔여턴을 total로 잡고, 이후 더 큰 값이 오면 갱신
            if (_cachedPattern != pattern)
            {
                _cachedPattern = pattern;
                _cachedTotal = Mathf.Max(1, remaining);
            }
            else
            {
                _cachedTotal = Mathf.Max(_cachedTotal, remaining);
            }
            return _cachedTotal;
        }

        // V2 패턴 ID(0~9) → 색. 설계 문서 §2 색상표.
        private Color ColorFor(int pattern)
        {
            switch ((RaidPatternId)pattern)
            {
                case RaidPatternId.TripleClaw:   return tripleClawColor;
                case RaidPatternId.EarthCrush:   return earthCrushColor;
                case RaidPatternId.FrenzyRush:   return frenzyRushColor;
                case RaidPatternId.PillarThrow:  return pillarThrowColor;
                case RaidPatternId.SpinSweep:    return spinSweepColor;
                case RaidPatternId.BloodRoar:    return bloodRoarColor;
                case RaidPatternId.CrimsonBrand: return crimsonBrandColor;
                case RaidPatternId.CounterRush:  return counterRushColor;
                case RaidPatternId.StaggerLift:  return staggerLiftColor;
                case RaidPatternId.SealWipe:     return sealWipeColor;
                default: return Color.white;
            }
        }
    }
}
