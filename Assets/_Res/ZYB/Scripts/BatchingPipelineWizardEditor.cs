using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 工程侧前提（SRP Batcher、Dynamic Batching、GPU Instancing、Depth Write、Occlusion、Cast/Receive Shadow）与 Play 下 Render 计数器采样对照。
/// </summary>
public class BatchingPipelineWizardEditor : EditorWindow
{
    private const string MenuPath = "Tools/合批与剔除向导";

    private int _step;
    private Vector2 _scroll;
    private List<DiagnosisRow> _rows = new List<DiagnosisRow>();
    private string _stepSummary = "";
    private bool _stepAllOk;
    private bool _urpMaterialMismatchFoldout = true;

    private const double RuntimePerfSampleSeconds = 1.0;

    private ProfilerRecorder _perfDrawCalls;
    private ProfilerRecorder _perfBatches;
    private ProfilerRecorder _perfSetPass;
    private ProfilerRecorder _perfTriangles;
    private bool _perfRecordersCreated;
    private bool _perfTrianglesValid;

    private bool _perfSamplingActive;
    private bool _perfSamplingIsBaseline;
    private double _perfSamplingEndTime;
    private readonly List<long> _perfSampleDraws = new List<long>(128);
    private readonly List<long> _perfSampleBatches = new List<long>(128);
    private readonly List<long> _perfSampleSetPass = new List<long>(128);
    private readonly List<long> _perfSampleTriangles = new List<long>(128);

    private RuntimePerfSnapshot _perfBaseline;
    private RuntimePerfSnapshot _perfAfter;
    private bool _perfHasBaseline;
    private bool _perfHasAfter;

    [MenuItem(MenuPath)]
    public static void Open()
    {
        var w = GetWindow<BatchingPipelineWizardEditor>();
        w.titleContent = new GUIContent("合批与剔除向导");
        w.minSize = new Vector2(580, 460);
        w.AnalyzeCurrentStep();
    }

