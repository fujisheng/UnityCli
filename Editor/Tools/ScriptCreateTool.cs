using System;
using System.Collections.Generic;
using System.IO;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("script.create", Description = "Create C# script assets under Assets/", Mode = ToolMode.Both, Capabilities = ToolCapabilities.WriteAssets, Category = "editor")]
    public sealed class ScriptCreateTool : IUnityCliTool
    {
        public string Id => "script.create";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Create C# script assets under Assets/",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "path",
                        type = "string",
                        description = "Script asset path under Assets/ ending with .cs",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "contents",
                        type = "string",
                        description = "Full C# source text to write",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "script_type",
                        type = "string",
                        description = "Optional script type hint returned in the result",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "namespace",
                        type = "string",
                        description = "Optional namespace hint returned in the result",
                        required = false
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

            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "contents", out string contents, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "script_type", string.Empty, out string scriptType, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "namespace", string.Empty, out string scriptNamespace, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeScriptPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            if (string.IsNullOrEmpty(contents))
            {
                return ToolResult.Error("invalid_parameter", "参数 'contents' 不能为空。", new
                {
                    parameter = "contents"
                });
            }

            var fullPath = GetFullPath(normalizedPath, context);
            var directoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty;

            try
            {
                if (!string.IsNullOrEmpty(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                File.WriteAllText(fullPath, contents);
                AssetDatabase.ImportAsset(normalizedPath, ImportAssetOptions.ForceUpdate);
                var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(normalizedPath);

                return ToolResult.Ok(new
                {
                    action = "create",
                    path = normalizedPath,
                    fullPath,
                    exists = File.Exists(fullPath),
                    imported = monoScript != null,
                    script_type = NormalizeOptionalValue(scriptType),
                    @namespace = NormalizeOptionalValue(scriptNamespace)
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"创建脚本失败：{exception.Message}", new
                {
                    action = "create",
                    path = normalizedPath,
                    exception = exception.GetType().FullName
                });
            }
        }

        static bool EnsureWritable(ToolContext context, out ToolResult error)
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

        static string NormalizeOptionalValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
