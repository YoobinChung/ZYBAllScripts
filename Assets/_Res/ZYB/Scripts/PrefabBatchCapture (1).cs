using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

public class PrefabBatchCapture : MonoBehaviour
{
    [Header("预制体文件夹路径 (需以 Assets/ 开头)")]
    public string prefabFolderPath = "Assets/Prefabs";

    [Header("渲染控制设置")]
    [Tooltip("如果勾选，将强制开启不在隐藏列表中的所有 MeshRenderer。如果不勾选，则保持预制体原本的状态。")]
    public bool forceEnableOtherRenderers = true;

    [Header("需要隐藏的对象名称 (匹配名称的对象将关闭渲染)")]
    public List<string> hideObjectNames = new List<string>();

    [Header("保存文件夹名称")]
    public string saveFolder = "CapturedImages";

    [Header("截图时序")]
    [Tooltip("预制体挂到本物体下后，等待多少帧再截图（渲染稳定）。")]
    [Min(0)]
    public int preCaptureWaitFrames = 5;

    [Tooltip("截图完成并销毁实例后，再等待多少帧才加载下一个预制体（两次截图之间的间隔）。")]
    [Min(0)]
    public int postCaptureWaitFrames = 0;

    private string[] prefabGUIDs;
    private int index = 0;
    private GameObject currentInstance;
    private bool waitingForCapture;
    private bool waitingPostCapture;
    private int frameWait;

    void Start()
    {
#if UNITY_EDITOR
        // 查找指定路径下的所有预制体
        prefabGUIDs = AssetDatabase.FindAssets("t:Prefab", new[] { prefabFolderPath });

        // 如果文件夹不存在则创建
        if (!Directory.Exists(saveFolder))
            Directory.CreateDirectory(saveFolder);

        if (prefabGUIDs.Length == 0)
            Debug.LogError("在指定路径未找到预制体: " + prefabFolderPath);
#endif
    }

    void Update()
    {
#if UNITY_EDITOR
        if (prefabGUIDs == null || prefabGUIDs.Length == 0) return;

        if (waitingPostCapture)
        {
            frameWait--;
            if (frameWait > 0)
                return;
            waitingPostCapture = false;
        }

        if (currentInstance == null && index < prefabGUIDs.Length && !waitingPostCapture)
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGUIDs[index]);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            if (prefab != null)
            {
                // 作为本物体子节点生成，保留预制体根上的本地位置/旋转/缩放
                currentInstance = Instantiate(prefab, transform);
                var root = prefab.transform;
                currentInstance.transform.localPosition = root.localPosition;
                currentInstance.transform.localRotation = root.localRotation;
                currentInstance.transform.localScale = root.localScale;

                // 1. 禁用实例中的所有脚本 (防止动画或逻辑干扰)
                DisableAllScriptsInInstance(currentInstance);

                // 2. 控制网格渲染器 (根据选项强制开启或隐藏)
                ControlMeshRenderers(currentInstance);

                // 3. 隐藏场景中其他同名的目标对象
                HideAllTargetObjects();

                waitingForCapture = true;
                frameWait = Mathf.Max(0, preCaptureWaitFrames);
            }
            else
            {
                index++;
            }
        }
        else if (waitingForCapture)
        {
            frameWait--;
            if (frameWait > 0)
                return;

            string cleanName = currentInstance.name.Replace("(Clone)", "").Trim();
            string fileName = saveFolder + "/" + cleanName + ".png";

            // 执行截图
            ScreenCapture.CaptureScreenshot(fileName);
            Debug.Log($"截图完成: {fileName}");

            // 清理实例并准备下一个
            DestroyImmediate(currentInstance);
            currentInstance = null;
            index++;
            waitingForCapture = false;

            var gap = Mathf.Max(0, postCaptureWaitFrames);
            if (gap > 0)
            {
                waitingPostCapture = true;
                frameWait = gap;
            }
        }
        else if (index >= prefabGUIDs.Length)
        {
            Debug.Log("<color=green>所有预制体批量截图任务已完成！</color>");
            prefabGUIDs = null;
        }
#endif
    }

    /// <summary>
    /// 控制预制体内部的 MeshRenderer。
    /// 在隐藏列表中的关闭，不在列表中的根据选项决定是否强制开启。
    /// </summary>
    void ControlMeshRenderers(GameObject instance)
    {
        MeshRenderer[] renderers = instance.GetComponentsInChildren<MeshRenderer>(true);

        foreach (MeshRenderer renderer in renderers)
        {
            bool isTargetToHide = hideObjectNames.Contains(renderer.gameObject.name);

            if (isTargetToHide)
            {
                // 如果在隐藏名单中，直接禁用渲染
                renderer.enabled = false;
            }
            else if (forceEnableOtherRenderers)
            {
                // 如果不在名单中，且开启了“强制渲染”选项，则启用
                renderer.enabled = true;
            }
            // 否则保持预制体原本的 renderer.enabled 状态
        }
    }

    /// <summary>
    /// 禁用实例及其子对象上的所有脚本（不包括当前截图脚本）
    /// </summary>
    void DisableAllScriptsInInstance(GameObject instance)
    {
        MonoBehaviour[] scripts = instance.GetComponentsInChildren<MonoBehaviour>();
        foreach (MonoBehaviour script in scripts)
        {
            if (script != null && script.GetType() != typeof(PrefabBatchCapture))
            {
                script.enabled = false;
            }
        }
    }

    /// <summary>
    /// 隐藏场景中（非当前实例）名称匹配的目标对象
    /// </summary>
    void HideAllTargetObjects()
    {
        if (hideObjectNames == null || hideObjectNames.Count == 0) return;

        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        foreach (GameObject obj in allObjects)
        {
            // 过滤掉无效对象
            if (!obj.activeInHierarchy && obj.transform.parent == null && !obj.scene.isLoaded) continue;

            if (hideObjectNames.Contains(obj.name))
            {
                // 不要隐藏正在截图的实例本身
                if (currentInstance != null && obj == currentInstance) continue;
                obj.SetActive(false);
            }
        }
    }
}