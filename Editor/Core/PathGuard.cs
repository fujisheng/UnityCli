using System;
using System.IO;

namespace UnityCli.Editor.Core
{
    internal static class PathGuard
    {
        const string AssetsPrefix = "Assets/";

        public static bool TryNormalizeAssetPath(string inputPath, out string normalizedPath, out ToolResult error)
        {
            normalizedPath = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                error = ToolResult.Error("missing_parameter", "缺少路径参数。", new
                {
                    parameter = "path"
                });
                return false;
            }

            var candidate = inputPath.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(candidate))
            {
                error = ToolResult.Error("path_violation", "不允许绝对路径，仅允许 Assets/ 下路径。", new
                {
                    path = inputPath
                });
                return false;
            }

            if (candidate.Contains("../", StringComparison.Ordinal)
                || string.Equals(candidate, "..", StringComparison.Ordinal)
                || candidate.Contains("/..", StringComparison.Ordinal))
            {
                error = ToolResult.Error("path_violation", "路径包含上级目录跳转，已拒绝。", new
                {
                    path = inputPath
                });
                return false;
            }

            if (string.Equals(candidate, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = "Assets";
                return true;
            }

            if (!candidate.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = ToolResult.Error("path_violation", "仅允许访问 Assets/ 下路径。", new
                {
                    path = inputPath
                });
                return false;
            }

            normalizedPath = NormalizeSegments(candidate);
            return true;
        }

        public static bool TryNormalizeScriptPath(string inputPath, out string normalizedPath, out ToolResult error)
        {
            normalizedPath = string.Empty;
            error = null;

            if (!TryNormalizeAssetPath(inputPath, out normalizedPath, out error))
            {
                return false;
            }

            if (!normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolResult.Error("path_violation", "脚本路径必须是 .cs 文件。", new
                {
                    path = inputPath,
                    normalizedPath
                });
                normalizedPath = string.Empty;
                return false;
            }

            return true;
        }

        static string NormalizeSegments(string path)
        {
            while (path.Contains("//", StringComparison.Ordinal))
            {
                path = path.Replace("//", "/", StringComparison.Ordinal);
            }

            if (path.EndsWith("/", StringComparison.Ordinal) && !string.Equals(path, "Assets/", StringComparison.Ordinal))
            {
                path = path.TrimEnd('/');
            }

            return path;
        }
    }
}
