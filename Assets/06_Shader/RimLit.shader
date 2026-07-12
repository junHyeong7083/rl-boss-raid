Shader "BossRaid/RimLit"
{
    // 캐릭터용 림라이트 쉐이더 (URP). URP Lit 을 단순화 + 프레넬 림 강조.
    //   · _BaseMap * _BaseColor 알베도 + 단순 Lambert(메인/추가 라이트) + SH 앰비언트
    //   · 프레넬 림라이트(_RimColor HDR, _RimPower, _RimIntensity) — 실루엣을 살림
    //   · _EmissionColor(+_EMISSION 키워드) — 피격 이미시브 플래시가 그대로 동작
    //   · ShadowCaster 패스 — 캐릭터가 혈월 바닥에 그림자를 드리움
    //
    // 히트플래시/역할틴트 호환:
    //   · 역할 틴트: BossGameViewer.ApplyRoleTint 가 material._BaseColor(없으면 _Color) 세팅 → _BaseColor 사용.
    //   · UnitView 피격/디졸브: MPB 로 _BaseColor/_Color/_EmissionColor 세팅 → 모두 선언(호환).
    //   · BossController 피격: MPB _EmissionColor + r.material.EnableKeyword("_EMISSION") → _EMISSION 대응.
    //   · _Color 는 별칭 호환용으로 선언(MPB 세팅 무해). 실제 알베도 틴트는 _BaseColor 사용.
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Color("Color (alias, compat)", Color) = (1, 1, 1, 1)
        [HDR] _EmissionColor("Emission Color", Color) = (0, 0, 0, 0)
        [HDR] _RimColor("Rim Color (HDR)", Color) = (1.0, 0.35, 0.30, 1)
        _RimPower("Rim Power", Range(0.5, 8)) = 3.0
        _RimIntensity("Rim Intensity", Range(0, 4)) = 1.4
        _Smoothness("Diffuse Wrap", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        // ─────────── Forward(라이팅 + 림 + 이미시브) ───────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _EMISSION
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _Color;
                half4 _EmissionColor;
                half4 _RimColor;
                half  _RimPower;
                half  _RimIntensity;
                half  _Smoothness;
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

            V2F vert(Attr IN)
            {
                V2F OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogCoord   = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 frag(V2F IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half3 albedo = tex.rgb * _BaseColor.rgb;

                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                // half-lambert 성향(로우폴리에서 그림자면이 너무 검게 죽지 않도록 _Smoothness 로 wrap)
                float ndl = dot(N, mainLight.direction);
                float diff = saturate(lerp(ndl, ndl * 0.5 + 0.5, _Smoothness));
                float3 lit = albedo * mainLight.color * (diff * mainLight.shadowAttenuation);
                float3 ambient = SampleSH(N) * albedo;

            #ifdef _ADDITIONAL_LIGHTS
                uint addCount = GetAdditionalLightsCount();
                for (uint li = 0u; li < addCount; li++)
                {
                    Light al = GetAdditionalLight(li, IN.positionWS);
                    float anl = saturate(dot(N, al.direction));
                    lit += albedo * al.color * (anl * al.distanceAttenuation * al.shadowAttenuation);
                }
            #endif

                // 프레넬 림라이트
                float fres = pow(1.0 - saturate(dot(N, V)), _RimPower);
                float3 rim = _RimColor.rgb * (fres * _RimIntensity);

                float3 col = lit + ambient + rim;
            #ifdef _EMISSION
                col += _EmissionColor.rgb;
            #endif

                col = MixFog(col, IN.fogCoord);
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        // ─────────── ShadowCaster(캐릭터 그림자 → 혈월 바닥) ───────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Shadows.hlsl 이 내부에서 LerpWhiteTo 를 사용 — CommonMaterial 을 먼저 포함해야 컴파일됨.
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct AttrS { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V2FS  { float4 positionCS : SV_POSITION; };

            float4 GetShadowPositionHClip(AttrS IN)
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            V2FS shadowVert(AttrS IN)
            {
                V2FS OUT;
                OUT.positionCS = GetShadowPositionHClip(IN);
                return OUT;
            }
            half4 shadowFrag(V2FS IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
