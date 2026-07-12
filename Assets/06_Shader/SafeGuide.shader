Shader "BossRaid/SafeGuide"
{
    // "어디로 가야 하는가" 안전 유도 쉐이더. (로스트아크식 초록 유도 장판)
    // 바닥에 평평히 눕힌 Quad(1x1) 위에 초록 발광 원 + 중심으로 흘러드는
    // 방향성 화살촉(chevron) 링 애니메이션을 그려 "이쪽으로 오라"를 표현한다.
    // Quad 의 localScale/position 은 SafeGuideMarker.cs 가 안전 지점에 맞춘다.
    //
    // 구조는 BossTelegraph.shader 와 동일: URP Unlit, Transparent, ZWrite Off.
    //   - 초록 코어 글로우(중심이 밝고 가장자리로 감쇠)
    //   - chevron 링: 반경 방향으로 배열된 V자 화살촉이 시간에 따라 중심으로 수축
    //   - 은은한 시간 기반 펄스
    Properties
    {
        [HDR] _Color("Guide Color (green)", Color) = (0.25, 1.6, 0.6, 0.9)
        _PulseSpeed("Pulse Speed", Range(0, 8)) = 2.2
        _ArrowTiling("Arrow Ring Count", Range(1, 12)) = 4
        _ArrowSpeed("Arrow Inflow Speed", Range(0, 4)) = 1.1
        _ArrowSharp("Arrow Head Sharpness", Range(0.02, 0.5)) = 0.16
        _ChevronDepth("Chevron V Depth", Range(0, 1)) = 0.45
        _Segments("Chevron Segments", Range(4, 24)) = 12
        _CoreGlow("Core Glow Strength", Range(0, 3)) = 1.3
        _EdgeSoftness("Outer Edge Softness", Range(0.01, 0.4)) = 0.12
        _RingAlpha("Base Ring Alpha", Range(0, 1)) = 0.28
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+11" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha One          // 가산 블렌딩 — 초록 발광 유도(위험 붉은 장판과 대비)
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
                float _PulseSpeed;
                float _ArrowTiling;
                float _ArrowSpeed;
                float _ArrowSharp;
                float _ChevronDepth;
                float _Segments;
                float _CoreGlow;
                float _EdgeSoftness;
                float _RingAlpha;
            CBUFFER_END

            static const float TAU = 6.28318530718;

            V2F vert(Attr IN)
            {
                V2F OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(V2F IN) : SV_Target
            {
                float2 p = IN.uv - 0.5;             // 중심 기준
                float r = length(p) * 2.0;          // 0(center) ~ 1(outer edge)
                if (r > 1.0) discard;

                float ang = atan2(p.y, p.x);        // -PI ~ PI

                // ── 바깥 경계 소프트 감쇠(원형 마스크) ──
                float disc = smoothstep(1.0, 1.0 - _EdgeSoftness, r);

                // ── 코어 글로우: 중심이 밝은 "목적지" 발광 ──
                float core = smoothstep(0.55, 0.0, r) * _CoreGlow;

                // ── chevron 링: 각 세그먼트 안에서 V자로 접힌 반경 밴드가 중심으로 수축 ──
                // 세그먼트별 삼각 좌표(0=세그 중앙, 1=세그 경계) → V자 굴곡 생성
                float a01 = ang / TAU + 0.5;                       // 0~1
                float segTri = abs(frac(a01 * _Segments) - 0.5) * 2.0;
                // +시간: 위상이 커지며 프론트(frac==1)가 작은 r 로 이동 → 중심으로 흘러듦
                float phase = r * _ArrowTiling + _Time.y * _ArrowSpeed - segTri * _ChevronDepth;
                float f = frac(phase);
                // 화살촉: 밴드 프론트(f→1) 근처만 밝게 → 안쪽을 향한 뾰족한 헤드
                float chev = smoothstep(1.0 - _ArrowSharp, 1.0, f);
                // 화살은 링 영역(중심/가장자리 제외)에서만 또렷하게
                float ringBand = smoothstep(0.12, 0.35, r) * smoothstep(1.0, 0.82, r);
                chev *= ringBand;

                // ── 은은한 전체 펄스 ──
                float pulse = 0.78 + 0.22 * sin(_Time.y * _PulseSpeed);

                // 합성: 기본 링 알파 + 화살촉 + 코어
                float intensity = (_RingAlpha * ringBand + chev * 1.6 + core) * pulse;
                intensity *= disc;

                float3 col = _Color.rgb * intensity;
                float a = saturate(intensity * _Color.a);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
}
