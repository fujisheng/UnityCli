using System;
using System.Collections.Generic;
using System.IO;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityCli.Editor.Tools
{
    /// <summary>
    /// UnityCli 构建工具 — 支持通过命令行执行 WebGL 构建。
    /// 用法: '{"args":{"platform":"webgl","outputPath":"Build/WebGL"}}' | unitycli invoke --tool build --stdin
    /// </summary>
    [UnityCliTool(
        "build",
        Description = "Build Unity project for target platform",
        Mode = ToolMode.EditOnly,
        Capabilities = ToolCapabilities.Dangerous | ToolCapabilities.WriteAssets,
        Category = "editor")]
    public class BuildTool : IUnityCliTool
    {
        public string Id => "build";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Build Unity project for target platform (WebGL, Android, iOS, Standalone)",
                mode = ToolMode.EditOnly,
                capabilities = ToolCapabilities.Dangerous | ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "platform",
                        type = "string",
                        description = "Target platform: webgl, android, ios, standalone",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "outputPath",
                        type = "string",
                        description = "Build output directory (relative to project root)",
                        required = false,
                        defaultValue = "Build/WebGL"
                    },
                    new ParamDescriptor
                    {
                        name = "development",
                        type = "boolean",
                        description = "Development build with debug symbols",
                        required = false,
                        defaultValue = false
                    },
                    new ParamDescriptor
                    {
                        name = "compression",
                        type = "string",
                        description = "Compression method for WebGL: gzip, brotli, disabled",
                        required = false,
                        defaultValue = "gzip"
                    }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (context == null)
                return ToolResult.Error("invalid_context", "工具上下文不能为空。");

            if (!ArgsHelper.TryGetRequired(args, "platform", out string platformStr, out var error))
                return error;

            if (!TryParsePlatform(platformStr, out BuildTarget buildTarget, out string platformName, out error))
                return error;

            if (!ArgsHelper.TryGetOptional(args, "outputPath", "Build/" + platformName, out string outputPath, out error))
                return error;

            if (!ArgsHelper.TryGetOptional(args, "development", false, out bool development, out error))
                return error;

            if (!ArgsHelper.TryGetOptional(args, "compression", "gzip", out string compression, out error))
                return error;

            // 确保不在 Play Mode
            if (EditorApplication.isPlaying)
            {
                return ToolResult.Error("invalid_state",
                    "构建前需要退出 Play Mode。请先执行 editor stop 再重试。");
            }

            // 确保不在编译中
            if (EditorApplication.isCompiling)
            {
                return ToolResult.Error("invalid_state",
                    "Unity 正在编译，请等待编译完成后再构建。");
            }

            try
            {
                // 1. 切换构建平台
                var currentTarget = EditorUserBuildSettings.activeBuildTarget;
                var targetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);

                if (currentTarget != buildTarget)
                {
                    Debug.Log($"[BuildTool] Switching platform: {currentTarget} → {buildTarget}");
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, buildTarget))
                    {
                        return ToolResult.Error("build_failed",
                            $"无法切换到 {platformName} 平台。请确认已安装对应模块。");
                    }
                }

                // 2. 解析输出路径
                string projectRoot = context.EditorState?.ProjectPath ?? Directory.GetCurrentDirectory();
                string fullOutputPath = Path.Combine(projectRoot, outputPath);

                // 确保输出目录存在
                string outputDir = Path.GetDirectoryName(fullOutputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                // 3. 获取构建场景列表
                var scenes = GetBuildScenes();
                if (scenes.Length == 0)
                {
                    return ToolResult.Error("build_failed",
                        "EditorBuildSettings 中没有启用任何场景。请在 Build Settings 中添加场景。");
                }

                // 4. 配置构建设置
                var buildOptions = BuildOptions.None;
                if (development)
                    buildOptions |= BuildOptions.Development;

                // WebGL 特定设置
                if (buildTarget == BuildTarget.WebGL)
                {
                    ApplyWebGLSettings(compression, development);
                }

                // 5. 输出构建信息
                Debug.Log($"[BuildTool] Starting {platformName} build...");
                Debug.Log($"[BuildTool]   Output: {fullOutputPath}");
                Debug.Log($"[BuildTool]   Scenes: {scenes.Length}");
                Debug.Log($"[BuildTool]   Development: {development}");
                foreach (var scene in scenes)
                    Debug.Log($"[BuildTool]     - {Path.GetFileNameWithoutExtension(scene)}");

                // 6. 执行构建
                var report = BuildPipeline.BuildPlayer(
                    scenes,
                    fullOutputPath,
                    buildTarget,
                    buildOptions);

                if (report.summary.result == BuildResult.Succeeded)
                {
                    var summary = report.summary;
                    var outputSize = GetDirectorySize(fullOutputPath);

                    Debug.Log($"[BuildTool] ✓ Build succeeded!");
                    Debug.Log($"[BuildTool]   Time: {summary.totalTime.TotalSeconds:F1}s");
                    Debug.Log($"[BuildTool]   Size: {FormatSize(outputSize)}");
                    Debug.Log($"[BuildTool]   Output: {fullOutputPath}");

                    return ToolResult.Ok(new
                    {
                        platform = platformName,
                        outputPath = fullOutputPath,
                        buildTime = summary.totalTime.TotalSeconds,
                        outputSizeBytes = outputSize,
                        outputSizeFormatted = FormatSize(outputSize),
                        scenes = scenes.Length,
                        development,
                        errors = summary.totalErrors,
                        warnings = summary.totalWarnings,
                    }, $"构建成功！{platformName} 版本已输出到 {outputPath}");
                }
                else
                {
                    var errors = new List<string>();
                    foreach (var step in report.steps)
                    {
                        foreach (var msg in step.messages)
                        {
                            if (msg.type == LogType.Error || msg.type == LogType.Exception)
                                errors.Add($"[{msg.type}] {msg.content}");
                        }
                    }

                    Debug.LogError($"[BuildTool] ✗ Build failed: {report.summary.result}");
                    foreach (var err in errors)
                        Debug.LogError(err);

                    return ToolResult.Error("build_failed",
                        $"构建失败：{report.summary.result}（{errors.Count} 个错误）",
                        new { errors });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BuildTool] Build exception: {ex}");
                return ToolResult.Error("build_exception", ex.Message);
            }
        }

        #region Helpers

        private static bool TryParsePlatform(string platform, out BuildTarget buildTarget, out string platformName, out ToolResult error)
        {
            platformName = platform.ToLowerInvariant();
            error = null;

            switch (platformName)
            {
                case "webgl":
                    buildTarget = BuildTarget.WebGL;
                    break;
                case "android":
                    buildTarget = BuildTarget.Android;
                    break;
                case "ios":
                    buildTarget = BuildTarget.iOS;
                    break;
                case "standalone":
                case "windows":
                case "win":
                    buildTarget = BuildTarget.StandaloneWindows64;
                    break;
                default:
                    buildTarget = BuildTarget.NoTarget;
                    error = ToolResult.Error("invalid_parameter",
                        $"不支持的平台 '{platform}'。支持: webgl, android, ios, standalone",
                        new { supported = new[] { "webgl", "android", "ios", "standalone" } });
                    return false;
            }

            return true;
        }

        private static string[] GetBuildScenes()
        {
            var scenes = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled && !string.IsNullOrEmpty(scene.path))
                    scenes.Add(scene.path);
            }
            return scenes.ToArray();
        }

        private static void ApplyWebGLSettings(string compression, bool development)
        {
            // 压缩格式
            switch (compression.ToLowerInvariant())
            {
                case "gzip":
                    PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                    break;
                case "brotli":
                    PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
                    break;
                case "disabled":
                case "none":
                    PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
                    break;
            }

            // 开发构建不要启用异常（减小包体）
            if (!development)
            {
                PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
            }

            // Data Caching
            PlayerSettings.WebGL.dataCaching = true;

            // 使用项目配置的模板
            // (已在 ProjectSettings.asset 中设置为 PROJECT:TikTok)

            Debug.Log($"[BuildTool] WebGL settings: compression={compression}, exception={(development ? "Full" : "None")}");
        }

        private static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path))
                return 0;

            long size = 0;
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch { /* 跳过无法访问的文件 */ }
            }
            return size;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        #endregion
    }
}
