Shader "BossRaid/BloodMoonSky"
{
    // 절차적 "혈월 하늘" 스카이박스 (RenderSettings.skybox 로 적용).
    // 텍스처 없이 전부 수식 기반:
    //   · 그라데이션: 지평선 깊은 진홍 → 중간 암자주 → 천정 거의 검은 남색
    //   · 별: 해시 기반 점광. 고도(dir.y)가 높을수록 밀도↑, 미세 반짝임(twinkle)
    //   · 혈월: _MoonDir 방향 주변 붉은 글로우 원반(작은 코어 + 넓은 헤일로)
    // TowerScape.cs 가 new Material(this) 로 만들어 RenderSettings.skybox 에 세팅한다.
    // (Cull Off / ZWrite Off — 스카이박스 규약)
    Properties
    {
        _HorizonColor("Horizon (deep crimson)", Color) = (0.25, 0.04, 0.06, 1)
        _MidColor("Mid (dark magenta)",        Color) = (0.09, 0.03, 0.10, 1)
        _ZenithColor("Zenith (near-black navy)", Color) = (0.015, 0.012, 0.04, 1)
        _MoonDir("Blood Moon Direction (world)", Vector) = (45, 22, 65, 0)
        [HDR] _MoonGlowColor("Moon Glow (HDR red)", Color) = (1.8, 0.28, 0.20, 1)
        _StarDensity("Star Density", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _HorizonColor;
                half4 _MidColor;
                half4 _ZenithColor;
                float4 _MoonDir;
                half4 _MoonGlowColor;
                half  _StarDensity;
            CBUFFER_END

            struct Attr { float4 positionOS : POSITION; };
            struct V2F  { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            V2F vert(Attr IN)
            {
                V2F OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                // 스카이박스 메시의 오브젝트 공간 정점 = 시선 방향
                OUT.dir = IN.positionOS.xyz;
                return OUT;
            }

            // 해시(텍스처 의존 없음)
            float hash31(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            // 고도 가중 별밭 + 미세 반짝임
            float Stars(float3 dir)
            {
                float3 p = dir * 160.0;                 // 촘촘한 셀
                float3 i = floor(p);
                float3 f = frac(p) - 0.5;
                float h = hash31(i);
                float thresh = 1.0 - _StarDensity * 0.035;   // 희소
                float present = step(thresh, h);
                float d = length(f);
                float dot_ = smoothstep(0.30, 0.0, d);       // 셀 내 둥근 점 ('point' 는 HLSL 예약어)
                float bright = frac(h * 13.13);
                float tw = 0.6 + 0.4 * sin(_Time.y * 2.5 + h * 40.0);   // 미세 반짝임
                float star = present * dot_ * bright * tw;
                star *= smoothstep(-0.05, 0.40, dir.y);       // 고도 위주(지평선 아래 억제)
                return star;
            }

            half4 frag(V2F IN) : SV_Target
            {
                float3 dir = normalize(IN.dir);
                float h = dir.y;

                // ─── 세로 그라데이션 ───
                float t1 = smoothstep(-0.10, 0.25, h);
                float3 sky = lerp(_HorizonColor.rgb, _MidColor.rgb, t1);
                float t2 = smoothstep(0.20, 0.75, h);
                sky = lerp(sky, _ZenithColor.rgb, t2);
                // 지평선 아래로 갈수록 어둡게(탑/공허 콘셉트)
                sky *= (h < 0.0) ? saturate(1.0 + h * 0.6) : 1.0;

                // ─── 혈월 ───
                float3 md = normalize(_MoonDir.xyz);
                float m = dot(dir, md);
                float halo = pow(saturate(m), 6.0) * 0.55;        // 넓은 붉은 헤일로
                float disc = smoothstep(0.984, 0.994, m);         // 작은 밝은 원반
                float3 moon = _MoonGlowColor.rgb * (halo + disc * 1.6);

                // ─── 별 ───
                float3 stars = float3(1.0, 0.95, 0.9) * Stars(dir);

                float3 col = sky + moon + stars;
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
