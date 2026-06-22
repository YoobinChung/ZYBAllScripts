using System;
using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(fileName = "MaterialConfig", menuName = "Config/Material Config")]
public class MaterialConfig : ScriptableObject
{
    public enum TextureChannel
    {
        AlbedoAlpha = 0,
        CustomR = 1,
        CustomG = 2,
        CustomB = 3,
        CustomA = 4
    }

    public enum UVSet
    {
        UV1 = 0,
        UV2 = 1
    }

    public enum RenderFace
    {
        Both = 0,
        Back = 1,
        Front = 2
    }

    public enum RenderingMode
    {
        Opaque = 0,
        Fade = 1,
        Transparent = 2
    }

    [Header("Base")]
    public Texture2D baseMap;
    public Color baseColor = Color.white;
    public Color highlightColor = Color.white;
    public Color shadowColor = new Color(0.2f, 0.2f, 0.2f, 1f);

    [Header("Albedo HSV")]
    public float albedoHue;
    public float albedoSaturation;
    public float albedoValue;

    [Header("Occlusion")]
    public bool useOcclusion;
    public Texture2D occlusionMap;
    [Range(0f, 1f)] public float occlusionStrength = 1f;
    public TextureChannel occlusionChannel = TextureChannel.AlbedoAlpha;
    public UVSet occlusionUV = UVSet.UV1;

    [Header("Outline")]
    public bool useOutline;
    public float outlineWidth = 1f;
    public Color outlineColor = Color.black;

