using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.UI
{
    /// <summary>
    /// 把 UnityCli 包内 Skills 安装到项目级目录（OpenCode 或 Codex）。
    /// </summary>
    public static class UnityCliSkillInstaller
    {
        const string PackageName = "com.fujisheng.unitycli";

        static string cachedPackageSkillsRoot;

        /// <summary>通过 PackageInfo 解析包内 Skills 的实际磁盘路径。</summary>
        public static string GetPackageSkillsRoot()
        {
            if (cachedPackageSkillsRoot != null)
            {
                return cachedPackageSkillsRoot;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityCliSkillInstaller).Assembly);
            if (packageInfo != null)
            {
                cachedPackageSkillsRoot = NormalizePath(Path.Combine(packageInfo.resolvedPath, "Skills"));
                return cachedPackageSkillsRoot;
            }

            // 兜底
            var projectRoot = GetProjectRoot();
            cachedPackageSkillsRoot = NormalizePath(Path.Combine(projectRoot, "Packages", PackageName, "Skills"));
            return cachedPackageSkillsRoot;
        }

        /// <summary>OpenCode 项目级 skill 目标路径。</summary>
        public static string GetOpenCodeSkillsRoot()
        {
            return NormalizePath(Path.Combine(GetProjectRoot(), ".opencode", "skills"));
        }

        /// <summary>Codex 项目级 skill 目标路径。</summary>
        public static string GetCodexSkillsRoot()
        {
            return NormalizePath(Path.Combine(GetProjectRoot(), ".agents", "skills"));
        }

        /// <summary>安装 skills 到指定目标目录。返回安装的 skill 数量。</summary>
        public static int InstallSkills(string destinationRoot)
        {
            var sourceSkillsRoot = GetPackageSkillsRoot();
            if (string.IsNullOrWhiteSpace(sourceSkillsRoot) || !Directory.Exists(sourceSkillsRoot))
            {
                Debug.LogWarning("[UnityCliSkillInstaller] 未找到包内 Skills 目录。");
                return 0;
            }

            var copiedCount = 0;
            foreach (var sourceSkillDirectory in Directory.GetDirectories(sourceSkillsRoot))
            {
                var skillDirectoryName = Path.GetFileName(sourceSkillDirectory);
                if (string.IsNullOrWhiteSpace(skillDirectoryName))
                {
                    continue;
                }

                // 跳过 .meta 同名目录
                if (skillDirectoryName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var skillFilePath = Path.Combine(sourceSkillDirectory, "SKILL.md");
                if (!File.Exists(skillFilePath))
                {
                    continue;
                }

                var destinationSkillDirectory = Path.Combine(destinationRoot, skillDirectoryName);
                CopyDirectory(sourceSkillDirectory, destinationSkillDirectory);
                copiedCount++;
            }

            if (copiedCount > 0)
            {
                AssetDatabase.Refresh();
            }

            return copiedCount;
        }

        static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var sourceFilePath in Directory.GetFiles(sourceDirectory))
            {
                var fileName = Path.GetFileName(sourceFilePath);
                if (ShouldSkip(fileName))
                {
                    continue;
                }

                var destinationFilePath = Path.Combine(destinationDirectory, fileName);
                File.Copy(sourceFilePath, destinationFilePath, true);
            }

            foreach (var sourceSubDirectory in Directory.GetDirectories(sourceDirectory))
            {
                var directoryName = Path.GetFileName(sourceSubDirectory);
                if (ShouldSkip(directoryName))
                {
                    continue;
                }

                var destinationSubDirectory = Path.Combine(destinationDirectory, directoryName);
                CopyDirectory(sourceSubDirectory, destinationSubDirectory);
            }
        }

        static bool ShouldSkip(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return true;
            }

            if (fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }
    }
}
