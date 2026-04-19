using System;
using System.Collections.Generic;
using System.Threading;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("editor", Description = "Control Unity Editor play/pause/stop/refresh/menu", Mode = ToolMode.Both, Capabilities = ToolCapabilities.PlayMode | ToolCapabilities.WriteAssets, Category = "editor")]
    public sealed class EditorControlTool : IUnityCliTool
    {
        static readonly string[] SupportedActions =
        {
            "play",
            "pause",
            "stop",
            "refresh",
            "menu_item"
        };

        static readonly string[] SupportedRefreshModes =
        {
            "if_dirty",
            "force"
        };

        static readonly string[] SupportedRefreshScopes =
        {
            "assets",
            "scripts",
            "all"
        };

        static readonly string[] SupportedCompileModes =
        {
            "none",
            "request"
        };

        public string Id => "editor";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Control Unity Editor play/pause/stop/refresh/menu",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.PlayMode | ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "action",
                        type = "string",
                        description = "Editor action: play/pause/stop/refresh/menu_item",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "paused",
                        type = "boolean",
                        description = "Optional explicit pause target for action=pause. If omitted, toggle pause state.",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "mode",
                        type = "string",
                        description = "Refresh mode for action=refresh: if_dirty/force",
                        required = false,
                        defaultValue = "if_dirty"
                    },
                    new ParamDescriptor
                    {
                        name = "scope",
                        type = "string",
                        description = "Refresh scope for action=refresh: assets/scripts/all",
                        required = false,
                        defaultValue = "all"
                    },
                    new ParamDescriptor
                    {
                        name = "compile",
                        type = "string",
                        description = "Compilation request for action=refresh: none/request",
                        required = false,
                        defaultValue = "none"
                    },
                    new ParamDescriptor
                    {
                        name = "wait_for_ready",
                        type = "boolean",
                        description = "Wait until editor is not compiling/updating after refresh",
                        required = false,
                        defaultValue = true
                    },
                    new ParamDescriptor
                    {
                        name = "menu_path",
                        type = "string",
                        description = "Unity menu path for action=menu_item",
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

            if (!ArgsHelper.TryGetRequired(args, "action", out string action, out var error))
            {
                return error;
            }

            switch (action)
            {
                case "play":
                    return HandlePlay(context);
                case "pause":
                    return HandlePause(args, context);
                case "stop":
                    return HandleStop(context);
                case "refresh":
                    return HandleRefresh(args, context);
                case "menu_item":
                    return HandleMenuItem(args, context);
                default:
                    return ToolResult.Error("invalid_parameter", $"不支持的 editor 操作 '{action}'。", new
                    {
                        parameter = "action",
                        value = action,
                        supportedActions = SupportedActions
                    });
            }
        }

        static ToolResult HandlePlay(ToolContext context)
        {
            if (!StateGuard.EnsurePlayMode(context, false, out var error))
            {
                return error;
            }

            EditorApplication.isPlaying = true;
            return ToolResult.Ok(new
            {
                action = "play",
                success = true,
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode
            });
        }

        static ToolResult HandlePause(Dictionary<string, object> args, ToolContext context)
        {
            if (!StateGuard.EnsureNotCompiling(context, out var error))
            {
                return error;
            }

            if (!context.IsPlaying)
            {
                return ToolResult.Error("not_allowed", "当前运行模式不允许暂停。期望: Play，当前: Edit。", new
                {
                    expected = "Play",
                    current = "Edit"
                });
            }

            if (!ArgsHelper.TryGetOptional(args, "paused", false, out bool requestedPaused, out error))
            {
                return error;
            }

            var previousPaused = EditorApplication.isPaused;
            var hasExplicitPause = args != null
                && args.TryGetValue("paused", out var pausedRawValue)
                && pausedRawValue != null;
            var nextPaused = hasExplicitPause ? requestedPaused : !previousPaused;
            EditorApplication.isPaused = nextPaused;

            return ToolResult.Ok(new
            {
                action = "pause",
                success = true,
                mode = hasExplicitPause ? "set" : "toggle",
                previousPaused,
                currentPaused = EditorApplication.isPaused
            });
        }

        static ToolResult HandleStop(ToolContext context)
        {
            if (!StateGuard.EnsurePlayMode(context, true, out var error))
            {
                return error;
            }

            EditorApplication.isPlaying = false;
            return ToolResult.Ok(new
            {
                action = "stop",
                success = true,
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode
            });
        }

        static ToolResult HandleRefresh(Dictionary<string, object> args, ToolContext context)
        {
            if (!StateGuard.EnsureNotPlaying(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "mode", "if_dirty", out string mode, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "scope", "all", out string scope, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "compile", "none", out string compileMode, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "wait_for_ready", true, out bool waitForReady, out error))
            {
                return error;
            }

            if (!TryNormalizeEnumLike(mode, SupportedRefreshModes, "mode", out var normalizedMode, out error))
            {
                return error;
            }

            if (!TryNormalizeEnumLike(scope, SupportedRefreshScopes, "scope", out var normalizedScope, out error))
            {
                return error;
            }

            if (!TryNormalizeEnumLike(compileMode, SupportedCompileModes, "compile", out var normalizedCompileMode, out error))
            {
                return error;
            }

            var importOptions = normalizedMode == "force"
                ? ImportAssetOptions.ForceUpdate
                : ImportAssetOptions.Default;

            var refreshAssets = normalizedScope == "assets" || normalizedScope == "all";
            var refreshScripts = normalizedScope == "scripts" || normalizedScope == "all";
            var refreshTriggered = refreshAssets || refreshScripts;
            if (refreshTriggered)
            {
                AssetDatabase.Refresh(importOptions);
            }

            var compileRequested = normalizedCompileMode == "request";
            if (compileRequested)
            {
                CompilationPipeline.RequestScriptCompilation();
            }

            var timedOut = false;
            if (waitForReady)
            {
                timedOut = !WaitUntilEditorReady(2000d);
            }

            return ToolResult.Ok(new
            {
                action = "refresh",
                success = true,
                mode = normalizedMode,
                scope = normalizedScope,
                compile = normalizedCompileMode,
                waitForReady,
                refreshTriggered,
                compileRequested,
                timedOut,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating
            });
        }

        static ToolResult HandleMenuItem(Dictionary<string, object> args, ToolContext context)
        {
            if (!StateGuard.EnsureReady(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "menu_path", out string menuPath, out error))
            {
                return error;
            }

            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return ToolResult.Error("invalid_parameter", "参数 'menu_path' 不能为空。", new
                {
                    parameter = "menu_path"
                });
            }

            var executed = EditorApplication.ExecuteMenuItem(menuPath);
            if (!executed)
            {
                return ToolResult.Error("invalid_parameter", "未找到可执行的菜单项。", new
                {
                    parameter = "menu_path",
                    value = menuPath
                });
            }

            return ToolResult.Ok(new
            {
                action = "menu_item",
                success = true,
                menuPath,
                executed
            });
        }

        static bool TryNormalizeEnumLike(string rawValue, IReadOnlyCollection<string> supported, string parameterName, out string normalizedValue, out ToolResult error)
        {
            normalizedValue = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 不能为空。", new
                {
                    parameter = parameterName,
                    supported
                });
                return false;
            }

            normalizedValue = rawValue.Trim().ToLowerInvariant();
            foreach (var value in supported)
            {
                if (string.Equals(normalizedValue, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 不支持值 '{rawValue}'。", new
            {
                parameter = parameterName,
                value = rawValue,
                supported
            });
            normalizedValue = string.Empty;
            return false;
        }

        static bool WaitUntilEditorReady(double timeoutMs)
        {
            var startTime = EditorApplication.timeSinceStartup;
            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                var elapsedMs = (EditorApplication.timeSinceStartup - startTime) * 1000d;
                if (elapsedMs > timeoutMs)
                {
                    return false;
                }

                Thread.Sleep(50);
            }

            return true;
        }
    }
}