    [Header("Rim")]
    public bool useRim;
    public bool useRimLightMask = true;
    public float rimMin = 0.5f;
    public float rimMax = 1f;
    public Color rimColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);

    [Header("Ramp")]
    public float rampType;
    public float rampThreshold = 0.75f;
    public float rampSmoothing = 0.1f;
    public float rampBands = 4f;
    public float rampBandsSmoothing = 0.1f;
    public float rampScale = 1f;
    public float rampOffset;
    public Texture2D ramp;

    [Header("MatCap")]
    public bool useMatCap;
    public Texture2D matCapTex;
    public Color matCapColor = Color.white;
    public bool useMatCapMask;
    public Texture2D matCapMask;
    public TextureChannel matCapMaskChannel = TextureChannel.AlbedoAlpha;
    public float matCapType;

    [Header("Specular")]
    public bool useSpecular;
    public Color specularColor = new Color(0.75f, 0.75f, 0.75f, 1f);
    public float specularType;
    public float specularRoughness = 0.5f;
    public float specularToonSize = 0.25f;
    public float specularToonSmoothness = 0.05f;
    public TextureChannel specularMapType = TextureChannel.AlbedoAlpha;

    [Header("Rendering")]
    public RenderingMode renderingMode;
    public RenderFace renderFace = RenderFace.Front;
    public bool zWrite = true;
    public bool useAlphaTest;
    [Range(0f, 1f)] public float alphaCutoff = 0.5f;

    [Header("All Shader Properties")]
    public bool applySavedShader = true;
    public Shader shader;
    public string shaderName;
    public string[] shaderKeywords = Array.Empty<string>();
    public int renderQueue = -1;
    public bool enableInstancing;
    public bool doubleSidedGI;
    public MaterialGlobalIlluminationFlags globalIlluminationFlags;
    public ShaderPropertyConfig[] shaderProperties = Array.Empty<ShaderPropertyConfig>();

    public void SaveFromMaterial(Material material)
    {
        if (material == null)
        {
            return;
        }

        baseMap = GetTexture(material, "_BaseMap");
        baseColor = GetColor(material, "_BaseColor", Color.white);
        highlightColor = GetColor(material, "_HColor", Color.white);
        shadowColor = GetColor(material, "_SColor", new Color(0.2f, 0.2f, 0.2f, 1f));

        albedoHue = GetFloat(material, "_TCP2AlbedoHSV_H", 0f);
        albedoSaturation = GetFloat(material, "_TCP2AlbedoHSV_S", 0f);
        albedoValue = GetFloat(material, "_TCP2AlbedoHSV_V", 0f);

        useOcclusion = GetBool(material, "_UseOcclusion");
        occlusionMap = GetTexture(material, "_OcclusionMap");
        occlusionStrength = GetFloat(material, "_OcclusionStrength", 1f);
        occlusionChannel = (TextureChannel)GetFloat(material, "_OcclusionChannel", 0f);
        occlusionUV = (UVSet)GetFloat(material, "_OcclusionUV", 0f);

        useOutline = GetBool(material, "_UseOutline");
        outlineWidth = GetFloat(material, "_OutlineWidth", 1f);
        outlineColor = GetColor(material, "_OutlineColor", Color.black);

        useRim = GetBool(material, "_UseRim");
        useRimLightMask = GetBool(material, "_UseRimLightMask", true);
        rimMin = GetFloat(material, "_RimMin", 0.5f);
        rimMax = GetFloat(material, "_RimMax", 1f);
        rimColor = GetColor(material, "_RimColor", new Color(0.8f, 0.8f, 0.8f, 0.5f));

        rampType = GetFloat(material, "_RampType", 0f);
        rampThreshold = GetFloat(material, "_RampThreshold", 0.75f);
        rampSmoothing = GetFloat(material, "_RampSmoothing", 0.1f);
        rampBands = GetFloat(material, "_RampBands", 4f);
        rampBandsSmoothing = GetFloat(material, "_RampBandsSmoothing", 0.1f);
        rampScale = GetFloat(material, "_RampScale", 1f);
        rampOffset = GetFloat(material, "_RampOffset", 0f);
        ramp = GetTexture(material, "_Ramp");

        useMatCap = GetBool(material, "_UseMatCap");
        matCapTex = GetTexture(material, "_MatCapTex");
        matCapColor = GetColor(material, "_MatCapColor", Color.white);
        useMatCapMask = GetBool(material, "_UseMatCapMask");
        matCapMask = GetTexture(material, "_MatCapMask");
        matCapMaskChannel = (TextureChannel)GetFloat(material, "_MatCapMaskChannel", 0f);
        matCapType = GetFloat(material, "_MatCapType", 0f);

        useSpecular = GetBool(material, "_UseSpecular");
        specularColor = GetColor(material, "_SpecularColor", new Color(0.75f, 0.75f, 0.75f, 1f));
        specularType = GetFloat(material, "_SpecularType", 0f);
        specularRoughness = GetFloat(material, "_SpecularRoughness", 0.5f);
        specularToonSize = GetFloat(material, "_SpecularToonSize", 0.25f);
        specularToonSmoothness = GetFloat(material, "_SpecularToonSmoothness", 0.05f);
        specularMapType = (TextureChannel)GetFloat(material, "_SpecularMapType", 0f);

        renderingMode = (RenderingMode)GetFloat(material, "_RenderingMode", 0f);
        renderFace = (RenderFace)GetFloat(material, "_Cull", 2f);
        zWrite = GetBool(material, "_ZWrite", true);
        useAlphaTest = GetBool(material, "_UseAlphaTest");
        alphaCutoff = GetFloat(material, "_Cutoff", 0.5f);

        SaveAllShaderProperties(material);
    }

    public void ApplyToMaterial(Material material)
    {
        if (material == null)
        {
            return;
        }

        SetTexture(material, "_BaseMap", baseMap);
        SetColor(material, "_BaseColor", baseColor);
        SetColor(material, "_HColor", highlightColor);
        SetColor(material, "_SColor", shadowColor);

        SetFloat(material, "_TCP2AlbedoHSV_H", albedoHue);
        SetFloat(material, "_TCP2AlbedoHSV_S", albedoSaturation);
        SetFloat(material, "_TCP2AlbedoHSV_V", albedoValue);

        SetBool(material, "_UseOcclusion", useOcclusion);
        SetTexture(material, "_OcclusionMap", occlusionMap);
        SetFloat(material, "_OcclusionStrength", occlusionStrength);
        SetFloat(material, "_OcclusionChannel", (float)occlusionChannel);
        SetFloat(material, "_OcclusionUV", (float)occlusionUV);

        SetBool(material, "_UseOutline", useOutline);
        SetFloat(material, "_OutlineWidth", outlineWidth);
        SetColor(material, "_OutlineColor", outlineColor);

        SetBool(material, "_UseRim", useRim);
        SetBool(material, "_UseRimLightMask", useRimLightMask);
        SetFloat(material, "_RimMin", rimMin);
        SetFloat(material, "_RimMax", rimMax);
        SetColor(material, "_RimColor", rimColor);

        SetFloat(material, "_RampType", rampType);
        SetFloat(material, "_RampThreshold", rampThreshold);
        SetFloat(material, "_RampSmoothing", rampSmoothing);
        SetFloat(material, "_RampBands", rampBands);
        SetFloat(material, "_RampBandsSmoothing", rampBandsSmoothing);
        SetFloat(material, "_RampScale", rampScale);
        SetFloat(material, "_RampOffset", rampOffset);
        SetTexture(material, "_Ramp", ramp);

        SetBool(material, "_UseMatCap", useMatCap);
        SetTexture(material, "_MatCapTex", matCapTex);
        SetColor(material, "_MatCapColor", matCapColor);
        SetBool(material, "_UseMatCapMask", useMatCapMask);
        SetTexture(material, "_MatCapMask", matCapMask);
        SetFloat(material, "_MatCapMaskChannel", (float)matCapMaskChannel);
        SetFloat(material, "_MatCapType", matCapType);

        SetBool(material, "_UseSpecular", useSpecular);
        SetColor(material, "_SpecularColor", specularColor);
        SetFloat(material, "_SpecularType", specularType);
        SetFloat(material, "_SpecularRoughness", specularRoughness);
        SetFloat(material, "_SpecularToonSize", specularToonSize);
        SetFloat(material, "_SpecularToonSmoothness", specularToonSmoothness);
        SetFloat(material, "_SpecularMapType", (float)specularMapType);

        SetFloat(material, "_RenderingMode", (float)renderingMode);
        SetFloat(material, "_Cull", (float)renderFace);
        SetBool(material, "_ZWrite", zWrite);
        SetBool(material, "_UseAlphaTest", useAlphaTest);
        SetFloat(material, "_Cutoff", alphaCutoff);

        ApplyAllShaderProperties(material);
    }

    void SaveAllShaderProperties(Material material)
    {
        shader = material.shader;
        shaderName = shader != null ? shader.name : string.Empty;
        shaderKeywords = material.shaderKeywords ?? Array.Empty<string>();
        renderQueue = material.renderQueue;
        enableInstancing = material.enableInstancing;
        doubleSidedGI = material.doubleSidedGI;
        globalIlluminationFlags = material.globalIlluminationFlags;

        if (shader == null)
        {
            shaderProperties = Array.Empty<ShaderPropertyConfig>();
            return;
        }

        int propertyCount = shader.GetPropertyCount();
        shaderProperties = new ShaderPropertyConfig[propertyCount];

        for (int i = 0; i < propertyCount; i++)
        {
            string propertyName = shader.GetPropertyName(i);
            var propertyType = shader.GetPropertyType(i);

            var property = new ShaderPropertyConfig
            {
                name = propertyName,
                type = propertyType
            };

            switch (propertyType)
            {
                case ShaderPropertyType.Texture:
                    property.textureValue = material.GetTexture(propertyName);
                    property.textureScale = material.GetTextureScale(propertyName);
                    property.textureOffset = material.GetTextureOffset(propertyName);
                    break;

                case ShaderPropertyType.Color:
                    property.colorValue = material.GetColor(propertyName);
                    break;

                case ShaderPropertyType.Vector:
                    property.vectorValue = material.GetVector(propertyName);
                    break;

                default:
                    property.floatValue = material.GetFloat(propertyName);
                    break;
            }

            shaderProperties[i] = property;
        }
    }

    void ApplyAllShaderProperties(Material material)
    {
        if (applySavedShader && shader != null && material.shader != shader)
        {
            material.shader = shader;
        }

        material.shaderKeywords = shaderKeywords ?? Array.Empty<string>();
        material.renderQueue = renderQueue;
        material.enableInstancing = enableInstancing;
        material.doubleSidedGI = doubleSidedGI;
        material.globalIlluminationFlags = globalIlluminationFlags;

        if (shaderProperties == null)
        {
            return;
        }

        for (int i = 0; i < shaderProperties.Length; i++)
        {
            var property = shaderProperties[i];
            if (property == null || string.IsNullOrEmpty(property.name) || !material.HasProperty(property.name))
            {
                continue;
            }

            switch (property.type)
            {
                case ShaderPropertyType.Texture:
                    material.SetTexture(property.name, property.textureValue);
                    material.SetTextureScale(property.name, property.textureScale);
                    material.SetTextureOffset(property.name, property.textureOffset);
                    break;

                case ShaderPropertyType.Color:
                    material.SetColor(property.name, property.colorValue);
                    break;

                case ShaderPropertyType.Vector:
                    material.SetVector(property.name, property.vectorValue);
                    break;

                default:
                    material.SetFloat(property.name, property.floatValue);
                    break;
            }
        }
    }

    static Texture2D GetTexture(Material material, string propertyName)
    {
        return material.HasProperty(propertyName) ? material.GetTexture(propertyName) as Texture2D : null;
    }

    static Color GetColor(Material material, string propertyName, Color fallback)
    {
        return material.HasProperty(propertyName) ? material.GetColor(propertyName) : fallback;
    }

    static float GetFloat(Material material, string propertyName, float fallback)
    {
        return material.HasProperty(propertyName) ? material.GetFloat(propertyName) : fallback;
    }

    static bool GetBool(Material material, string propertyName, bool fallback = false)
    {
        return material.HasProperty(propertyName) ? material.GetFloat(propertyName) > 0.5f : fallback;
    }

    static void SetTexture(Material material, string propertyName, Texture texture)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetTexture(propertyName, texture);
        }
    }

    static void SetColor(Material material, string propertyName, Color value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetColor(propertyName, value);
        }
    }

    static void SetFloat(Material material, string propertyName, float value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetFloat(propertyName, value);
        }
    }

    static void SetBool(Material material, string propertyName, bool value)
    {
        SetFloat(material, propertyName, value ? 1f : 0f);
    }

    [Serializable]
    public class ShaderPropertyConfig
    {
        public string name;
        public ShaderPropertyType type;
        public float floatValue;
        public Color colorValue;
        public Vector4 vectorValue;
        public Texture textureValue;
        public Vector2 textureScale = Vector2.one;
        public Vector2 textureOffset;
    }
}
