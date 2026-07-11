Shader "BossRaid/Telegraph"
{
    // 패턴 텔레그래프 전용 쉐이더. (로스트아크 장판 스타일)
    // Quad(1x1, 바닥에 평평히 회전된 상태) 위에 shape 종류에 따라 실제 도형을 그림.
    // Quad의 localScale과 rotation은 C#에서 shape에 맞게 맞춰주면 됨.
    //
    // 로스트아크 장판 시그니처:
    //   테두리(outline)가 먼저 밝게 표시 → 내부가 시전 진행률(_Fill)만큼
    //   중심에서 가장자리로 차오름 → 100% 도달(_Fill=1) 시 발동.
    //
    // _ShapeType:
    //   0 = Circle        (UV 중심 기준 원)
    //   1 = Fan           (UV 중심 오른쪽 방향 부채꼴, _FanWidthRad 반각)
    //   2 = Line (beam)   (가로 전체, 세로는 중앙 띠)
    //   3 = Cross         (가로+세로 십자 + 안전 사분면)
    //   4 = Donut         (r_in~r_out 링 위험, 내부 안전. _DonutInnerRatio=r_in/r_out)
    //
    // _Progress: 0(wind-up 시작) → 1(발동 직전). 알파/펄스 속도에 반영.
    // _Fill:     0(테두리만) → 1(내부 가득). 중심→가장자리 차오름 마스크.
    // _Pulse:    잔여 1턴 이내일 때 1로 켜서 강한 깜빡임 강조.
    Properties
    {
        _Color("Base Color", Color) = (1, 0.3, 0.3, 0.55)
        _RimColor("Rim/Glow Color (legacy)", Color) = (1, 1, 0.4, 1)
        [HDR] _OutlineColor("Outline HDR Color", Color) = (2.5, 0.7, 0.15, 1)
        _ShapeType("Shape (0=Circle 1=Fan 2=Line 3=Cross)", Int) = 0
        _Progress("Progress 0-1", Range(0, 1)) = 0
        _Fill("Fill 0-1 (center->edge)", Range(0, 1)) = 1
        _Pulse("Pulse (urgent blink 0/1)", Range(0, 1)) = 0
        _FanWidthRad("Fan Half-Angle (rad)", Range(0, 3.15)) = 0.785
        _EdgeSoftness("Edge Softness", Range(0.001, 0.2)) = 0.04
        _PulseSpeed("Pulse Speed", Range(0, 12)) = 5
        _LineWidth("Line Width (UV)", Range(0, 1)) = 0.15
        _CrossBandWidth("Cross Band Half Width (UV)", Range(0, 0.5)) = 0.08
        _SafeMask("Safe Quad Mask (bits 0-3)", Float) = 0
        _DonutInnerRatio("Donut Inner Ratio (r_in/r_out)", Range(0, 1)) = 0.4

        // ── 로스트아크 장판 룩 파라미터 ──
        _OutlineWidth("Outline Width", Range(0.001, 0.4)) = 0.08
        _FillSoftness("Fill Edge Softness", Range(0.001, 0.3)) = 0.06
        _FillEdgeWidth("Fill Front Glow Width", Range(0, 0.3)) = 0.05
        _InteriorDim("Interior Dim (unfilled)", Range(0, 1)) = 0.4
        _InteriorBright("Interior Bright (filled)", Range(0, 3)) = 1.15
        _UnfilledAlpha("Unfilled Alpha", Range(0, 1)) = 0.5
        _NoiseScale("Noise Scale", Range(0, 32)) = 6
        _NoiseSpeed("Noise Scroll Speed", Range(0, 4)) = 0.5
        _NoiseStrength("Noise Strength", Range(0, 0.3)) = 0.04
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
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
                float4 _RimColor;
                float4 _OutlineColor;
                int _ShapeType;
                float _Progress;
                float _Fill;
                float _Pulse;
                float _FanWidthRad;
                float _EdgeSoftness;
                float _PulseSpeed;
                float _LineWidth;
                float _CrossBandWidth;
                float _SafeMask;
                float _DonutInnerRatio;
                float _OutlineWidth;
                float _FillSoftness;
                float _FillEdgeWidth;
                float _InteriorDim;
                float _InteriorBright;
                float _UnfilledAlpha;
                float _NoiseScale;
                float _NoiseSpeed;
                float _NoiseStrength;
            CBUFFER_END

            V2F vert(Attr IN)
            {
                V2F OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            // UV(0~1) 중심 기준 좌표로 변환
            float2 centered(float2 uv) { return uv - 0.5; }

            // ── 절차적 value noise (텍스처 의존 없음) ──
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 345.45));
                p += dot(p, p + 34.345);
                return frac(p.x * p.y);
            }
            float valueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // 도형 샘플 결과.
            //  mask     : 도형 커버리지 (0~1)
            //  fillCoord: 중심(0)→가장자리(1) 정규화 좌표. _Fill과 비교해 차오름 판정.
            //  edgeDist : 외곽 경계까지 거리(경계=0, 안쪽으로 갈수록 증가). 테두리 판정용.
            struct ShapeSample { float mask; float fillCoord; float edgeDist; };

            ShapeSample sampleCircle(float2 p)
            {
                ShapeSample s;
                float r = length(p) * 2.0;              // 0(center) ~ 1(edge)
                s.mask = smoothstep(1.0, 1.0 - _EdgeSoftness, r);
                s.fillCoord = saturate(r);              // 반경 방향 차오름
                s.edgeDist = 1.0 - r;
                return s;
            }

            ShapeSample sampleFan(float2 p)
            {
                ShapeSample s; s.mask = 0; s.fillCoord = 1; s.edgeDist = 0;
                float r = length(p) * 2.0;
                float ang = atan2(p.y, p.x);            // -PI ~ PI, +X축 기준
                float diff = abs(ang);                  // 부채꼴 중앙이 +X(오른쪽) 기준
                float inside = smoothstep(_FanWidthRad + _EdgeSoftness, _FanWidthRad - _EdgeSoftness, diff);
                float edge = smoothstep(1.0, 1.0 - _EdgeSoftness, r);
                s.mask = inside * edge;
                s.fillCoord = saturate(r);              // 반경 방향 차오름
                float radialDist = 1.0 - r;
                float sideDist = saturate((_FanWidthRad - diff) / max(1e-3, _FanWidthRad));
                s.edgeDist = min(radialDist, sideDist); // 호/양쪽 직선변 중 가까운 경계
                return s;
            }

            ShapeSample sampleLine(float2 p)
            {
                // 가로 전체(시전축=X), 세로는 중앙 띠(폭=Y). 차오름은 시전축 방향.
                ShapeSample s;
                float trans = abs(p.y) / max(1e-3, _LineWidth);   // 0 center ~ 1 edge (폭)
                float axial = abs(p.x) * 2.0;                     // 0 center ~ 1 end (시전축)
                s.mask = smoothstep(_LineWidth, _LineWidth - _EdgeSoftness, abs(p.y));
                s.fillCoord = saturate(axial);                    // 시전축 방향 차오름
                float transDist = 1.0 - saturate(trans);
                float endDist = 1.0 - saturate(axial);
                s.edgeDist = min(transDist, endDist);             // 띠 가장자리/양 끝
                return s;
            }

            ShapeSample sampleCross(float2 p)
            {
                ShapeSample s; s.mask = 0; s.fillCoord = 1; s.edgeDist = 0;
                float bw = _CrossBandWidth;
                float hBand = smoothstep(bw, bw - _EdgeSoftness, abs(p.y));
                float vBand = smoothstep(bw, bw - _EdgeSoftness, abs(p.x));
                float cross = max(hBand, vBand);

                // 십자에 맞았으면 무조건 위험
                if (cross > 0.001)
                {
                    s.mask = cross;
                    s.fillCoord = saturate(length(p) * 2.0);      // 중심→바깥 반경 차오름
                    float e = 0.0;
                    if (abs(p.y) <= bw) e = max(e, min((bw - abs(p.y)) / max(1e-3, bw), (0.5 - abs(p.x)) / 0.5));
                    if (abs(p.x) <= bw) e = max(e, min((bw - abs(p.x)) / max(1e-3, bw), (0.5 - abs(p.y)) / 0.5));
                    s.edgeDist = saturate(e);
                    return s;
                }

                // 그 외 사분면: safe_mask에 걸리면 안전(0=discard), 아니면 약한 경고색
                int q = 0;
                if (p.x >= 0) q |= 1;
                if (p.y >= 0) q |= 2;
                int mask = (int)_SafeMask;
                bool isSafe = ((mask >> q) & 1) == 1;
                s.mask = isSafe ? 0.0 : 0.25;
                s.fillCoord = 2.0;    // 경고 영역은 채워지지 않음
                s.edgeDist = 1.0;     // 경계에서 먼 값 → 테두리(HDR outline) 미표시
                return s;
            }

            // V2 Donut: r_in~r_out 링만 위험, 내부(r<r_in)는 안전(투명).
            // Quad 크기 = r_out*2 로 세팅되므로 r=1 이 바깥 경계, ratio=r_in/r_out 가 안쪽 경계.
            ShapeSample sampleDonut(float2 p)
            {
                ShapeSample s;
                float ratio = saturate(_DonutInnerRatio);
                float r = length(p) * 2.0;                          // 0(center) ~ 1(outer edge)

                float outer = smoothstep(1.0, 1.0 - _EdgeSoftness, r);              // 바깥 경계 감쇠
                float inner = smoothstep(ratio - _EdgeSoftness, ratio + _EdgeSoftness, r); // 안쪽 hole 제거
                s.mask = outer * inner;                             // 링(도넛)만 커버리지

                // Fill 은 r_in → r_out 방향 (링 내부에서 정규화)
                s.fillCoord = saturate((r - ratio) / max(1e-3, 1.0 - ratio));

                // 테두리: 안/밖 경계 모두 (둘 중 가까운 쪽)
                float outerDist = saturate(1.0 - r);
                float innerDist = saturate(r - ratio);
                s.edgeDist = min(outerDist, innerDist);
                return s;
            }

            half4 frag(V2F IN) : SV_Target
            {
                float2 p = centered(IN.uv);

                ShapeSample s;
                if (_ShapeType == 0) s = sampleCircle(p);
                else if (_ShapeType == 1) s = sampleFan(p);
                else if (_ShapeType == 2) s = sampleLine(p);
                else if (_ShapeType == 4) s = sampleDonut(p);
                else s = sampleCross(p);

                if (s.mask <= 0.001) discard;

                // 스크롤 value noise: 테두리/채움 경계가 은은하게 일렁이도록
                float2 nUV = p * _NoiseScale + _Time.y * _NoiseSpeed * float2(0.15, 0.27);
                float wob = (valueNoise(nUV) - 0.5) * _NoiseStrength;

                // 시전 진행 채움 (중심→가장자리). fillCoord < _Fill 이면 채워진 영역.
                float fc = s.fillCoord + wob;
                float fillMask = smoothstep(_Fill + _FillSoftness, _Fill - _FillSoftness, fc);

                // 내부 색: 미충전=어두운 붉은색, 충전=더 밝게
                float3 dimCol = _Color.rgb * _InteriorDim;
                float3 hotCol = _Color.rgb * _InteriorBright;
                float3 col = lerp(dimCol, hotCol, fillMask);

                // 펄스: 기본은 은은, _Pulse(잔여 1턴 이하) 시 강한 깜빡임
                float pulseHz = lerp(2.0, _PulseSpeed, _Progress);
                float baseBlink = 0.85 + 0.15 * sin(_Time.y * pulseHz * 6.283);
                float urgentBlink = 0.5 + 0.5 * sin(_Time.y * _PulseSpeed * 6.283);
                float blink = lerp(baseBlink, urgentBlink, saturate(_Pulse));

                // 테두리(outline): SDF 경계 근처 얇은 HDR 라인
                float ew = max(1e-3, _OutlineWidth);
                float outline = 1.0 - smoothstep(0.0, ew, s.edgeDist + max(0.0, wob) * 0.5);
                outline *= step(0.0, s.edgeDist);

                // 채움 전선(fill front): 차오르는 경계에 밝은 글로우
                float front = 1.0 - smoothstep(0.0, max(1e-3, _FillEdgeWidth), abs(fc - _Fill));
                front *= step(fc, 1.001) * step(_Fill, 0.999) * step(0.001, _Fill);

                // HDR 테두리/전선 합성 (블룸 반응)
                float rim = saturate(max(outline, front));
                col = lerp(col, _OutlineColor.rgb, rim);
                col *= blink;

                // 알파: 채운 영역은 진하게, 테두리는 항상 보이게. 진행할수록 진하게(기존 룩 근사).
                float a = _Color.a * s.mask * lerp(_UnfilledAlpha, 1.0, fillMask);
                a = max(a, rim * _OutlineColor.a);
                a *= lerp(0.6, 1.0, _Progress);
                a *= blink;

                return half4(col, saturate(a));
            }
            ENDHLSL
        }
    }
}
