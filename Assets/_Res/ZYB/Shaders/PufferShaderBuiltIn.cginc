#ifndef PUFFER_SHADER_BUILTIN_INCLUDED
#define PUFFER_SHADER_BUILTIN_INCLUDED

#include "UnityCG.cginc"
#include "Lighting.cginc"
#include "AutoLight.cginc"

sampler2D _BaseMap;
sampler2D _NormalMap;
sampler2D _OcclusionMap;
sampler2D _MatCapMap;

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
float4 _OutlineColor;
float _OutlineWidth;
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

struct PufferAppData
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct PufferVaryings
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 worldPos : TEXCOORD1;
    half3 normalWS : TEXCOORD2;
    half4 tangentWS : TEXCOORD3;
    SHADOW_COORDS(4)
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct PufferOutlineVaryings
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

half3 PufferSafeNormalize(half3 value)
{
    return normalize(value + 1.0e-5h);
}

half3 PufferUnpackNormalScale(fixed4 packedNormal, half scale)
{
    half3 normalTS = UnpackNormal(packedNormal);
    normalTS.xy *= scale;
    normalTS.z = sqrt(saturate(1.0h - dot(normalTS.xy, normalTS.xy)));
    return normalTS;
}

PufferVaryings PufferBuiltInLitVertex(PufferAppData v)
{
    PufferVaryings output;
    UNITY_INITIALIZE_OUTPUT(PufferVaryings, output);

    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    output.pos = UnityObjectToClipPos(v.vertex);
    output.uv = TRANSFORM_TEX(v.uv, _BaseMap);
    output.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
    output.normalWS = UnityObjectToWorldNormal(v.normal);
    output.tangentWS = half4(UnityObjectToWorldDir(v.tangent.xyz), v.tangent.w * unity_WorldTransformParams.w);
    TRANSFER_SHADOW(output);

    return output;
}

half3 PufferBuiltInGetShadingNormal(PufferVaryings input)
{
    half3 normalWS = normalize(input.normalWS);

    UNITY_BRANCH
    if (_NormalScale > 0.0001h)
    {
        half3 normalTS = PufferUnpackNormalScale(tex2D(_NormalMap, input.uv), _NormalScale);
        half3 tangentWS = normalize(input.tangentWS.xyz);
        half3 bitangentWS = input.tangentWS.w * cross(normalWS, tangentWS);
        half3x3 tangentToWorld = half3x3(tangentWS, bitangentWS, normalWS);
        normalWS = normalize(mul(normalTS, tangentToWorld));
    }

    return normalWS;
}

half3 PufferBuiltInSampleMatCap(half3 normalWS)
{
    half3 normalVS = mul((half3x3)UNITY_MATRIX_V, normalWS);
    half2 matCapUV = normalVS.xy * 0.5h + 0.5h;
    return tex2D(_MatCapMap, matCapUV).rgb * _MatCapColor.rgb;
}

half3 PufferBuiltInEvaluateGGXSpecular(half3 normalWS, half3 lightDirWS, half3 viewDirWS, half ndotl)
{
    half3 halfDirWS = PufferSafeNormalize(lightDirWS + viewDirWS);
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

half4 PufferBuiltInLitFragment(PufferVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half4 baseSample = tex2D(_BaseMap, input.uv) * _BaseColor;
    clip(lerp(baseSample.a - _Cutoff, 1.0h, saturate(_Transparency)));

    half3 normalWS = PufferBuiltInGetShadingNormal(input);

    half ao = 1.0h;
    UNITY_BRANCH
    if (_OcclusionStrength > 0.0001h)
    {
        half aoSample = tex2D(_OcclusionMap, input.uv).r;
        ao = lerp(1.0h, aoSample, _OcclusionStrength);
    }

    half3 lightDirWS = PufferSafeNormalize(UnityWorldSpaceLightDir(input.worldPos));
    UNITY_LIGHT_ATTENUATION(lightAttenuation, input, input.worldPos);
    half receivedShadow = _ReceiveShadows > 0.5h ? lightAttenuation : 1.0h;
    half ndotl = saturate(dot(normalWS, lightDirWS));
    half smoothLighting = ndotl * receivedShadow;
    half toonLighting = smoothstep(
        _ShadowThreshold - _ShadowSoftness,
        _ShadowThreshold + _ShadowSoftness,
        smoothLighting
    );
    half shadowAttenuation = _ShadowThreshold <= 0.0001h ? 1.0h : receivedShadow;
    half lightMask = _ShadowThreshold <= 0.0001h ? 1.0h : toonLighting;

    half3 direct = lerp(_ShadowColor.rgb, _LightColor0.rgb, lightMask);
    half3 color = baseSample.rgb * ao * direct;

    UNITY_BRANCH
    if (_SpecularStrength > 0.0001h)
    {
        half3 viewDirWS = PufferSafeNormalize(_WorldSpaceCameraPos.xyz - input.worldPos);
        half3 specular = PufferBuiltInEvaluateGGXSpecular(normalWS, lightDirWS, viewDirWS, ndotl);
        specular *= shadowAttenuation;
        color += specular * _LightColor0.rgb * _SpecularStrength;
    }

    UNITY_BRANCH
    if (_MatCapStrength > 0.0001h)
    {
        half3 matCap = PufferBuiltInSampleMatCap(normalWS);
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

PufferOutlineVaryings PufferBuiltInOutlineVertex(PufferAppData input)
{
    PufferOutlineVaryings output;
    UNITY_INITIALIZE_OUTPUT(PufferOutlineVaryings, output);

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 outlineDirectionOS = input.vertex.xyz / max(length(input.vertex.xyz), 0.0001);
    float3 positionOS = input.vertex.xyz + outlineDirectionOS * _OutlineWidth;
    output.pos = UnityObjectToClipPos(float4(positionOS, 1.0));
    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

    return output;
}

half4 PufferBuiltInOutlineFragment(PufferOutlineVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half alpha = tex2D(_BaseMap, input.uv).a * _BaseColor.a;
    clip(lerp(alpha - _Cutoff, 1.0h, saturate(_Transparency)));

    return _OutlineColor;
}

struct PufferShadowVaryings
{
    V2F_SHADOW_CASTER;
    float2 uv : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

PufferShadowVaryings PufferBuiltInShadowVertex(PufferAppData input)
{
    PufferShadowVaryings output;
    UNITY_INITIALIZE_OUTPUT(PufferShadowVaryings, output);

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    output.pos = UnityClipSpaceShadowCasterPos(input.vertex, input.normal);
    output.pos = UnityApplyLinearShadowBias(output.pos);
    return output;
}

half4 PufferBuiltInShadowFragment(PufferShadowVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half alpha = tex2D(_BaseMap, input.uv).a * _BaseColor.a;
    clip(alpha - _Cutoff);

    SHADOW_CASTER_FRAGMENT(input)
}

#endif
