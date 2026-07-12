using System.Collections.Generic;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 오디오 에셋 0개로 상용 톤 SFX를 코드 합성하는 정적 팩토리.
    /// AudioClip.Create 로 44.1kHz 모노 클립을 파형 합성하고 종류당 1회만 만들어 캐시한다.
    /// (나중에 사용자가 SFX 팩을 넣으면 RaidAudioManager 쪽 재생부만 교체하면 됨.)
    ///
    /// 합성 기법: 사인/삼각/톱니 + 화이트/브라운 노이즈 → ADSR·지수 감쇠 엔벨로프 →
    ///   피치 스윕 → 원폴 로우패스 근사. 마지막에 정규화(클리핑 방지) + 양끝 페이드(클릭 제거).
    /// </summary>
    public static class ProceduralSfx
    {
        public const int SampleRate = 44100;

        // 종류당 1개만 합성해 재사용.
        private static readonly Dictionary<SfxKind, AudioClip> _cache = new Dictionary<SfxKind, AudioClip>();

        // 합성용 난수(결정적) — 매 합성 시작 시 재시드해 클립을 재현 가능하게.
        private static System.Random _rng = new System.Random();

        /// <summary>종류에 해당하는 합성 클립을 반환(최초 1회 합성 후 캐시).</summary>
        public static AudioClip Get(SfxKind kind)
        {
            if (_cache.TryGetValue(kind, out var cached) && cached != null) return cached;

            _rng = new System.Random(0x5EED + (int)kind);   // 종류별 고정 시드
            float[] data = Synth(kind);
            FinalizeBuffer(data);   // 정규화 + 어택 페이드인 + 끝 페이드아웃

            var clip = AudioClip.Create("Sfx_" + kind, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            _cache[kind] = clip;
            return clip;
        }

        // ─────────────────────────── 종류 → 합성 라우팅 ───────────────────────────

        private static float[] Synth(SfxKind kind)
        {
            switch (kind)
            {
                case SfxKind.Hit:        return SynthHit();
                case SfxKind.HitCrit:    return SynthHitCrit();
                case SfxKind.Explosion:  return SynthExplosion();
                case SfxKind.Throw:      return SynthThrow();
                case SfxKind.Dash:       return SynthDash();
                case SfxKind.Counter:    return SynthCounter();
                case SfxKind.Guard:      return SynthGuard();
                case SfxKind.HealChime:  return SynthHealChime();
                case SfxKind.BuffChime:  return SynthBuffChime();
                case SfxKind.Warning:    return SynthWarning();
                case SfxKind.SealAlarm:  return SynthSealAlarm();
                case SfxKind.UiClick:    return SynthUiClick();
                case SfxKind.Victory:    return SynthVictory();
                case SfxKind.Defeat:     return SynthDefeat();
                case SfxKind.BossRoar:   return SynthBossRoar();
                default:                 return SynthUiClick();
            }
        }

        // ─────────────────────────── 개별 합성기 ───────────────────────────

        /// <summary>평타 타격: 노이즈 버스트 60ms(빠른 감쇠) + 저역 사인 펀치(90→60Hz).</summary>
        private static float[] SynthHit()
        {
            var buf = Buf(0.09f);
            NoiseBurst(buf, 0f, 0.06f, 0.9f, 12f, 4200f);          // 짧은 노이즈 어택
            SweepTone(buf, 0f, 0.09f, 90f, 60f, 0.85f, 0.028f, Wave.Sine);   // 저역 펀치
            return buf;
        }

        /// <summary>크리티컬 가산 레이어: 고음 금속성 링 모드 잔향(비배음 파셜) 120ms.</summary>
        private static float[] SynthHitCrit()
        {
            var buf = Buf(0.13f);
            // 비배음(inharmonic) 파셜 합 → 금속성 "칭" 잔향.
            AddPartial(buf, 0f, 0.12f, 2450f, 0.5f, 0.05f, Wave.Sine);
            AddPartial(buf, 0f, 0.12f, 3670f, 0.38f, 0.045f, Wave.Sine);
            AddPartial(buf, 0f, 0.12f, 5230f, 0.28f, 0.04f, Wave.Sine);
            AddPartial(buf, 0f, 0.10f, 7100f, 0.16f, 0.03f, Wave.Sine);
            NoiseBurst(buf, 0f, 0.015f, 0.25f, 6f, 9000f);         // 살짝의 밝은 어택 노이즈
            return buf;
        }

        /// <summary>혈월 임팩트: 브라운 노이즈 300ms + 저역 사인 스윕 120→40Hz.</summary>
        private static float[] SynthExplosion()
        {
            var buf = Buf(0.34f);
            BrownNoise(buf, 0f, 0.30f, 0.95f, 0.10f, 1400f);       // 무게감 있는 저역 노이즈
            SweepTone(buf, 0f, 0.14f, 120f, 40f, 0.9f, 0.06f, Wave.Sine);   // 저역 임팩트 스윕
            SweepTone(buf, 0f, 0.30f, 60f, 30f, 0.5f, 0.14f, Wave.Sine);    // 잔여 럼블
            return buf;
        }

        /// <summary>혈창 발사(슉): 화이트 노이즈 밴드 + 상승 로우패스 스윕.</summary>
        private static float[] SynthThrow()
        {
            var buf = Buf(0.20f);
            // 컷오프가 오르내리는 노이즈 = 바람 가르는 "슉".
            SweptNoise(buf, 0f, 0.18f, 0.8f, 700f, 5200f, 2600f, 0.06f);
            return buf;
        }

        /// <summary>대시(짧은 바람 슉 80ms).</summary>
        private static float[] SynthDash()
        {
            var buf = Buf(0.09f);
            SweptNoise(buf, 0f, 0.08f, 0.7f, 1200f, 6000f, 1500f, 0.02f);
            return buf;
        }

        /// <summary>저지 성공: 금속 클랭(밝은 비배음) + 슬로모용 피치다운 테일 400ms.</summary>
        private static float[] SynthCounter()
        {
            var buf = Buf(0.45f);
            NoiseBurst(buf, 0f, 0.02f, 0.7f, 4f, 8000f);                   // 밝은 클랭 어택
            AddPartial(buf, 0f, 0.16f, 1850f, 0.55f, 0.06f, Wave.Sine);    // 금속 파셜들
            AddPartial(buf, 0f, 0.16f, 2770f, 0.4f, 0.05f, Wave.Sine);
            AddPartial(buf, 0f, 0.14f, 4100f, 0.28f, 0.04f, Wave.Sine);
            SweepTone(buf, 0.03f, 0.40f, 520f, 140f, 0.5f, 0.22f, Wave.Triangle);  // 피치다운 테일(슬로모감)
            return buf;
        }

        /// <summary>방패 가드: 둔탁한 금속(저역 파셜, 강한 감쇠).</summary>
        private static float[] SynthGuard()
        {
            var buf = Buf(0.20f);
            NoiseBurst(buf, 0f, 0.02f, 0.5f, 6f, 2200f);                   // 둔탁한 어택
            AddPartial(buf, 0f, 0.16f, 320f, 0.7f, 0.06f, Wave.Sine);
            AddPartial(buf, 0f, 0.14f, 540f, 0.45f, 0.05f, Wave.Triangle);
            AddPartial(buf, 0f, 0.12f, 880f, 0.25f, 0.04f, Wave.Sine);
            return buf;
        }

        /// <summary>2음 상승 차임(사인 벨): 치유감.</summary>
        private static float[] SynthHealChime()
        {
            var buf = Buf(0.5f);
            Bell(buf, 0.00f, 0.30f, 784f, 0.7f);    // G5
            Bell(buf, 0.12f, 0.36f, 1047f, 0.7f);   // C6
            return buf;
        }

        /// <summary>3음 아르페지오 상승(버프).</summary>
        private static float[] SynthBuffChime()
        {
            var buf = Buf(0.55f);
            Bell(buf, 0.00f, 0.24f, 659f, 0.6f);    // E5
            Bell(buf, 0.09f, 0.24f, 880f, 0.6f);    // A5
            Bell(buf, 0.18f, 0.34f, 1319f, 0.65f);  // E6
            return buf;
        }

        /// <summary>패턴 경고: 짧은 저음 혼 2회(로우패스 톱니).</summary>
        private static float[] SynthWarning()
        {
            var buf = Buf(0.55f);
            Horn(buf, 0.00f, 0.18f, 155f, 0.85f);
            Horn(buf, 0.26f, 0.20f, 155f, 0.85f);
            return buf;
        }

        /// <summary>전멸기 사이렌: 반음 왕복(불안한) 700ms.</summary>
        private static float[] SynthSealAlarm()
        {
            var buf = Buf(0.72f);
            // 두 음(반음차) 사이를 흔들리며 오가는 사이렌 + 로우패스 톱니로 경보음색.
            int n = buf.Length;
            double frac = 0;
            float f1 = 440f, f2 = 466.16f;  // A4 ↔ A#4 (반음)
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / SampleRate;
                float lfo = 0.5f * (1f + Mathf.Sin(2f * Mathf.PI * 5.5f * t));   // 5.5Hz 왕복
                float f = Mathf.Lerp(f1, f2, lfo);
                frac += f / SampleRate; if (frac >= 1.0) frac -= 1.0;
                float saw = 2f * (float)frac - 1f;
                float env = Mathf.Min(1f, t / 0.02f) * (0.7f + 0.3f * lfo);      // 음량도 약간 흔들림
                buf[i] += 0.8f * env * saw;
            }
            OnePoleLowPass(buf, 2000f);   // 톱니 고역 정리 → 경보 사이렌 톤
            return buf;
        }

        /// <summary>UI 클릭: 아주 짧은 사인 블립.</summary>
        private static float[] SynthUiClick()
        {
            var buf = Buf(0.05f);
            SweepTone(buf, 0f, 0.045f, 1400f, 900f, 0.7f, 0.012f, Wave.Sine);
            return buf;
        }

        /// <summary>승리 팡파레: 장3화음 근사(루트→3화음) 600ms.</summary>
        private static float[] SynthVictory()
        {
            var buf = Buf(0.62f);
            // C major: C5-E5-G5. 살짝의 계단식 진입 후 화음 지속.
            Horn(buf, 0.00f, 0.18f, 523.25f, 0.55f);
            Horn(buf, 0.10f, 0.16f, 659.25f, 0.5f);
            Horn(buf, 0.20f, 0.40f, 523.25f, 0.5f);   // 화음 지속부
            Horn(buf, 0.20f, 0.40f, 659.25f, 0.45f);
            Horn(buf, 0.20f, 0.40f, 783.99f, 0.45f);
            Bell(buf, 0.20f, 0.40f, 1046.5f, 0.3f);   // 옥타브 반짝임
            return buf;
        }

        /// <summary>패배: 단3화음 하강.</summary>
        private static float[] SynthDefeat()
        {
            var buf = Buf(0.62f);
            // A minor 하강: A4 → C5는 아니고 하강감 위해 A4→F4→D4.
            Horn(buf, 0.00f, 0.22f, 440.00f, 0.6f);
            Horn(buf, 0.16f, 0.24f, 349.23f, 0.6f);
            Horn(buf, 0.34f, 0.28f, 293.66f, 0.6f);
            return buf;
        }

        /// <summary>보스 포효: 저역 톱니 + 노이즈, tanh 디스토션 근사 400ms.</summary>
        private static float[] SynthBossRoar()
        {
            var buf = Buf(0.42f);
            int n = buf.Length;
            double frac = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / SampleRate;
                float f = Mathf.Lerp(78f, 62f, t / 0.42f);          // 살짝 하강하는 저역
                frac += f / SampleRate; if (frac >= 1.0) frac -= 1.0;
                float saw = 2f * (float)frac - 1f;
                float noise = (float)(_rng.NextDouble() * 2.0 - 1.0) * 0.4f;
                float env = Mathf.Min(1f, t / 0.03f) * Mathf.Exp(-t / 0.28f);
                float raw = (saw + noise) * env * 2.2f;
                buf[i] += (float)System.Math.Tanh(raw);             // 디스토션(포화)
            }
            OnePoleLowPass(buf, 1600f);
            return buf;
        }

        // ─────────────────────────── 합성 프리미티브 ───────────────────────────

        private enum Wave { Sine, Triangle, Saw }

        private static float[] Buf(float seconds)
            => new float[Mathf.Max(1, (int)(seconds * SampleRate))];

        private static float Osc(Wave w, double frac)
        {
            switch (w)
            {
                case Wave.Triangle: return 4f * Mathf.Abs((float)frac - 0.5f) - 1f;
                case Wave.Saw:      return 2f * (float)frac - 1f;
                default:            return Mathf.Sin(2f * Mathf.PI * (float)frac);
            }
        }

        /// <summary>고정 주파수 파셜을 지수 감쇠(감쇠 시간상수 decay)로 가산. 어택 2ms.</summary>
        private static void AddPartial(float[] buf, float start, float dur, float freq,
                                       float amp, float decay, Wave wave)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)(dur * SampleRate);
            double frac = 0, dp = freq / SampleRate;
            for (int i = 0; i < n; i++)
            {
                int idx = s0 + i; if (idx < 0) continue; if (idx >= buf.Length) break;
                float t = (float)i / SampleRate;
                float env = Mathf.Min(1f, t / 0.002f) * Mathf.Exp(-t / Mathf.Max(1e-4f, decay));
                frac += dp; if (frac >= 1.0) frac -= 1.0;
                buf[idx] += amp * env * Osc(wave, frac);
            }
        }

        /// <summary>주파수가 f0→f1 로 스윕하는 톤을 지수 감쇠로 가산.</summary>
        private static void SweepTone(float[] buf, float start, float dur, float f0, float f1,
                                      float amp, float decay, Wave wave)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)(dur * SampleRate);
            double frac = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = s0 + i; if (idx < 0) continue; if (idx >= buf.Length) break;
                float t = (float)i / SampleRate;
                float k = n > 1 ? (float)i / (n - 1) : 0f;
                float f = Mathf.Lerp(f0, f1, k);
                frac += f / SampleRate; if (frac >= 1.0) frac -= 1.0;
                float env = Mathf.Min(1f, t / 0.002f) * Mathf.Exp(-t / Mathf.Max(1e-4f, decay));
                buf[idx] += amp * env * Osc(wave, frac);
            }
        }

        /// <summary>화이트 노이즈 버스트 + 지수 감쇠 + 로우패스(클릭 없는 타격 노이즈).</summary>
        private static void NoiseBurst(float[] buf, float start, float dur, float amp,
                                       float decayRate, float lpHz)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)(dur * SampleRate);
            // 임시 버퍼에 만들고 로우패스 후 가산(전역 필터 오염 방지).
            var tmp = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / SampleRate;
                float env = Mathf.Min(1f, t / 0.001f) * Mathf.Exp(-t * decayRate);
                tmp[i] = (float)(_rng.NextDouble() * 2.0 - 1.0) * env;
            }
            OnePoleLowPass(tmp, lpHz);
            for (int i = 0; i < n; i++)
            {
                int idx = s0 + i; if (idx < 0) continue; if (idx >= buf.Length) break;
                buf[idx] += amp * tmp[i];
            }
        }

        /// <summary>브라운(적분) 노이즈 + 지수 감쇠 저역 럼블.</summary>
        private static void BrownNoise(float[] buf, float start, float dur, float amp,
                                       float decay, float lpHz)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)(dur * SampleRate);
            var tmp = new float[n];
            float last = 0f;
            for (int i = 0; i < n; i++)
            {
                float white = (float)(_rng.NextDouble() * 2.0 - 1.0);
                last = (last + 0.02f * white) * 0.996f;   // 적분 + 누수(발산 방지)
                float t = (float)i / SampleRate;
                float env = Mathf.Min(1f, t / 0.003f) * Mathf.Exp(-t / Mathf.Max(1e-4f, decay));
                tmp[i] = last * env;
            }
            OnePoleLowPass(tmp, lpHz);
            // 브라운 노이즈는 진폭이 작으므로 지역 정규화 후 가산.
            NormalizeLocal(tmp, 1f);
            for (int i = 0; i < n; i++)
            {
                int idx = s0 + i; if (idx < 0) continue; if (idx >= buf.Length) break;
                buf[idx] += amp * tmp[i];
            }
        }

        /// <summary>컷오프가 lp0→lpPeak→lp1 로 오르내리는 노이즈("슉" 휘프).</summary>
        private static void SweptNoise(float[] buf, float start, float dur, float amp,
                                       float lp0, float lpPeak, float lp1, float decay)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)(dur * SampleRate);
            var tmp = new float[n];
            for (int i = 0; i < n; i++) tmp[i] = (float)(_rng.NextDouble() * 2.0 - 1.0);

            // 가변 원폴 로우패스(샘플별 컷오프) — 컷오프가 삼각형으로 오르내림.
            float y = 0f;
            for (int i = 0; i < n; i++)
            {
                float k = n > 1 ? (float)i / (n - 1) : 0f;
                float cut = k < 0.5f ? Mathf.Lerp(lp0, lpPeak, k * 2f)
                                     : Mathf.Lerp(lpPeak, lp1, (k - 0.5f) * 2f);
                float a = 1f - Mathf.Exp(-2f * Mathf.PI * cut / SampleRate);
                y += a * (tmp[i] - y);
                float t = (float)i / SampleRate;
                // 부드러운 벨형 엔벨로프(어택-릴리스) + 지수 감쇠 꼬리.
                float env = Mathf.Sin(Mathf.PI * k) * Mathf.Exp(-t / Mathf.Max(1e-4f, decay + dur));
                tmp[i] = y * env;
            }
            NormalizeLocal(tmp, 1f);
            for (int i = 0; i < n; i++)
            {
                int idx = s0 + i; if (idx < 0) continue; if (idx >= buf.Length) break;
                buf[idx] += amp * tmp[i];
            }
        }

        /// <summary>벨(사인 + 옥타브 배음, 지수 감쇠) — 차임/반짝임.</summary>
        private static void Bell(float[] buf, float start, float dur, float freq, float amp)
        {
            AddPartial(buf, start, dur, freq, amp, dur * 0.45f, Wave.Sine);
            AddPartial(buf, start, dur * 0.8f, freq * 2.01f, amp * 0.35f, dur * 0.3f, Wave.Sine);
            AddPartial(buf, start, dur * 0.6f, freq * 3.0f, amp * 0.15f, dur * 0.2f, Wave.Sine);
        }

        /// <summary>혼(로우패스 톱니, 부드러운 어택-서스테인-릴리스) — 경고/팡파레.</summary>
        private static void Horn(float[] buf, float start, float dur, float freq, float amp)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)(dur * SampleRate);
            var tmp = new float[n];
            double frac = 0;
            for (int i = 0; i < n; i++)
            {
                frac += freq / SampleRate; if (frac >= 1.0) frac -= 1.0;
                float saw = 2f * (float)frac - 1f;
                float k = n > 1 ? (float)i / (n - 1) : 0f;
                // 사다리꼴 엔벨로프: 어택 8%, 릴리스 30%.
                float env = 1f;
                if (k < 0.08f) env = k / 0.08f;
                else if (k > 0.70f) env = Mathf.Max(0f, (1f - k) / 0.30f);
                tmp[i] = saw * env;
            }
            OnePoleLowPass(tmp, freq * 6f + 400f);   // 배음 다듬어 부드러운 혼 톤
            for (int i = 0; i < n; i++)
            {
                int idx = s0 + i; if (idx < 0) continue; if (idx >= buf.Length) break;
                buf[idx] += amp * tmp[i];
            }
        }

        // ─────────────────────────── 필터 / 후처리 ───────────────────────────

        /// <summary>원폴 로우패스(제자리). 이동평균 대비 위상/롤오프가 자연스러움.</summary>
        private static void OnePoleLowPass(float[] buf, float cutoffHz)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(1f, cutoffHz) / SampleRate);
            float y = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                y += a * (buf[i] - y);
                buf[i] = y;
            }
        }

        /// <summary>지역 버퍼를 피크 target 으로 정규화.</summary>
        private static void NormalizeLocal(float[] buf, float target)
        {
            float peak = 0f;
            for (int i = 0; i < buf.Length; i++) { float a = Mathf.Abs(buf[i]); if (a > peak) peak = a; }
            if (peak < 1e-6f) return;
            float g = target / peak;
            for (int i = 0; i < buf.Length; i++) buf[i] *= g;
        }

        /// <summary>최종 처리: 피크 0.9 정규화 + 어택 페이드인(1.5ms) + 끝 페이드아웃(10ms).</summary>
        private static void FinalizeBuffer(float[] buf)
        {
            NormalizeLocal(buf, 0.9f);

            int fin = Mathf.Min(buf.Length, (int)(0.0015f * SampleRate));   // 클릭 방지 어택
            for (int i = 0; i < fin; i++) buf[i] *= (float)i / fin;

            int fout = Mathf.Min(buf.Length, (int)(0.010f * SampleRate));   // 끝 10ms 페이드아웃
            for (int i = 0; i < fout; i++)
            {
                int idx = buf.Length - 1 - i;
                if (idx < 0) break;
                buf[idx] *= (float)i / fout;
            }
        }
    }

    /// <summary>합성 SFX 종류(캐시 키).</summary>
    public enum SfxKind
    {
        Hit,
        HitCrit,
        Explosion,
        Throw,
        Dash,
        Counter,
        Guard,
        HealChime,
        BuffChime,
        Warning,
        SealAlarm,
        UiClick,
        Victory,
        Defeat,
        BossRoar,
    }
}
