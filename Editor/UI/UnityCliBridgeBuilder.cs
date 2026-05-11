using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.UI
{
    /// <summary>
    /// Bridge CLI 编译逻辑，供 Bootstrap 自动编译和 Window 手动编译共用。
    /// </summary>
    public static class UnityCliBridgeBuilder
    {
        const string PackageName = "com.fujisheng.unitycli";
        const string BridgeOutputDir = "Library/UnityCliBridge";

        static bool isBuilding;

        /// <summary>是否正在编译中。</summary>
        public static bool IsBuilding => isBuilding;

        /// <summary>返回 Bridge 可执行文件路径。</summary>
        public static string GetBridgeExecutablePath()
        {
            var projectRoot = GetProjectRoot();
            return string.IsNullOrEmpty(projectRoot)
                ? string.Empty
                : Path.Combine(projectRoot, BridgeOutputDir, "unitycli.exe");
        }

        /// <summary>Bridge 可执行文件是否已存在。</summary>
        public static bool HasBridgeExecutable()
        {
            var path = GetBridgeExecutablePath();
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        /// <summary>
        /// 同步编译 Bridge。返回 true 表示成功。
        /// </summary>
        public static bool Build()
        {
            if (isBuilding)
            {
                return false;
            }

            var packagePath = GetPackagePath();
            if (string.IsNullOrEmpty(packagePath))
            {
                Debug.LogError($"[UnityCliBridgeBuilder] 未找到 {PackageName} 包路径。");
                return false;
            }

            var bridgeDir = Path.Combine(packagePath, "UnityCliBridge~");
            var projectFile = Path.Combine(bridgeDir, "UnityCli.csproj");
            if (!File.Exists(projectFile))
            {
                Debug.LogError($"[UnityCliBridgeBuilder] 未找到 UnityCli.csproj：{projectFile}");
                return false;
            }

            isBuilding = true;
            Debug.Log($"[UnityCliBridgeBuilder] 开始编译 Bridge：{projectFile}");

            try
            {
                using var process = new System.Diagnostics.Process();
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
                    Debug.LogError($"[UnityCliBridgeBuilder] Bridge 编译失败 (Exit Code: {process.ExitCode})\n{error}\n{output}");
                    return false;
                }

                Debug.Log("[UnityCliBridgeBuilder] Bridge 编译成功。");

                var outputExe = GetBridgeExecutablePath();
                if (File.Exists(outputExe))
                {
                    Debug.Log($"[UnityCliBridgeBuilder] Bridge 产物：{outputExe}");
                }

                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[UnityCliBridgeBuilder] Bridge 编译异常：{exception.Message}");
                return false;
            }
            finally
            {
                isBuilding = false;
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

        static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
        }
    }
}
