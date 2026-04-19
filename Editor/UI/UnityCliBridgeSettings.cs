using UnityEngine;

namespace UnityCli.Editor.UI
{
    /// <summary>
    /// UnityCli Bridge 编辑器设置
    /// </summary>
    public class UnityCliBridgeSettings : ScriptableObject
    {
        const string SettingsPath = "Assets/Editor Default Resources/UnityCli/Settings/BridgeSettings.asset";

        /// <summary>
        /// dotnet 发布配置（Release / Debug）
        /// </summary>
        public string buildConfiguration = "Release";

        /// <summary>
        /// 是否在编译后自动复制到 Tools 目录
        /// </summary>
        public bool autoCopyToTools = true;

        public static string GetSettingsPath()
        {
            return SettingsPath;
        }

        public static UnityCliBridgeSettings LoadOrCreate()
        {
            var settings = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityCliBridgeSettings>(SettingsPath);
            if (settings != null)
            {
                return settings;
            }

            var directory = System.IO.Path.GetDirectoryName(SettingsPath);
            if (!System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            settings = CreateInstance<UnityCliBridgeSettings>();
            UnityEditor.AssetDatabase.CreateAsset(settings, SettingsPath);
            UnityEditor.AssetDatabase.Refresh();
            return settings;
        }
    }
}
