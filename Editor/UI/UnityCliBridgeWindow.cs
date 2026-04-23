using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.UI
{
    /// <summary>
    /// UnityCli Bridge 管理窗口
    /// </summary>
    public class UnityCliBridgeWindow : EditorWindow
    {
        const string PackageName = "com.fujisheng.unitycli";
        const string BridgeOutputDir = "Library/UnityCliBridge";
        const int StatusDotSize = 16;
        const int InitialVisibleLogCount = 20;
        const int VisibleLogBatchSize = 20;
        const double LogRefreshIntervalSeconds = 0.5d;
        static readonly string[] LogLevelOptions = { "全部级别", "信息", "警告", "错误" };

        Vector2 toolsScrollPosition;
        Vector2 logScrollPosition;
        string buildLog = string.Empty;
        bool isBuilding;
        int selectedTab;
        string searchFilter = string.Empty;
        int visibleLogCount = InitialVisibleLogCount;
        int totalLogCount;
        int filteredLogCount;
        double nextLogRefreshAt;
        int selectedLogLevelIndex;
        int selectedLogStatusIndex;
        string logToolFilter = string.Empty;
        string[] logStatusOptions = { "全部状态" };

        List<UnityCliBridgeLogEntry> allLogEntries = new List<UnityCliBridgeLogEntry>();
        List<UnityCliBridgeLogEntry> filteredLogEntries = new List<UnityCliBridgeLogEntry>();
        List<UnityCliBridgeLogEntry> visibleLogs = new List<UnityCliBridgeLogEntry>();

        // 缓存工具列表
        List<ToolDescriptor> cachedTools;
        List<ToolDescriptor> filteredTools;

        // 分类折叠状态
        Dictionary<string, bool> categoryFoldout = new Dictionary<string, bool>(StringComparer.Ordinal);

        GUIStyle headerLabelStyle;
        GUIStyle categoryBadgeStyle;
        GUIStyle modeBadgeStyle;
        Texture2D statusDotGreen;
        Texture2D statusDotRed;
        Texture2D statusDotYellow;
        bool stylesInitialized;

        [MenuItem("UnityCli/Window")]
        public static void ShowWindow()
        {
            var window = GetWindow<UnityCliBridgeWindow>();
            window.titleContent = new GUIContent("UnityCli");
            window.minSize = new Vector2(420, 500);
        }

        void OnEnable()
        {
            RefreshToolList();
            RefreshVisibleLogs(resetVisibleCount: true, forceRepaint: false);
        }

        void Update()
        {
            if (selectedTab != 1)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup < nextLogRefreshAt)
            {
                return;
            }

            nextLogRefreshAt = EditorApplication.timeSinceStartup + LogRefreshIntervalSeconds;
            RefreshVisibleLogs(forceRepaint: true);
        }

        void InitStyles()
        {
            if (stylesInitialized)
            {
                return;
            }

            stylesInitialized = true;

            statusDotGreen = MakeStatusDotTexture(new Color(0.3f, 0.85f, 0.4f));
            statusDotRed = MakeStatusDotTexture(new Color(0.9f, 0.3f, 0.3f));
            statusDotYellow = MakeStatusDotTexture(new Color(0.95f, 0.8f, 0.2f));

            headerLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft
            };

            categoryBadgeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 9,
                normal = { textColor = Color.white }
            };

            modeBadgeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 9,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };
        }

        void OnGUI()
        {
            InitStyles();

            EditorGUILayout.BeginVertical();
            DrawConnectionStatus();

            var previousTab = selectedTab;
            selectedTab = GUILayout.Toolbar(selectedTab, new[] { "工具列表", "Bridge 日志" });
            if (selectedTab == 1 && previousTab != selectedTab)
            {
                RefreshVisibleLogs(forceRepaint: false);
            }

            EditorGUILayout.Space(4);

            if (selectedTab == 0)
            {
                DrawToolList();
            }
            else
            {
                DrawBridgeLogs();
            }

            EditorGUILayout.EndVertical();
        }

        // ──────────────────────────── 连接状态 ────────────────────────────

        void DrawConnectionStatus()
        {
            EditorGUILayout.BeginVertical("HelpBox");
            {
                EditorGUILayout.BeginHorizontal();
                var isRunning = UnityCliServer.IsRunning;
                var hasBridgeExecutable = HasBridgeExecutable();
                var bridgeExecutablePath = GetBridgeExecutablePath();
                var dotTexture = isRunning ? statusDotGreen : statusDotRed;
                var dotRect = GUILayoutUtility.GetRect(StatusDotSize, StatusDotSize, GUILayout.Width(StatusDotSize), GUILayout.Height(StatusDotSize));
                GUI.DrawTexture(dotRect, dotTexture, ScaleMode.StretchToFill);
                EditorGUILayout.LabelField("Bridge 状态", headerLabelStyle);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(2);

                DrawInfoRow("CLI", hasBridgeExecutable ? "已安装" : "未安装");
                if (hasBridgeExecutable)
                {
                    DrawInfoRow("路径", bridgeExecutablePath);
                }

                if (isRunning)
                {
                    var endpoint = UnityCliServer.CurrentEndpoint;
                    if (endpoint != null)
                    {
                        DrawInfoRow("Pipe", endpoint.pipeName);
                        DrawInfoRow("PID", endpoint.pid.ToString());
                        DrawInfoRow("Generation", endpoint.generation.ToString());
                        DrawInfoRow("协议版本", endpoint.protocolVersion);
                    }
                }

                EditorGUILayout.Space(4);

                if (!hasBridgeExecutable)
                {
                    EditorGUILayout.HelpBox("未检测到 Bridge CLI 产物。点击下方「安装 Bridge」后会自动编译并启动。", MessageType.Warning);
                }
                else if (!isRunning)
                {
                    EditorGUILayout.HelpBox("Bridge 未运行。点击下方「启动 Server」或等待自动启动。", MessageType.Warning);
                }

                EditorGUI.BeginDisabledGroup(isBuilding);
                if (GUILayout.Button(GetPrimaryActionLabel(hasBridgeExecutable, isRunning), GUILayout.Height(24)))
                {
                    if (!hasBridgeExecutable || isRunning)
                    {
                        BuildBridge(restartAfterBuild: true);
                    }
                    else
                    {
                        StartOrRestartServer();
                    }
                }
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        // ──────────────────────────── 工具列表 ────────────────────────────

        void DrawToolList()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"已注册工具 ({filteredTools?.Count ?? 0})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            var newFilter = EditorGUILayout.TextField(searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(180));
            if (newFilter != searchFilter)
            {
                searchFilter = newFilter;
                ApplyFilter();
            }

            if (GUILayout.Button("↻", EditorStyles.miniButton, GUILayout.Width(24)))
            {
                RefreshToolList();
            }

            EditorGUILayout.EndHorizontal();

            if (filteredTools == null || filteredTools.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(searchFilter) ? "暂无已注册工具。" : "没有匹配搜索条件的工具。",
                    MessageType.Info);
                return;
            }

            var allowlist = UnityCliAllowlist.EnabledTools;
            var allowlistSet = new HashSet<string>(allowlist ?? Array.Empty<string>());

            // 按分类分组
            var groups = new List<KeyValuePair<string, List<ToolDescriptor>>>();
            var groupMap = new Dictionary<string, List<ToolDescriptor>>(StringComparer.Ordinal);
            foreach (var tool in filteredTools)
            {
                var cat = string.IsNullOrEmpty(tool.category) ? "other" : tool.category;
                if (!groupMap.TryGetValue(cat, out var list))
                {
                    list = new List<ToolDescriptor>();
                    groupMap[cat] = list;
                    groups.Add(new KeyValuePair<string, List<ToolDescriptor>>(cat, list));
                }

                list.Add(tool);
            }

            toolsScrollPosition = EditorGUILayout.BeginScrollView(toolsScrollPosition);
            {
                foreach (var group in groups)
                {
                    var categoryName = group.Key;
                    var categoryTools = group.Value;

                    // 折叠头部
                    if (!categoryFoldout.TryGetValue(categoryName, out var expanded))
                    {
                        expanded = true;
                        categoryFoldout[categoryName] = expanded;
                    }

                    EditorGUILayout.BeginHorizontal("Toolbar");
                    {
                        var toggleRect = GUILayoutUtility.GetRect(new GUIContent(categoryName), EditorStyles.boldLabel);
                        var arrow = expanded ? "▼" : "▶";
                        var headerContent = new GUIContent($" {arrow} {categoryName} ({categoryTools.Count})");
                        var newExpanded = GUI.Toggle(toggleRect, expanded, headerContent, EditorStyles.boldLabel);
                        if (newExpanded != expanded)
                        {
                            categoryFoldout[categoryName] = newExpanded;
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    if (!categoryFoldout[categoryName])
                    {
                        continue;
                    }

                    // 展开时绘制工具
                    foreach (var tool in categoryTools)
                    {
                        DrawToolItem(tool, allowlistSet);
                    }

                    EditorGUILayout.Space(4);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawToolItem(ToolDescriptor tool, HashSet<string> allowlistSet)
        {
            EditorGUILayout.BeginVertical("HelpBox");
            {
                EditorGUILayout.BeginHorizontal();

                var isEnabled = allowlistSet.Contains(tool.id);
                var newEnabled = GUILayout.Toggle(isEnabled, GUIContent.none, GUILayout.Width(16));
                if (newEnabled != isEnabled)
                {
                    UnityCliAllowlist.SetToolEnabled(tool.id, newEnabled);
                    UnityCliAllowlist.Save();
                    if (newEnabled)
                    {
                        allowlistSet.Add(tool.id);
                    }
                    else
                    {
                        allowlistSet.Remove(tool.id);
                    }
                }

                EditorGUILayout.LabelField(tool.id, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                // 模式标签
                var modeText = GetModeText(tool.mode);
                EditorGUILayout.LabelField(modeText, modeBadgeStyle, GUILayout.Width(60));

                // 启用状态文字
                var statusText = isEnabled ? "已启用" : "已禁用";
                var statusColor = isEnabled ? new Color(0.3f, 0.85f, 0.4f) : new Color(0.6f, 0.6f, 0.6f);
                var statusStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = statusColor },
                    alignment = TextAnchor.MiddleRight,
                    fontStyle = FontStyle.Bold
                };
                EditorGUILayout.LabelField(statusText, statusStyle, GUILayout.Width(45));

                EditorGUILayout.EndHorizontal();

                // 描述
                if (!string.IsNullOrEmpty(tool.description))
                {
                    EditorGUILayout.LabelField(tool.description, EditorStyles.wordWrappedMiniLabel);
                }

                // 能力标签
                var capText = GetCapabilitiesText(tool.capabilities);
                if (!string.IsNullOrEmpty(capText))
                {
                    EditorGUILayout.LabelField(capText, EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        // ──────────────────────────── 编译日志 ────────────────────────────

        void DrawBuildLog()
        {
            if (string.IsNullOrEmpty(buildLog))
            {
                EditorGUILayout.HelpBox("尚无 Bridge 日志。", MessageType.Info);
                return;
            }

            logScrollPosition = EditorGUILayout.BeginScrollView(logScrollPosition);
            EditorGUILayout.TextArea(buildLog, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        void DrawBridgeLogs()
        {
            RefreshVisibleLogs(forceRepaint: false);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Bridge 日志 ({visibleLogs.Count}/{filteredLogCount}，全部 {totalLogCount})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("刷新", EditorStyles.miniButton, GUILayout.Width(48)))
            {
                RefreshVisibleLogs(forceRepaint: false);
            }

            if (GUILayout.Button("清空", EditorStyles.miniButton, GUILayout.Width(48)))
            {
                ClearBridgeLogs();
            }

            EditorGUI.BeginDisabledGroup(filteredLogEntries.Count == 0);
            if (GUILayout.Button("导出", EditorStyles.miniButton, GUILayout.Width(48)))
            {
                ExportBridgeLogs();
            }

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            DrawBridgeLogFilters();

            EditorGUILayout.LabelField("默认显示 20 条，最新日志在最上方，滚动到底部会继续加载更早的记录。", EditorStyles.miniLabel);
            EditorGUILayout.Space(2);

            if (visibleLogs.Count == 0)
            {
                EditorGUILayout.HelpBox("尚无 Bridge 调用日志。", MessageType.Info);
                return;
            }

            var shouldLoadMore = false;

            logScrollPosition = EditorGUILayout.BeginScrollView(logScrollPosition);
            {
                foreach (var entry in visibleLogs)
                {
                    DrawLogEntry(entry);
                }

                GUILayout.Box(GUIContent.none, GUIStyle.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
            }

            var contentEndRect = GUILayoutUtility.GetLastRect();
            EditorGUILayout.EndScrollView();
            var scrollRect = GUILayoutUtility.GetLastRect();

            if (Event.current.type == EventType.Repaint && logScrollPosition.y > 1f)
            {
                var visibleBottom = logScrollPosition.y + scrollRect.height;
                shouldLoadMore = visibleBottom >= contentEndRect.yMax - 24f;
            }

            if (shouldLoadMore)
            {
                LoadMoreLogs();
            }
        }

        void DrawBridgeLogFilters()
        {
            EditorGUILayout.BeginVertical("HelpBox");
            {
                var filterChanged = false;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("工具", EditorStyles.miniLabel, GUILayout.Width(32));
                var newToolFilter = EditorGUILayout.TextField(logToolFilter, GUILayout.MinWidth(120));
                if (!string.Equals(newToolFilter, logToolFilter, StringComparison.Ordinal))
                {
                    logToolFilter = newToolFilter;
                    filterChanged = true;
                }

                EditorGUILayout.LabelField("状态", EditorStyles.miniLabel, GUILayout.Width(32));
                var newStatusIndex = EditorGUILayout.Popup(selectedLogStatusIndex, logStatusOptions, GUILayout.Width(110));
                if (newStatusIndex != selectedLogStatusIndex)
                {
                    selectedLogStatusIndex = newStatusIndex;
                    filterChanged = true;
                }

                EditorGUILayout.LabelField("级别", EditorStyles.miniLabel, GUILayout.Width(32));
                var newLevelIndex = EditorGUILayout.Popup(selectedLogLevelIndex, LogLevelOptions, GUILayout.Width(88));
                if (newLevelIndex != selectedLogLevelIndex)
                {
                    selectedLogLevelIndex = newLevelIndex;
                    filterChanged = true;
                }

                if (GUILayout.Button("重置", EditorStyles.miniButton, GUILayout.Width(44)))
                {
                    ResetLogFilters();
                    filterChanged = true;
                }

                EditorGUILayout.EndHorizontal();

                if (filterChanged)
                {
                    RefreshVisibleLogs(resetVisibleCount: true, forceRepaint: false);
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ──────────────────────────── 安装逻辑 ────────────────────────────

        void BuildBridge(bool restartAfterBuild = false)
        {
            if (isBuilding)
            {
                return;
            }

            var packagePath = GetPackagePath();
            if (string.IsNullOrEmpty(packagePath))
            {
                buildLog = $"[错误] 未找到 {PackageName} 包路径。";
                return;
            }

            var bridgeDir = Path.Combine(packagePath, "UnityCliBridge~");
            var projectFile = Path.Combine(bridgeDir, "UnityCli.csproj");
            if (!File.Exists(projectFile))
            {
                buildLog = $"[错误] 未找到 UnityCli.csproj：{projectFile}";
                return;
            }

            isBuilding = true;
            buildLog = $"开始安装 Bridge (Release)...\n项目：{projectFile}\n\n";
            UnityCliBridgeLogStore.AddSystem("开始安装 Bridge", LogType.Log, Path.GetFileName(projectFile));

            try
            {
                using var process = new Process();
                process.StartInfo.FileName = "dotnet";
                process.StartInfo.Arguments = $"publish \"{projectFile}\" -c Release --nologo -v minimal";
                process.StartInfo.WorkingDirectory = bridgeDir;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    buildLog += $"安装失败 (Exit Code: {process.ExitCode})\n\n{error}\n{output}";
                    UnityCliBridgeLogStore.AddSystem($"安装失败 (Exit Code: {process.ExitCode})", LogType.Error);
                    return;
                }

                buildLog += $"安装成功。\n\n{output}\n";
                UnityCliBridgeLogStore.AddSystem("安装成功", LogType.Log);

                var outputExe = GetBridgeExecutablePath();
                if (File.Exists(outputExe))
                {
                    buildLog += $"\n产物路径：{outputExe}";
                    UnityCliBridgeLogStore.AddSystem("检测到 Bridge 产物", LogType.Log, outputExe);
                }
                else
                {
                    buildLog += "\n[警告] 安装成功但未找到产物 unitycli.exe";
                    UnityCliBridgeLogStore.AddSystem("安装完成但未找到 Bridge 产物", LogType.Warning);
                }

                if (restartAfterBuild)
                {
                    StartOrRestartServer();
                }
            }
            catch (Exception exception)
            {
                buildLog += $"\n安装异常：{exception}";
                UnityCliBridgeLogStore.AddSystem("安装异常", LogType.Error, exception.Message);
            }
            finally
            {
                isBuilding = false;
                RefreshVisibleLogs(forceRepaint: false);
            }
        }

        // ──────────────────────────── 工具列表刷新 ────────────────────────────

        void RefreshToolList()
        {
            cachedTools = UnityCliRegistry.GetRegisteredDescriptors()?.ToList() ?? new List<ToolDescriptor>();
            ApplyFilter();
        }

        void ApplyFilter()
        {
            if (string.IsNullOrEmpty(searchFilter))
            {
                filteredTools = cachedTools;
            }
            else
            {
                var keyword = searchFilter.Trim().ToLowerInvariant();
                filteredTools = cachedTools
                    .Where(t =>
                        (t.id ?? string.Empty).ToLowerInvariant().Contains(keyword) ||
                        (t.description ?? string.Empty).ToLowerInvariant().Contains(keyword) ||
                        (t.category ?? string.Empty).ToLowerInvariant().Contains(keyword))
                    .ToList();
            }
        }

        // ──────────────────────────── 辅助方法 ────────────────────────────

        void StartOrRestartServer()
        {
            var wasRunning = UnityCliServer.IsRunning;
            if (wasRunning)
            {
                UnityCliServer.Stop(clearSessionState: false);
            }

            UnityCliServer.EnsureRunning();
            buildLog += $"\n{(wasRunning ? "已重启" : "已启动")} Bridge Server。";
            UnityCliBridgeLogStore.AddSystem(wasRunning ? "已重启 Bridge Server" : "已启动 Bridge Server", LogType.Log);
            RefreshToolList();
            RefreshVisibleLogs(forceRepaint: false);
        }

        void RefreshVisibleLogs(bool resetVisibleCount = false, bool forceRepaint = true)
        {
            if (resetVisibleCount)
            {
                visibleLogCount = InitialVisibleLogCount;
                logScrollPosition = Vector2.zero;
            }

            allLogEntries = UnityCliBridgeLogStore.GetAllEntries(out totalLogCount);
            UpdateLogStatusOptions(allLogEntries);

            filteredLogEntries = allLogEntries
                .Where(MatchesLogFilters)
                .ToList();

            filteredLogCount = filteredLogEntries.Count;
            visibleLogs = filteredLogEntries
                .Take(visibleLogCount)
                .ToList();

            if (forceRepaint)
            {
                Repaint();
            }
        }

        void LoadMoreLogs()
        {
            if (visibleLogs.Count >= filteredLogCount)
            {
                return;
            }

            visibleLogCount += VisibleLogBatchSize;
            RefreshVisibleLogs(forceRepaint: false);
        }

        void ClearBridgeLogs()
        {
            if (!EditorUtility.DisplayDialog("清空 Bridge 日志", "确定要清空当前 Bridge 日志吗？", "清空", "取消"))
            {
                return;
            }

            UnityCliBridgeLogStore.Clear();
            buildLog = string.Empty;
            RefreshVisibleLogs(resetVisibleCount: true, forceRepaint: false);
        }

        void ExportBridgeLogs()
        {
            var path = EditorUtility.SaveFilePanel(
                "导出 Bridge 日志",
                Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty,
                $"unitycli-bridge-log-{DateTime.Now:yyyyMMdd-HHmmss}",
                "txt");

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            File.WriteAllText(path, BuildLogExportText(filteredLogEntries), new UTF8Encoding(false));
            EditorUtility.RevealInFinder(path);
        }

        void ResetLogFilters()
        {
            logToolFilter = string.Empty;
            selectedLogLevelIndex = 0;
            selectedLogStatusIndex = 0;
        }

        void UpdateLogStatusOptions(List<UnityCliBridgeLogEntry> entries)
        {
            var statuses = entries
                .Select(entry => entry.Status)
                .Where(status => !string.IsNullOrWhiteSpace(status))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(status => status, StringComparer.Ordinal)
                .ToList();

            logStatusOptions = new[] { "全部状态" }
                .Concat(statuses)
                .ToArray();

            if (selectedLogStatusIndex >= logStatusOptions.Length)
            {
                selectedLogStatusIndex = 0;
            }
        }

        bool MatchesLogFilters(UnityCliBridgeLogEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(logToolFilter)
                && (entry.ToolId ?? string.Empty).IndexOf(logToolFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (selectedLogStatusIndex > 0)
            {
                var selectedStatus = logStatusOptions[selectedLogStatusIndex];
                if (!string.Equals(entry.Status, selectedStatus, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            switch (selectedLogLevelIndex)
            {
                case 1:
                    return entry.LogType == LogType.Log;
                case 2:
                    return entry.LogType == LogType.Warning;
                case 3:
                    return entry.LogType == LogType.Error
                        || entry.LogType == LogType.Assert
                        || entry.LogType == LogType.Exception;
                default:
                    return true;
            }
        }

        static string BuildLogExportText(List<UnityCliBridgeLogEntry> entries)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"UnityCli Bridge 日志导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"导出条数: {entries?.Count ?? 0}");
            builder.AppendLine();

            if (entries == null)
            {
                return builder.ToString();
            }

            foreach (var entry in entries)
            {
                builder.Append('[')
                    .Append(entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append("] ")
                    .Append(GetLogLevelText(entry.LogType))
                    .Append(" | ")
                    .Append(string.IsNullOrEmpty(entry.Category) ? "-" : entry.Category)
                    .Append(" | ")
                    .Append(string.IsNullOrEmpty(entry.ToolId) ? "-" : entry.ToolId)
                    .Append(" | ")
                    .Append(string.IsNullOrEmpty(entry.Status) ? "-" : entry.Status)
                    .AppendLine();

                if (!string.IsNullOrEmpty(entry.Message))
                {
                    builder.AppendLine(entry.Message);
                }

                if (!string.IsNullOrEmpty(entry.RequestId))
                {
                    builder.Append("requestId=").AppendLine(entry.RequestId);
                }

                if (!string.IsNullOrEmpty(entry.Details))
                {
                    builder.AppendLine(entry.Details);
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        void DrawLogEntry(UnityCliBridgeLogEntry entry)
        {
            EditorGUILayout.BeginVertical("HelpBox");
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(GetLogTypeSymbol(entry.LogType), GUILayout.Width(18));
                EditorGUILayout.LabelField(entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"), EditorStyles.miniLabel, GUILayout.Width(72));
                EditorGUILayout.LabelField(entry.Category, EditorStyles.miniBoldLabel, GUILayout.Width(48));

                if (!string.IsNullOrEmpty(entry.ToolId))
                {
                    EditorGUILayout.LabelField(entry.ToolId, EditorStyles.miniBoldLabel);
                }

                GUILayout.FlexibleSpace();

                if (!string.IsNullOrEmpty(entry.Status))
                {
                    EditorGUILayout.LabelField(entry.Status, EditorStyles.miniLabel, GUILayout.Width(72));
                }

                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(entry.Message))
                {
                    EditorGUILayout.LabelField(entry.Message, EditorStyles.wordWrappedMiniLabel);
                }

                var metaParts = new List<string>();
                if (!string.IsNullOrEmpty(entry.RequestId))
                {
                    metaParts.Add($"requestId={entry.RequestId}");
                }

                if (!string.IsNullOrEmpty(entry.Details))
                {
                    metaParts.Add(entry.Details);
                }

                if (metaParts.Count > 0)
                {
                    EditorGUILayout.LabelField(string.Join(" · ", metaParts), EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        static string GetLogTypeSymbol(LogType logType)
        {
            switch (logType)
            {
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return "!";
                case LogType.Warning:
                    return "~";
                default:
                    return ">";
            }
        }

        static string GetLogLevelText(LogType logType)
        {
            switch (logType)
            {
                case LogType.Warning:
                    return "Warning";
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return "Error";
                default:
                    return "Info";
            }
        }

        static string GetPackagePath()
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            foreach (var package in packages)
            {
                if (string.Equals(package.name, PackageName, StringComparison.Ordinal))
                {
                    return package.resolvedPath;
                }
            }

            return string.Empty;
        }

        static string GetBridgeExecutablePath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return string.IsNullOrEmpty(projectRoot)
                ? string.Empty
                : Path.Combine(projectRoot, BridgeOutputDir, "unitycli.exe");
        }

        static bool HasBridgeExecutable()
        {
            var executablePath = GetBridgeExecutablePath();
            return !string.IsNullOrEmpty(executablePath) && File.Exists(executablePath);
        }

        static string GetPrimaryActionLabel(bool hasBridgeExecutable, bool isRunning)
        {
            if (!hasBridgeExecutable)
            {
                return "安装 Bridge";
            }

            return isRunning ? "重启 Server" : "启动 Server";
        }

        static string GetModeText(ToolMode mode)
        {
            switch (mode)
            {
                case ToolMode.EditOnly:
                    return "Edit Only";
                case ToolMode.PlayOnly:
                    return "Play Only";
                default:
                    return "Both";
            }
        }

        static string GetCapabilitiesText(ToolCapabilities caps)
        {
            if (caps == ToolCapabilities.None)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if ((caps & ToolCapabilities.ReadOnly) != 0)
            {
                parts.Add("只读");
            }

            if ((caps & ToolCapabilities.WriteAssets) != 0)
            {
                parts.Add("写资源");
            }

            if ((caps & ToolCapabilities.SceneMutation) != 0)
            {
                parts.Add("场景修改");
            }

            if ((caps & ToolCapabilities.PlayMode) != 0)
            {
                parts.Add("运行时");
            }

            if ((caps & ToolCapabilities.ExternalProcess) != 0)
            {
                parts.Add("外部进程");
            }

            if ((caps & ToolCapabilities.Dangerous) != 0)
            {
                parts.Add("危险");
            }

            return "能力：" + string.Join(" · ", parts);
        }

        void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(70));
            EditorGUILayout.SelectableLabel(value, EditorStyles.miniLabel, GUILayout.Height(14));
            EditorGUILayout.EndHorizontal();
        }

        static Texture2D MakeStatusDotTexture(Color color)
        {
            const int textureSize = 32;
            var tex = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            var center = (textureSize - 1) * 0.5f;
            var radius = textureSize * 0.38f;
            var edgeSoftness = 1.5f;

            for (var y = 0; y < textureSize; y++)
            {
                for (var x = 0; x < textureSize; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    var distance = Mathf.Sqrt(dx * dx + dy * dy);
                    var alpha = 1f - Mathf.Clamp01((distance - radius) / edgeSoftness);

                    tex.SetPixel(x, y, new Color(color.r, color.g, color.b, alpha));
                }
            }

            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply();
            return tex;
        }
    }
}
