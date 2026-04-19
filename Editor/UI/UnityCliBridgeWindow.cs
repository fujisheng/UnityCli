using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        const string BridgeOutputDir = "Library/UnityCliBridge";

        Vector2 toolsScrollPosition;
        Vector2 logScrollPosition;
        string buildLog = string.Empty;
        bool isBuilding;
        int selectedTab;
        string searchFilter = string.Empty;

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

        [MenuItem("UnityCli/Bridge Window")]
        public static void ShowWindow()
        {
            var window = GetWindow<UnityCliBridgeWindow>();
            window.titleContent = new GUIContent("UnityCli Bridge");
            window.minSize = new Vector2(420, 500);
        }

        void OnEnable()
        {
            RefreshToolList();
        }

        void InitStyles()
        {
            if (stylesInitialized)
            {
                return;
            }

            stylesInitialized = true;

            statusDotGreen = MakeColorTexture(new Color(0.3f, 0.85f, 0.4f));
            statusDotRed = MakeColorTexture(new Color(0.9f, 0.3f, 0.3f));
            statusDotYellow = MakeColorTexture(new Color(0.95f, 0.8f, 0.2f));

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
            DrawBuildSection();

            selectedTab = GUILayout.Toolbar(selectedTab, new[] { "工具列表", "编译日志" });
            EditorGUILayout.Space(4);

            if (selectedTab == 0)
            {
                DrawToolList();
            }
            else
            {
                DrawBuildLog();
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
                var dotTexture = isRunning ? statusDotGreen : statusDotRed;
                GUILayout.Box(dotTexture, GUILayout.Width(10), GUILayout.Height(10));
                EditorGUILayout.LabelField("Bridge 状态", headerLabelStyle);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(2);

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

                    EditorGUILayout.Space(4);

                    if (GUILayout.Button("重启 Server", GUILayout.Height(24)))
                    {
                        UnityCliServer.Stop(clearSessionState: false);
                        UnityCliServer.EnsureRunning();
                        RefreshToolList();
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Bridge 未运行。点击下方「编译并启动」或等待自动启动。", MessageType.Warning);
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        // ──────────────────────────── 编译区域 ────────────────────────────

        void DrawBuildSection()
        {
            EditorGUILayout.BeginVertical("HelpBox");
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Box(statusDotYellow, GUILayout.Width(10), GUILayout.Height(10));
                EditorGUILayout.LabelField("Bridge 编译", headerLabelStyle);

                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField("配置", GUILayout.Width(30));
                var settings = UnityCliBridgeSettings.LoadOrCreate();
                var configs = new[] { "Release", "Debug" };
                var configIndex = Array.IndexOf(configs, settings.buildConfiguration);
                if (configIndex < 0)
                {
                    configIndex = 0;
                }

                var newConfigIndex = EditorGUILayout.Popup(configIndex, configs, GUILayout.Width(80));
                if (newConfigIndex != configIndex)
                {
                    settings.buildConfiguration = configs[newConfigIndex];
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(2);

                EditorGUI.BeginDisabledGroup(isBuilding);
                {
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("编译 Bridge", GUILayout.Height(28)))
                    {
                        BuildBridge(settings.buildConfiguration);
                    }

                    if (GUILayout.Button("编译并启动", GUILayout.Height(28)))
                    {
                        BuildBridge(settings.buildConfiguration, restartAfterBuild: true);
                    }

                    EditorGUILayout.EndHorizontal();
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
                EditorGUILayout.HelpBox("尚无编译日志。", MessageType.Info);
                return;
            }

            logScrollPosition = EditorGUILayout.BeginScrollView(logScrollPosition);
            EditorGUILayout.TextArea(buildLog, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        // ──────────────────────────── 编译逻辑 ────────────────────────────

        void BuildBridge(string configuration, bool restartAfterBuild = false)
        {
            if (isBuilding)
            {
                return;
            }

            var packagePath = GetPackagePath();
            if (string.IsNullOrEmpty(packagePath))
            {
                buildLog = "[错误] 未找到 com.UnityCli 包路径。";
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
            buildLog = $"开始编译 ({configuration})...\n项目：{projectFile}\n\n";

            try
            {
                var process = new Process();
                process.StartInfo.FileName = "dotnet";
                process.StartInfo.Arguments = $"publish \"{projectFile}\" -c {configuration} --nologo -v minimal";
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
                    buildLog += $"编译失败 (Exit Code: {process.ExitCode})\n\n{error}\n{output}";
                    return;
                }

                buildLog += $"编译成功。\n\n{output}\n";

                // 验证产物
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
                var outputExe = Path.Combine(projectRoot, BridgeOutputDir, "unitycli.exe");
                if (File.Exists(outputExe))
                {
                    buildLog += $"\n产物路径：{outputExe}";
                }
                else
                {
                    buildLog += "\n[警告] 编译成功但未找到产物 unitycli.exe";
                }

                if (restartAfterBuild)
                {
                    UnityCliServer.Stop(clearSessionState: false);
                    UnityCliServer.EnsureRunning();
                    buildLog += "\n已重启 Bridge Server。";
                    RefreshToolList();
                }
            }
            catch (Exception exception)
            {
                buildLog += $"\n编译异常：{exception}";
            }
            finally
            {
                isBuilding = false;
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

        static string GetPackagePath()
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            foreach (var package in packages)
            {
                if (string.Equals(package.name, "com.UnityCli", StringComparison.Ordinal))
                {
                    return package.resolvedPath;
                }
            }

            return string.Empty;
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

        static Texture2D MakeColorTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}
