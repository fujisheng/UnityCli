using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityCli.Protocol;
using UnityEngine;

namespace UnityCli.Editor.Core
{
    public static class UnityCliAllowlist
    {
        const string DefaultAllowlistRelativePath = "Packages/com.UnityCli/Editor/Tools/BuiltIn/__default_allowlist.json";
        const string ProjectAllowlistRelativePath = "ProjectSettings/UnityCliAllowlist.json";

        static readonly StringComparer ToolIdComparer = StringComparer.Ordinal;
        static HashSet<string> enabledTools = new HashSet<string>(ToolIdComparer);

        public static string ActiveAllowlistPath { get; private set; } = string.Empty;

        public static IReadOnlyCollection<string> EnabledTools => enabledTools.ToArray();

        public static void Reload()
        {
            var allowlistFile = LoadActiveAllowlist();
            var toolIds = allowlistFile.enabledTools ?? Array.Empty<string>();
            enabledTools = new HashSet<string>(toolIds.Where(toolId => !string.IsNullOrWhiteSpace(toolId)), ToolIdComparer);
        }

        public static bool IsAllowed(string toolId)
        {
            return !string.IsNullOrWhiteSpace(toolId) && enabledTools.Contains(toolId);
        }

        public static IReadOnlyList<IUnityCliTool> GetAllowedTools()
        {
            var allowedTools = new List<IUnityCliTool>();
            foreach (var toolId in UnityCliRegistry.GetRegisteredToolIds())
            {
                if (!IsAllowed(toolId))
                {
                    continue;
                }

                if (UnityCliRegistry.TryGetTool(toolId, out var tool))
                {
                    allowedTools.Add(tool);
                }
            }

            return allowedTools;
        }

        public static IReadOnlyList<ToolDescriptor> GetAllowedDescriptors()
        {
            var descriptors = new List<ToolDescriptor>();
            foreach (var toolId in UnityCliRegistry.GetRegisteredToolIds())
            {
                if (!IsAllowed(toolId))
                {
                    continue;
                }

                if (UnityCliRegistry.TryGetDescriptor(toolId, out var descriptor))
                {
                    descriptors.Add(descriptor);
                }
            }

            return descriptors;
        }

        static AllowlistFile LoadActiveAllowlist()
        {
            var projectAllowlistPath = GetAbsolutePath(ProjectAllowlistRelativePath);
            if (TryLoadAllowlist(projectAllowlistPath, out var projectAllowlist))
            {
                ActiveAllowlistPath = projectAllowlistPath;
                return projectAllowlist;
            }

            var defaultAllowlistPath = GetAbsolutePath(DefaultAllowlistRelativePath);
            if (TryLoadAllowlist(defaultAllowlistPath, out var defaultAllowlist))
            {
                ActiveAllowlistPath = defaultAllowlistPath;
                return defaultAllowlist;
            }

            ActiveAllowlistPath = string.Empty;
            return new AllowlistFile();
        }

        static bool TryLoadAllowlist(string path, out AllowlistFile allowlistFile)
        {
            allowlistFile = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonUtility.FromJson<AllowlistFile>(json);
                if (parsed == null)
                {
                    Debug.LogWarning($"[UnityCli] 读取 allowlist 失败，JSON 为空对象：{path}");
                    return false;
                }

                allowlistFile = parsed;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[UnityCli] 读取 allowlist 失败：{path}\n{exception}");
                return false;
            }
        }

        /// <summary>
        /// 设置工具启用/禁用状态（仅内存，需调用 Save 刷盘）
        /// </summary>
        public static void SetToolEnabled(string toolId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(toolId))
            {
                return;
            }

            if (enabled)
            {
                enabledTools.Add(toolId);
            }
            else
            {
                enabledTools.Remove(toolId);
            }
        }

        /// <summary>
        /// 将当前白名单保存到项目级配置文件
        /// </summary>
        public static void Save()
        {
            var path = GetAbsolutePath(ProjectAllowlistRelativePath);
            var file = new AllowlistFile
            {
                enabledTools = enabledTools.OrderBy(id => id, ToolIdComparer).ToArray()
            };

            try
            {
                var json = JsonUtility.ToJson(file, true);
                File.WriteAllText(path, json);
                ActiveAllowlistPath = path;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[UnityCli] 保存 allowlist 失败：{path}\n{exception}");
            }
        }

        static string GetAbsolutePath(string relativePath)
        {
            return Path.GetFullPath(relativePath);
        }

        [Serializable]
        sealed class AllowlistFile
        {
            public string[] enabledTools = Array.Empty<string>();
        }
    }
}
