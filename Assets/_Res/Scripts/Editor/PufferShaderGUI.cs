using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class PufferShaderGUI : ShaderGUI
{
    private const string ShadowCasterPass = "ShadowCaster";
    private const float ToggleOffset = 12f;
    private const float ToggleRightPadding = 12f;
    private const float ToggleSize = 16f;
    private static GUIStyle sectionTitleStyle;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        DrawMaterialProperties(materialEditor, properties);

        if (materialEditor.targets == null)
            return;

        foreach (Object target in materialEditor.targets)
        {
            if (target is Material material)
                SetupMaterial(material);
        }
    }

    public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
    {
        base.AssignNewShaderToMaterial(material, oldShader, newShader);
        SetupMaterial(material);
    }

    private static void DrawMaterialProperties(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        DrawSection("Transparency", false);
        DrawToggleProperty(materialEditor, properties, "_Transparency");
        DrawProperty(materialEditor, properties, "_RenderFace");
        DrawProperty(materialEditor, properties, "_Cutoff");

        DrawTextureSection(materialEditor, properties, "Base", "Albedo", "_BaseMap", "_BaseColor");

        DrawSection("Shadow");
        DrawProperty(materialEditor, properties, "_ShadowColor");
        DrawProperty(materialEditor, properties, "_ShadowThreshold");
        DrawProperty(materialEditor, properties, "_ShadowSoftness");

        DrawTextureSection(materialEditor, properties, "Normal", "Normal", "_NormalMap");
        DrawProperty(materialEditor, properties, "_NormalScale");

        DrawSection("Specular");
        DrawProperty(materialEditor, properties, "_SpecularColor");
        DrawProperty(materialEditor, properties, "_SpecularStrength");
        DrawProperty(materialEditor, properties, "_SpecularSmoothness");

        DrawTextureSection(materialEditor, properties, "AO", "AO", "_OcclusionMap");
        DrawProperty(materialEditor, properties, "_OcclusionStrength");

        DrawTextureSection(materialEditor, properties, "MatCap", "MatCap", "_MatCapMap", null, false);
        DrawProperty(materialEditor, properties, "_MatCapColor");
        DrawProperty(materialEditor, properties, "_MatCapStrength");
        DrawProperty(materialEditor, properties, "_MatCapBlend");

        DrawOutlineProperties(materialEditor, properties);

        DrawSection("Option");
        DrawToggleProperty(materialEditor, properties, "_ReceiveShadows");
        DrawMaterialToggle(materialEditor, "Double Sided Global Illumination", material => material.doubleSidedGI, (material, value) => material.doubleSidedGI = value);

        EditorGUILayout.Space(6f);
        materialEditor.RenderQueueField();
        DrawInstancingToggle(materialEditor);
    }

    private static void DrawSection(string title, bool drawLine = true)
    {
        EditorGUILayout.Space(8f);
        if (drawLine)
        {
            Rect lineRect = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(lineRect, new Color(0.35f, 0.35f, 0.35f, 1f));
            EditorGUILayout.Space(4f);
        }

        EditorGUILayout.LabelField(title, SectionTitleStyle);
    }

    private static void DrawProperty(MaterialEditor materialEditor, MaterialProperty[] properties, string propertyName)
    {
        MaterialProperty property = FindProperty(propertyName, properties, false);
        if (property != null)
            materialEditor.ShaderProperty(property, property.displayName);
    }

    private static void DrawOutlineProperties(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        if (FindProperty("_OutlineColor", properties, false) == null)
            return;

        DrawSection("Outline");
        DrawProperty(materialEditor, properties, "_OutlineColor");
        DrawProperty(materialEditor, properties, "_OutlineWidth");
    }

    private static void DrawToggleProperty(MaterialEditor materialEditor, MaterialProperty[] properties, string propertyName)
    {
        MaterialProperty property = FindProperty(propertyName, properties, false);
        if (property == null)
            return;

        EditorGUI.BeginChangeCheck();
        bool enabled = DrawOffsetToggle(property.displayName, property.floatValue > 0.5f, property.hasMixedValue);

        if (EditorGUI.EndChangeCheck())
        {
            materialEditor.RegisterPropertyChangeUndo(property.displayName);
            property.floatValue = enabled ? 1f : 0f;
        }
    }

    private static void DrawMaterialToggle(MaterialEditor materialEditor, string label, System.Func<Material, bool> getter, System.Action<Material, bool> setter)
    {
        Material[] materials = GetSelectedMaterials(materialEditor);
        if (materials.Length == 0)
            return;

        bool value = getter(materials[0]);
        bool mixedValue = false;

        for (int i = 1; i < materials.Length; i++)
        {
            if (getter(materials[i]) != value)
            {
                mixedValue = true;
                break;
            }
        }

        EditorGUI.BeginChangeCheck();
        bool newValue = DrawOffsetToggle(label, value, mixedValue);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObjects(materials, label);
            foreach (Material material in materials)
                setter(material, newValue);
        }
    }

    private static void DrawInstancingToggle(MaterialEditor materialEditor)
    {
        DrawMaterialToggle(
            materialEditor,
            "Enable GPU Instancing",
            material => material.enableInstancing,
            (material, value) => material.enableInstancing = value);
    }

    private static bool DrawOffsetToggle(string label, bool value, bool mixedValue)
    {
        Rect rect = EditorGUILayout.GetControlRect();
        float toggleX = rect.xMax - ToggleSize - ToggleRightPadding;
        Rect labelRect = new Rect(rect.x, rect.y, Mathf.Max(0f, toggleX - rect.x - 6f), rect.height);
        Rect toggleRect = new Rect(toggleX, rect.y + 1f, ToggleSize, ToggleSize);

        EditorGUI.LabelField(labelRect, label);

        EditorGUI.showMixedValue = mixedValue;
        bool newValue = EditorGUI.Toggle(toggleRect, value);
        EditorGUI.showMixedValue = false;

        return newValue;
    }

    private static Material[] GetSelectedMaterials(MaterialEditor materialEditor)
    {
        Object[] targets = materialEditor.targets;
        int materialCount = 0;

        foreach (Object target in targets)
        {
            if (target is Material)
                materialCount++;
        }

        Material[] materials = new Material[materialCount];
        int index = 0;

        foreach (Object target in targets)
        {
            if (target is Material material)
                materials[index++] = material;
        }

        return materials;
    }

    private static GUIStyle SectionTitleStyle
    {
        get
        {
            if (sectionTitleStyle == null)
            {
                sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel);
                sectionTitleStyle.normal.textColor = new Color(1f, 0.55f, 0f);
            }

            return sectionTitleStyle;
        }
    }

    private static void DrawTextureSection(
        MaterialEditor materialEditor,
        MaterialProperty[] properties,
        string title,
        string textureLabel,
        string textureName,
        string colorName = null,
        bool drawScaleOffset = true)
    {
        MaterialProperty texture = FindProperty(textureName, properties, false);
        if (texture == null)
            return;

        MaterialProperty color = string.IsNullOrEmpty(colorName) ? null : FindProperty(colorName, properties, false);

        DrawSectionLine();

        bool hasColor = color != null;
        Rect rect = EditorGUILayout.GetControlRect(false, hasColor ? 108f : 98f);
        const float textureSize = 64f;
        const float colorHeight = 16f;
        const float rowHeight = 18f;
        const float rightGap = 6f;

        float y = rect.y + 4f;
        float labelX = rect.x;
        float labelWidth = 66f;
        float textureX = rect.xMax - textureSize - 2f;
        float fieldsEnd = textureX - rightGap;
        float textureY = hasColor ? y + 34f : y + 24f;

        Rect titleRect = new Rect(labelX, y, labelWidth, rowHeight);
        Rect textureRect = new Rect(textureX, textureY, textureSize, textureSize);
        Rect textureLabelRect = new Rect(labelX, textureY, labelWidth, rowHeight);

        EditorGUI.LabelField(titleRect, title, SectionTitleStyle);

        EditorGUI.BeginChangeCheck();

        if (hasColor)
        {
            Rect colorLabelRect = new Rect(labelX, y + 20f, labelWidth, rowHeight);
            Rect colorRect = new Rect(textureX, y + 20f, textureSize, colorHeight);
            EditorGUI.LabelField(colorLabelRect, "Color");
            color.colorValue = EditorGUI.ColorField(colorRect, GUIContent.none, color.colorValue, true, true, false);
        }

        EditorGUI.LabelField(textureLabelRect, textureLabel);

        Vector4 scaleOffset = texture.textureScaleAndOffset;
        Vector2 scale = new Vector2(scaleOffset.x, scaleOffset.y);
        Vector2 offset = new Vector2(scaleOffset.z, scaleOffset.w);

        if (drawScaleOffset)
        {
            float fieldStart = labelX + 16f;
            Rect tilingRect = new Rect(fieldStart, textureY + 28f, fieldsEnd - fieldStart, rowHeight);
            Rect offsetRect = new Rect(fieldStart, textureY + 48f, fieldsEnd - fieldStart, rowHeight);

            scale = DrawCompactVector2(tilingRect, "Tiling", scale);
            offset = DrawCompactVector2(offsetRect, "Offset", offset);
        }

        Object newTexture = EditorGUI.ObjectField(textureRect, GUIContent.none, texture.textureValue, typeof(Texture), false);

        if (EditorGUI.EndChangeCheck())
        {
            materialEditor.RegisterPropertyChangeUndo(texture.displayName);
            texture.textureValue = newTexture as Texture;
            texture.textureScaleAndOffset = new Vector4(scale.x, scale.y, offset.x, offset.y);
        }

        EditorGUILayout.Space(3f);
    }

    private static void DrawSectionLine()
    {
        EditorGUILayout.Space(8f);
        Rect lineRect = EditorGUILayout.GetControlRect(false, 1f);
        EditorGUI.DrawRect(lineRect, new Color(0.35f, 0.35f, 0.35f, 1f));
    }

    private static Vector2 DrawCompactVector2(Rect rect, string label, Vector2 value)
    {
        const float labelWidth = 48f;
        const float axisLabelWidth = 12f;
        const float fieldGap = 3f;
        float fieldWidth = Mathf.Max(48f, (rect.width - labelWidth - axisLabelWidth * 2f - fieldGap * 3f) * 0.5f);

        Rect labelRect = new Rect(rect.x, rect.y, labelWidth, rect.height);
        Rect xLabelRect = new Rect(labelRect.xMax + fieldGap, rect.y, axisLabelWidth, rect.height);
        Rect xRect = new Rect(xLabelRect.xMax, rect.y, fieldWidth, rect.height);
        Rect yLabelRect = new Rect(xRect.xMax + fieldGap, rect.y, axisLabelWidth, rect.height);
        Rect yRect = new Rect(yLabelRect.xMax, rect.y, fieldWidth, rect.height);

        EditorGUI.LabelField(labelRect, label);
        EditorGUI.LabelField(xLabelRect, "X");
        value.x = EditorGUI.FloatField(xRect, value.x);
        EditorGUI.LabelField(yLabelRect, "Y");
        value.y = EditorGUI.FloatField(yRect, value.y);

        return value;
    }

    private static void SetupMaterial(Material material)
    {
        bool transparent = GetToggle(material, "_Transparency");

        if (transparent)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.renderQueue = (int)RenderQueue.Transparent;
        }
        else
        {
            material.SetOverrideTag("RenderType", "Opaque");
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.Zero);
            material.SetFloat("_ZWrite", 1f);
            material.renderQueue = (int)RenderQueue.Geometry;
        }

        material.SetShaderPassEnabled(ShadowCasterPass, !transparent);
    }

    private static bool GetToggle(Material material, string propertyName)
    {
        return material.HasProperty(propertyName) && material.GetFloat(propertyName) > 0.5f;
    }

}
