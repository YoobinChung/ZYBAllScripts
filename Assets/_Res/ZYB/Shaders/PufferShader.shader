Shader "ZYB/PufferShader"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base Color", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Tint", Color) = (1, 1, 1, 1)

        [Normal] _NormalMap ("Normal", 2D) = "bump" {}
        _NormalScale ("Normal Scale", Range(0, 2)) = 0

        _SpecularColor ("Specular Color", Color) = (1, 1, 1, 1)
        _SpecularStrength ("Specular Strength", Range(0, 2)) = 0
        _SpecularSmoothness ("Specular Smoothness", Range(0, 1)) = 0.5

        _OcclusionMap ("AO", 2D) = "white" {}
        _OcclusionStrength ("AO Strength", Range(0, 1)) = 0

        _MatCapMap ("MatCap", 2D) = "gray" {}
        _MatCapColor ("MatCap Tint", Color) = (1, 1, 1, 1)
        _MatCapStrength ("MatCap Strength", Range(0, 4)) = 0
        [Enum(Additive, 0, Replace, 1)] _MatCapBlend ("Matcap Blending", Float) = 0

        [Toggle] _ReceiveShadows ("Receive Shadows", Float) = 1

        _ShadowColor ("Shadow Color", Color) = (0.45, 0.47, 0.52, 1)
        _ShadowThreshold ("Toon Shadow Threshold", Range(0, 1)) = 0.5
        _ShadowSoftness ("Toon Shadow Softness", Range(0.001, 1)) = 0.08

        [Toggle] _Transparency ("Transparency", Float) = 0
        [Enum(Front, 2, Back, 1)] _RenderFace ("Render Face", Float) = 2
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        [HideInInspector] _Surface ("Surface", Float) = 0
        [HideInInspector] _SrcBlend ("Src Blend", Float) = 1
        [HideInInspector] _DstBlend ("Dst Blend", Float) = 0
        [HideInInspector] _ZWrite ("Z Write", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }

        LOD 150

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_RenderFace]
            ZWrite [_ZWrite]
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex LitPassVertex
            #pragma fragment LitPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_OcclusionMap);
            SAMPLER(sampler_OcclusionMap);
            TEXTURE2D(_MatCapMap);
            SAMPLER(sampler_MatCapMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _NormalMap_ST;
                float _NormalScale;
                float4 _SpecularColor;
                float _SpecularStrength;
                float _SpecularSmoothness;
                float4 _OcclusionMap_ST;
                float _OcclusionStrength;
                float4 _MatCapMap_ST;
                float4 _MatCapColor;
                float _MatCapStrength;
                float _MatCapBlend;
                float _ReceiveShadows;
                float4 _ShadowColor;
                float _ShadowThreshold;
                float _ShadowSoftness;
                float _Transparency;
                float _RenderFace;
                float _Cutoff;
                float _Surface;
                float _SrcBlend;
                float _DstBlend;
                float _ZWrite;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half4 tangentWS : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings LitPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = positionInput.positionCS;
                output.positionWS = positionInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.tangentWS = half4(normalInput.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                return output;
            }

            half3 GetShadingNormal(Varyings input)
            {
                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);

                UNITY_BRANCH
                if (_NormalScale > 0.0001h)
                {
                    half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv), _NormalScale);
                    half3 tangentWS = normalize(input.tangentWS.xyz);
                    half3 bitangentWS = input.tangentWS.w * cross(normalWS, tangentWS);
                    half3x3 tangentToWorld = half3x3(tangentWS, bitangentWS, normalWS);
                    normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));
                }

                return normalWS;
            }

            half3 SampleMatCap(half3 normalWS)
            {
                half3 normalVS = mul((half3x3)UNITY_MATRIX_V, normalWS);
                half2 matCapUV = normalVS.xy * 0.5h + 0.5h;
                return SAMPLE_TEXTURE2D(_MatCapMap, sampler_MatCapMap, matCapUV).rgb * _MatCapColor.rgb;
            }

            half3 EvaluateGGXSpecular(half3 normalWS, half3 lightDirWS, half3 viewDirWS, half ndotl)
            {
                half3 halfDirWS = SafeNormalize(lightDirWS + viewDirWS);
                half ndotv = saturate(dot(normalWS, viewDirWS));
                half ndoth = saturate(dot(normalWS, halfDirWS));
                half ldoth = saturate(dot(lightDirWS, halfDirWS));

                half roughness = max(0.04h, 1.0h - _SpecularSmoothness);
                half alpha = roughness * roughness;
                half alpha2 = alpha * alpha;

                half dDenom = ndoth * ndoth * (alpha2 - 1.0h) + 1.0h;
                half d = alpha2 / max(3.14159265h * dDenom * dDenom, 0.0001h);

                half k = alpha * 0.5h;
                half visibility = 1.0h / max((ndotl * (1.0h - k) + k) * (ndotv * (1.0h - k) + k) * 4.0h, 0.0001h);
                half3 fresnel = _SpecularColor.rgb + (1.0h - _SpecularColor.rgb) * pow(1.0h - ldoth, 5.0h);

                return fresnel * d * visibility * ndotl;
            }

            half4 LitPassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                clip(lerp(baseSample.a - _Cutoff, 1.0h, saturate(_Transparency)));

                half3 normalWS = GetShadingNormal(input);

                half ao = 1.0h;
                UNITY_BRANCH
                if (_OcclusionStrength > 0.0001h)
                {
                    half aoSample = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, input.uv).r;
                    ao = lerp(1.0h, aoSample, _OcclusionStrength);
                }

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half ndotl = saturate(dot(normalWS, mainLight.direction));
                half receivedShadow = _ReceiveShadows > 0.5h ? mainLight.shadowAttenuation : 1.0h;
                half smoothLighting = ndotl * receivedShadow * mainLight.distanceAttenuation;
                half toonLighting = smoothstep(
                    _ShadowThreshold - _ShadowSoftness,
                    _ShadowThreshold + _ShadowSoftness,
                    smoothLighting
                );
                half shadowAttenuation = _ShadowThreshold <= 0.0001h ? 1.0h : receivedShadow;
                half lightMask = _ShadowThreshold <= 0.0001h ? 1.0h : toonLighting;

                half3 direct = lerp(_ShadowColor.rgb, mainLight.color.rgb, lightMask);
                half3 color = baseSample.rgb * ao * direct;

                UNITY_BRANCH
                if (_SpecularStrength > 0.0001h)
                {
                    half3 viewDirWS = SafeNormalize(GetCameraPositionWS() - input.positionWS);
                    half3 specular = EvaluateGGXSpecular(normalWS, mainLight.direction, viewDirWS, ndotl);
                    specular *= shadowAttenuation * mainLight.distanceAttenuation;
                    color += specular * mainLight.color.rgb * _SpecularStrength;
                }

                UNITY_BRANCH
                if (_MatCapStrength > 0.0001h)
                {
                    half3 matCap = SampleMatCap(normalWS);
                    UNITY_BRANCH
                    if (_MatCapBlend < 0.5h)
                    {
                        color += matCap * _MatCapStrength;
                    }
                    else
                    {
                        color = lerp(color, matCap, saturate(_MatCapStrength));
                    }
                }

                half alpha = lerp(1.0h, baseSample.a, saturate(_Transparency));

                return half4(color, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_RenderFace]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _NormalMap_ST;
                float _NormalScale;
                float4 _SpecularColor;
                float _SpecularStrength;
                float _SpecularSmoothness;
                float4 _OcclusionMap_ST;
                float _OcclusionStrength;
                float4 _MatCapMap_ST;
                float4 _MatCapColor;
                float _MatCapStrength;
                float _MatCapBlend;
                float _ReceiveShadows;
                float4 _ShadowColor;
                float _ShadowThreshold;
                float _ShadowSoftness;
                float _Transparency;
                float _RenderFace;
                float _Cutoff;
                float _Surface;
                float _SrcBlend;
                float _DstBlend;
                float _ZWrite;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 lightDirectionWS = _LightDirection;

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    lightDirectionWS = normalize(_LightPosition - positionWS);
                #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);

                return 0;
            }
            ENDHLSL
        }
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }

        LOD 150

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "ForwardBase" }

            Cull [_RenderFace]
            ZWrite [_ZWrite]
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex PufferBuiltInLitVertex
            #pragma fragment PufferBuiltInLitFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fwdbase

            #include "Assets/_Res/ZYB/Shaders/PufferShaderBuiltIn.cginc"
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_RenderFace]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex PufferBuiltInShadowVertex
            #pragma fragment PufferBuiltInShadowFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_shadowcaster

            #include "Assets/_Res/ZYB/Shaders/PufferShaderBuiltIn.cginc"
            ENDCG
        }
    }

    CustomEditor "PufferShaderGUI"
    FallBack Off
}
