Shader "BossRaid/Particle"
{
    // 텍스처 없이 UV로 도형 그리는 파티클용 쉐이더.
    // 파티클의 Start Color / Color over Lifetime이 그대로 반영됨.
    // Shape: 0=Circle(소프트), 1=Star(4각별), 2=Diamond, 3=Ring
    Properties
    {
        _Shape("Shape (0=Circle 1=Star 2=Diamond 3=Ring)", Int) = 0
        _Softness("Edge Softness", Range(0.001, 0.5)) = 0.15
        _InnerRadius("Inner Radius (Ring)", Range(0, 1)) = 0.6
        _Glow("Glow Multiplier", Range(1, 6)) = 2.0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "RenderPipeline"="UniversalPipeline" "PreviewType"="Plane" }
        Blend SrcAlpha One         // Additive (빛나는 느낌)
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attr { float4 positionOS: POSITION; float2 uv: TEXCOORD0; float4 color: COLOR; };
            struct V2F  { float4 positionCS: SV_POSITION; float2 uv: TEXCOORD0; float4 color: COLOR; };

            CBUFFER_START(UnityPerMaterial)
                int _Shape;
                float _Softness;
                float _InnerRadius;
                float _Glow;
            CBUFFER_END

            V2F vert(Attr IN)
            {
                V2F OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                return OUT;
            }

            float shapeMask(float2 uv)
            {
                float2 p = uv - 0.5;
                float r = length(p) * 2.0; // 0(center) ~ 1(edge)

                if (_Shape == 0) // soft circle
                    return smoothstep(1.0, 1.0 - _Softness, r);

                if (_Shape == 1) // 4-point star
                {
                    float a = atan2(p.y, p.x);
                    float rays = abs(cos(a * 2.0));
                    float starR = 0.3 + 0.7 * rays;
                    return smoothstep(starR, starR - _Softness, r);
                }

                if (_Shape == 2) // diamond
                {
                    float d = abs(p.x) + abs(p.y);
                    return smoothstep(0.5, 0.5 - _Softness, d);
                }

                if (_Shape == 3) // ring
                {
                    float outer = smoothstep(1.0, 1.0 - _Softness, r);
                    float inner = smoothstep(_InnerRadius - _Softness, _InnerRadius, r);
                    return outer * inner;
                }
                return 0;
            }

            half4 frag(V2F IN) : SV_Target
            {
                float mask = shapeMask(IN.uv);
                if (mask <= 0.001) discard;
                half3 col = IN.color.rgb * _Glow;
                return half4(col, IN.color.a * mask);
            }
            ENDHLSL
        }
    }
}
