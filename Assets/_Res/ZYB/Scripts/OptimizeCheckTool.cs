using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 工程侧前提（SRP Batcher、Dynamic Batching、GPU Instancing、Depth Write、Occlusion、Cast/Receive Shadow）与 Play 下 Render 计数器采样对照。
/// </summary>
public class OptimizeCheckTool : EditorWindow
{
    private const string MenuPath = "Tools/优化检测工具";

    private int _step;
    private Vector2 _scroll;
    private List<DiagnosisRow> _rows = new List<DiagnosisRow>();
    private readonly Dictionary<string, bool> _listFoldouts = new Dictionary<string, bool>();
    private string _stepSummary = "";
    private bool _stepAllOk;
    private bool _urpMaterialMismatchFoldout = true;
    private bool _singleFixApplying;
    private string _singleFixTitle = "";
    private Action _queuedSingleFix;
    private string _queuedSingleFixTitle = "";
    private string _fixStatusMessage = "";
    private MessageType _fixStatusType = MessageType.Info;
    private static readonly Stack<List<Action>> s_toolUndoStack = new Stack<List<Action>>();
    private static List<Action> s_currentToolUndoGroup;
    private static bool s_replayingToolUndo;

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
        var w = GetWindow<OptimizeCheckTool>();
        w.titleContent = new GUIContent("优化检测工具");
        w.minSize = new Vector2(580, 460);
        w.AnalyzeCurrentStep();
    }

    private void OnEnable()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
        AnalyzeCurrentStep();
    }

    private void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        StopPerfSamplingIfNeeded();
        DisposePerfRecorders();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
            StopPerfSamplingIfNeeded();
    }

    private void OnUndoRedoPerformed()
    {
        AnalyzeCurrentStep();
    }

    private void OnGUI()
    {
        HandleUndoRedoShortcut();

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("优化检测工具", EditorStyles.largeLabel);
        EditorGUILayout.LabelField(
            "第一页按「待处理」与「无需处理」分组显示工程检查项；第二页为 Play 下 Render 计数器采样对比。合批与瓶颈请以 Profiler / Frame Debugger 为准。",
            EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(8);

        DrawStepTabs();
        EditorGUILayout.Space(10);

        if (GUILayout.Button("重新分析", GUILayout.Width(88)))
            AnalyzeCurrentStep();

        using (new EditorGUI.DisabledScope(s_toolUndoStack.Count == 0 || _singleFixApplying))
        {
            if (GUILayout.Button("撤销上一次自动修复", GUILayout.Width(136)))
                TryPerformToolUndo();
        }

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
        DrawFixStatus();

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
        using (new EditorGUI.DisabledScope(_singleFixApplying || !HasAnyFixableRow()))
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
            using (new EditorGUI.DisabledScope(_singleFixApplying))
            {
                if (GUILayout.Button(fixLabel, GUILayout.Width(Mathf.Max(100, GUI.skin.button.CalcSize(new GUIContent(fixLabel)).x + 16))))
                {
                    QueueSingleFix(row);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(4);
    }

    private void DrawFixStatus()
    {
        if (string.IsNullOrEmpty(_fixStatusMessage))
            return;

        EditorGUILayout.HelpBox(_fixStatusMessage, _fixStatusType);
    }

    private void HandleUndoRedoShortcut()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
            return;

        var actionKey = e.control || e.command;
        if (!actionKey)
            return;

        if (e.keyCode == KeyCode.Z && !e.shift)
        {
            if (!TryPerformToolUndo())
                Undo.PerformUndo();
            e.Use();
        }
        else if (e.keyCode == KeyCode.Y || (e.keyCode == KeyCode.Z && e.shift))
        {
            Undo.PerformRedo();
            e.Use();
        }
    }

    private void QueueSingleFix(DiagnosisRow row)
    {
        if (_singleFixApplying || _queuedSingleFix != null || row == null || row.Fix == null)
            return;

        _queuedSingleFix = row.Fix;
        _queuedSingleFixTitle = row.Title ?? "当前项目";

        var win = this;
        EditorApplication.delayCall += () =>
        {
            if (win != null)
                win.StartQueuedSingleFix();
        };
    }

    private void StartQueuedSingleFix()
    {
        if (_singleFixApplying || _queuedSingleFix == null)
            return;

        _singleFixApplying = true;
        _singleFixTitle = _queuedSingleFixTitle;
        _fixStatusType = MessageType.Info;
        _fixStatusMessage = $"正在应用「{_singleFixTitle}」…";
        ShowNotification(new GUIContent(_fixStatusMessage));
        Repaint();

        var win = this;
        EditorApplication.delayCall += () =>
        {
            if (win != null)
                win.ApplyQueuedSingleFix();
        };
    }

    private void ApplyQueuedSingleFix()
    {
        var fix = _queuedSingleFix;
        _queuedSingleFix = null;
        _queuedSingleFixTitle = "";

        if (fix == null)
        {
            _singleFixApplying = false;
            _singleFixTitle = "";
            Repaint();
            return;
        }

        var undoGroup = BeginToolUndoGroup(_singleFixTitle);
        try
        {
            fix.Invoke();
            AnalyzeCurrentStep();

            _fixStatusType = MessageType.Info;
            _fixStatusMessage = $"已应用「{_singleFixTitle}」，检查结果已刷新。";
            ShowNotification(new GUIContent(_fixStatusMessage));
        }
        catch (Exception ex)
        {
            _fixStatusType = MessageType.Warning;
            _fixStatusMessage = $"应用「{_singleFixTitle}」失败：{ex.Message}";
            Debug.LogWarning($"[合批向导] {_singleFixTitle}: {ex.Message}");
            ShowNotification(new GUIContent(_fixStatusMessage));
        }
        finally
        {
            EndToolUndoGroup(undoGroup);
            _singleFixApplying = false;
            _singleFixTitle = "";
            Repaint();
        }
    }

    private static Color ResolveRowColor(DiagnosisRow row, Color okColor, Color warnColor, Color badColor)
    {
        if (row.IsOk)
            return okColor;
        return row.Severity == DiagnosisSeverity.Warning ? warnColor : badColor;
    }

    private bool TryPerformToolUndo()
    {
        if (s_toolUndoStack.Count == 0)
            return false;

        var actions = s_toolUndoStack.Pop();
        s_replayingToolUndo = true;
        try
        {
            for (var i = actions.Count - 1; i >= 0; i--)
                actions[i]?.Invoke();
        }
        finally
        {
            s_replayingToolUndo = false;
        }

        AnalyzeCurrentStep();
        _fixStatusType = MessageType.Info;
        _fixStatusMessage = "已撤销上一次自动修复，检查结果已刷新。";
        ShowNotification(new GUIContent(_fixStatusMessage));
        Repaint();
        return true;
    }

    /// <summary>工程检查页按待处理/无需处理分组；运行时对比页保留完整操作面板。</summary>
    private List<DiagnosisRow> GetDisplayRows()
    {
        if (_step == 1)
            return _rows;

        var result = new List<DiagnosisRow>();
        var pending = new List<DiagnosisRow>();
        var ok = new List<DiagnosisRow>();
        foreach (var row in _rows)
        {
            if (row.SectionHeader)
                continue;

            if (!row.AffectsSummary)
                continue;

            if (row.IsOk)
                ok.Add(row);
            else
                pending.Add(row);
        }

        result.Add(new DiagnosisRow
        {
            SectionHeader = true,
            Title = pending.Count > 0 ? $"待处理（{pending.Count}）" : "待处理（0）",
            IsOk = true,
            AffectsSummary = false
        });
        result.AddRange(pending);

        result.Add(new DiagnosisRow
        {
            SectionHeader = true,
            Title = ok.Count > 0 ? $"无需处理 / 已通过（{ok.Count}）" : "无需处理 / 已通过（0）",
            IsOk = true,
            AffectsSummary = false
        });
        result.AddRange(ok);

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

    private static int BeginToolUndoGroup(string actionName)
    {
        Undo.IncrementCurrentGroup();
        var group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(string.IsNullOrEmpty(actionName) ? "优化检测工具修复" : actionName);
        s_currentToolUndoGroup = new List<Action>();
        return group;
    }

    private static void EndToolUndoGroup(int group)
    {
        Undo.FlushUndoRecordObjects();
        Undo.CollapseUndoOperations(group);
        if (s_currentToolUndoGroup != null && s_currentToolUndoGroup.Count > 0)
            s_toolUndoStack.Push(s_currentToolUndoGroup);
        s_currentToolUndoGroup = null;
    }

    private static void RegisterToolUndo(Action undoAction)
    {
        if (s_replayingToolUndo || undoAction == null)
            return;

        if (s_currentToolUndoGroup != null)
        {
            s_currentToolUndoGroup.Add(undoAction);
            return;
        }

        s_toolUndoStack.Push(new List<Action> { undoAction });
    }

    private void ApplyAllFixesInCurrentStep()
    {
        var n = 0;
        var undoGroup = BeginToolUndoGroup("应用全部自动修复");
        try
        {
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
        }
        finally
        {
            EndToolUndoGroup(undoGroup);
        }

        EditorUtility.DisplayDialog("优化检测工具", n > 0 ? $"已执行 {n} 项自动修复。" : "没有可执行的自动修复项。", "确定");
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
        var renderPipelineKind = GetRenderPipelineKind();
        if (renderPipelineKind == RenderPipelineKind.Universal)
        {
            AddWizardSectionHeader("URP Asset");
            AnalyzeUrpMsaa();
            AnalyzeUrpRenderScale();
            AnalyzeUrpSoftShadows();
            AddWizardSectionHeader("SRP Batcher");
            AnalyzeSrpBatcher();
            AddWizardSectionHeader("Dynamic Batching");
            AnalyzeDynamicBatching();
        }

        AddWizardSectionHeader("GPU Instancing");
        AnalyzeGpuInstancing();
        if (renderPipelineKind == RenderPipelineKind.Universal)
        {
            AddWizardSectionHeader("Depth Write");
            AnalyzeDepthWrite();
        }

        AddWizardSectionHeader("模型材质");
        AnalyzeModelMaterialSlots();
        AddWizardSectionHeader("Occlusion Culling");
        AnalyzeOcclusionCulling();
        AddWizardSectionHeader("Cast Shadow");
        AnalyzeCastShadows();
        if (renderPipelineKind == RenderPipelineKind.Universal)
        {
            AddWizardSectionHeader("Receive Shadows");
            AnalyzeReceiveShadows();
        }

        AddWizardSectionHeader("Scene Stats (Active Scene)");
        AnalyzeSceneTotalPolygons();
        AnalyzeSceneBatches();
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

    private enum RenderPipelineKind
    {
        BuiltIn,
        Universal,
        OtherSrp
    }

    private static RenderPipelineAsset TryGetActiveRenderPipelineAsset()
    {
        var currentProp = typeof(GraphicsSettings).GetProperty(
            "currentRenderPipeline",
            BindingFlags.Public | BindingFlags.Static);
        if (currentProp != null && currentProp.GetValue(null, null) is RenderPipelineAsset current)
            return current;

        if (QualitySettings.renderPipeline != null)
            return QualitySettings.renderPipeline;

        return GraphicsSettings.defaultRenderPipeline;
    }

    private static RenderPipelineKind GetRenderPipelineKind()
    {
        var asset = TryGetActiveRenderPipelineAsset();
        return GetRenderPipelineKind(asset);
    }

    private static RenderPipelineAsset TryGetUrpAsset()
    {
        var asset = TryGetActiveRenderPipelineAsset();
        return GetRenderPipelineKind(asset) == RenderPipelineKind.Universal ? asset : null;
    }

    private static RenderPipelineKind GetRenderPipelineKind(RenderPipelineAsset asset)
    {
        if (asset == null)
            return RenderPipelineKind.BuiltIn;

        var typeName = asset.GetType().FullName ?? asset.GetType().Name;
        return typeName.Contains("UniversalRenderPipelineAsset", StringComparison.Ordinal)
            ? RenderPipelineKind.Universal
            : RenderPipelineKind.OtherSrp;
    }

    private static bool TryGetBoolMember(UnityEngine.Object target, string propertyName, string serializedName, out bool value)
    {
        value = false;
        if (target == null)
            return false;

        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null && prop.PropertyType == typeof(bool))
        {
            value = (bool)prop.GetValue(target, null);
            return true;
        }

        var so = new SerializedObject(target);
        var sp = so.FindProperty(serializedName);
        if (sp != null && sp.propertyType == SerializedPropertyType.Boolean)
        {
            value = sp.boolValue;
            return true;
        }

        return false;
    }

    private static bool TryGetIntMember(UnityEngine.Object target, string propertyName, string serializedName, out int value)
    {
        value = 0;
        if (target == null)
            return false;

        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null && prop.PropertyType == typeof(int))
        {
            value = (int)prop.GetValue(target, null);
            return true;
        }

        var so = new SerializedObject(target);
        var sp = so.FindProperty(serializedName);
        if (sp != null && sp.propertyType == SerializedPropertyType.Integer)
        {
            value = sp.intValue;
            return true;
        }

        var field = target.GetType().GetField(serializedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType == typeof(int))
        {
            value = (int)field.GetValue(target);
            return true;
        }

        return false;
    }

    private static bool TryGetFloatMember(UnityEngine.Object target, string propertyName, string serializedName, out float value)
    {
        value = 0f;
        if (target == null)
            return false;

        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null && prop.PropertyType == typeof(float))
        {
            value = (float)prop.GetValue(target, null);
            return true;
        }

        var so = new SerializedObject(target);
        var sp = so.FindProperty(serializedName);
        if (sp != null && sp.propertyType == SerializedPropertyType.Float)
        {
            value = sp.floatValue;
            return true;
        }

        var field = target.GetType().GetField(serializedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType == typeof(float))
        {
            value = (float)field.GetValue(target);
            return true;
        }

        return false;
    }

    private static bool TryGetUnityStatsInt(string memberName, out int value)
    {
        value = 0;

        var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.UnityStats");
        if (type == null)
            return false;

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var prop = type.GetProperty(memberName, flags);
        if (prop != null && TryConvertToInt(prop.GetValue(null, null), out value))
            return true;

        var field = type.GetField(memberName, flags);
        return field != null && TryConvertToInt(field.GetValue(null), out value);
    }

    private static bool TryConvertToInt(object raw, out int value)
    {
        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l when l <= int.MaxValue && l >= int.MinValue:
                value = (int)l;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool TrySetBoolMember(UnityEngine.Object target, string propertyName, string serializedName, bool value)
    {
        if (target == null)
            return false;

        Undo.RecordObject(target, $"Set {propertyName}");

        var so = new SerializedObject(target);
        so.Update();
        var sp = so.FindProperty(serializedName);
        if (sp != null && sp.propertyType == SerializedPropertyType.Boolean)
        {
            var oldValue = sp.boolValue;
            RegisterToolUndo(() => TrySetBoolMember(target, propertyName, serializedName, oldValue));
            sp.boolValue = value;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            return true;
        }

        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
        {
            var oldValue = (bool)prop.GetValue(target, null);
            RegisterToolUndo(() => TrySetBoolMember(target, propertyName, serializedName, oldValue));
            prop.SetValue(target, value, null);
            EditorUtility.SetDirty(target);
            return true;
        }

        var field = target.GetType().GetField(serializedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType == typeof(bool))
        {
            var oldValue = (bool)field.GetValue(target);
            RegisterToolUndo(() => TrySetBoolMember(target, propertyName, serializedName, oldValue));
            field.SetValue(target, value);
            EditorUtility.SetDirty(target);
            return true;
        }

        return false;
    }

    private static bool TrySetIntMember(UnityEngine.Object target, string propertyName, string serializedName, int value)
    {
        if (target == null)
            return false;

        Undo.RecordObject(target, $"Set {propertyName}");

        var so = new SerializedObject(target);
        so.Update();
        var sp = so.FindProperty(serializedName);
        if (sp != null && sp.propertyType == SerializedPropertyType.Integer)
        {
            var oldValue = sp.intValue;
            RegisterToolUndo(() => TrySetIntMember(target, propertyName, serializedName, oldValue));
            sp.intValue = value;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            return true;
        }

        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null && prop.PropertyType == typeof(int) && prop.CanWrite)
        {
            var oldValue = (int)prop.GetValue(target, null);
            RegisterToolUndo(() => TrySetIntMember(target, propertyName, serializedName, oldValue));
            prop.SetValue(target, value, null);
            EditorUtility.SetDirty(target);
            return true;
        }

        var field = target.GetType().GetField(serializedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType == typeof(int))
        {
            var oldValue = (int)field.GetValue(target);
            RegisterToolUndo(() => TrySetIntMember(target, propertyName, serializedName, oldValue));
            field.SetValue(target, value);
            EditorUtility.SetDirty(target);
            return true;
        }

        return false;
    }

    private static bool TrySetFloatMember(UnityEngine.Object target, string propertyName, string serializedName, float value)
    {
        if (target == null)
            return false;

        Undo.RecordObject(target, $"Set {propertyName}");

        var so = new SerializedObject(target);
        so.Update();
        var sp = so.FindProperty(serializedName);
        if (sp != null && sp.propertyType == SerializedPropertyType.Float)
        {
            var oldValue = sp.floatValue;
            RegisterToolUndo(() => TrySetFloatMember(target, propertyName, serializedName, oldValue));
            sp.floatValue = value;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            return true;
        }

        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null && prop.PropertyType == typeof(float) && prop.CanWrite)
        {
            var oldValue = (float)prop.GetValue(target, null);
            RegisterToolUndo(() => TrySetFloatMember(target, propertyName, serializedName, oldValue));
            prop.SetValue(target, value, null);
            EditorUtility.SetDirty(target);
            return true;
        }

        var field = target.GetType().GetField(serializedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType == typeof(float))
        {
            var oldValue = (float)field.GetValue(target);
            RegisterToolUndo(() => TrySetFloatMember(target, propertyName, serializedName, oldValue));
            field.SetValue(target, value);
            EditorUtility.SetDirty(target);
            return true;
        }

        return false;
    }

    private static bool ShouldSkipAssetPathForProjectScan(string assetPath) =>
        string.IsNullOrEmpty(assetPath) || IsPackageAssetPath(assetPath);

    private static bool ShouldSkipMaterialForCheck(Material material)
    {
        if (material == null)
            return true;

        var assetPath = AssetDatabase.GetAssetPath(material);
        if (ShouldSkipAssetPathForProjectScan(assetPath))
            return true;

        return IsEmbeddedNonMaterialAssetPath(assetPath);
    }

    private static bool IsEmbeddedNonMaterialAssetPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return false;

        var importer = AssetImporter.GetAtPath(assetPath);
        if (!(importer is ModelImporter) && !IsFontAssetPath(assetPath))
            return false;

        var extension = Path.GetExtension(assetPath);
        return !string.IsNullOrEmpty(extension) &&
               !string.Equals(extension, ".mat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFontAssetPath(string assetPath)
    {
        var extension = Path.GetExtension(assetPath);
        return string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".ttc", StringComparison.OrdinalIgnoreCase);
    }

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

    private enum SrpBatcherRecommendation
    {
        RuntimeCompare,
        Enable,
        Disable,
        FixShadersFirst
    }

    private struct SrpBatcherSceneStats
    {
        public bool HasValidScene;
        public int RendererCount;
        public int PackageRendererCount;
        public int MaterialSlotCount;
        public int UniqueMaterialCount;
        public int RepeatedMaterialSlotCount;
        public int InstancingMaterialSlotCount;
        public int RepeatedInstancingMaterialSlotCount;
        public int NonSrpMaterialSlotCount;
    }

    private void AnalyzeSrpBatcher()
    {
        var urp = TryGetUrpAsset();
        if (urp == null)
            return;

        var path = AssetDatabase.GetAssetPath(urp);
        _rows.Add(new DiagnosisRow
        {
            IsOk = true,
            AffectsSummary = false,
            Title = "已指定 URP 为当前渲染管线",
            Detail = string.IsNullOrEmpty(path) ? "资源可能来自内置/只读，无法显示工程路径。" : $"资源：{path}",
            Fix = null
        });

        if (!TryGetBoolMember(urp, "useSRPBatcher", "m_UseSRPBatcher", out var useSrpBatcher))
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = true,
                AffectsSummary = false,
            Title = "URP Asset 中 SRP Batcher 开关",
            Detail = "当前 Unity/URP 版本未暴露 useSRPBatcher 字段，已跳过该项。",
                Fix = null
            });
            return;
        }

        var legacyPaths = CollectMaterialsUsingNonSrpShaders();
        var srpStats = CollectSrpBatcherSceneStats();
        var srpRecommendation = GetSrpBatcherRecommendation(srpStats, legacyPaths.Count);
        var recommendedState = srpRecommendation == SrpBatcherRecommendation.Enable
            ? (bool?)true
            : srpRecommendation == SrpBatcherRecommendation.Disable
                ? (bool?)false
                : null;
        var srpStateMatchesRecommendation = !recommendedState.HasValue || useSrpBatcher == recommendedState.Value;
        var srpDetail = BuildSrpBatcherRecommendationDetail(useSrpBatcher, srpRecommendation, srpStats, legacyPaths.Count);

        _rows.Add(new DiagnosisRow
        {
            IsOk = srpStateMatchesRecommendation,
            Severity = DiagnosisSeverity.Warning,
            AffectsSummary = true,
            Title = "SRP Batcher 开关建议（按当前场景启发式）",
            Detail = srpDetail,
            FixButtonLabel = recommendedState.HasValue
                ? recommendedState.Value ? "按建议开启 SRP Batcher" : "按建议关闭 SRP Batcher"
                : null,
            ExcludeFromBatchFix = true,
            Fix = recommendedState.HasValue && !srpStateMatchesRecommendation
                ? () =>
                {
                    var p = TryGetUrpAsset();
                    if (p == null)
                        return;
                    TrySetBoolMember(p, "useSRPBatcher", "m_UseSRPBatcher", recommendedState.Value);
                }
                : null
        });

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

    private static SrpBatcherSceneStats CollectSrpBatcherSceneStats()
    {
        var stats = new SrpBatcherSceneStats();
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid())
            return stats;

        stats.HasValidScene = true;
        var materialUseCounts = new Dictionary<Material, int>();
        foreach (var root in scene.GetRootGameObjects())
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers == null)
                continue;

            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;
                if (RendererReferencesPackageAsset(renderer))
                {
                    stats.PackageRendererCount++;
                    continue;
                }

                stats.RendererCount++;
                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0)
                    continue;

                foreach (var mat in mats)
                {
                    if (ShouldSkipMaterialForCheck(mat) || mat.shader == null)
                        continue;

                    stats.MaterialSlotCount++;
                    if (mat.enableInstancing)
                        stats.InstancingMaterialSlotCount++;
                    if (IsLikelyNonSrpShader(mat.shader))
                        stats.NonSrpMaterialSlotCount++;

                    materialUseCounts.TryGetValue(mat, out var count);
                    materialUseCounts[mat] = count + 1;
                }
            }
        }

        stats.UniqueMaterialCount = materialUseCounts.Count;
        foreach (var pair in materialUseCounts)
        {
            if (pair.Value <= 1)
                continue;

            stats.RepeatedMaterialSlotCount += pair.Value;
            if (pair.Key != null && pair.Key.enableInstancing)
                stats.RepeatedInstancingMaterialSlotCount += pair.Value;
        }

        return stats;
    }

    private static SrpBatcherRecommendation GetSrpBatcherRecommendation(SrpBatcherSceneStats stats, int projectNonSrpMaterialCount)
    {
        if (!stats.HasValidScene || stats.MaterialSlotCount == 0)
            return projectNonSrpMaterialCount > 0
                ? SrpBatcherRecommendation.FixShadersFirst
                : SrpBatcherRecommendation.RuntimeCompare;

        var nonSrpRatio = Ratio(stats.NonSrpMaterialSlotCount, stats.MaterialSlotCount);
        if (nonSrpRatio >= 0.35f)
            return SrpBatcherRecommendation.FixShadersFirst;

        var repeatedRatio = Ratio(stats.RepeatedMaterialSlotCount, stats.MaterialSlotCount);
        var repeatedInstancingRatio = Ratio(stats.RepeatedInstancingMaterialSlotCount, stats.MaterialSlotCount);
        var instancingAmongRepeatedRatio = Ratio(stats.RepeatedInstancingMaterialSlotCount, stats.RepeatedMaterialSlotCount);

        if (stats.RepeatedInstancingMaterialSlotCount >= 8 &&
            (repeatedInstancingRatio >= 0.4f || instancingAmongRepeatedRatio >= 0.6f))
            return SrpBatcherRecommendation.Disable;

        if (stats.MaterialSlotCount >= 12 && repeatedRatio >= 0.3f)
            return SrpBatcherRecommendation.Enable;

        if (stats.MaterialSlotCount >= 30 && stats.UniqueMaterialCount <= Mathf.CeilToInt(stats.MaterialSlotCount * 0.75f))
            return SrpBatcherRecommendation.Enable;

        return SrpBatcherRecommendation.RuntimeCompare;
    }

    private static string BuildSrpBatcherRecommendationDetail(
        bool useSrpBatcher,
        SrpBatcherRecommendation recommendation,
        SrpBatcherSceneStats stats,
        int projectNonSrpMaterialCount)
    {
        var current = useSrpBatcher ? "已开启" : "已关闭";
        string recommendationText;
        string reason;
        switch (recommendation)
        {
            case SrpBatcherRecommendation.Enable:
                recommendationText = "建议开启 SRP Batcher。";
                reason = "当前场景里重复材质槽占比较高，且重复 GPU Instancing 材质占比不高，SRP Batcher 更可能降低 CPU 端材质状态切换开销。";
                break;
            case SrpBatcherRecommendation.Disable:
                recommendationText = "建议关闭 SRP Batcher，或至少先关闭后做一次运行时对比。";
                reason = "当前场景里重复且已开启 GPU Instancing 的材质占比较高；SRP Batcher 可能会优先于 GPU Instancing，反而让这类对象的合批效果变差。";
                break;
            case SrpBatcherRecommendation.FixShadersFirst:
                recommendationText = "不建议只切换开关；建议先处理疑似非 URP/SRP 兼容 Shader，再做运行时对比。";
                reason = "疑似不适配 URP/SRP Batcher 的材质占比较高，单纯开启 SRP Batcher 很可能看不到预期收益。";
                break;
            default:
                recommendationText = "当前证据不足，不建议自动切换；建议在「运行时对比」页分别采样开启/关闭后的 Batches、SetPass、Draw Calls。";
                reason = "当前场景没有明显的重复材质优势，也没有明显的 GPU Instancing 主导特征。";
                break;
        }

        var detail = $"当前 useSRPBatcher：{current}。{recommendationText}\n{reason}";
        if (stats.HasValidScene)
        {
            detail +=
                $"\n场景采样：Renderer {stats.RendererCount} 个，材质槽 {stats.MaterialSlotCount} 个，唯一材质 {stats.UniqueMaterialCount} 个，" +
                $"重复材质槽 {stats.RepeatedMaterialSlotCount} 个（{FormatRatio(stats.RepeatedMaterialSlotCount, stats.MaterialSlotCount)}），" +
                $"重复且开启 GPU Instancing 的材质槽 {stats.RepeatedInstancingMaterialSlotCount} 个（{FormatRatio(stats.RepeatedInstancingMaterialSlotCount, stats.MaterialSlotCount)}），" +
                $"疑似非 URP/SRP Shader 槽 {stats.NonSrpMaterialSlotCount} 个（{FormatRatio(stats.NonSrpMaterialSlotCount, stats.MaterialSlotCount)}）。";
            if (stats.PackageRendererCount > 0)
                detail += $" 已跳过包资源 Renderer {stats.PackageRendererCount} 个。";
        }
        else
        {
            detail += "\n当前没有可用场景采样数据。";
        }

        if (projectNonSrpMaterialCount > 0)
            detail += $"\nAssets 中另有 {projectNonSrpMaterialCount} 个材质疑似使用非 URP/SRP Shader，见下方一致性检查。";

        return detail;
    }

    private static float Ratio(int value, int total)
    {
        return total > 0 ? (float)value / total : 0f;
    }

    private static string FormatRatio(int value, int total)
    {
        return total > 0 ? $"{(value * 100f / total):0.#}%" : "0%";
    }

    private void AnalyzeUrpMsaa()
    {
        var urp = TryGetUrpAsset();
        if (urp == null)
            return;

        if (!TryGetIntMember(urp, "msaaSampleCount", "m_MSAA", out var msaa))
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = true,
                AffectsSummary = false,
                Title = "URP Asset 中 MSAA 设置",
                Detail = "当前 Unity/URP 版本未暴露 MSAA 字段，已跳过该项。",
                Fix = null
            });
            return;
        }

        var isOk = msaa <= 2;
        _rows.Add(new DiagnosisRow
        {
            IsOk = isOk,
            AffectsSummary = true,
            Severity = DiagnosisSeverity.Error,
            Title = "URP Asset 中 MSAA 设置",
            Detail = isOk
                ? $"当前 MSAA：{msaa}x。"
                : $"当前 MSAA：{msaa}x。MSAA 高于 2x 会增加 GPU 开销；如无特殊画质需求，建议设为 2x。",
            FixButtonLabel = "设为 2x",
            Fix = isOk
                ? null
                : () =>
                {
                    var p = TryGetUrpAsset();
                    if (p == null)
                        return;
                    TrySetIntMember(p, "msaaSampleCount", "m_MSAA", 2);
            }
        });
    }

    private void AnalyzeUrpRenderScale()
    {
        var urp = TryGetUrpAsset();
        if (urp == null)
            return;

        if (!TryGetFloatMember(urp, "renderScale", "m_RenderScale", out var renderScale))
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = true,
                AffectsSummary = false,
                Title = "URP Asset 中 Render Scale 设置",
                Detail = "当前 Unity/URP 版本未暴露 Render Scale 字段，已跳过该项。",
                Fix = null
            });
            return;
        }

        var isOk = renderScale <= 1f;
        _rows.Add(new DiagnosisRow
        {
            IsOk = isOk,
            AffectsSummary = true,
            Severity = DiagnosisSeverity.Error,
            Title = "URP Asset 中 Render Scale 设置",
            Detail = isOk
                ? $"当前 Render Scale：{renderScale:0.##}。"
                : $"当前 Render Scale：{renderScale:0.##}。Render Scale 高于 1 会增加 GPU 渲染开销；建议设为 1。",
            FixButtonLabel = "设为 1",
            Fix = isOk
                ? null
                : () =>
                {
                    var p = TryGetUrpAsset();
                    if (p == null)
                        return;
                    TrySetFloatMember(p, "renderScale", "m_RenderScale", 1f);
            }
        });
    }

    private void AnalyzeUrpSoftShadows()
    {
        var urp = TryGetUrpAsset();
        if (urp == null)
            return;

        if (!TryGetBoolMember(urp, "supportsSoftShadows", "m_SoftShadowsSupported", out var softShadows))
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = true,
                AffectsSummary = false,
                Title = "URP Asset 中 Soft Shadows 设置",
                Detail = "当前 Unity/URP 版本未暴露 Soft Shadows 字段，已跳过该项。",
                Fix = null
            });
            return;
        }

        _rows.Add(new DiagnosisRow
        {
            IsOk = !softShadows,
            AffectsSummary = true,
            Severity = DiagnosisSeverity.Error,
            Title = "URP Asset 中 Soft Shadows 设置",
            Detail = !softShadows
                ? "Soft Shadows 已关闭。"
                : "Soft Shadows 已开启，会增加阴影采样与 GPU 开销；建议关闭。",
            FixButtonLabel = "关闭 Soft Shadows",
            Fix = !softShadows
                ? null
                : () =>
                {
                    var p = TryGetUrpAsset();
                    if (p == null)
                        return;
                    TrySetBoolMember(p, "supportsSoftShadows", "m_SoftShadowsSupported", false);
                }
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

    private void DrawFoldoutList(string key, string label, List<string> items)
    {
        DrawFoldoutList(key, label, items, null);
    }

    private void DrawFoldoutList(string key, string label, List<string> items, Action<string> drawItem)
    {
        if (items == null || items.Count == 0)
            return;

        if (items.Count == 1)
        {
            DrawListItem(items[0], drawItem);
            return;
        }

        if (!_listFoldouts.TryGetValue(key, out var expanded))
            expanded = false;

        expanded = EditorGUILayout.Foldout(expanded, $"{label}（{items.Count}）", true);
        _listFoldouts[key] = expanded;
        if (!expanded)
            return;

        EditorGUI.indentLevel++;
        foreach (var item in items)
            DrawListItem(item, drawItem);
        EditorGUI.indentLevel--;
    }

    private static void DrawListItem(string item, Action<string> drawItem)
    {
        if (drawItem != null)
            drawItem(item);
        else
            EditorGUILayout.LabelField(item, EditorStyles.wordWrappedMiniLabel);
    }

    private static void DrawAssetPathLocateRow(string assetPath)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(assetPath, EditorStyles.wordWrappedMiniLabel);
        if (GUILayout.Button("定位", GUILayout.Width(44)))
            PingModelAsset(assetPath);
        EditorGUILayout.EndHorizontal();
    }

    private static void DrawPlainListItem(string text)
    {
        EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);
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

    private static void DrawReceiveShadowMaterialRow(string materialKey)
    {
        var mat = ResolveReceiveShadowMaterialKey(materialKey);
        EditorGUILayout.BeginHorizontal();
        if (mat != null)
        {
            EditorGUILayout.ObjectField(mat, typeof(Material), false);
            if (GUILayout.Button("定位", GUILayout.Width(44)))
            {
                Selection.activeObject = mat;
                EditorGUIUtility.PingObject(mat);
            }
        }
        else
        {
            EditorGUILayout.LabelField(materialKey, EditorStyles.wordWrappedMiniLabel);
        }

        EditorGUILayout.EndHorizontal();
    }

    /// <summary>
    /// 启发式：非 URP 常见命名且为 Built-in 侧 Standard / Legacy / Particles 等路径的材质资源路径。
    /// </summary>
    private static bool IsLikelyNonSrpShader(Shader shader)
    {
        if (shader == null)
            return false;

        var sn = shader.name;
        if (sn.StartsWith("Hidden/", StringComparison.Ordinal))
            return false;

        var urpish = sn.Contains("Universal Render Pipeline", StringComparison.Ordinal) ||
                     sn.Contains("URP/", StringComparison.Ordinal) ||
                     sn.Contains("Shader Graphs", StringComparison.Ordinal) ||
                     sn.StartsWith("UI/", StringComparison.Ordinal) ||
                     sn.Contains("TextMeshPro", StringComparison.Ordinal) ||
                     sn.StartsWith("Sprites/", StringComparison.Ordinal);

        return !urpish && (sn.StartsWith("Standard", StringComparison.Ordinal) ||
                           sn.StartsWith("Legacy Shaders/", StringComparison.Ordinal) ||
                           sn.StartsWith("Particles/", StringComparison.Ordinal));
    }

    private static List<string> CollectMaterialsUsingNonSrpShaders()
    {
        var list = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (ShouldSkipAssetPathForProjectScan(p))
                continue;
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (ShouldSkipMaterialForCheck(m) || m.shader == null)
                continue;
            if (IsLikelyNonSrpShader(m.shader))
                list.Add(p);
        }

        return list;
    }

    private void AnalyzeDynamicBatching()
    {
        var urp = TryGetUrpAsset();
        if (urp == null)
            return;

        if (!TryGetBoolMember(urp, "supportsDynamicBatching", "m_SupportsDynamicBatching", out var supportsDynamicBatching))
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = true,
                AffectsSummary = false,
                Title = "URP Asset 中 Dynamic Batching 开关",
                Detail = "当前 Unity/URP 版本未暴露 supportsDynamicBatching 字段，已跳过该项。",
                Fix = null
            });
            return;
        }

        _rows.Add(new DiagnosisRow
        {
            IsOk = supportsDynamicBatching,
            AffectsSummary = true,
            Title = "URP Asset 中 Dynamic Batching 开关",
            Detail = supportsDynamicBatching
                ? "supportsDynamicBatching 已开启。"
                : "未勾选。若希望启用动态合批，可打开此项（小网格、同材质等条件下才可能生效）。",
            Fix = supportsDynamicBatching
                ? null
                : () =>
                {
                    var p = TryGetUrpAsset();
                    if (p == null)
                        return;
                    TrySetBoolMember(p, "supportsDynamicBatching", "m_SupportsDynamicBatching", true);
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
            if (ShouldSkipMaterialForCheck(m) || m.shader == null || m.enableInstancing)
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
            Title = toFix.Count == 0
                ? "已勾选所有材质里的 GPU Instancing（Assets）"
                : "材质 GPU Instancing 未勾选（Assets）",
            Detail = toFix.Count == 0
                ? "勾选所有材质里的 GPU Instancing（或均在排除列表内：Hidden / Particles / Skybox）。"
                : $"共 {toFix.Count} 个材质未勾选 Enable GPU Instancing。点击修复将批量勾选；如需回退，可点击「撤销上一次自动修复」。",
            FixButtonLabel = "勾选 GPU Instancing",
            Fix = toFix.Count == 0
                ? null
                : () =>
                {
                    var failed = new List<string>();
                    foreach (var path in toFixCopy)
                    {
                        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (!TryEnableMaterialGpuInstancing(m))
                            failed.Add(path);
                    }

                    if (failed.Count > 0)
                        Debug.LogWarning("[优化检测工具] 部分材质 GPU Instancing 自动修复失败：\n" + string.Join("\n", failed));
                },
            CustomGui = toFix.Count == 0
                ? null
                : () => DrawFoldoutList("gpu_instancing_materials", "未勾选 GPU Instancing 的材质", toFixCopy, DrawAssetPathLocateRow)
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

    private static bool TryEnableMaterialGpuInstancing(Material m)
    {
        if (ShouldSkipMaterialForCheck(m) || m.shader == null)
            return false;
        if (m.enableInstancing)
            return true;
        if (ShaderExcludedFromGpuInstancingScan(m.shader))
            return false;

        Undo.RecordObject(m, "Enable material GPU Instancing");
        var oldEnableInstancing = m.enableInstancing;
        RegisterToolUndo(() =>
        {
            if (m == null)
                return;
            m.enableInstancing = oldEnableInstancing;
            EditorUtility.SetDirty(m);
        });

        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        return m.enableInstancing;
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
            if (ShouldSkipMaterialForCheck(m) || m.shader == null)
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
                        if (ShouldSkipMaterialForCheck(m) || m.shader == null)
                            continue;
                        if (!IsNonTransparentMaterial(m))
                            continue;
                        if (!TryEnableMaterialDepthWrite(m))
                            failed.Add(path);
                    }
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

        Undo.RecordObject(m, "Enable material depth write");
        var oldZWrite = m.GetFloat("_ZWrite");
        RegisterToolUndo(() =>
        {
            if (m == null || !m.HasProperty("_ZWrite"))
                return;
            m.SetFloat("_ZWrite", oldZWrite);
            EditorUtility.SetDirty(m);
        });
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
            Title = "项目模型多材质提醒（材质 ≥ 2）",
            Detail = multiMaterialModels.Count == 0
                ? "已检查资源目录下模型：未发现单个模型使用 2 个及以上材质。"
                : $"共 {multiMaterialModels.Count} 个模型使用 2 个及以上材质。多材质通常会拆分 SubMesh / Draw Call；建议在 DCC 合并材质、烘焙贴图 Atlas，或确认该模型确实需要多材质。",
            FixButtonLabel = "定位第一个",
            Fix = multiMaterialModels.Count == 0
                ? null
                : () => PingModelAsset(modelsCopy[0].AssetPath),
            CustomGui = multiMaterialModels.Count == 0
                ? null
                : () => DrawModelMaterialUsageFoldout("model_multi_materials", "多材质模型", modelsCopy)
        });
    }

    private void DrawModelMaterialUsageFoldout(string key, string label, List<ModelMaterialUsage> models)
    {
        if (models == null || models.Count == 0)
            return;

        if (models.Count == 1)
        {
            DrawModelMaterialUsageRow(models[0]);
            return;
        }

        if (!_listFoldouts.TryGetValue(key, out var expanded))
            expanded = false;

        expanded = EditorGUILayout.Foldout(expanded, $"{label}（{models.Count}）", true);
        _listFoldouts[key] = expanded;
        if (!expanded)
            return;

        EditorGUI.indentLevel++;
        foreach (var model in models)
            DrawModelMaterialUsageRow(model);
        EditorGUI.indentLevel--;
    }

    private static void DrawModelMaterialUsageRow(ModelMaterialUsage model)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(
            $"{model.AssetPath}（材质 {model.UniqueMaterialCount}，槽位 {model.MaterialSlotCount}）",
            EditorStyles.wordWrappedMiniLabel);
        if (GUILayout.Button("定位", GUILayout.Width(44)))
            PingModelAsset(model.AssetPath);
        EditorGUILayout.EndHorizontal();
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
                    if (ShouldSkipMaterialForCheck(mat))
                        continue;
                    var matPath = AssetDatabase.GetAssetPath(mat);
                    materialPaths.Add(string.IsNullOrEmpty(matPath) ? mat.name : matPath);
                }
            }

            if (materialPaths.Count >= 2)
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
        const string occlusionNote = "Occlusion Culling 仅检查每个场景中的 Camera 是否已启用 Occlusion Culling。";
        var scenePaths = CollectScenePathsToScan();
        if (scenePaths.Count == 0)
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = false,
                AffectsSummary = true,
                Title = "未发现可扫描的场景",
                Detail = "构建设置中无场景时，将扫描资源目录下所有 .unity 场景。",
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
                : $"共 {camOff.Count} 个场景中存在未勾选 Occlusion Culling 的 Camera（YAML 中 m_OcclusionCulling: 0）。可使用「仅应用此项」或底部「应用全部自动修复」：将依次打开场景并把场景中所有 Camera 的 Occlusion Culling 勾选打开后保存。",
            Fix = camOff.Count == 0
                ? null
                : () => FixCamerasOcclusionCullingInScenes(camOffCopy),
            CustomGui = () =>
            {
                EditorGUILayout.LabelField(occlusionNote, EditorStyles.wordWrappedMiniLabel);
                DrawFoldoutList("occlusion_camera_off_scenes", "未开启 Occlusion Culling 的场景", camOffCopy, DrawPlainListItem);
            }
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
                : $"共 {scenesWithCastShadow.Count} 个场景中仍有渲染器开启 Cast Shadow（YAML 中 m_CastShadows / m_ShadowCastingMode 非 Off）。点击「选择处理方式」：若不需要阴影则关闭扫描范围内所有场景的渲染器 Cast Shadow；若选择维持开启则不修改场景。",
            FixButtonLabel = "选择处理方式",
            Fix = scenesWithCastShadow.Count == 0
                ? null
                : () => PromptAndFixCastShadowsInScenes(allScenesCopy, scenesWithCastShadowCopy),
            CustomGui = scenesWithCastShadow.Count == 0
                ? null
                : () => DrawFoldoutList("cast_shadow_scenes", "开启 Cast Shadow 的场景", scenesWithCastShadowCopy, DrawPlainListItem)
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
        var receiveShadowsOk = materialsWithReceiveShadowsOn.Count == 0;
        _rows.Add(new DiagnosisRow
        {
            IsOk = receiveShadowsOk,
            AffectsSummary = true,
            ExcludeFromBatchFix = true,
            Title = "场景材质 Receive Shadows（接收阴影）",
            Detail = receiveShadowsOk
                ? $"已检查 {scenePaths.Count} 个场景、{materialPaths.Count} 个材质：材质 Receive Shadows 均已关闭。"
                : $"仍有 {materialsWithReceiveShadowsOn.Count} 个材质开启 Receive Shadows。点击「选择处理方式」：将关闭这些材质上的 Receive Shadows；维持开启则不修改。",
            FixButtonLabel = "选择处理方式",
            Fix = receiveShadowsOk
                ? null
                : () => PromptAndFixReceiveShadows(scenePathsCopy, materialsToFixCopy),
            CustomGui = receiveShadowsOk
                ? null
                : () =>
                {
                    DrawFoldoutList("receive_shadow_materials", "开启 Receive Shadows 的材质", materialsToFixCopy, DrawReceiveShadowMaterialRow);
                }
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
        DrawRuntimePerfMetricRow("批次数", _perfHasBaseline, _perfHasAfter, _perfBaseline, _perfAfter,
            s => s.BatchesValid, s => s.AvgBatches, true, colW);
        DrawRuntimePerfMetricRow("SetPass 调用", _perfHasBaseline, _perfHasAfter, _perfBaseline, _perfAfter,
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
            EditorUtility.DisplayDialog("优化检测工具", "请先进入 Play 模式后再采样。", "确定");
            return;
        }

        if (!_perfDrawCalls.Valid && !_perfBatches.Valid && !_perfSetPass.Valid && !_perfTrianglesValid)
        {
            EditorUtility.DisplayDialog(
                "优化检测工具",
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
            EditorUtility.DisplayDialog("优化检测工具", "已退出 Play，采样已取消。", "确定");
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
            EditorUtility.DisplayDialog("优化检测工具", "未采集到有效样本，请保持场景运行后重试。", "确定");
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
            EditorUtility.DisplayDialog("优化检测工具", "请先退出 Play 模式后再执行场景写入。", "确定");
            return;
        }

        var sceneHint = scenesWithCastShadow != null && scenesWithCastShadow.Count > 0
            ? $"（约 {scenesWithCastShadow.Count} 个场景检出开启 Cast Shadow）"
            : "";
        var choice = EditorUtility.DisplayDialogComplex(
            "优化检测工具",
            "扫描场景中发现仍有渲染器开启 Cast Shadow（投射阴影）。" + sceneHint +
            "\n\n· 不需要阴影：关闭本次扫描范围内所有场景中全部渲染器的 Cast Shadow\n· 维持开启：不修改任何场景",
            "不需要阴影（全部关闭）",
            "取消",
            "维持开启（不修改）");

        if (choice != 0)
            return;

        DisableCastShadowsInScenes(allScenePaths);
    }

    /// <summary>依次打开场景，将所有 MeshRenderer 的 shadowCastingMode 设为 Off 并保存场景。</summary>
    private static void DisableCastShadowsInScenes(List<string> sceneAssetPaths)
    {
        if (sceneAssetPaths == null || sceneAssetPaths.Count == 0)
            return;
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("优化检测工具", "请先退出 Play 模式后再执行场景写入。", "确定");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var startPath = EditorSceneManager.GetActiveScene().path;
        var changedSceneCount = 0;
        var changedRendererCount = 0;
        var changedScenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                    if (renderers == null || renderers.Length == 0)
                        continue;
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null || renderer.shadowCastingMode == ShadowCastingMode.Off)
                            continue;
                        Undo.RecordObject(renderer, "Disable cast shadows");
                        var oldMode = renderer.shadowCastingMode;
                        RegisterToolUndo(() =>
                        {
                            if (renderer == null)
                                return;
                            renderer.shadowCastingMode = oldMode;
                            EditorUtility.SetDirty(renderer);
                        });
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                        EditorUtility.SetDirty(renderer);
                        changed = true;
                        changedRendererCount++;
                    }
                }

                if (changed)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    if (EditorSceneManager.SaveScene(scene))
                    {
                        changedSceneCount++;
                        changedScenePaths.Add(assetPath);
                    }
                }
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(startPath) && File.Exists(GetProjectPath(startPath)))
                EditorSceneManager.OpenScene(startPath, OpenSceneMode.Single);
        }

        foreach (var assetPath in sceneAssetPaths)
        {
            if (changedScenePaths.Contains(assetPath))
                continue;
            if (TryDisableCastShadowsInSceneYaml(assetPath, out var yamlChangedCount))
            {
                changedSceneCount++;
                changedRendererCount += yamlChangedCount;
            }
        }

        EditorUtility.DisplayDialog(
            "优化检测工具",
            changedSceneCount > 0
                ? $"已在 {changedSceneCount} 个场景中将 {changedRendererCount} 个 MeshRenderer 的 Cast Shadows 设为 Off。"
                : "未发现需要修改的 MeshRenderer（可能已为 Off）。",
            "确定");
    }

    private static bool TryDisableCastShadowsInSceneYaml(string sceneAssetPath, out int changedCount)
    {
        changedCount = 0;
        if (string.IsNullOrEmpty(sceneAssetPath) || !sceneAssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            return false;

        var projectPath = GetProjectPath(sceneAssetPath);
        if (!File.Exists(projectPath))
            return false;

        var count = 0;
        var original = File.ReadAllText(projectPath);
        var updated = Regex.Replace(
            original,
            @"--- !u!23 &[\s\S]*?(?=\n--- !u!|\z)",
            blockMatch =>
            {
                var block = blockMatch.Value;
                return Regex.Replace(
                    block,
                    @"m_CastShadows:\s*([1-9]\d*)",
                    match =>
                    {
                        count++;
                        return "m_CastShadows: 0";
                    },
                    RegexOptions.Multiline);
            },
            RegexOptions.Multiline);

        changedCount = count;
        if (changedCount == 0 || string.Equals(original, updated, StringComparison.Ordinal))
            return false;

        File.WriteAllText(projectPath, updated);
        AssetDatabase.ImportAsset(sceneAssetPath);
        return true;
    }

    /// <summary>询问后：选择关闭则只写入材质资源；选择维持开启则不修改。</summary>
    private static void PromptAndFixReceiveShadows(
        List<string> scenePaths,
        List<string> materialPathsToFix)
    {
        if (scenePaths == null || scenePaths.Count == 0)
            return;
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("优化检测工具", "请先退出 Play 模式后再修改 Receive Shadows。", "确定");
            return;
        }

        var matHint = materialPathsToFix != null && materialPathsToFix.Count > 0
            ? $"材质 {materialPathsToFix.Count} 个"
            : "";
        var hint = "";
        if (!string.IsNullOrEmpty(matHint))
            hint = $"（{matHint}）";
        var choice = EditorUtility.DisplayDialogComplex(
            "优化检测工具",
            "扫描场景内物体引用的材质时发现仍有材质 Receive Shadows 处于开启状态。" + hint +
            "\n\n· 不需要接收阴影：关闭材质上的 Receive Shadows\n· 维持开启：不修改任何材质",
            "不需要接收阴影（关闭材质）",
            "取消",
            "维持开启（不修改材质）");

        if (choice != 0)
            return;

        var materialChanged = DisableReceiveShadowsOnSceneMaterials(scenePaths, materialPathsToFix, false);

        EditorUtility.DisplayDialog(
            "优化检测工具",
            materialChanged > 0
                ? $"已关闭 {materialChanged} 个材质上的 Receive Shadows。"
                : "未发现需要修改的材质 Receive Shadows（可能已关闭）。",
            "确定");
    }

    /// <summary>询问后：选择关闭则写入材质资源；选择维持开启则不修改。</summary>
    private static void PromptAndFixReceiveShadowsOnSceneMaterials(List<string> scenePaths, List<string> materialPathsToFix)
    {
        PromptAndFixReceiveShadows(scenePaths, materialPathsToFix);
    }

    /// <summary>关闭场景 Renderer 所引用材质上的 Receive Shadows（不修改 Renderer.receiveShadows）。</summary>
    private static int DisableReceiveShadowsOnSceneMaterials(
        List<string> scenePaths,
        List<string> materialPathsToFix,
        bool showDialog = true)
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("优化检测工具", "请先退出 Play 模式后再修改材质。", "确定");
            return 0;
        }

        if (scenePaths == null || scenePaths.Count == 0)
        {
            if (showDialog)
                EditorUtility.DisplayDialog("优化检测工具", "未发现需要修改的材质。", "确定");
            return 0;
        }

        var changed = 0;
        var failed = new List<string>();
        var handled = new HashSet<int>();

        if (materialPathsToFix != null)
        {
            foreach (var materialKey in materialPathsToFix)
            {
                var m = ResolveReceiveShadowMaterialKey(materialKey);
                if (ShouldSkipMaterialForReceiveShadowsCheck(m))
                    continue;

                if (!IsMaterialReceiveShadowsEnabled(m))
                {
                    handled.Add(m.GetInstanceID());
                    continue;
                }

                if (TryDisableMaterialReceiveShadows(m))
                {
                    handled.Add(m.GetInstanceID());
                    changed++;
                }
                else
                {
                    failed.Add(materialKey);
                }
            }
        }

        DisableReceiveShadowsOnSceneMaterialAssetsFromYaml(scenePaths, handled, failed, ref changed);

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return 0;

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
                            if (ShouldSkipMaterialForReceiveShadowsCheck(mat))
                                continue;
                            if (handled.Contains(mat.GetInstanceID()))
                                continue;
                            if (!IsMaterialReceiveShadowsEnabled(mat))
                            {
                                handled.Add(mat.GetInstanceID());
                                continue;
                            }

                            var key = MakeReceiveShadowMaterialKey(mat);
                            if (TryDisableMaterialReceiveShadows(mat))
                            {
                                handled.Add(mat.GetInstanceID());
                                changed++;
                                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mat)))
                                    EditorSceneManager.MarkSceneDirty(scene);
                            }
                            else
                            {
                                failed.Add(key);
                            }
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

        var msg = changed > 0
            ? $"已关闭 {changed} 个材质上的 Receive Shadows。"
            : "未发现需要修改的材质（可能已关闭）。";
        if (failed.Count > 0)
            msg += $"\n\n以下 {failed.Count} 个材质未能写入（无 Receive Shadows 属性或不可写）：\n" + string.Join("\n", failed);
        if (changed > 0)
            AssetDatabase.SaveAssets();
        if (showDialog)
            EditorUtility.DisplayDialog("优化检测工具", msg, "确定");
        return changed;
    }

    private static void DisableReceiveShadowsOnSceneMaterialAssetsFromYaml(
        List<string> scenePaths,
        HashSet<int> handled,
        List<string> failed,
        ref int changed)
    {
        if (scenePaths == null)
            return;
        if (handled == null)
            handled = new HashSet<int>();

        foreach (var scenePath in scenePaths)
        {
            if (string.IsNullOrEmpty(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                continue;
            var projectPath = GetProjectPath(scenePath);
            if (!File.Exists(projectPath))
                continue;

            var sceneYaml = File.ReadAllText(projectPath);
            foreach (Match rendererMatch in Regex.Matches(sceneYaml, @"--- !u!(?:23|137) &[\s\S]*?(?=\n--- !u!|\z)"))
            {
                var block = rendererMatch.Value;
                foreach (Match matMatch in Regex.Matches(block, @"guid:\s*([0-9a-fA-F]{32}),\s*type:\s*2"))
                {
                    var materialPath = AssetDatabase.GUIDToAssetPath(matMatch.Groups[1].Value);
                    if (string.IsNullOrEmpty(materialPath) ||
                        IsPackageAssetPath(materialPath) ||
                        IsEmbeddedNonMaterialAssetPath(materialPath))
                        continue;

                    var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if (ShouldSkipMaterialForReceiveShadowsCheck(mat))
                        continue;
                    if (handled.Contains(mat.GetInstanceID()))
                        continue;
                    if (!MaterialHasReceiveShadowsProperty(mat))
                    {
                        handled.Add(mat.GetInstanceID());
                        continue;
                    }

                    if (TryDisableMaterialReceiveShadows(mat))
                    {
                        handled.Add(mat.GetInstanceID());
                        changed++;
                    }
                    else
                    {
                        failed?.Add(materialPath);
                    }
                }
            }
        }
    }

    private static void DisableReceiveShadowsOnSceneMaterials(List<string> scenePaths, List<string> materialPathsToFix)
    {
        DisableReceiveShadowsOnSceneMaterials(scenePaths, materialPathsToFix, true);
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
                            if (ShouldSkipMaterialForReceiveShadowsCheck(mat))
                                continue;
                            var materialKey = MakeReceiveShadowMaterialKey(mat);
                            if (string.IsNullOrEmpty(materialKey))
                                continue;
                            allSet.Add(materialKey);
                            if (IsMaterialReceiveShadowsEnabled(mat))
                                onSet.Add(materialKey);
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

        AddSceneMaterialPathsFromYaml(scenePaths, allSet, onSet);
        allMaterialPaths.AddRange(allSet);
        materialsWithReceiveShadowsOn.AddRange(onSet);
        return true;
    }

    private static void AddSceneMaterialPathsFromYaml(
        List<string> scenePaths,
        HashSet<string> allSet,
        HashSet<string> receiveShadowsOnSet)
    {
        if (scenePaths == null || allSet == null || receiveShadowsOnSet == null)
            return;

        foreach (var scenePath in scenePaths)
        {
            if (string.IsNullOrEmpty(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                continue;
            var projectPath = GetProjectPath(scenePath);
            if (!File.Exists(projectPath))
                continue;

            var sceneYaml = File.ReadAllText(projectPath);
            foreach (Match rendererMatch in Regex.Matches(sceneYaml, @"--- !u!(?:23|137) &[\s\S]*?(?=\n--- !u!|\z)"))
            {
                var block = rendererMatch.Value;
                foreach (Match matMatch in Regex.Matches(block, @"guid:\s*([0-9a-fA-F]{32}),\s*type:\s*2"))
                {
                    var guid = matMatch.Groups[1].Value;
                    var materialPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(materialPath))
                        materialPath = $"missing-material-guid:{guid}";
                    if (IsPackageAssetPath(materialPath) || IsEmbeddedNonMaterialAssetPath(materialPath))
                        continue;

                    allSet.Add(materialPath);
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if (IsMaterialReceiveShadowsEnabled(mat))
                        receiveShadowsOnSet.Add(materialPath);
                }
            }
        }
    }

    private static bool MaterialHasReceiveShadowsProperty(Material m) =>
        m != null && (m.HasProperty("_ReceiveShadowsOff") || m.HasProperty("_ReceiveShadows"));

    private static bool ShouldSkipMaterialForReceiveShadowsCheck(Material material)
    {
        if (material == null)
            return true;

        var assetPath = AssetDatabase.GetAssetPath(material);
        if (IsPackageAssetPath(assetPath))
            return true;

        return IsEmbeddedNonMaterialAssetPath(assetPath);
    }

    private static string MakeReceiveShadowMaterialKey(Material material)
    {
        if (material == null)
            return "";

        var assetPath = AssetDatabase.GetAssetPath(material);
        return string.IsNullOrEmpty(assetPath)
            ? $"scene-material:{material.GetInstanceID()}:{material.name}"
            : assetPath;
    }

    private static Material ResolveReceiveShadowMaterialKey(string materialKey)
    {
        if (string.IsNullOrEmpty(materialKey))
            return null;

        if (materialKey.StartsWith("scene-material:", StringComparison.Ordinal))
        {
            var parts = materialKey.Split(':');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var instanceId))
                return EditorUtility.InstanceIDToObject(instanceId) as Material;
            return null;
        }

        return AssetDatabase.LoadAssetAtPath<Material>(materialKey);
    }

    /// <summary>URP Lit：_ReceiveShadows&gt;0 为开启；TCP2 Hybrid：[ToggleOff] _ReceiveShadowsOff==1 为开启。</summary>
    private static bool IsMaterialReceiveShadowsEnabled(Material m)
    {
        if (!MaterialHasReceiveShadowsProperty(m))
            return false;

        if (m.HasProperty("_ReceiveShadowsOff"))
            return m.GetFloat("_ReceiveShadowsOff") > 0.5f;

        if (m.HasProperty("_ReceiveShadows") && m.GetFloat("_ReceiveShadows") > 0.5f)
            return true;

        return false;
    }

    private static bool TryDisableMaterialReceiveShadows(Material m)
    {
        if (!MaterialHasReceiveShadowsProperty(m))
            return false;

        var changed = false;
        Undo.RecordObject(m, "Disable material receive shadows");
        var hadReceiveShadowsOff = m.HasProperty("_ReceiveShadowsOff");
        var oldReceiveShadowsOff = hadReceiveShadowsOff ? m.GetFloat("_ReceiveShadowsOff") : 0f;
        var hadReceiveShadows = m.HasProperty("_ReceiveShadows");
        var oldReceiveShadows = hadReceiveShadows ? m.GetFloat("_ReceiveShadows") : 0f;
        var oldKeywordEnabled = m.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF");
        RegisterToolUndo(() =>
        {
            if (m == null)
                return;
            if (hadReceiveShadowsOff && m.HasProperty("_ReceiveShadowsOff"))
                m.SetFloat("_ReceiveShadowsOff", oldReceiveShadowsOff);
            if (hadReceiveShadows && m.HasProperty("_ReceiveShadows"))
                m.SetFloat("_ReceiveShadows", oldReceiveShadows);
            if (oldKeywordEnabled)
                m.EnableKeyword("_RECEIVE_SHADOWS_OFF");
            else
                m.DisableKeyword("_RECEIVE_SHADOWS_OFF");
            EditorUtility.SetDirty(m);
        });

        if (m.HasProperty("_ReceiveShadowsOff"))
        {
            changed |= Math.Abs(m.GetFloat("_ReceiveShadowsOff") - 0f) > 0.001f;
            m.SetFloat("_ReceiveShadowsOff", 0f);
        }

        if (m.HasProperty("_ReceiveShadows"))
        {
            changed |= Math.Abs(m.GetFloat("_ReceiveShadows") - 0f) > 0.001f;
            m.SetFloat("_ReceiveShadows", 0f);
        }

        changed |= !m.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF");
        m.EnableKeyword("_RECEIVE_SHADOWS_OFF");
        EditorUtility.SetDirty(m);
        if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(m)))
            AssetDatabase.SaveAssetIfDirty(m);
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
            EditorUtility.DisplayDialog("优化检测工具", "请先退出 Play 模式后再执行场景写入。", "确定");
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
                        var oldUseOcclusionCulling = cam.useOcclusionCulling;
                        RegisterToolUndo(() =>
                        {
                            if (cam == null)
                                return;
                            cam.useOcclusionCulling = oldUseOcclusionCulling;
                            EditorUtility.SetDirty(cam);
                        });
                        cam.useOcclusionCulling = true;
                        changed = true;
                    }
                }

                if (changed)
                    EditorSceneManager.MarkSceneDirty(scene);
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
        public GameObject GameObject;
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
                    GameObject = mf.gameObject,
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
                    GameObject = smr.gameObject,
                    GameObjectName = smr.gameObject.name,
                    MeshName = smr.sharedMesh.name,
                    TriCount = GetMeshTriCount(smr.sharedMesh)
                });
            }
        }

        return result;
    }

    /// <summary>场景总多边形数：80,000 以上警告 / 100,000 以上错误。</summary>
    private void AnalyzeSceneTotalPolygons()
    {
        const int warnAt = 80_000;
        const int errorAt = 100_000;

        if (!TryGetUnityStatsInt("triangles", out var totalTris))
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = false,
                Severity = DiagnosisSeverity.Warning,
                AffectsSummary = true,
                Title = "当前三角面数检查不可用",
                Detail = "无法读取 UnityEditor.UnityStats.triangles。请打开 Game 视图的 Statistics 面板后重新分析；该项以 Statistics 中显示的三角面数为准。",
                Fix = null
            });
            return;
        }

        var isOk = totalTris < warnAt;
        var severity = totalTris >= errorAt ? DiagnosisSeverity.Error : DiagnosisSeverity.Warning;

        string detail;
        if (totalTris >= errorAt)
            detail = $"当前三角面数：{totalTris:N0}，已超过 {errorAt:N0}。该值读取自 Game 视图 Statistics 中显示的三角面数。建议使用 LOD、移除不必要网格或优化网格。";
        else if (totalTris >= warnAt)
            detail = $"当前三角面数：{totalTris:N0}，已接近 {errorAt:N0}。该值读取自 Game 视图 Statistics 中显示的三角面数。建议检查是否需要优化。";
        else
            detail = $"当前三角面数：{totalTris:N0}（良好）。该值读取自 Game 视图 Statistics 中显示的三角面数。";

        _rows.Add(new DiagnosisRow
        {
            IsOk = isOk,
            Severity = severity,
            AffectsSummary = true,
            Title = "当前三角面数（Game 视图 Statistics）",
            Detail = detail,
            Fix = null
        });
    }

    /// <summary>未应用合批时的预估 Draw Call：80 以上警告 / 100 以上错误。</summary>
    /// <summary>Checks the current Game View Statistics Batches value.</summary>
    private void AnalyzeSceneBatches()
    {
        const int warnAt = 80;
        const int errorAt = 100;

        if (!TryGetUnityStatsInt("batches", out var batches))
        {
            _rows.Add(new DiagnosisRow
            {
                IsOk = false,
                Severity = DiagnosisSeverity.Warning,
                AffectsSummary = true,
                Title = "当前 Batches 检查不可用",
                Detail = "无法读取 UnityEditor.UnityStats.batches。请在 Play 模式下使用「运行时对比」页，或打开 Game 视图的 Statistics 面板后重新分析。",
                Fix = null
            });
            return;
        }

        var isOk = batches < warnAt;
        var severity = batches >= errorAt ? DiagnosisSeverity.Error : DiagnosisSeverity.Warning;

        string detail;
        if (batches >= errorAt)
            detail = $"当前 Batches：{batches}，已超过 {errorAt}。此值读取自 Game 视图 Statistics 中显示的 Batches 计数。";
        else if (batches >= warnAt)
            detail = $"当前 Batches：{batches}，已接近 {errorAt}。此值读取自 Game 视图 Statistics 中显示的 Batches 计数。";
        else
            detail = $"当前 Batches：{batches}。此值读取自 Game 视图 Statistics 中显示的 Batches 计数。";

        _rows.Add(new DiagnosisRow
        {
            IsOk = isOk,
            Severity = severity,
            AffectsSummary = true,
            Title = "当前 Batches（Game 视图 Statistics）",
            Detail = detail,
            Fix = null
        });
    }

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
            detail = $"预估 Draw Call：约 {estimated} 个，已超过 100（按未应用合批估算）。\n" +
                     "建议检查 SRP 批处理、GPU 实例化、静态合批、对象合并或材质合并。";
        else if (estimated >= warnAt)
            detail = $"预估 Draw Call：约 {estimated} 个，已接近 100（按未应用合批估算）。";
        else
            detail = $"预估 Draw Call：约 {estimated} 个（按未应用合批估算，良好）。";

        _rows.Add(new DiagnosisRow
        {
            IsOk = isOk,
            Severity = severity,
            AffectsSummary = true,
            Title = "场景预估 Draw Call 数（合批前估算）",
            Detail = detail,
            Fix = null
        });
    }

    /// <summary>场景内唯一纹理数：20 个以上建议纹理图集。</summary>
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
                    if (ShouldSkipMaterialForCheck(mat) || mat.shader == null) continue;
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
            Title = "场景使用纹理数",
            Detail = count >= warnAt
                ? $"场景中正在使用 {count} 个唯一纹理。\n" +
                  "可使用 Texture Atlas / Sprite Atlas 合并多张纹理，以减少 Draw Call 与内存带宽。\n" +
                  "可在 Unity 菜单 Window → 2D → Sprite Atlas 中创建图集。"
                : $"场景使用唯一纹理数：{count} 个（良好）。",
            Fix = null
        });
    }

    /// <summary>场景内网格多边形异常值（平均 + 2σ 超过阈值）警告。</summary>
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
            Title = "场景内多边形过高网格（异常值）",
            Detail = outliers.Count == 0
                ? $"场景内未发现多边形异常值（超过平均 + 2σ）。场景平均：{mean:N0} 个。"
                : $"发现 {outliers.Count} 个明显高于场景平均（{mean:N0} 个）的网格，阈值：{threshold:N0} 个。",
            Fix = null,
            CustomGui = outliers.Count == 0
                ? null
                : () => DrawSceneMeshInfoFoldout(
                    "scene_polygon_outliers",
                    "多边形过高网格",
                    outliers)
        });
    }

    private void DrawSceneMeshInfoFoldout(string key, string label, List<SceneMeshInfo> meshes)
    {
        if (meshes == null || meshes.Count == 0)
            return;

        if (meshes.Count == 1)
        {
            DrawSceneMeshInfoRow(meshes[0]);
            return;
        }

        if (!_listFoldouts.TryGetValue(key, out var expanded))
            expanded = false;

        expanded = EditorGUILayout.Foldout(expanded, $"{label}（{meshes.Count}）", true);
        _listFoldouts[key] = expanded;
        if (!expanded)
            return;

        EditorGUI.indentLevel++;
        foreach (var mesh in meshes)
            DrawSceneMeshInfoRow(mesh);
        EditorGUI.indentLevel--;
    }

    private static void DrawSceneMeshInfoRow(SceneMeshInfo mesh)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(
            $"{mesh.GameObjectName} [{mesh.MeshName}]：{mesh.TriCount:N0} 个",
            EditorStyles.wordWrappedMiniLabel);
        if (GUILayout.Button("定位", GUILayout.Width(44)) && mesh.GameObject != null)
        {
            Selection.activeObject = mesh.GameObject;
            EditorGUIUtility.PingObject(mesh.GameObject);
        }

        EditorGUILayout.EndHorizontal();
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
