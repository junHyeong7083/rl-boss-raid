Shader "BossRaid/BloodRuneFloor"
{
    // 절차적 "혈월 마법진" 바닥 쉐이더 (URP / Universal Forward).
    // 텍스처 없이 전부 수식 기반:
    //   · 베이스: 어두운 석판 톤 + cellular(voronoi) 근사로 타일/균열 패턴, 셀별 미세 색 변조
    //   · 혈월 마법진: 중심(UV 0.5) 기준 동심원 3개 + 방사 룬 세그먼트(각도 분할 SDF,
    //     틱/사각 룬 마크 반복) → 진홍 HDR 이미시브, 은은한 회전(_RuneSpin)·펄스(_RunePulse)
    //   · 라이팅: 메인 라이트 Lambert + SH 앰비언트 + (옵션)추가 포인트 라이트 + 그림자 수신
    //
    // 호환: EnvironmentDirector 가 MPB 로 _BaseColor 를 브리딩(호흡)하므로 _BaseColor 프로퍼티 필수.
    //       _BaseColor 는 석판 베이스 톤의 틴트로 쓰이고, 룬은 그 위에 HDR 가산되어 브리딩과 공존한다.
    Properties
    {
        _BaseColor("Base Color (dark slate)", Color) = (0.10, 0.07, 0.11, 1)
        [HDR] _RuneColor("Rune Color (HDR crimson)", Color) = (2.6, 0.32, 0.28, 1)
        _RuneIntensity("Rune Intensity", Range(0, 6)) = 2.2
        _CrackIntensity("Crack / Tile Intensity", Range(0, 1)) = 0.55
        _RuneSpin("Rune Spin Speed", Range(-2, 2)) = 0.12
        _RunePulse("Rune Pulse Speed", Range(0, 8)) = 1.4
        _CircleRadius("Magic Circle Radius (UV 0..0.5)", Range(0.05, 0.5)) = 0.42
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // 그림자 수신 / 추가 라이트 / 안개 지원
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define TAU 6.28318530718

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _RuneColor;
                half  _RuneIntensity;
                half  _CrackIntensity;
                half  _RuneSpin;
                half  _RunePulse;
                half  _CircleRadius;
            CBUFFER_END

            struct Attr
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };
            struct V2F
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float  fogCoord   : TEXCOORD3;
            };

            // ── 해시 노이즈(텍스처 의존 없음) ──
            float2 hash22(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return frac(sin(p) * 43758.5453);
            }
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 345.45));
                p += dot(p, p + 34.345);
                return frac(p.x * p.y);
            }

            // cellular(voronoi) F1/F2 + 셀 id. 균열은 F2-F1 이 작은(셀 경계) 곳.
            float3 cellular(float2 uv)
            {
                float2 g = floor(uv);
                float2 f = frac(uv);
                float f1 = 8.0, f2 = 8.0;
                float2 id = float2(0, 0);
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 o = float2(x, y);
                        float2 r = o + hash22(g + o) - f;
                        float d = dot(r, r);
                        if (d < f1) { f2 = f1; f1 = d; id = g + o; }
                        else if (d < f2) { f2 = d; }
                    }
                }
                return float3(sqrt(f1), sqrt(f2), hash21(id));   // F1, F2, cellId
            }

            // 얇은 링 밴드
            float ringBand(float rn, float target, float w)
            {
                return smoothstep(w, 0.0, abs(rn - target));
            }

            V2F vert(Attr IN)
            {
                V2F OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv         = IN.uv;
                OUT.fogCoord   = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 frag(V2F IN) : SV_Target
            {
                // ─────────── 석판 베이스(cellular) ───────────
                float3 cell = cellular(IN.uv * 9.0);       // 9x9 타일
                float crackEdge = smoothstep(0.06, 0.0, cell.y - cell.x);       // 셀 경계 균열
                float tileShade = lerp(0.82, 1.08, cell.z);                     // 셀별 밝기 변조

                float3 baseCol = _BaseColor.rgb * tileShade;
                // 균열: 어둡게 파이되 미세한 붉은 잔광
                baseCol = lerp(baseCol, baseCol * 0.35 + float3(0.05, 0.005, 0.01), crackEdge * _CrackIntensity);

                // ─────────── 혈월 마법진(중심 기준 절차) ───────────
                float2 c = IN.uv - 0.5;
                float r = length(c);
                float ang = atan2(c.y, c.x) + _Time.y * _RuneSpin;   // 은은한 회전
                float rn = r / max(1e-4, _CircleRadius);             // 0(center)~1(circle edge)

                float a01 = ang / TAU + 0.5;                          // 0~1

                // 동심원 3개
                float rings = ringBand(rn, 1.00, 0.020)
                            + ringBand(rn, 0.66, 0.016)
                            + ringBand(rn, 0.33, 0.013);

                // 바깥 룬 밴드(0.66~1.0)의 방사 틱 마크(룬 세그먼트)
                float outerBand = smoothstep(0.64, 0.68, rn) * smoothstep(1.00, 0.94, rn);
                float segT = abs(frac(a01 * 24.0) - 0.5) * 2.0;       // 24 세그먼트 삼각파(0=중앙)
                float ticks = smoothstep(0.16, 0.02, segT) * outerBand;

                // 안쪽 룬 밴드(0.33~0.66)의 사각 룬 마크
                float innerBand = smoothstep(0.31, 0.35, rn) * smoothstep(0.66, 0.60, rn);
                float segS = abs(frac(a01 * 8.0 + 0.5) - 0.5) * 2.0;  // 8 세그먼트(바깥과 위상 어긋남)
                float glyph = smoothstep(0.30, 0.10, segS) * innerBand;

                // 세그먼트 경계 방사 스포크(짧은 룬 선)
                float spoke = smoothstep(0.04, 0.0, abs(frac(a01 * 12.0) - 0.5))
                            * smoothstep(0.33, 0.37, rn) * smoothstep(1.0, 0.90, rn);

                float runeMask = saturate(rings + ticks + glyph * 0.85 + spoke * 0.7);
                runeMask *= 1.0 - smoothstep(1.0, 1.08, rn);          // 마법진 밖 페이드
                float pulse = 0.70 + 0.30 * sin(_Time.y * _RunePulse);
                float3 emissive = _RuneColor.rgb * (_RuneIntensity * runeMask * pulse);

                // ─────────── 라이팅(Lambert + SH 앰비언트 + 그림자) ───────────
                float3 N = normalize(IN.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float ndotl = saturate(dot(N, mainLight.direction));
                float3 lit = baseCol * mainLight.color * (ndotl * mainLight.shadowAttenuation);
                float3 ambient = SampleSH(N) * baseCol;

            #ifdef _ADDITIONAL_LIGHTS
                uint addCount = GetAdditionalLightsCount();
                for (uint li = 0u; li < addCount; li++)
                {
                    Light al = GetAdditionalLight(li, IN.positionWS);
                    float anl = saturate(dot(N, al.direction));
                    lit += baseCol * al.color * (anl * al.distanceAttenuation * al.shadowAttenuation);
                }
            #endif

                float3 col = lit + ambient + emissive;
                col = MixFog(col, IN.fogCoord);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }

    // 그림자 수신은 위 패스로 충분(바닥은 캐스팅 불필요). 폴백.
    FallBack "Universal Render Pipeline/Lit"
}
