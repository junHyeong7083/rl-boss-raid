Shader "BossRaid/RushMark"
{
    // 돌진 표식 전용 쉐이더. "이 녀석을 노린다"가 한눈에 읽히는 지면 조준 표식.
    // 바닥에 평평히 눕힌 Quad(1x1) 위에 대상 발밑을 감싸는 붉은 조준 마커를 그린다.
    //   - 붉은 이중 링(고정 반경 2개)
    //   - 회전하는 조준 십자(crosshair) + 3개 삼각 꼭지(tick)
    //   - windup 진행도(_Progress 0→1)에 따라 바깥→중심으로 조여드는 링
    //   - _Progress 가 커질수록 펄스가 가속(임박 경고)
    // 구조는 SafeGuide/BossTelegraph 와 동일: URP Unlit, Transparent, ZWrite Off.
    // 가산 블렌딩(SrcAlpha One)으로 붉은 발광(HDR+Bloom) 반응.
    Properties
    {
        [HDR] _Color("Mark Color (HDR red)", Color) = (2.4, 0.25, 0.18, 1.0)
        _Progress("Windup Progress 0-1", Range(0, 1)) = 0
        _SpinSpeed("Crosshair Spin Speed", Range(-6, 6)) = 1.4
        _PulseSpeed("Pulse Speed", Range(0, 12)) = 3.0

        // ── 스타일 튜닝 ──
        _RingWidth("Ring Width", Range(0.005, 0.15)) = 0.045
        _OuterRingR("Outer Ring Radius", Range(0.4, 1.0)) = 0.92
        _InnerRingR("Inner Ring Radius", Range(0.2, 0.9)) = 0.70
        _CrossWidth("Crosshair Arm Width", Range(0.005, 0.08)) = 0.022
        _TickRingR("Tick Ring Radius", Range(0.3, 0.95)) = 0.84
        _TickSharp("Tick Angular Sharpness", Range(0.02, 0.4)) = 0.12
        _EdgeSoftness("Outer Edge Softness", Range(0.01, 0.3) ) = 0.10
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+12" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha One          // 가산 — 붉은 조준 발광
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attr { float4 positionOS: POSITION; float2 uv: TEXCOORD0; };
            struct V2F  { float4 positionCS: SV_POSITION; float2 uv: TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Progress;
                float _SpinSpeed;
                float _PulseSpeed;
                float _RingWidth;
                float _OuterRingR;
                float _InnerRingR;
                float _CrossWidth;
                float _TickRingR;
                float _TickSharp;
                float _EdgeSoftness;
            CBUFFER_END

            static const float TAU = 6.28318530718;

            V2F vert(Attr IN)
            {
                V2F OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            // 반경 center 근방의 얇은 링 밴드(0~1).
            float ringBand(float r, float center, float width)
            {
                return 1.0 - smoothstep(0.0, width, abs(r - center));
            }

            half4 frag(V2F IN) : SV_Target
            {
                float2 p = IN.uv - 0.5;
                float r = length(p) * 2.0;          // 0(center) ~ 1(outer edge)
                if (r > 1.0) discard;
                float ang = atan2(p.y, p.x);        // -PI ~ PI

                float prog = saturate(_Progress);

                // 펄스: 진행도가 커질수록 주파수 가속(임박 경고).
                float pulseHz = _PulseSpeed * (1.0 + prog * 2.5);
                float pulse = 0.72 + 0.28 * sin(_Time.y * pulseHz);

                // ── 붉은 이중 링(고정) ──
                float ringOuter = ringBand(r, _OuterRingR, _RingWidth);
                float ringInner = ringBand(r, _InnerRingR, _RingWidth);
                float doubleRing = max(ringOuter, ringInner);

                // ── 회전하는 조준 십자 ──
                float spin = _Time.y * _SpinSpeed;
                float cs = cos(spin), sn = sin(spin);
                float2 rp = float2(cs * p.x + sn * p.y, -sn * p.x + cs * p.y);   // 회전 좌표계
                float crossH = smoothstep(_CrossWidth, 0.0, abs(rp.y));
                float crossV = smoothstep(_CrossWidth, 0.0, abs(rp.x));
                float crosshair = max(crossH, crossV);
                crosshair *= smoothstep(_OuterRingR + 0.02, _OuterRingR - 0.05, r);  // 바깥 링 안쪽만

                // ── 3개 삼각 꼭지(회전 tick) ──
                float tickPhase = frac((ang - spin) / TAU * 3.0);
                float tick = smoothstep(_TickSharp, 0.0, abs(tickPhase - 0.5));
                float ticks = tick * ringBand(r, _TickRingR, 0.09);

                // ── 조여드는 진행 링(바깥→중심) ──
                float pr = lerp(0.94, 0.10, prog);
                float progRing = ringBand(r, pr, _RingWidth * 1.5) * (0.9 + 0.6 * prog);

                // ── 중심 발광(진행할수록 커짐) ──
                float centerDot = smoothstep(0.06 + 0.14 * prog, 0.0, r) * prog;

                float intensity = (doubleRing * 0.9
                                 + crosshair * 0.55
                                 + ticks * 1.25
                                 + progRing * 1.7
                                 + centerDot * 1.4) * pulse;

                // 바깥 경계 소프트 마스크.
                intensity *= smoothstep(1.0, 1.0 - _EdgeSoftness, r);

                float3 col = _Color.rgb * intensity;
                float a = saturate(intensity * _Color.a);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
}
