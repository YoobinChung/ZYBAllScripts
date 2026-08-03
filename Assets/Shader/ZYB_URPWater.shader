// ============================================================================
//  ZYB / URP Water  -  低成本卡通水面 (WebGL / 微信小游戏 友好)
//  Low-cost stylized water for URP. Designed for mobile / WebGL performance.
//
//  实现两个核心效果 / Two core effects:
//    1) 波光: 同一张 alpha(灰度) 纹理两次采样 + 互相扭曲, 形成"要动"的水纹亮线。
//       Undulating light wisps: the SAME ripple texture sampled twice with
//       different scroll speeds, one distorting the other.
//    2) 接触白边: 与其它物体相交处显示白色泡沫 (基于相机深度)。
//       White contact foam where the surface meets other objects (depth based).
//
//  优化点 / Optimizations:
//    - Unlit(无光照计算)。只有 2 次纹理采样 + 1 次深度采样。
//    - 世界 XZ 做 UV, 无需网格 UV, 大平面平铺稳定。
//    - 泡沫(_FOAM)与雾(fog)都是可关闭的 shader_feature, 不用就零开销。
//    - 不透明输出(不混合), 走 Transparent 队列只是为了能读不透明物体的深度。
//
//  ★ 使用泡沫需在 URP Renderer / Pipeline Asset 中开启 "Depth Texture"。
//    To use foam, enable "Depth Texture" on the URP Asset / Renderer.
// ============================================================================
Shader "ZYB/URP Water"
{
    Properties
    {
        [Header(Water Color)]
        // 横向渐变图: 左=近/深, 右=远/浅。用距离(视深度)采样, 可画多色非线性水色。
        [NoScaleOffset] _GradientTex ("水色渐变图 (远近)", 2D) = "white" {}
        _GradStart      ("渐变起点 (屏幕Y 0=底)", Range(0,1)) = 0.0
        _GradRange      ("渐变范围 (屏幕高度比例)", Range(0.05,1)) = 0.55
        _Alpha          ("水体透明度", Range(0,1))       = 1.0

        [Header(Surface Wave)]
        // 顶点起伏波动。★需要细分过的水面网格(顶点足够多)才看得到。
        [Toggle(_WAVE)] _EnableWave ("启用水面波动", Float) = 1
        _WaveAmplitude  ("波动高度 (世界单位)", Float)    = 0.2
        _WaveScale      ("波动密度", Float)              = 0.5
        _WaveSpeed      ("波动速度", Float)              = 1.0
        _WaveShading    ("波峰明暗对比", Range(0,1))     = 0.15

        [Header(Ripple Wisps)]
        [NoScaleOffset] _RippleTex ("水纹纹理 (灰度)", 2D) = "black" {}
        _RippleColor    ("水纹亮线颜色", Color)          = (0.75, 0.95, 1.0, 1)
        _RippleScale    ("水纹平铺 (世界单位)", Float)   = 0.12
        _RippleSpeed    ("流速 (层1.xy / 层2.zw)", Vector) = (0.010, 0.013, -0.008, 0.006)
        _RippleDistort  ("互相扭曲强度", Range(0,1))     = 0.25
        _RippleStrength ("亮线强度", Range(0,2))         = 0.9
        _RippleThreshold("亮线阈值", Range(0,1))         = 0.55
        _RippleSmooth   ("亮线柔和度", Range(0.001,0.5)) = 0.20

        [Header(Contact Foam)]
        [Toggle(_FOAM)] _EnableFoam ("启用接触白边", Float) = 1
        _FoamColor      ("泡沫颜色", Color)              = (1, 1, 1, 1)
        _FoamDistance   ("白边宽度 (视距, 世界单位)", Float) = 0.8
        _FoamPower      ("白边锐利度", Range(0.2, 8))    = 1.0
        _FoamWave       ("白边扰动", Range(0, 1))        = 0.25
        _FoamCutoff     ("白边硬度阈值", Range(0, 1))    = 0.15

        [Header(Options)]
        [Toggle(_FOG)] _EnableFog ("启用雾效", Float)    = 0
    }

    SubShader
    {
        // Transparent 队列: 让不透明物体先写深度, 水面才能读到相交深度做泡沫。
        // 颜色本身仍是不透明输出。
        Tags
        {
            "RenderType"        = "Transparent"
            "Queue"             = "Transparent"
            "RenderPipeline"    = "UniversalPipeline"
            "IgnoreProjector"   = "True"
        }
        LOD 100

        Pass
        {
            Name "WaterUnlit"
            Tags { "LightMode" = "UniversalForward" }

            // ZWrite Off: 关键! 防止水面把自己写进深度纹理(某些渲染器在透明后拷贝
            // 深度), 否则 submerge≈0 会导致整片泛白。水是背景, 不写深度也不影响遮挡。
            ZWrite Off
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha   // 支持透明度

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   2.0

            // 可关闭的功能: 不勾选时被完全编译掉, 零开销。
            #pragma shader_feature_local          _WAVE   // 顶点+片元都用
            #pragma shader_feature_local_fragment _FOAM
            #pragma shader_feature_local_fragment _FOG

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #if defined(_FOAM)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #endif

            TEXTURE2D(_RippleTex);
            SAMPLER(sampler_RippleTex);
            TEXTURE2D(_GradientTex);
            SAMPLER(sampler_GradientTex);

            CBUFFER_START(UnityPerMaterial)
                float  _WaveAmplitude;
                float  _WaveScale;
                float  _WaveSpeed;
                float  _WaveShading;

                float  _GradStart;
                float  _GradRange;
                float  _Alpha;

                float4 _RippleColor;
                float  _RippleScale;
                float4 _RippleSpeed;
                float  _RippleDistort;
                float  _RippleStrength;
                float  _RippleThreshold;
                float  _RippleSmooth;

                float4 _FoamColor;
                float  _FoamDistance;
                float  _FoamPower;
                float  _FoamWave;
                float  _FoamCutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 rippleUV   : TEXCOORD0;   // 世界 XZ 派生的水纹 UV
                float4 screenPos  : TEXCOORD1;   // 深度采样用; .w = 视空间线性深度
                #if defined(_WAVE)
                    float waveCrest : TEXCOORD2; // 波峰因子(约 -1.6~1.6), 用于假明暗
                #endif
            };

            // 两个方向的正弦叠加 -> 滚动起伏。返回原始波值(未乘振幅)。
            float SurfaceWave (float2 posXZ)
            {
                float t = _Time.y * _WaveSpeed;
                float w  = sin(dot(posXZ, float2(0.86, 0.51)) * _WaveScale + t);
                w += 0.6 * sin(dot(posXZ, float2(-0.57, 0.82)) * _WaveScale * 1.37 + t * 0.8);
                return w;
            }

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                #if defined(_WAVE)
                    float wave = SurfaceWave(positionWS.xz);
                    positionWS.y += wave * _WaveAmplitude;   // 顶点起伏
                    OUT.waveCrest = wave;
                #endif
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.rippleUV   = positionWS.xz * _RippleScale;
                OUT.screenPos  = ComputeScreenPos(OUT.positionCS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // ---------- 1. 波光 (同一纹理两次采样, 互相扭曲) ----------
                float2 t = _Time.y * _RippleSpeed.xy;
                float  r1 = SAMPLE_TEXTURE2D(_RippleTex, sampler_RippleTex,
                                             IN.rippleUV + t).r;

                // 用第一次采样结果偏移第二次采样的 UV -> "要动/晃动"感
                float2 t2 = _Time.y * _RippleSpeed.zw + (r1 - 0.5) * _RippleDistort;
                float  r2 = SAMPLE_TEXTURE2D(_RippleTex, sampler_RippleTex,
                                             IN.rippleUV * 1.37 + t2).r;

                float ripple = r1 * r2;                      // 交叠出细亮线
                float wisp = smoothstep(_RippleThreshold,
                                        _RippleThreshold + _RippleSmooth, ripple);

                // ---------- 2. 远近水色渐变 (按屏幕纵向, 相机无关) ----------
                // 用屏幕 Y 采样渐变图: 底部(近/深) -> 顶部(远/浅)。
                // 不再用视距, 避免掠射角下整屏被压成单色。
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                float  grad = saturate((screenUV.y - _GradStart) / max(_GradRange, 1e-3));
                half3  col  = SAMPLE_TEXTURE2D(_GradientTex, sampler_GradientTex,
                                               float2(grad, 0.5)).rgb;

                // 叠加亮线
                col = lerp(col, _RippleColor.rgb, wisp * _RippleStrength);

                // 波峰假明暗: Unlit 无光照, 用波高给起伏一点明暗对比, 更有"一荡一荡"感。
                #if defined(_WAVE)
                    col *= 1.0 + IN.waveCrest * _WaveShading;
                #endif

                // ---------- 3. 接触白边 (沿水面向四周横向扩散的泡沫环) ----------
                // 用"水下物体沿视线离水面有多近(视距差)"计算。
                // 泡沫落在水面像素上, 会绕着物体在水面上向两侧扩散(环形),
                // 而不是贴着物体立面往下延伸。
                float foamMask = 0.0;
                #if defined(_FOAM)
                    float rawDepth = SampleSceneDepth(screenUV);

                    // 排除天空盒/远平面 (与平台 Z 方向无关)
                    float validGeo = step(Linear01Depth(rawDepth, _ZBufferParams), 0.999);

                    // 视空间深度差: 水下物体在水面像素后方多远
                    float sceneEye = LinearEyeDepth(rawDepth, _ZBufferParams);
                    float diff = sceneEye - IN.screenPos.w;   // >0 表示物体在水面之后
                    // 用水纹扰动接触线, 破掉生硬直线
                    diff += (ripple - 0.5) * _FoamWave * _FoamDistance;

                    float foam = 1.0 - saturate(diff / max(_FoamDistance, 1e-3));
                    foam = pow(saturate(foam), _FoamPower);
                    foam = smoothstep(_FoamCutoff, 1.0, foam) * validGeo;

                    foamMask = saturate(foam);
                    col = lerp(col, _FoamColor.rgb, foamMask);
                #endif

                // ---------- 4. 雾 (可选) ----------
                #if defined(_FOG)
                    float fogFactor = ComputeFogFactor(IN.positionCS.z);
                    col = MixFog(col, fogFactor);
                #endif

                // 水体用 _Alpha; 泡沫处保持不透明, 让白边始终清晰
                float alpha = lerp(_Alpha, 1.0, foamMask);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
