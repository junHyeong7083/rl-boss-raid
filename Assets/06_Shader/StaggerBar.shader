Shader "BossRaid/StaggerBar"
{
    // ─────────────────────────────────────────────────────────────
    // uGUI Image 전용 무력화 게이지 fill 셰이더.
    // UI/Default 를 베이스로 스텐실 / RectMask2D(클립렉트) 완전 호환.
    //   · _FlowIntensity : 좌→우로 흐르는 좁은 광택 밴드(시간 기반). 평상시 0.25 은은.
    //   · _PulseSpeed/_PulseAmp : 전체 밝기 펄스. 그로기 중 스크립트가 값 상향.
    //   · _GlowColor(HDR) : 그로기 중 금색 부스트(평상시 0).
    // 색(fill) 자체는 Image.color(정점 컬러)로 전달 → 여기서는 광택/펄스/글로우만 가산.
    // ─────────────────────────────────────────────────────────────
    Properties
    {
        // ── UI/Default 필수 프로퍼티 (스텐실 / 마스크 호환) ──
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255

        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0

        // ── 무력화 바 전용 프로퍼티 ──
        _FlowIntensity ("Flow Intensity", Range(0, 2)) = 0.25
        _FlowWidth ("Flow Band Width", Range(0.01, 0.5)) = 0.06
        _FlowSpeed ("Flow Speed", Range(0, 4)) = 0.7
        _PulseSpeed ("Pulse Speed", Range(0, 20)) = 2
        _PulseAmp ("Pulse Amplitude", Range(0, 1)) = 0.05
        [HDR] _GlowColor ("Glow Color (HDR)", Color) = (0,0,0,0)
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
        Blend SrcAlpha OneMinusSrcAlpha
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

            float  _FlowIntensity;
            float  _FlowWidth;
            float  _FlowSpeed;
            float  _PulseSpeed;
            float  _PulseAmp;
            fixed4 _GlowColor;

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
                // UI 기본 샘플(스프라이트 * 정점컬러) — fill 색은 정점컬러로 들어옴
                half4 color = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd) * IN.color;

                // (1) 전체 밝기 펄스
                float pulse = 1.0 + sin(_Time.y * _PulseSpeed) * _PulseAmp;
                color.rgb *= pulse;

                // (2) 좌→우로 흐르는 좁은 광택 밴드
                float sweep = frac(_Time.y * _FlowSpeed);
                float d = abs(IN.texcoord.x - sweep);
                float band = smoothstep(_FlowWidth, 0.0, d);
                color.rgb += band * _FlowIntensity * color.a;

                // (3) 그로기 금색 글로우(HDR) — 평상시 _GlowColor=0
                color.rgb += _GlowColor.rgb * _GlowColor.a * color.a;

                // ── UI 클립/알파클립 (RectMask2D 호환) ──
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif

                return color;
            }
        ENDCG
        }
    }
}
