using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[CustomEditor(typeof(SceneTemplateConfig))]
public class SceneTemplateConfigEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Scene Template Sync", EditorStyles.boldLabel);

        var config = (SceneTemplateConfig)target;

        using (new EditorGUI.DisabledScope(!SceneManager.GetActiveScene().isLoaded))
        {
            if (GUILayout.Button("Save From Current Scene"))
            {
                SaveFromCurrentScene(config);
            }

            if (GUILayout.Button("Apply To Current Scene"))
            {
                ApplyToCurrentScene(config);
            }
        }
    }

    [MenuItem("Config/Scene Template/Create New Config From Current Scene")]
    static void OpenCreateWindow()
    {
        SceneTemplateConfigCreateWindow.Open();
    }

    static void ApplyToCurrentScene(SceneTemplateConfig config)
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            Undo.RegisterFullObjectHierarchyUndo(roots[i], "Apply Scene Template Config");
        }

        Undo.RecordObject(config, "Apply Scene Template Config");

        config.ApplyToCurrentScene();

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"Applied {config.name} to current scene.");
    }

    static void SaveFromCurrentScene(SceneTemplateConfig config)
    {
        Undo.RecordObject(config, "Save Scene Template Config");

        config.SaveFromCurrentScene();

        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Saved current scene settings to {config.name}.");
    }
}

public class SceneTemplateConfigCreateWindow : EditorWindow
{
    const string DefaultDirectory = "Assets/_Res/ZYB/SceneTemplates";

    string configName;
    string directory = DefaultDirectory;

    public static void Open()
    {
        var window = GetWindow<SceneTemplateConfigCreateWindow>(true, "Create Scene Template");
        window.configName = SceneManager.GetActiveScene().name + " Template";
        window.minSize = new Vector2(420f, 130f);
        window.ShowUtility();
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Create New Scene Template Config", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        configName = EditorGUILayout.TextField("Config Name", configName);
        directory = EditorGUILayout.TextField("Directory", directory);

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(configName)))
        {
            if (GUILayout.Button("Create Config From Current Scene"))
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

        var config = CreateInstance<SceneTemplateConfig>();
        config.SaveFromCurrentScene();

        string safeName = string.Join("_", configName.Split(System.IO.Path.GetInvalidFileNameChars()));
        string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{safeName}.asset");

        AssetDatabase.CreateAsset(config, assetPath);
        config.SaveEmbeddedVolumeProfiles();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeObject = config;
        EditorGUIUtility.PingObject(config);

        Debug.Log($"Created scene template config: {assetPath}");
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
