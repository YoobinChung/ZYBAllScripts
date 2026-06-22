using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MaterialConfig))]
public class MaterialConfigEditor : Editor
{
    Material targetMaterial;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Material Sync", EditorStyles.boldLabel);

        targetMaterial = (Material)EditorGUILayout.ObjectField("Target Material", targetMaterial, typeof(Material), false);
        using (new EditorGUI.DisabledScope(targetMaterial == null))
        {
            if (GUILayout.Button("Apply To Target Material"))
            {
                Undo.RecordObject(targetMaterial, "Apply Material Config");
                ((MaterialConfig)target).ApplyToMaterial(targetMaterial);
                EditorUtility.SetDirty(targetMaterial);
            }
        }
    }

    [MenuItem("Config/Material Template/Create New Config From Material")]
    static void OpenCreateWindow()
    {
        MaterialConfigCreateWindow.Open();
    }
}

public class MaterialConfigCreateWindow : EditorWindow
{
    const string DefaultDirectory = "Assets/_Res/ZYB/MaterialTemplates";

    string configName;
    string directory = DefaultDirectory;
    Material sourceMaterial;

    public static void Open()
    {
        var window = GetWindow<MaterialConfigCreateWindow>(true, "Create Material Template");
        window.sourceMaterial = Selection.activeObject as Material;
        window.configName = window.sourceMaterial != null ? window.sourceMaterial.name + " Template" : "Material Template";
        window.minSize = new Vector2(420f, 160f);
        window.ShowUtility();
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Create New Material Template Config", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        sourceMaterial = (Material)EditorGUILayout.ObjectField("Source Material", sourceMaterial, typeof(Material), false);
        configName = EditorGUILayout.TextField("Config Name", configName);
        directory = EditorGUILayout.TextField("Directory", directory);

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(sourceMaterial == null || string.IsNullOrWhiteSpace(configName)))
        {
            if (GUILayout.Button("Create Config From Material"))
            {
                CreateConfig();
            }
        }
    }

    void CreateConfig()
    {
        if (!directory.StartsWith("Assets"))
        {
            Debug.LogWarning("Directory must start with Assets.");
            return;
        }

        EnsureDirectoryExists(directory);

        var config = CreateInstance<MaterialConfig>();
        config.SaveFromMaterial(sourceMaterial);

        string safeName = string.Join("_", configName.Split(System.IO.Path.GetInvalidFileNameChars()));
        string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{safeName}.asset");

        AssetDatabase.CreateAsset(config, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeObject = config;
        EditorGUIUtility.PingObject(config);

        Debug.Log($"Created material template config: {assetPath}");
        Close();
    }

    static void EnsureDirectoryExists(string assetDirectory)
    {
        if (AssetDatabase.IsValidFolder(assetDirectory))
        {
            return;
        }

        string[] parts = assetDirectory.Split('/');
        string current = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }

            current = next;
        }
    }
}