    private void OnEnable()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AnalyzeCurrentStep();
    }

    private void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        StopPerfSamplingIfNeeded();
        DisposePerfRecorders();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
            StopPerfSamplingIfNeeded();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("合批与剔除向导", EditorStyles.largeLabel);
        EditorGUILayout.LabelField(
            "第一页仅列出待处理的工程检查项（已通过项不显示）；第二页为 Play 下 Render 计数器采样对比。合批与瓶颈请以 Profiler / Frame Debugger 为准。",
            EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(8);

        DrawStepTabs();
        EditorGUILayout.Space(10);

        if (GUILayout.Button("重新分析", GUILayout.Width(88)))
            AnalyzeCurrentStep();

        EditorGUILayout.Space(8);

        var okColor = new Color(0.25f, 0.72f, 0.4f);
        var warnColor = new Color(0.92f, 0.72f, 0.18f);
        var badColor = new Color(0.9f, 0.35f, 0.3f);
        var accent = _stepAllOk ? okColor : HasPendingErrorRow() ? badColor : warnColor;
        var r = EditorGUILayout.GetControlRect(false, 4f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(r, accent);
        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField(_stepSummary, EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        var displayRows = GetDisplayRows();
        if (displayRows.Count == 0)
        {
            EditorGUILayout.HelpBox(
                _step == 0
                    ? "工程检查均已满足，当前无待处理项。可点击「重新分析」刷新，或切换到「运行时对比」采样性能。"
                    : "尚无待显示内容。",
                MessageType.Info);
        }
        else
        {
            foreach (var row in displayRows)
                DrawRow(row, okColor, warnColor, badColor);
        }

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(10);
        using (new EditorGUI.DisabledScope(!HasAnyFixableRow()))
        {
            var batchLabel = _step == 0 ? "应用全部自动修复（本页项目）" : "应用本步骤全部自动修复";
            if (GUILayout.Button(batchLabel, GUILayout.Height(32)))
            {
                ApplyAllFixesInCurrentStep();
                AnalyzeCurrentStep();
            }
        }

#if false
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(
            "说明：Occlusion Culling 仅检测各场景中 Camera 是否勾选 Occlusion Culling。",
            EditorStyles.miniLabel);
#endif
    }

    private void DrawStepTabs()
    {
        var titles = new[]
        {
            "工程检查",
            "运行时对比"
        };

        EditorGUILayout.BeginHorizontal();
        for (var i = 0; i < titles.Length; i++)
        {
            var sel = i == _step;
            var prev = GUI.backgroundColor;
            if (sel)
                GUI.backgroundColor = new Color(0.55f, 0.72f, 1f, 1f);
            if (GUILayout.Toggle(sel, titles[i], "MiniButton", GUILayout.Height(24)))
            {
                if (_step != i)
                {
                    _step = i;
                    AnalyzeCurrentStep();
                }
            }

            GUI.backgroundColor = prev;
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawRow(DiagnosisRow row, Color okColor, Color warnColor, Color badColor)
    {
        if (row.SectionHeader)
        {
            EditorGUILayout.Space(10);
            var line = EditorGUILayout.GetControlRect(false, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(line, new Color(0.45f, 0.45f, 0.48f, 0.45f));
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(row.Title, EditorStyles.largeLabel);
            EditorGUILayout.Space(2);
            return;
        }

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        var mark = row.IsOk ? "●" : "○";
        var c = GUI.color;
        GUI.color = ResolveRowColor(row, okColor, warnColor, badColor);
        GUILayout.Label(mark, GUILayout.Width(16));
        GUI.color = c;
        EditorGUILayout.LabelField(row.Title, EditorStyles.boldLabel);
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(row.Detail))
            EditorGUILayout.LabelField(row.Detail, EditorStyles.wordWrappedMiniLabel);

        row.CustomGui?.Invoke();

        if (row.Fix != null && (!row.IsOk || row.ExcludeFromBatchFix))
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var fixLabel = string.IsNullOrEmpty(row.FixButtonLabel) ? "仅应用此项" : row.FixButtonLabel;
            if (GUILayout.Button(fixLabel, GUILayout.Width(Mathf.Max(100, GUI.skin.button.CalcSize(new GUIContent(fixLabel)).x + 16))))
            {
                try
                {
                    row.Fix.Invoke();
                    AssetDatabase.SaveAssets();
                    var win = this;
                    EditorApplication.delayCall += () =>
                    {
                        if (win != null)
                            win.AnalyzeCurrentStep();
                    };
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[合批向导] {row.Title}: {ex.Message}");
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(4);
    }

    private static Color ResolveRowColor(DiagnosisRow row, Color okColor, Color warnColor, Color badColor)
    {
        if (row.IsOk)
            return okColor;
        return row.Severity == DiagnosisSeverity.Warning ? warnColor : badColor;
    }

    /// <summary>工程检查页仅展示待处理项；运行时对比页保留完整操作面板。</summary>
    private List<DiagnosisRow> GetDisplayRows()
    {
        if (_step == 1)
            return _rows;

        var result = new List<DiagnosisRow>();
        DiagnosisRow pendingSection = null;
        foreach (var row in _rows)
        {
            if (row.SectionHeader)
            {
                pendingSection = row;
                continue;
            }

            if (row.IsOk)
                continue;

            if (pendingSection != null)
            {
                result.Add(pendingSection);
                pendingSection = null;
            }

            result.Add(row);
        }

        return result;
    }

    private bool HasAnyFixableRow()
    {
        foreach (var row in GetDisplayRows())
        {
            if (row.SectionHeader)
                continue;
            if (!row.IsOk && row.Fix != null && !row.ExcludeFromBatchFix)
                return true;
        }

        return false;
    }

    private bool HasPendingErrorRow()
    {
        foreach (var row in _rows)
        {
            if (row.SectionHeader || !row.AffectsSummary || row.IsOk)
                continue;
            if (row.Severity != DiagnosisSeverity.Warning)
                return true;
        }

        return false;
    }

    private void ApplyAllFixesInCurrentStep()
    {
        var n = 0;
        foreach (var row in _rows)
        {
            if (row.SectionHeader)
                continue;
            if (row.IsOk || row.Fix == null || row.ExcludeFromBatchFix)
                continue;
            try
            {
                row.Fix.Invoke();
                n++;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[合批向导] 跳过「{row.Title}」: {ex.Message}");
            }
        }

        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("合批与剔除向导", n > 0 ? $"已执行 {n} 项自动修复。" : "没有可执行的自动修复项。", "确定");
    }

    private void AnalyzeCurrentStep()
    {
        if (_step != 1)
            StopPerfSamplingIfNeeded();

        _rows.Clear();
        switch (_step)
        {
            case 0:
                AnalyzeCombinedBatchingSteps();
                break;
            case 1:
                AnalyzeRuntimePerfCompare();
                break;
            default:
                _step = 0;
                AnalyzeCombinedBatchingSteps();
                break;
        }

        RecomputeStepSummary();
        Repaint();
    }

    private void AddWizardSectionHeader(string title)
    {
        _rows.Add(new DiagnosisRow
        {
            SectionHeader = true,
            Title = title,
            IsOk = true,
            AffectsSummary = false,
            Fix = null
        });
    }

    private void AnalyzeCombinedBatchingSteps()
    {
        AddWizardSectionHeader("SRP Batcher");
        AnalyzeSrpBatcher();
        AddWizardSectionHeader("Dynamic Batching");
        AnalyzeDynamicBatching();
        AddWizardSectionHeader("GPU Instancing");
        AnalyzeGpuInstancing();
        AddWizardSectionHeader("Depth Write");
        AnalyzeDepthWrite();
        AddWizardSectionHeader("Model Materials");
        AnalyzeModelMaterialSlots();
        AddWizardSectionHeader("Occlusion Culling");
        AnalyzeOcclusionCulling();
        AddWizardSectionHeader("Cast Shadow");
        AnalyzeCastShadows();
        AddWizardSectionHeader("Receive Shadows");
        AnalyzeReceiveShadows();
        AddWizardSectionHeader("Scene Stats (Active Scene)");
        AnalyzeSceneTotalPolygons();
        AnalyzeSceneEstimatedDrawCalls();
        AnalyzeSceneTextureCount();
        AnalyzeSceneOutlierMeshes();
    }

    private void RecomputeStepSummary()
    {
        if (_step == 1)
        {
            _stepAllOk = true;
            _stepSummary = _perfSamplingActive
                ? "正在采样 Render 计数器（约 1 秒）… 请保持 Play 与负载稳定。"
                : "Play 下采样约 1 秒 Render 计数器均值；对比为参考值，请以 Profiler / Frame Debugger 为准。";
            return;
        }

        var considered = _rows.FindAll(r => r.AffectsSummary && !r.SectionHeader);
        _stepAllOk = considered.Count > 0 && considered.TrueForAll(r => r.IsOk);
        if (_stepAllOk)
            _stepSummary = "工程检查均已满足，无待处理项。";
        else if (considered.Count == 0)
            _stepSummary = "本页无纳入汇总的检查项。";
        else
        {
            var pending = considered.FindAll(r => !r.IsOk).Count;
            _stepSummary = pending > 0
                ? $"待处理 {pending} 项（仅列出需处理项）：可逐项「仅应用此项」，或使用底部「应用全部自动修复（本页项目）」。"
                : "存在待处理项：可逐项「仅应用此项」，或使用底部「应用全部自动修复（本页项目）」。";
        }
    }

    private static UniversalRenderPipelineAsset TryGetUrpAsset() =>
        GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;

    private static bool ShouldSkipAssetPathForProjectScan(string assetPath) =>
        string.IsNullOrEmpty(assetPath) || IsPackageAssetPath(assetPath);

    private static bool IsPackageAssetPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return false;

        var normalized = assetPath.Replace('\\', '/');
        foreach (var segment in normalized.Split('/'))
        {
            if (string.Equals(segment, "Packages", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "_Packages", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "PackageCache", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "JMO Assets", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool RendererReferencesPackageAsset(Renderer renderer)
    {
        if (renderer == null)
            return false;

        if (renderer is SkinnedMeshRenderer skinnedMeshRenderer &&
            IsPackageAssetPath(AssetDatabase.GetAssetPath(skinnedMeshRenderer.sharedMesh)))
            return true;

        var meshFilter = renderer.GetComponent<MeshFilter>();
        if (meshFilter != null && IsPackageAssetPath(AssetDatabase.GetAssetPath(meshFilter.sharedMesh)))
            return true;

        var mats = renderer.sharedMaterials;
        if (mats == null)
            return false;

        foreach (var mat in mats)
        {
            if (mat != null && IsPackageAssetPath(AssetDatabase.GetAssetPath(mat)))
                return true;
        }

        return false;
    }

    private void AnalyzeSrpBatcher()
    {
        var urp = TryGetUrpAsset();
        if (urp == null)
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = false,
                AffectsSummary = true,
                Title = "当前未使用 URP Asset（Graphics Settings）",
                Detail = "SRP Batcher 仅对 URP/HDRP 等可编程管线生效。请在 Project Settings → Graphics 中指定 Universal Render Pipeline Asset。",
                Fix = null
            });
            return;
        }

        var path = AssetDatabase.GetAssetPath(urp);
        _rows.Add(new DiagnosisRow
        {
            IsOk = true,
            AffectsSummary = false,
            Title = "已指定 URP 为当前渲染管线",
            Detail = string.IsNullOrEmpty(path) ? "资源可能来自内置/只读，无法显示工程路径。" : $"Asset：{path}",
            Fix = null
        });

        _rows.Add(new DiagnosisRow
        {
            IsOk = urp.useSRPBatcher,
            AffectsSummary = true,
            Title = "URP Asset 中 SRP Batcher 开关",
            Detail = urp.useSRPBatcher
                ? "useSRPBatcher 已开启。"
                : "应开启 SRP Batcher 以降低 CPU 端材质状态开销（与 Shader 是否兼容有关，详见官方文档）。",
            Fix = urp.useSRPBatcher
                ? null
                : () =>
                {
                    var p = TryGetUrpAsset();
                    if (p == null) return;
                    p.useSRPBatcher = true;
                    EditorUtility.SetDirty(p);
                }
        });

        var legacyPaths = CollectMaterialsUsingNonSrpShaders();
        var legacyPathsCopy = new List<string>(legacyPaths);
        _rows.Add(new DiagnosisRow
        {
            IsOk = legacyPaths.Count == 0,
            AffectsSummary = true,
            Title = "材质 Shader 与 URP 管线一致性（粗检）",
            Detail = legacyPaths.Count == 0
                ? "未发现明显使用 Built-in / 非工程 URP 路径 Shader 的材质（启发式扫描）。"
                : legacyPaths.Count == 1
                    ? "该材质使用的 Shader 可能不适配当前 URP（启发式），可能无法享受 SRP Batcher；请在材质上改用 URP/Lit 或 Shader Graph。点击「定位」在 Project 窗口中跳转。"
                    : $"共 {legacyPaths.Count} 个材质使用的 Shader 可能不适配当前 URP（启发式），可能无法享受 SRP Batcher；请在材质上改用 URP/Lit 或 Shader Graph。展开下列表后，逐项点击「定位」在 Project 窗口中跳转。",
            CustomGui = legacyPaths.Count == 0
                ? null
                : () => DrawUrpMismatchMaterialLocateGui(legacyPathsCopy)
        });
    }

    private void DrawUrpMismatchMaterialLocateGui(List<string> paths)
    {
        if (paths == null || paths.Count == 0)
            return;

        if (paths.Count == 1)
        {
            DrawUrpMismatchMaterialLocateRow(paths[0]);
            return;
        }

        _urpMaterialMismatchFoldout = EditorGUILayout.Foldout(
            _urpMaterialMismatchFoldout,
            $"不合规材质（{paths.Count}）",
            true);
        if (!_urpMaterialMismatchFoldout)
            return;

        EditorGUI.indentLevel++;
        foreach (var path in paths)
            DrawUrpMismatchMaterialLocateRow(path);
        EditorGUI.indentLevel--;
    }

    private static void DrawUrpMismatchMaterialLocateRow(string assetPath)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.ObjectField(mat, typeof(Material), false);
        if (GUILayout.Button("定位", GUILayout.Width(44)))
        {
            if (mat != null)
            {
                Selection.activeObject = mat;
                EditorGUIUtility.PingObject(mat);
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    /// <summary>
    /// 启发式：非 URP 常见命名且为 Built-in 侧 Standard / Legacy / Particles 等路径的材质资源路径。
    /// </summary>
    private static List<string> CollectMaterialsUsingNonSrpShaders()
    {
        var list = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (ShouldSkipAssetPathForProjectScan(p))
                continue;
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null || m.shader == null)
                continue;
            var sn = m.shader.name;
            if (sn.StartsWith("Hidden/", StringComparison.Ordinal))
                continue;
            var urpish = sn.Contains("Universal Render Pipeline", StringComparison.Ordinal) ||
                         sn.Contains("URP/", StringComparison.Ordinal) ||
                         sn.Contains("Shader Graphs", StringComparison.Ordinal) ||
                         sn.StartsWith("UI/", StringComparison.Ordinal) ||
                         sn.Contains("TextMeshPro", StringComparison.Ordinal) ||
                         sn.StartsWith("Sprites/", StringComparison.Ordinal);
            if (!urpish && (sn.StartsWith("Standard", StringComparison.Ordinal) ||
                            sn.StartsWith("Legacy Shaders/", StringComparison.Ordinal) ||
                            sn.StartsWith("Particles/", StringComparison.Ordinal)))
                list.Add(p);
        }

        return list;
    }

    private void AnalyzeDynamicBatching()
    {
        var urp = TryGetUrpAsset();
        if (urp == null)
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = false,
                AffectsSummary = true,
                Title = "未检测到 URP Asset",
                Detail = "Dynamic Batching（URP）由 Universal Render Pipeline Asset 上的 supportsDynamicBatching 控制。",
                Fix = null
            });
            return;
        }

        _rows.Add(new DiagnosisRow
        {
            IsOk = urp.supportsDynamicBatching,
            AffectsSummary = true,
            Title = "URP Asset 中 Dynamic Batching 开关",
            Detail = urp.supportsDynamicBatching
                ? "supportsDynamicBatching 已开启。"
                : "未勾选。若希望启用动态合批，可打开此项（小网格、同材质等条件下才可能生效）。",
            Fix = urp.supportsDynamicBatching
                ? null
                : () =>
                {
                    var p = TryGetUrpAsset();
                    if (p == null) return;
                    p.supportsDynamicBatching = true;
                    EditorUtility.SetDirty(p);
                }
        });
    }

    private void AnalyzeGpuInstancing()
    {
        var toFix = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (ShouldSkipAssetPathForProjectScan(p))
                continue;
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null || m.shader == null || m.enableInstancing)
                continue;
            if (ShaderExcludedFromGpuInstancingScan(m.shader))
                continue;
            toFix.Add(p);
        }

        var toFixCopy = new List<string>(toFix);
        _rows.Add(new DiagnosisRow
        {
            IsOk = toFix.Count == 0,
            AffectsSummary = true,
            Title = "已勾选所有材质里的 GPU Instancing（Assets）",
            Detail = toFix.Count == 0
                ? "勾选所有材质里的 GPU Instancing（或均在排除列表内：Hidden / Particles / Skybox）。"
                : $"共 {toFix.Count} 个材质未勾选 Enable GPU Instancing。点击下方按钮将依次尝试开启（Shader 不支持时仍会保持关闭）。",
            Fix = toFix.Count == 0
                ? null
                : () =>
                {
                    var failed = new List<string>();
                    foreach (var path in toFixCopy)
                    {
                        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (m == null || m.shader == null)
                            continue;
                        if (m.enableInstancing)
                            continue;
                        if (!TryEnableMaterialGpuInstancing(m))
                            failed.Add(path);
                    }

                    AssetDatabase.SaveAssets();
                    if (failed.Count > 0)
                        Debug.LogWarning(
                            "[合批向导] 以下材质未能开启 GPU Instancing（Shader 不支持或序列化字段不可写）：\n" +
                            string.Join("\n", failed));
                }
        });
    }

    /// <summary>不参与「未开启 GPU Instancing」列表的 Shader（减少明显不适用的噪音）。</summary>
    private static bool ShaderExcludedFromGpuInstancingScan(Shader shader)
    {
        if (shader == null)
            return true;
        var sn = shader.name;
        if (sn.StartsWith("Hidden/", StringComparison.Ordinal))
            return true;
        if (sn.StartsWith("Particles/", StringComparison.Ordinal))
            return true;
        if (sn.StartsWith("Skybox/", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>先走 API，失败则尝试常见序列化字段名（兼容部分自定义管线材质）。</summary>
    private static bool TryEnableMaterialGpuInstancing(Material m)
    {
        if (m == null || m.shader == null)
            return false;
        if (m.enableInstancing)
            return true;

        m.enableInstancing = true;
        if (m.enableInstancing)
        {
            EditorUtility.SetDirty(m);
            return true;
        }

        var so = new SerializedObject(m);
        so.Update();
        foreach (var propName in new[] { "m_EnableInstancing", "enableInstancing", "m_EnableGPUInstancing" })
        {
            var sp = so.FindProperty(propName);
            if (sp == null || sp.propertyType != SerializedPropertyType.Boolean)
                continue;
            sp.boolValue = true;
            so.ApplyModifiedProperties();
            if (m.enableInstancing)
            {
                EditorUtility.SetDirty(m);
                return true;
            }
        }

        return false;
    }

    private void AnalyzeDepthWrite()
    {
        var toFix = new List<string>();
        var scannedOpaque = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (ShouldSkipAssetPathForProjectScan(p))
                continue;
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null || m.shader == null)
                continue;
            if (ShaderExcludedFromMaterialPropertyScan(m.shader))
                continue;
            if (!IsNonTransparentMaterial(m))
                continue;
            if (!m.HasProperty("_ZWrite"))
                continue;

            scannedOpaque++;
            if (!IsMaterialDepthWriteEnabled(m))
                toFix.Add(p);
        }

        var toFixCopy = new List<string>(toFix);
        _rows.Add(new DiagnosisRow
        {
            IsOk = toFix.Count == 0,
            AffectsSummary = true,
            Title = "非透明材质 Depth Write（_ZWrite）",
            Detail = toFix.Count == 0
                ? scannedOpaque == 0
                    ? "未发现带 _ZWrite 属性的非透明材质（已跳过透明材质与无该属性的 Shader）。"
                    : $"已检查 {scannedOpaque} 个非透明材质：Depth Write 均已开启。"
                : $"共 {toFix.Count} 个非透明材质的 Depth Write 未开启（已扫描 {scannedOpaque} 个非透明且含 _ZWrite 的材质）。点击修复将 _ZWrite 设为开启。",
            Fix = toFix.Count == 0
                ? null
                : () =>
                {
                    var failed = new List<string>();
                    foreach (var path in toFixCopy)
                    {
                        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (m == null || m.shader == null)
                            continue;
                        if (!IsNonTransparentMaterial(m))
                            continue;
                        if (!TryEnableMaterialDepthWrite(m))
                            failed.Add(path);
                    }

                    AssetDatabase.SaveAssets();
                    if (failed.Count > 0)
                        Debug.LogWarning(
                            "[合批向导] 以下材质未能开启 Depth Write：\n" + string.Join("\n", failed));
                }
        });
    }

    /// <summary>非透明：URP _Surface、Hybrid _RenderingMode、Standard _Mode，或渲染队列启发式。</summary>
    private static bool IsNonTransparentMaterial(Material m)
    {
        if (m == null)
            return false;

        if (m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"))
            return false;

        if (m.HasProperty("_Surface"))
            return m.GetFloat("_Surface") < 0.5f;

        if (m.HasProperty("_RenderingMode"))
            return m.GetFloat("_RenderingMode") < 0.5f;

        if (m.HasProperty("_Mode"))
        {
            var mode = (int)m.GetFloat("_Mode");
            return mode == 0 || mode == 1;
        }

        return m.renderQueue < (int)RenderQueue.AlphaTest;
    }

    private static bool IsMaterialDepthWriteEnabled(Material m) =>
        m != null && m.HasProperty("_ZWrite") && m.GetFloat("_ZWrite") >= 0.5f;

    private static bool TryEnableMaterialDepthWrite(Material m)
    {
        if (m == null || !m.HasProperty("_ZWrite"))
            return false;
        if (IsMaterialDepthWriteEnabled(m))
            return true;

        m.SetFloat("_ZWrite", 1f);
        if (IsMaterialDepthWriteEnabled(m))
        {
            EditorUtility.SetDirty(m);
            return true;
        }

        var so = new SerializedObject(m);
        so.Update();
        var sp = so.FindProperty("m_ZWrite") ?? so.FindProperty("_ZWrite");
        if (sp != null && (sp.propertyType == SerializedPropertyType.Boolean ||
                            sp.propertyType == SerializedPropertyType.Float ||
                            sp.propertyType == SerializedPropertyType.Integer))
        {
            if (sp.propertyType == SerializedPropertyType.Boolean)
                sp.boolValue = true;
            else
                sp.floatValue = 1f;
            so.ApplyModifiedProperties();
        }

        if (IsMaterialDepthWriteEnabled(m))
        {
            EditorUtility.SetDirty(m);
            return true;
        }

        return false;
    }

    /// <summary>与 GPU Instancing 扫描共用：跳过 Hidden / Particles / Skybox。</summary>
    private static bool ShaderExcludedFromMaterialPropertyScan(Shader shader) =>
        ShaderExcludedFromGpuInstancingScan(shader);

    private void AnalyzeModelMaterialSlots()
    {
        var multiMaterialModels = CollectModelsWithMultipleMaterials();
        var modelsCopy = new List<ModelMaterialUsage>(multiMaterialModels);
        _rows.Add(new DiagnosisRow
        {
            IsOk = multiMaterialModels.Count == 0,
            Severity = DiagnosisSeverity.Warning,
            AffectsSummary = true,
            ExcludeFromBatchFix = true,
            Title = "项目模型多材质提醒（Material ≥ 2）",
            Detail = multiMaterialModels.Count == 0
                ? "已检查 Assets 下模型：未发现单个模型使用 2 个及以上材质。"
                : $"共 {multiMaterialModels.Count} 个模型使用 2 个及以上材质。多材质通常会拆分 SubMesh / Draw Call；建议在 DCC 合并材质、烘焙贴图 Atlas，或确认该模型确实需要多材质。\n" +
                  string.Join("\n", multiMaterialModels.ConvertAll(m => $"{m.AssetPath}（材质 {m.UniqueMaterialCount}，槽位 {m.MaterialSlotCount}）")),
            FixButtonLabel = "定位第一个",
            Fix = multiMaterialModels.Count == 0
                ? null
                : () => PingModelAsset(modelsCopy[0].AssetPath)
        });
    }

    private static List<ModelMaterialUsage> CollectModelsWithMultipleMaterials()
    {
        var result = new List<ModelMaterialUsage>();
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (ShouldSkipAssetPathForProjectScan(path))
                continue;
            if (!(AssetImporter.GetAtPath(path) is ModelImporter))
                continue;

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
                continue;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var materialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var materialSlotCount = 0;
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;
                var mats = renderer.sharedMaterials;
                if (mats == null)
                    continue;
                materialSlotCount += mats.Length;
                foreach (var mat in mats)
                {
                    if (mat == null)
                        continue;
                    var matPath = AssetDatabase.GetAssetPath(mat);
                    if (IsPackageAssetPath(matPath))
                        continue;
                    materialPaths.Add(string.IsNullOrEmpty(matPath) ? mat.name : matPath);
                }
            }

            if (materialPaths.Count >= 2 || materialSlotCount >= 2)
            {
                result.Add(new ModelMaterialUsage
                {
                    AssetPath = path,
                    UniqueMaterialCount = materialPaths.Count,
                    MaterialSlotCount = materialSlotCount
                });
            }
        }

        result.Sort((a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void PingModelAsset(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return;
        var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (asset == null)
            return;
        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
    }

    private void AnalyzeOcclusionCulling()
    {
#if false
        const string occlusionNote = "说明：Occlusion Culling 仅检测各场景中 Camera 是否勾选 Occlusion Culling。";
#endif
        const string occlusionNote = "Occlusion Culling only checks whether each scene Camera has Occlusion Culling enabled.";
        var scenePaths = CollectScenePathsToScan();
        if (scenePaths.Count == 0)
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = false,
                AffectsSummary = true,
                Title = "未发现可扫描的场景",
                Detail = "Build Settings 中无场景时，将扫描 Assets 下所有 .unity。",
                Fix = null,
                CustomGui = () => EditorGUILayout.LabelField(occlusionNote, EditorStyles.wordWrappedMiniLabel)
            });
            return;
        }

        var camOff = new List<string>();
        foreach (var path in scenePaths)
        {
            if (!File.Exists(GetProjectPath(path)))
                continue;
            var text = File.ReadAllText(GetProjectPath(path));
            if (SceneYamlHasCameraOcclusionDisabled(text))
                camOff.Add(path);
        }

        var camOffCopy = new List<string>(camOff);
        _rows.Add(new DiagnosisRow
        {
            IsOk = camOff.Count == 0,
            AffectsSummary = true,
            Title = "摄像机 Occlusion Culling 勾选",
            Detail = camOff.Count == 0
                ? $"已检查 {scenePaths.Count} 个场景：未发现关闭 Occlusion Culling 的 Camera（或场景中无 Camera）。"
                : "以下场景中存在未勾选 Occlusion Culling 的 Camera（YAML 中 m_OcclusionCulling: 0）：" +
                  "\n" + string.Join("\n", camOff) +
                  "\n\n可使用「仅应用此项」或底部「应用全部自动修复」：将依次打开场景并把场景中所有 Camera 的 Occlusion Culling 勾选打开后保存。",
            Fix = camOff.Count == 0
                ? null
                : () => FixCamerasOcclusionCullingInScenes(camOffCopy),
            CustomGui = () => EditorGUILayout.LabelField(occlusionNote, EditorStyles.wordWrappedMiniLabel)
        });
    }

    private void AnalyzeCastShadows()
    {
        var scenePaths = CollectScenePathsToScan();
        if (scenePaths.Count == 0)
            return;

        var scenesWithCastShadow = new List<string>();
        foreach (var path in scenePaths)
        {
            if (!File.Exists(GetProjectPath(path)))
                continue;
            var text = File.ReadAllText(GetProjectPath(path));
            if (SceneYamlHasCastShadowsEnabled(text))
                scenesWithCastShadow.Add(path);
        }

        var allScenesCopy = new List<string>(scenePaths);
        var scenesWithCastShadowCopy = new List<string>(scenesWithCastShadow);
        _rows.Add(new DiagnosisRow
        {
            IsOk = scenesWithCastShadow.Count == 0,
            AffectsSummary = true,
            ExcludeFromBatchFix = true,
            Title = "场景内渲染器 Cast Shadow（投射阴影）",
            Detail = scenesWithCastShadow.Count == 0
                ? $"已检查 {scenePaths.Count} 个场景：未发现开启 Cast Shadow 的渲染器（YAML 粗检）。"
                : "以下场景中仍有渲染器开启 Cast Shadow（YAML 中 m_CastShadows / m_ShadowCastingMode 非 Off）：" +
                  "\n" + string.Join("\n", scenesWithCastShadow) +
                  "\n\n点击「选择处理方式」：若不需要阴影则关闭扫描范围内所有场景的渲染器 Cast Shadow；若选择维持开启则不修改场景。",
            FixButtonLabel = "选择处理方式",
            Fix = scenesWithCastShadow.Count == 0
                ? null
                : () => PromptAndFixCastShadowsInScenes(allScenesCopy, scenesWithCastShadowCopy)
        });
    }

    private void AnalyzeReceiveShadows()
    {
        var scenePaths = CollectScenePathsToScan();
        if (scenePaths.Count == 0)
            return;

        if (!TryCollectSceneMaterialPaths(scenePaths, out var materialPaths, out var materialsWithReceiveShadowsOn))
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = false,
                AffectsSummary = true,
                ExcludeFromBatchFix = true,
                Title = "场景材质 Receive Shadows（接收阴影）",
                Detail = EditorApplication.isPlaying
                    ? "请先退出 Play 模式后再分析场景内材质。"
                    : "无法打开场景以收集材质（请确认场景路径有效）。",
                Fix = null
            });
            return;
        }

        var scenePathsCopy = new List<string>(scenePaths);
        var materialsToFixCopy = new List<string>(materialsWithReceiveShadowsOn);
        _rows.Add(new DiagnosisRow
        {
            IsOk = materialsWithReceiveShadowsOn.Count == 0,
            AffectsSummary = true,
            ExcludeFromBatchFix = true,
            Title = "场景内物体所用材质 Receive Shadows（接收阴影）",
            Detail = materialsWithReceiveShadowsOn.Count == 0
                ? $"已检查 {scenePaths.Count} 个场景中 {materialPaths.Count} 个材质资源：Receive Shadows 均已关闭。"
                : $"共 {materialsWithReceiveShadowsOn.Count} 个材质仍开启 Receive Shadows（来自 {scenePaths.Count} 个场景内 Renderer 引用）：\n" +
                  string.Join("\n", materialsWithReceiveShadowsOn) +
                  "\n\n点击「选择处理方式」：将关闭材质上的 Receive Shadows（URP：_ReceiveShadows；TCP2 Hybrid：_ReceiveShadowsOff + 关键字）；维持开启则不修改。",
            FixButtonLabel = "选择处理方式",
            Fix = materialsWithReceiveShadowsOn.Count == 0
                ? null
                : () => PromptAndFixReceiveShadowsOnSceneMaterials(scenePathsCopy, materialsToFixCopy)
        });
    }

    private struct RuntimePerfSnapshot
    {
        public int SampleCount;
        public long AvgDrawCalls;
        public long AvgBatches;
        public long AvgSetPassCalls;
        public long AvgTriangles;
        public bool DrawValid;
        public bool BatchesValid;
        public bool SetPassValid;
        public bool TrianglesValid;
    }

    private void AnalyzeRuntimePerfCompare()
    {
        _rows.Add(new DiagnosisRow
        {
            IsOk = true,
            AffectsSummary = true,
            Title = "运行时性能：优化前 / 后对比",
            Detail =
                "须在 Play 模式下操作：依次「采样基线」→ 修改工程或场景 →「采样优化后」，下方表格显示均值差。约 1 秒内多次读取 Profiler Render 内置计数器；请保持视角、画质与负载尽量一致。若某项显示「不可用」，请打开 Window → Analysis → Profiler 并勾选 Rendering 相关模块后再试。",
            Fix = null,
            CustomGui = DrawRuntimePerfComparePanel
        });
    }

    private void DrawRuntimePerfComparePanel()
    {
        EnsurePerfRecorders();
        var anyCounter = _perfDrawCalls.Valid || _perfBatches.Valid || _perfSetPass.Valid || _perfTrianglesValid;
        if (!anyCounter)
        {
            EditorGUILayout.HelpBox(
                "未能连接到 Render 内置计数器（ProfilerRecorder）。请确认 Unity 版本，或打开 Profiler 窗口后再进入本页重试。",
                MessageType.Warning);
        }

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || _perfSamplingActive))
            {
                if (GUILayout.Button("采样基线（约 1 秒）", GUILayout.Height(26)))
                    StartRuntimePerfSampling(true);
            }

            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || _perfSamplingActive))
            {
                if (GUILayout.Button("采样优化后（约 1 秒）", GUILayout.Height(26)))
                    StartRuntimePerfSampling(false);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!_perfHasBaseline && !_perfHasAfter))
            {
                if (GUILayout.Button("清除全部采样", GUILayout.Width(120)))
                {
                    ClearRuntimePerfSnapshots();
                    ShowNotification(new GUIContent("已清除采样数据"));
                }
            }
        }

        if (_perfSamplingActive)
            EditorGUILayout.HelpBox("正在采样… 请保持 Play 运行约 1 秒，勿暂停编辑器。", MessageType.Info);

        EditorGUILayout.Space(6);
        DrawRuntimePerfComparisonTable();
    }

    private void DrawRuntimePerfComparisonTable()
    {
        if (!_perfHasBaseline && !_perfHasAfter)
        {
            EditorGUILayout.LabelField("尚无采样数据。", EditorStyles.miniLabel);
            return;
        }

        var colW = (position.width - 48f) / 4f;
        if (colW < 72f)
            colW = 72f;

        EditorGUILayout.LabelField("指标（均值）", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("项目", EditorStyles.miniBoldLabel, GUILayout.Width(colW));
        GUILayout.Label("基线", EditorStyles.miniBoldLabel, GUILayout.Width(colW));
        GUILayout.Label("优化后", EditorStyles.miniBoldLabel, GUILayout.Width(colW));
        GUILayout.Label("差值（后 − 前）", EditorStyles.miniBoldLabel, GUILayout.Width(colW));
        EditorGUILayout.EndHorizontal();

        DrawRuntimePerfMetricRow("Draw Calls", _perfHasBaseline, _perfHasAfter, _perfBaseline, _perfAfter,
            s => s.DrawValid, s => s.AvgDrawCalls, true, colW);
        DrawRuntimePerfMetricRow("Batches", _perfHasBaseline, _perfHasAfter, _perfBaseline, _perfAfter,
            s => s.BatchesValid, s => s.AvgBatches, true, colW);
        DrawRuntimePerfMetricRow("SetPass Calls", _perfHasBaseline, _perfHasAfter, _perfBaseline, _perfAfter,
            s => s.SetPassValid, s => s.AvgSetPassCalls, true, colW);
        DrawRuntimePerfMetricRow("Triangles", _perfHasBaseline, _perfHasAfter, _perfBaseline, _perfAfter,
            s => s.TrianglesValid, s => s.AvgTriangles, false, colW);
    }

    private delegate bool PerfValid(RuntimePerfSnapshot s);
    private delegate long PerfValue(RuntimePerfSnapshot s);

    private static void DrawRuntimePerfMetricRow(
        string label,
        bool hasA,
        bool hasB,
        RuntimePerfSnapshot a,
        RuntimePerfSnapshot b,
        PerfValid valid,
        PerfValue value,
        bool lowerDeltaIsImprovement,
        float colW)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(colW));
        GUILayout.Label(FormatPerfCell(hasA, a, valid, value), GUILayout.Width(colW));
        GUILayout.Label(FormatPerfCell(hasB, b, valid, value), GUILayout.Width(colW));

        var deltaText = "—";
        Color? deltaTint = null;
        if (hasA && hasB && valid(a) && valid(b))
        {
            var d = value(b) - value(a);
            deltaText = d > 0 ? $"+{d}" : d.ToString();
            if (lowerDeltaIsImprovement)
                deltaTint = d < 0 ? new Color(0.25f, 0.72f, 0.4f) : d > 0 ? new Color(0.9f, 0.35f, 0.3f) : (Color?)null;
        }

        var c = GUI.color;
        if (deltaTint.HasValue)
            GUI.color = deltaTint.Value;
        GUILayout.Label(deltaText, GUILayout.Width(colW));
        GUI.color = c;
        EditorGUILayout.EndHorizontal();
    }

    private static string FormatPerfCell(bool has, RuntimePerfSnapshot s, PerfValid valid, PerfValue value)
    {
        if (!has)
            return "—";
        if (!valid(s))
            return "不可用";
        return value(s).ToString();
    }

    private void ClearRuntimePerfSnapshots()
    {
        _perfHasBaseline = false;
        _perfHasAfter = false;
        _perfBaseline = default;
        _perfAfter = default;
    }

    private void EnsurePerfRecorders()
    {
        if (_perfRecordersCreated)
            return;
        _perfRecordersCreated = true;
        _perfDrawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _perfBatches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _perfSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");

        _perfTrianglesValid = false;
        foreach (var triName in new[] { "Triangles Count", "Visible Triangles Count" })
        {
            var r = ProfilerRecorder.StartNew(ProfilerCategory.Render, triName);
            if (!r.Valid)
            {
                r.Dispose();
                continue;
            }

            _perfTriangles = r;
            _perfTrianglesValid = true;
            break;
        }
    }

    private void DisposePerfRecorders()
    {
        if (!_perfRecordersCreated)
            return;
        _perfRecordersCreated = false;
        if (_perfDrawCalls.Valid)
            _perfDrawCalls.Dispose();
        if (_perfBatches.Valid)
            _perfBatches.Dispose();
        if (_perfSetPass.Valid)
            _perfSetPass.Dispose();
        if (_perfTrianglesValid && _perfTriangles.Valid)
            _perfTriangles.Dispose();
        _perfTrianglesValid = false;
    }

    private void StartRuntimePerfSampling(bool baseline)
    {
        EnsurePerfRecorders();
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("合批与剔除向导", "请先进入 Play 模式后再采样。", "确定");
            return;
        }

        if (!_perfDrawCalls.Valid && !_perfBatches.Valid && !_perfSetPass.Valid && !_perfTrianglesValid)
        {
            EditorUtility.DisplayDialog(
                "合批与剔除向导",
                "Render 计数器不可用。请尝试打开 Profiler 窗口（Window → Analysis → Profiler）后重试。",
                "确定");
            return;
        }

        StopPerfSamplingIfNeeded();
        _perfSamplingIsBaseline = baseline;
        _perfSampleDraws.Clear();
        _perfSampleBatches.Clear();
        _perfSampleSetPass.Clear();
        _perfSampleTriangles.Clear();
        _perfSamplingActive = true;
        _perfSamplingEndTime = EditorApplication.timeSinceStartup + RuntimePerfSampleSeconds;
        EditorApplication.update += PerfSamplingTick;
        RecomputeStepSummary();
        Repaint();
    }

    private void PerfSamplingTick()
    {
        if (!_perfSamplingActive)
        {
            EditorApplication.update -= PerfSamplingTick;
            return;
        }

        if (!EditorApplication.isPlaying)
        {
            StopPerfSamplingIfNeeded();
            EditorUtility.DisplayDialog("合批与剔除向导", "已退出 Play，采样已取消。", "确定");
            return;
        }

        AppendRuntimePerfSampleFrame();
        if (EditorApplication.timeSinceStartup >= _perfSamplingEndTime)
        {
            FinishRuntimePerfSampling();
            return;
        }

        RecomputeStepSummary();
        Repaint();
    }

    private void AppendRuntimePerfSampleFrame()
    {
        if (_perfDrawCalls.Valid)
            _perfSampleDraws.Add(_perfDrawCalls.LastValue);
        if (_perfBatches.Valid)
            _perfSampleBatches.Add(_perfBatches.LastValue);
        if (_perfSetPass.Valid)
            _perfSampleSetPass.Add(_perfSetPass.LastValue);
        if (_perfTrianglesValid && _perfTriangles.Valid)
            _perfSampleTriangles.Add(_perfTriangles.LastValue);
    }

    private void FinishRuntimePerfSampling()
    {
        EditorApplication.update -= PerfSamplingTick;
        _perfSamplingActive = false;

        var snap = BuildRuntimePerfSnapshotFromSamples();
        if (snap.SampleCount == 0)
        {
            EditorUtility.DisplayDialog("合批与剔除向导", "未采集到有效样本，请保持场景运行后重试。", "确定");
            RecomputeStepSummary();
            Repaint();
            return;
        }

        if (_perfSamplingIsBaseline)
        {
            _perfBaseline = snap;
            _perfHasBaseline = true;
            ShowNotification(new GUIContent($"已记录基线（{snap.SampleCount} 帧样本）"));
        }
        else
        {
            _perfAfter = snap;
            _perfHasAfter = true;
            ShowNotification(new GUIContent($"已记录优化后（{snap.SampleCount} 帧样本）"));
        }

        RecomputeStepSummary();
        Repaint();
    }

    private RuntimePerfSnapshot BuildRuntimePerfSnapshotFromSamples()
    {
        var n = Mathf.Max(
            Mathf.Max(_perfSampleDraws.Count, _perfSampleBatches.Count),
            Mathf.Max(_perfSampleSetPass.Count, _perfSampleTriangles.Count));

        long Avg(List<long> list)
        {
            if (list == null || list.Count == 0)
                return 0;
            long sum = 0;
            foreach (var v in list)
                sum += v;
            return sum / list.Count;
        }

        return new RuntimePerfSnapshot
        {
            SampleCount = n,
            DrawValid = _perfSampleDraws.Count > 0,
            BatchesValid = _perfSampleBatches.Count > 0,
            SetPassValid = _perfSampleSetPass.Count > 0,
            TrianglesValid = _perfSampleTriangles.Count > 0,
            AvgDrawCalls = Avg(_perfSampleDraws),
            AvgBatches = Avg(_perfSampleBatches),
            AvgSetPassCalls = Avg(_perfSampleSetPass),
            AvgTriangles = Avg(_perfSampleTriangles)
        };
    }

    private void StopPerfSamplingIfNeeded()
    {
        if (!_perfSamplingActive)
            return;
        EditorApplication.update -= PerfSamplingTick;
        _perfSamplingActive = false;
        RecomputeStepSummary();
    }

    /// <summary>询问后：选择关闭则写入场景；选择维持开启则不修改。</summary>
    private static void PromptAndFixCastShadowsInScenes(List<string> allScenePaths, List<string> scenesWithCastShadow)
    {
        if (allScenePaths == null || allScenePaths.Count == 0)
            return;
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("合批与剔除向导", "请先退出 Play 模式后再执行场景写入。", "确定");
            return;
        }

        var sceneHint = scenesWithCastShadow != null && scenesWithCastShadow.Count > 0
            ? $"（约 {scenesWithCastShadow.Count} 个场景检出开启 Cast Shadow）"
            : "";
        var choice = EditorUtility.DisplayDialogComplex(
            "合批与剔除向导",
            "扫描场景中发现仍有渲染器开启 Cast Shadow（投射阴影）。" + sceneHint +
            "\n\n· 不需要阴影：关闭本次扫描范围内所有场景中全部渲染器的 Cast Shadow\n· 维持开启：不修改任何场景",
            "不需要阴影（全部关闭）",
            "取消",
            "维持开启（不修改）");

        if (choice != 0)
            return;

        DisableCastShadowsInScenes(allScenePaths);
    }

    /// <summary>依次打开场景，将所有 Renderer 的 shadowCastingMode 设为 Off。</summary>
    private static void DisableCastShadowsInScenes(List<string> sceneAssetPaths)
    {
        if (sceneAssetPaths == null || sceneAssetPaths.Count == 0)
            return;
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("合批与剔除向导", "请先退出 Play 模式后再执行场景写入。", "确定");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var startPath = EditorSceneManager.GetActiveScene().path;
        var changedSceneCount = 0;
        try
        {
            foreach (var assetPath in sceneAssetPaths)
            {
                if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(GetProjectPath(assetPath)))
                    continue;

                var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Single);
                var roots = scene.GetRootGameObjects();
                var changed = false;
                foreach (var root in roots)
                {
                    var renderers = root.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null || renderers.Length == 0)
                        continue;
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null || renderer.shadowCastingMode == ShadowCastingMode.Off)
                            continue;
                        Undo.RecordObject(renderer, "Disable cast shadows");
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                        changed = true;
                    }
                }

                if (changed)
                {
                    EditorSceneManager.SaveScene(scene);
                    changedSceneCount++;
                }
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(startPath) && File.Exists(GetProjectPath(startPath)))
                EditorSceneManager.OpenScene(startPath, OpenSceneMode.Single);
        }

        EditorUtility.DisplayDialog(
            "合批与剔除向导",
            changedSceneCount > 0
                ? $"已在 {changedSceneCount} 个场景中将渲染器 Cast Shadow 设为 Off。"
                : "未发现需要修改的渲染器（可能已为 Off）。",
            "确定");
    }

    /// <summary>询问后：选择关闭则写入材质资源；选择维持开启则不修改。</summary>
    private static void PromptAndFixReceiveShadowsOnSceneMaterials(List<string> scenePaths, List<string> materialPathsToFix)
    {
        if (scenePaths == null || scenePaths.Count == 0)
            return;
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("合批与剔除向导", "请先退出 Play 模式后再修改材质。", "确定");
            return;
        }

        var matHint = materialPathsToFix != null && materialPathsToFix.Count > 0
            ? $"（约 {materialPathsToFix.Count} 个材质仍开启 Receive Shadows）"
            : "";
        var choice = EditorUtility.DisplayDialogComplex(
            "合批与剔除向导",
            "扫描场景内物体引用的材质中，仍有 Receive Shadows 处于开启状态。" + matHint +
            "\n\n· 不需要接收阴影：关闭这些材质上的 Receive Shadows（修改材质资源本身）\n· 维持开启：不修改任何材质",
            "不需要接收阴影（全部关闭）",
            "取消",
            "维持开启（不修改）");

        if (choice != 0)
            return;

        DisableReceiveShadowsOnSceneMaterials(scenePaths, materialPathsToFix);
    }

    /// <summary>关闭场景 Renderer 所引用材质上的 Receive Shadows（URP _ReceiveShadows + 关键字）。</summary>
    private static void DisableReceiveShadowsOnSceneMaterials(List<string> scenePaths, List<string> materialPathsToFix)
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("合批与剔除向导", "请先退出 Play 模式后再修改材质。", "确定");
            return;
        }

        var paths = materialPathsToFix != null && materialPathsToFix.Count > 0
            ? new List<string>(materialPathsToFix)
            : new List<string>();

        if (paths.Count == 0 && scenePaths != null && scenePaths.Count > 0)
        {
            if (!TryCollectSceneMaterialPaths(scenePaths, out _, out var onList))
            {
                EditorUtility.DisplayDialog("合批与剔除向导", "无法打开场景以收集材质。", "确定");
                return;
            }

            paths = onList;
        }

        if (paths.Count == 0)
        {
            EditorUtility.DisplayDialog("合批与剔除向导", "未发现需要修改的材质。", "确定");
            return;
        }

        var changed = 0;
        var failed = new List<string>();
        foreach (var path in paths)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
                continue;
            if (!IsMaterialReceiveShadowsEnabled(m))
                continue;
            if (TryDisableMaterialReceiveShadows(m))
                changed++;
            else
                failed.Add(path);
        }

        AssetDatabase.SaveAssets();

        var msg = changed > 0
            ? $"已关闭 {changed} 个材质上的 Receive Shadows。"
            : "未发现需要修改的材质（可能已关闭）。";
        if (failed.Count > 0)
            msg += $"\n\n以下 {failed.Count} 个材质未能写入（无 Receive Shadows 属性或不可写）：\n" + string.Join("\n", failed);
        EditorUtility.DisplayDialog("合批与剔除向导", msg, "确定");
    }

    private static bool TryCollectSceneMaterialPaths(
        List<string> scenePaths,
        out List<string> allMaterialPaths,
        out List<string> materialsWithReceiveShadowsOn)
    {
        allMaterialPaths = new List<string>();
        materialsWithReceiveShadowsOn = new List<string>();
        if (scenePaths == null || scenePaths.Count == 0)
            return true;
        if (EditorApplication.isPlaying)
            return false;

        var allSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return false;

        var startPath = EditorSceneManager.GetActiveScene().path;
        try
        {
            foreach (var scenePath in scenePaths)
            {
                if (string.IsNullOrEmpty(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(GetProjectPath(scenePath)))
                    continue;

                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                foreach (var root in scene.GetRootGameObjects())
                {
                    var renderers = root.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null)
                        continue;
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null)
                            continue;
                        var mats = renderer.sharedMaterials;
                        if (mats == null)
                            continue;
                        foreach (var mat in mats)
                        {
                            if (mat == null)
                                continue;
                            var assetPath = AssetDatabase.GetAssetPath(mat);
                            if (ShouldSkipAssetPathForProjectScan(assetPath))
                                continue;
                            if (!allSet.Add(assetPath))
                                continue;
                            if (IsMaterialReceiveShadowsEnabled(mat))
                                onSet.Add(assetPath);
                        }
                    }
                }
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(startPath) && File.Exists(GetProjectPath(startPath)))
                EditorSceneManager.OpenScene(startPath, OpenSceneMode.Single);
        }

        allMaterialPaths.AddRange(allSet);
        materialsWithReceiveShadowsOn.AddRange(onSet);
        return true;
    }

    private static bool MaterialHasReceiveShadowsProperty(Material m) =>
        m != null && (m.HasProperty("_ReceiveShadowsOff") || m.HasProperty("_ReceiveShadows"));

    /// <summary>URP Lit：_ReceiveShadows&gt;0；TCP2 Hybrid：[ToggleOff] _ReceiveShadowsOff==0 为开启（并兼容材质里残留的 _ReceiveShadows）。</summary>
    private static bool IsMaterialReceiveShadowsEnabled(Material m)
    {
        if (!MaterialHasReceiveShadowsProperty(m))
            return false;

        if (m.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF"))
            return false;

        if (m.HasProperty("_ReceiveShadowsOff") && m.GetFloat("_ReceiveShadowsOff") < 0.5f)
            return true;

        if (m.HasProperty("_ReceiveShadows") && m.GetFloat("_ReceiveShadows") > 0.5f)
            return true;

        return false;
    }

    private static bool TryDisableMaterialReceiveShadows(Material m)
    {
        if (!MaterialHasReceiveShadowsProperty(m))
            return false;
        if (!IsMaterialReceiveShadowsEnabled(m))
            return true;

        Undo.RecordObject(m, "Disable material receive shadows");

        if (m.HasProperty("_ReceiveShadowsOff"))
            m.SetFloat("_ReceiveShadowsOff", 1f);

        if (m.HasProperty("_ReceiveShadows"))
            m.SetFloat("_ReceiveShadows", 0f);

        m.EnableKeyword("_RECEIVE_SHADOWS_OFF");
        EditorUtility.SetDirty(m);
        return !IsMaterialReceiveShadowsEnabled(m);
    }

    /// <summary>
    /// 依次打开相关场景，将场景中所有 Camera 的 Occlusion Culling 勾选打开并保存；最后尝试恢复原先活动场景。
    /// </summary>
    private static void FixCamerasOcclusionCullingInScenes(List<string> sceneAssetPaths)
    {
        if (sceneAssetPaths == null || sceneAssetPaths.Count == 0)
            return;
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("合批与剔除向导", "请先退出 Play 模式后再执行场景写入。", "确定");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var startPath = EditorSceneManager.GetActiveScene().path;
        try
        {
            foreach (var assetPath in sceneAssetPaths)
            {
                if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(GetProjectPath(assetPath)))
                    continue;

                var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Single);
                var roots = scene.GetRootGameObjects();
                var changed = false;
                foreach (var root in roots)
                {
                    var cams = root.GetComponentsInChildren<Camera>(true);
                    if (cams == null || cams.Length == 0)
                        continue;
                    foreach (var cam in cams)
                    {
                        if (cam == null || cam.useOcclusionCulling)
                            continue;
                        Undo.RecordObject(cam, "Enable camera occlusion culling");
                        cam.useOcclusionCulling = true;
                        changed = true;
                    }
                }

                if (changed)
                    EditorSceneManager.SaveScene(scene);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(startPath) && File.Exists(GetProjectPath(startPath)))
                EditorSceneManager.OpenScene(startPath, OpenSceneMode.Single);
        }
    }

    private static List<string> CollectScenePathsToScan()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var activeScene = EditorSceneManager.GetActiveScene();
        if (activeScene.IsValid() && !ShouldSkipAssetPathForProjectScan(activeScene.path))
            set.Add(activeScene.path);

        foreach (var s in EditorBuildSettings.scenes)
        {
            if (s.enabled && !ShouldSkipAssetPathForProjectScan(s.path))
                set.Add(s.path);
        }

        if (set.Count > 0)
            return new List<string>(set);

        foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (!ShouldSkipAssetPathForProjectScan(p))
                set.Add(p);
        }

        return new List<string>(set);
    }

    private static string GetProjectPath(string assetPath) =>
        Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, assetPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>存在 Camera 且 m_OcclusionCulling: 0 时列出场景（粗检）。</summary>
    private static bool SceneYamlHasCameraOcclusionDisabled(string sceneYaml)
    {
        if (!sceneYaml.Contains("!u!20 &", StringComparison.Ordinal))
            return false;
        return sceneYaml.Contains("m_OcclusionCulling: 0", StringComparison.Ordinal);
    }

    /// <summary>YAML 中存在非 Off 的 Cast Shadow 设置（粗检）。</summary>
    private static bool SceneYamlHasCastShadowsEnabled(string sceneYaml)
    {
        if (string.IsNullOrEmpty(sceneYaml))
            return false;
        if (Regex.IsMatch(sceneYaml, @"m_CastShadows:\s*([1-9]\d*)", RegexOptions.Multiline))
            return true;
        if (Regex.IsMatch(sceneYaml, @"m_ShadowCastingMode:\s*([1-9]\d*)", RegexOptions.Multiline))
            return true;
        return false;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Scene Stats helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static long GetMeshTriCount(Mesh mesh)
    {
        if (mesh == null) return 0;
        long n = 0;
        for (var s = 0; s < mesh.subMeshCount; s++)
            n += (long)mesh.GetIndexCount(s) / 3;
        return n;
    }

    private struct SceneMeshInfo
    {
        public string GameObjectName;
        public string MeshName;
        public long TriCount;
    }

    private static List<SceneMeshInfo> CollectSceneMeshInfos()
    {
        var result = new List<SceneMeshInfo>();
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid()) return result;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (IsPackageAssetPath(AssetDatabase.GetAssetPath(mf.sharedMesh))) continue;
                result.Add(new SceneMeshInfo
                {
                    GameObjectName = mf.gameObject.name,
                    MeshName = mf.sharedMesh.name,
                    TriCount = GetMeshTriCount(mf.sharedMesh)
                });
            }

            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                if (IsPackageAssetPath(AssetDatabase.GetAssetPath(smr.sharedMesh))) continue;
                result.Add(new SceneMeshInfo
                {
                    GameObjectName = smr.gameObject.name,
                    MeshName = smr.sharedMesh.name,
                    TriCount = GetMeshTriCount(smr.sharedMesh)
                });
            }
        }

        return result;
    }

    /// <summary>씬 총 폴리곤 수: 80,000개 이상 노란불 / 100,000개 이상 빨간불.</summary>
    private void AnalyzeSceneTotalPolygons()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = false,
                AffectsSummary = true,
                Title = "씬 총 폴리곤 수 분석 불가",
                Detail = "유효한 씬이 열려 있지 않습니다.",
                Fix = null
            });
            return;
        }

        var meshInfos = CollectSceneMeshInfos();
        long totalTris = 0;
        foreach (var info in meshInfos) totalTris += info.TriCount;

        const long warnAt = 80_000;
        const long errorAt = 100_000;

        var isOk = totalTris < warnAt;
        var severity = totalTris >= errorAt ? DiagnosisSeverity.Error : DiagnosisSeverity.Warning;

        string detail;
        if (totalTris >= errorAt)
            detail = $"씬 총 폴리곤 수: {totalTris:N0}개 — 10만 개를 초과합니다. LOD 적용, 불필요한 메시 제거, 메시 최적화를 권장합니다.";
        else if (totalTris >= warnAt)
            detail = $"씬 총 폴리곤 수: {totalTris:N0}개 — 10만 개에 근접합니다. 최적화를 검토해 주세요.";
        else
            detail = $"씬 총 폴리곤 수: {totalTris:N0}개 (양호).";

        _rows.Add(new DiagnosisRow
        {
            IsOk = isOk,
            Severity = severity,
            AffectsSummary = true,
            Title = "씬 총 폴리곤 수",
            Detail = detail,
            Fix = null
        });
    }

    /// <summary>배칭 미적용 기준 예상 Draw Call: 80개 이상 노란불 / 100개 이상 빨간불.</summary>
    private void AnalyzeSceneEstimatedDrawCalls()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid()) return;

        var estimated = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (RendererReferencesPackageAsset(r)) continue;
                var mats = r.sharedMaterials;
                estimated += mats != null ? Mathf.Max(1, mats.Length) : 1;
            }
        }

        const int warnAt = 80;
        const int errorAt = 100;

        var isOk = estimated < warnAt;
        var severity = estimated >= errorAt ? DiagnosisSeverity.Error : DiagnosisSeverity.Warning;

        string detail;
        if (estimated >= errorAt)
            detail = $"예상 Draw Call: 약 {estimated}개 — 100개를 초과합니다 (배칭 미적용 기준 추산).\n" +
                     "SRP Batcher·GPU Instancing 활성화, 오브젝트 합치기(Static Batching), 머테리얼 통합 등을 검토하세요.";
        else if (estimated >= warnAt)
            detail = $"예상 Draw Call: 약 {estimated}개 — 100개에 근접합니다 (배칭 미적용 기준 추산).";
        else
            detail = $"예상 Draw Call: 약 {estimated}개 (배칭 미적용 기준 추산, 양호).";

        _rows.Add(new DiagnosisRow
        {
            IsOk = isOk,
            Severity = severity,
            AffectsSummary = true,
            Title = "씬 예상 Draw Call 수 (배칭 전 추산)",
            Detail = detail,
            Fix = null
        });
    }

    /// <summary>씬 내 고유 텍스처 수: 20개 이상이면 Texture Atlas 권장(노란불).</summary>
    private void AnalyzeSceneTextureCount()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid()) return;

        var texturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                foreach (var mat in mats)
                {
                    if (mat == null || mat.shader == null) continue;
                    if (IsPackageAssetPath(AssetDatabase.GetAssetPath(mat))) continue;
                    var propCount = ShaderUtil.GetPropertyCount(mat.shader);
                    for (var i = 0; i < propCount; i++)
                    {
                        if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                            continue;
                        var tex = mat.GetTexture(ShaderUtil.GetPropertyName(mat.shader, i));
                        if (tex == null) continue;
                        var p = AssetDatabase.GetAssetPath(tex);
                        if (!ShouldSkipAssetPathForProjectScan(p))
                            texturePaths.Add(p);
                    }
                }
            }
        }

        const int warnAt = 20;
        var count = texturePaths.Count;

        _rows.Add(new DiagnosisRow
        {
            IsOk = count < warnAt,
            Severity = DiagnosisSeverity.Warning,
            AffectsSummary = true,
            Title = "씬 사용 텍스처 수",
            Detail = count >= warnAt
                ? $"씬에서 고유 텍스처 {count}개가 사용 중입니다.\n" +
                  "Texture Atlas(스프라이트 아틀라스)로 여러 텍스처를 묶으면 Draw Call 및 메모리 대역폭을 줄일 수 있습니다.\n" +
                  "Unity 메뉴 → Window → 2D → Sprite Atlas 에서 아틀라스를 생성하세요."
                : $"씬 사용 고유 텍스처 수: {count}개 (양호).",
            Fix = null
        });
    }

    /// <summary>씬 내 메시 폴리곤 이상치(평균 + 2σ 초과)를 노란불로 경고.</summary>
    private void AnalyzeSceneOutlierMeshes()
    {
        var meshInfos = CollectSceneMeshInfos();
        if (meshInfos.Count < 2) return;

        double sum = 0;
        foreach (var info in meshInfos) sum += info.TriCount;
        var mean = sum / meshInfos.Count;

        double sumSq = 0;
        foreach (var info in meshInfos) sumSq += (info.TriCount - mean) * (info.TriCount - mean);
        var stddev = Math.Sqrt(sumSq / meshInfos.Count);

        var threshold = mean + 2.0 * stddev;
        if (threshold < 1000) threshold = 1000;

        var outliers = new List<SceneMeshInfo>();
        foreach (var info in meshInfos)
        {
            if (info.TriCount > threshold)
                outliers.Add(info);
        }

        outliers.Sort((a, b) => b.TriCount.CompareTo(a.TriCount));

        _rows.Add(new DiagnosisRow
        {
            IsOk = outliers.Count == 0,
            Severity = DiagnosisSeverity.Warning,
            AffectsSummary = true,
            Title = "씬 내 폴리곤 과다 메시 (이상치)",
            Detail = outliers.Count == 0
                ? $"씬 내 폴리곤 이상치(평균+2σ 초과)가 없습니다. (씬 평균: {mean:N0}개)"
                : $"씬 평균({mean:N0}개)보다 현저히 면수가 많은 메시 {outliers.Count}개 발견 (기준: {threshold:N0}개):\n" +
                  string.Join("\n", outliers.ConvertAll(o =>
                      $"  {o.GameObjectName} [{o.MeshName}]: {o.TriCount:N0}개")),
            Fix = null
        });
    }

    private enum DiagnosisSeverity
    {
        Error,
        Warning
    }

    private struct ModelMaterialUsage
    {
        public string AssetPath;
        public int UniqueMaterialCount;
        public int MaterialSlotCount;
    }

    private sealed class DiagnosisRow
    {
        /// <summary>为 true 时仅绘制分区标题，不参与检查项逻辑。</summary>
        public bool SectionHeader;
        /// <summary>为 false 时仅作说明，不参与顶部「全部满足」汇总。</summary>
        public bool AffectsSummary = true;
        /// <summary>为 true 时：不参与底部「一键全部」，但仍可点「仅应用此项」（如打开窗口）。</summary>
        public bool ExcludeFromBatchFix;
        /// <summary>未通过时的提示等级：Error 为红色，Warning 为黄色。</summary>
        public DiagnosisSeverity Severity;
        public bool IsOk;
        public string Title;
        public string Detail;
        /// <summary>非空时覆盖「仅应用此项」按钮文案（如定位、打开窗口）。</summary>
        public string FixButtonLabel;
        public Action Fix;
        /// <summary>在说明与「仅应用此项」之间绘制自定义内容（如折叠材质列表）。</summary>
        public Action CustomGui;
    }
}
