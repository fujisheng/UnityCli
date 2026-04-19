using System;
using System.Collections.Generic;
using System.IO;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("script.validate", Description = "Validate a C# script asset path and current editor compile state", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets, Category = "editor")]
    public sealed class ScriptValidateTool : IUnityCliTool
    {
        public string Id => "script.validate";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Validate a C# script asset path and current editor compile state",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "path",
                        type = "string",
                        description = "Script asset path under Assets/ ending with .cs",
                        required = true
                    }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (context == null)
            {
                return ToolResult.Error("invalid_parameter", "工具上下文不能为空。", nameof(context));
            }

            if (!EnsureValidationReady(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeScriptPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            var fullPath = GetFullPath(normalizedPath, context);
            if (!File.Exists(fullPath))
            {
                return ToolResult.Error("not_found", "脚本文件不存在。", new
                {
                    path = rawPath,
                    normalizedPath,
                    fullPath
                });
            }

            try
            {
                AssetDatabase.ImportAsset(normalizedPath, ImportAssetOptions.ForceUpdate);
                var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(normalizedPath);
                var importer = AssetImporter.GetAtPath(normalizedPath);

                return ToolResult.Ok(new
                {
                    action = "validate",
                    path = normalizedPath,
                    fullPath,
                    exists = true,
                    imported = importer != null,
                    isMonoScript = monoScript != null,
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating,
                    currentCompileState = EditorApplication.isCompiling ? "compiling" : "idle"
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"校验脚本失败：{exception.Message}", new
                {
                    action = "validate",
                    path = normalizedPath,
                    exception = exception.GetType().FullName
                });
            }
        }

        static bool EnsureValidationReady(ToolContext context, out ToolResult error)
        {
            error = null;
            if (!StateGuard.EnsureReady(context, out error))
            {
                return false;
            }

            if (!StateGuard.EnsureNotPlaying(context, out error))
            {
                return false;
            }

            return true;
        }

        static string GetFullPath(string assetPath, ToolContext context)
        {
            var projectPath = context?.EditorState?.ProjectPath ?? string.Empty;
            var relativePath = assetPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(projectPath, relativePath);
        }
    }
}
