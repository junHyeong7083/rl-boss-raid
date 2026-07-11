Shader "BossRaid/Energy"
{
    // 보스 이펙트용 범용 쉐이더.
    // - 프레넬(외곽 발광)
    // - 시간 기반 펄스
    // - 투명/가산 혼합 블렌드 선택 가능
    Properties
    {
        _Color("Base Color", Color) = (0.2, 0.6, 1.0, 0.6)
        _RimColor("Rim Color", Color) = (0.5, 0.8, 1.0, 1.0)
        _RimPower("Rim Power", Range(0.1, 8)) = 2.5
        _PulseSpeed("Pulse Speed", Range(0, 10)) = 3
        _PulseAmp("Pulse Amplitude", Range(0, 1)) = 0.3
        _Additive("Additive Blend (1=Add, 0=Alpha)", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha One          // Additive by default; OneMinusSrcAlpha은 아래서 조절
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attr { float4 positionOS: POSITION; float3 normalOS: NORMAL; float2 uv: TEXCOORD0; };
            struct V2F { float4 positionCS: SV_POSITION; float3 normalWS: TEXCOORD0; float3 viewDirWS: TEXCOORD1; float2 uv: TEXCOORD2; };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _RimColor;
                float _RimPower;
                float _PulseSpeed;
                float _PulseAmp;
                float _Additive;
            CBUFFER_END

            V2F vert(Attr IN)
            {
                V2F OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pos.positionCS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.viewDirWS = GetWorldSpaceViewDir(pos.positionWS);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(V2F IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(IN.viewDirWS);
                float ndv = saturate(dot(N, V));
                float rim = pow(1.0 - ndv, _RimPower);

                float pulse = 1.0 + sin(_Time.y * _PulseSpeed) * _PulseAmp;

                float3 col = lerp(_Color.rgb, _RimColor.rgb, rim);
                col *= pulse;

                float a = lerp(_Color.a, _RimColor.a, rim) * pulse;
                return half4(col, a);
            }
            ENDHLSL
        }
    }
}
