Shader "BossRaid/UISlotGlow"
{
    // ─────────────────────────────────────────────────────────────
    // uGUI Image 전용 사각 슬롯 "테두리 링 글로우" 셰이더.
    // UI/Default 계열 구조(스텐실/마스크/_MainTex 필수 포함)라 UGUI Image 에 그대로 물린다.
    //   · uv 가장자리 최소거리로 사각 링(border)만 발광, 중앙은 완전 투명 → 슬롯 내용 안 가림.
    //   · _FlowSpeed : 테두리를 따라 도는 광택 스팟(atan2 각도 + 시간). 평상시 느리게(0.5).
    //   · _PulseSpeed/_PulseAmp : 링 전체 밝기 맥동.
    //   · _GlowColor(HDR) : 링 색. 스크립트가 상태별(평상 금색 / 조준 스킬색 / 쿨다운 저채도)로 구동.
    //   · Image.color(정점 알파)로 페이드 인/아웃.
    // ─────────────────────────────────────────────────────────────
    Properties
    {
        // ── uGUI Image 필수 (없으면 Canvas.SendWillRenderCanvases 가 매 프레임 에러) ──
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        // ── 링 글로우 전용 ──
        [HDR] _GlowColor ("Glow Color (HDR)", Color) = (0.78, 0.63, 0.29, 1)
        _BorderWidth ("Border Width (uv)", Range(0.01, 0.5)) = 0.08
        _BorderSoftness ("Border Softness", Range(0.01, 3.0)) = 1.0
        _FlowSpeed ("Flow Speed (spot around border)", Range(0, 4)) = 0.5
        _FlowWidth ("Flow Spot Width (angle)", Range(0.02, 1.0)) = 0.22
        _FlowIntensity ("Flow Intensity", Range(0, 4)) = 0.8
        _PulseSpeed ("Pulse Speed", Range(0, 20)) = 1.5
        _PulseAmp ("Pulse Amplitude", Range(0, 1)) = 0.08
        _Intensity ("Intensity", Range(0, 4)) = 1.0

        // ── UI 스텐실/마스크 표준 프로퍼티 (UI/Default 계열) ──
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha One          // 링 발광(가산). 중앙 알파 0 → 무기여.
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            fixed4 _GlowColor;
            float  _BorderWidth;
            float  _BorderSoftness;
            float  _FlowSpeed;
            float  _FlowWidth;
            float  _FlowIntensity;
            float  _PulseSpeed;
            float  _PulseAmp;
            float  _Intensity;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                float2 uv = IN.texcoord;

                // 스프라이트/정점 알파(= Image.color.a 페이드) 마스크
                half spriteA = (tex2D(_MainTex, uv) + _TextureSampleAdd).a * IN.color.a;

                // (1) 사각 링: 가장자리 최소거리(0=경계 ~ 0.5=중앙) → 경계 근처만 1
                float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float ring = 1.0 - smoothstep(0.0, max(1e-4, _BorderWidth), edge);
                ring = pow(saturate(ring), max(0.01, _BorderSoftness));

                // (2) 테두리를 도는 광택 스팟: atan2 각도 정규화 + 시간 스윕(랩어라운드)
                float ang = atan2(uv.y - 0.5, uv.x - 0.5) * 0.15915494 + 0.5;  // /(2π)+0.5 → [0,1]
                float sweep = frac(_Time.y * _FlowSpeed);
                float ad = abs(ang - sweep);
                ad = min(ad, 1.0 - ad);                                        // 원형 최단 각거리
                float spot = smoothstep(_FlowWidth, 0.0, ad);

                // (3) 전체 밝기 맥동
                float pulse = 1.0 + sin(_Time.y * _PulseSpeed) * _PulseAmp;

                float glow = ring * (1.0 + spot * _FlowIntensity) * pulse * _Intensity;
                float a = saturate(glow * _GlowColor.a * spriteA);

                fixed3 rgb = _GlowColor.rgb;   // HDR 색(정점 rgb 는 페이드에만 관여 → 색 왜곡 방지)

                // ── UI 클립/알파클립 (RectMask2D 호환) ──
                #ifdef UNITY_UI_CLIP_RECT
                a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(a - 0.001);
                #endif

                return fixed4(rgb, a);   // Blend SrcAlpha One → rgb*a 가산
            }
        ENDCG
        }
    }
}
