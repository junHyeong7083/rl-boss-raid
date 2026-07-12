Shader "BossRaid/AimScreenEdge"
{
    // 조준 중 화면 외곽 발광 아웃라인(풀스크린 UI 오버레이 전용).
    // UI/Default 계열 구조(스텐실/마스크 프로퍼티 포함)라 UGUI Image 에 그대로 물릴 수 있다.
    // 풀스크린 스트레치 Image 위에서 UV(0~1) 가장자리 거리로 소프트 글로우 밴드를 그리고,
    // 중앙(_EdgeWidth 안쪽)은 완전 투명 → 게임플레이 시야를 가리지 않는다.
    //
    //   _Color      : HDR 스킬 색(진홍/금색/은백 등). rgb=색, a=기본 밝기 스케일.
    //   _EdgeWidth  : 가장자리에서 안쪽으로 밴드가 퍼지는 UV 폭(기본 0.06).
    //   _PulseSpeed : 은은한 밝기 맥동 속도(0 이면 정지).
    //   Image.color / CanvasRenderer 알파(정점 색 IN.color.a)로 페이드 인/아웃 제어.
    Properties
    {
        // uGUI Image 는 머티리얼에 _MainTex 프로퍼티가 반드시 있어야 한다
        // (없으면 Canvas.SendWillRenderCanvases 가 매 프레임 에러 — 크래시처럼 보임).
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [HDR] _Color ("Edge Color (HDR)", Color) = (1.8, 0.15, 0.12, 1)
        _EdgeWidth ("Edge Width (uv)", Range(0.001, 0.5)) = 0.06
        _Softness ("Edge Softness", Range(0.0, 3.0)) = 1.5
        _PulseSpeed ("Pulse Speed", Range(0, 8)) = 1.5
        _PulseAmount ("Pulse Amount", Range(0, 1)) = 0.18
        _Intensity ("Intensity", Range(0, 4)) = 1.2

        // ── UI 스텐실/마스크 표준 프로퍼티 (UI/Default 계열) ──
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
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
        Blend SrcAlpha One          // 가장자리 발광(가산 블렌드). 중앙은 알파 0 → 무기여.
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            fixed4 _Color;
            float _EdgeWidth;
            float _Softness;
            float _PulseSpeed;
            float _PulseAmount;
            float _Intensity;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 화면 가장자리까지 최소 거리(UV, 0=경계 ~ 0.5=중앙).
                float2 uv = i.uv;
                float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));

                // 경계에서 1 → _EdgeWidth 안쪽에서 0 으로 떨어지는 소프트 밴드.
                float band = 1.0 - smoothstep(0.0, max(1e-4, _EdgeWidth), edge);
                band = pow(saturate(band), max(0.01, _Softness));   // 안쪽으로 부드럽게 감쇠

                // 은은한 밝기 맥동.
                float pulse = 1.0 - _PulseAmount + _PulseAmount * sin(_Time.y * _PulseSpeed * 6.28318530718);

                float a = band * _Color.a * i.color.a * _Intensity * pulse;
                float3 rgb = _Color.rgb * i.color.rgb;   // HDR 색은 _Color, 페이드는 정점 색 알파
                return fixed4(rgb, saturate(a));         // Blend SrcAlpha One → rgb*a 가산
            }
            ENDCG
        }
    }
}
