using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Core
{
    internal static class GameObjectResolver
    {
        public static bool TryResolve(IDictionary<string, object> args, out GameObject gameObject, out ToolResult error)
        {
            gameObject = null;
            error = null;

            if (!ArgsHelper.TryGetOptional(args, "search_method", string.Empty, out string searchMethod, out error))
            {
                return false;
            }

            object targetValue = null;
            var targetText = string.Empty;
            if (args != null && args.TryGetValue("target", out var rawTarget) && rawTarget != null)
            {
                targetValue = rawTarget;
                targetText = rawTarget.ToString() ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(searchMethod))
            {
                return TryResolveByMethod(args, searchMethod, targetText, out gameObject, out error);
            }

            if (args != null && args.TryGetValue("instanceID", out var instanceIdValue) && instanceIdValue != null)
            {
                return TryResolveByInstanceId(instanceIdValue, out gameObject, out error);
            }

            if (args != null && args.TryGetValue("guid", out var guidValue) && guidValue != null)
            {
                return TryResolveByGuid(guidValue.ToString() ?? string.Empty, out gameObject, out error);
            }

            if (args != null && args.TryGetValue("path", out var pathValue) && pathValue != null)
            {
                return TryResolveByPath(pathValue.ToString() ?? string.Empty, out gameObject, out error);
            }

            if (args != null && args.TryGetValue("name", out var nameValue) && nameValue != null)
            {
                var name = nameValue.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return TryResolveByName(name, out gameObject, out error);
                }
            }

            if (targetValue != null)
            {
                return TryResolveByTarget(targetValue, out gameObject, out error);
            }

            error = ToolResult.Error("missing_parameter", "缺少目标参数。请提供 name / instanceID / guid / path 或 target。", new
            {
                supported = new[] { "name", "instanceID", "guid", "path", "target" }
            });
            return false;
        }

        public static bool TryResolveByName(string name, out GameObject gameObject, out ToolResult error)
        {
            gameObject = null;
            error = null;

            if (string.IsNullOrWhiteSpace(name))
            {
                error = ToolResult.Error("missing_parameter", "缺少参数 'name'。", new { parameter = "name" });
                return false;
            }

            var candidates = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(IsSceneObject)
                .Where(candidate => string.Equals(candidate.name, name, StringComparison.Ordinal))
                .ToArray();

            if (candidates.Length == 0)
            {
                error = ToolResult.Error("not_found", $"未找到名称为 '{name}' 的 GameObject。", new { name });
                return false;
            }

            if (candidates.Length > 1)
            {
                error = ToolResult.Error("duplicate_name", $"名称 '{name}' 对应多个 GameObject，请改用 path 或 instanceID。", new
                {
                    name,
                    candidates = candidates.Select(GetHierarchyPath).ToArray()
                });
                return false;
            }

            gameObject = candidates[0];
            return true;
        }

        public static bool TryResolveByInstanceId(object instanceIdValue, out GameObject gameObject, out ToolResult error)
        {
            gameObject = null;
            error = null;

            if (!ArgsHelper.TryGetOptional(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["instanceID"] = instanceIdValue
                }, "instanceID", 0, out int instanceId, out error))
            {
                return false;
            }

            if (instanceId == 0)
            {
                error = ToolResult.Error("invalid_parameter", "参数 'instanceID' 不能为 0。", new { instanceID = instanceIdValue });
                return false;
            }

            var resolved = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (resolved == null)
            {
                error = ToolResult.Error("not_found", $"未找到 instanceID={instanceId} 对应的 GameObject。", new { instanceID = instanceId });
                return false;
            }

            gameObject = resolved;
            return true;
        }

        public static bool TryResolveByGuid(string guid, out GameObject gameObject, out ToolResult error)
        {
            gameObject = null;
            error = null;

            if (string.IsNullOrWhiteSpace(guid))
            {
                error = ToolResult.Error("missing_parameter", "缺少参数 'guid'。", new { parameter = "guid" });
                return false;
            }

            if (GlobalObjectId.TryParse(guid, out var globalObjectId))
            {
                var sceneObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalObjectId) as GameObject;
                if (sceneObject != null)
                {
                    gameObject = sceneObject;
                    return true;
                }
            }

            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                var prefabObject = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefabObject != null)
                {
                    gameObject = prefabObject;
                    return true;
                }
            }

            error = ToolResult.Error("not_found", $"未找到 guid='{guid}' 对应的 GameObject。", new { guid });
            return false;
        }

        public static bool TryResolveByPath(string path, out GameObject gameObject, out ToolResult error)
        {
            gameObject = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = ToolResult.Error("missing_parameter", "缺少参数 'path'。", new { parameter = "path" });
                return false;
            }

            var normalizedPath = NormalizePath(path);
            if (normalizedPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var assetObject = AssetDatabase.LoadAssetAtPath<GameObject>(normalizedPath);
                if (assetObject != null)
                {
                    gameObject = assetObject;
                    return true;
                }

                error = ToolResult.Error("not_found", $"未找到资源路径为 '{normalizedPath}' 的 GameObject。", new { path = normalizedPath });
                return false;
            }

            var candidates = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(IsSceneObject)
                .Where(candidate => string.Equals(GetHierarchyPath(candidate), normalizedPath, StringComparison.Ordinal))
                .ToArray();

            if (candidates.Length == 0)
            {
                error = ToolResult.Error("not_found", $"未找到路径为 '{normalizedPath}' 的 GameObject。", new { path = normalizedPath });
                return false;
            }

            if (candidates.Length > 1)
            {
                error = ToolResult.Error("duplicate_name", $"路径 '{normalizedPath}' 对应多个 GameObject。", new
                {
                    path = normalizedPath,
                    instanceIDs = candidates.Select(candidate => candidate.GetInstanceID()).ToArray()
                });
                return false;
            }

            gameObject = candidates[0];
            return true;
        }

        static bool TryResolveByTarget(object targetValue, out GameObject gameObject, out ToolResult error)
        {
            gameObject = null;
            error = null;

            if (targetValue == null)
            {
                error = ToolResult.Error("missing_parameter", "缺少参数 'target'。", new { parameter = "target" });
                return false;
            }

            if (TryConvertToInstanceId(targetValue, out var instanceId))
            {
                return TryResolveByInstanceId(instanceId, out gameObject, out error);
            }

            var text = targetValue.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = ToolResult.Error("missing_parameter", "参数 'target' 不能为空。", new { parameter = "target" });
                return false;
            }

            if (text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return TryResolveByPath(text, out gameObject, out error);
            }

            if (text.Contains("/", StringComparison.Ordinal))
            {
                return TryResolveByPath(text, out gameObject, out error);
            }

            if (GlobalObjectId.TryParse(text, out _))
            {
                return TryResolveByGuid(text, out gameObject, out error);
            }

            if (!string.IsNullOrWhiteSpace(AssetDatabase.GUIDToAssetPath(text)))
            {
                return TryResolveByGuid(text, out gameObject, out error);
            }

            return TryResolveByName(text, out gameObject, out error);
        }

        static bool TryResolveByMethod(IDictionary<string, object> args, string searchMethod, string target, out GameObject gameObject, out ToolResult error)
        {
            gameObject = null;
            error = null;

            switch (searchMethod.Trim().ToLowerInvariant())
            {
                case "by_name":
                    if (string.IsNullOrWhiteSpace(target) && !TryReadRaw(args, "name", out target))
                    {
                        error = ToolResult.Error("missing_parameter", "search_method=by_name 需要参数 target 或 name。", new { search_method = "by_name" });
                        return false;
                    }

                    return TryResolveByName(target, out gameObject, out error);

                case "by_id":
                case "by_instanceid":
                    if (args == null || !args.TryGetValue("instanceID", out var idRaw) || idRaw == null)
                    {
                        if (!string.IsNullOrWhiteSpace(target))
                        {
                            idRaw = target;
                        }
                        else
                        {
                            error = ToolResult.Error("missing_parameter", "search_method=by_id 需要参数 instanceID 或 target。", new { search_method = "by_id" });
                            return false;
                        }
                    }

                    return TryResolveByInstanceId(idRaw, out gameObject, out error);

                case "by_guid":
                    if (string.IsNullOrWhiteSpace(target) && !TryReadRaw(args, "guid", out target))
                    {
                        error = ToolResult.Error("missing_parameter", "search_method=by_guid 需要参数 guid 或 target。", new { search_method = "by_guid" });
                        return false;
                    }

                    return TryResolveByGuid(target, out gameObject, out error);

                case "by_path":
                    if (string.IsNullOrWhiteSpace(target) && !TryReadRaw(args, "path", out target))
                    {
                        error = ToolResult.Error("missing_parameter", "search_method=by_path 需要参数 path 或 target。", new { search_method = "by_path" });
                        return false;
                    }

                    return TryResolveByPath(target, out gameObject, out error);

                default:
                    error = ToolResult.Error("invalid_parameter", $"不支持的 search_method: '{searchMethod}'。", new
                    {
                        search_method = searchMethod,
                        supported = new[] { "by_name", "by_id", "by_guid", "by_path" }
                    });
                    return false;
            }
        }

        static bool TryReadRaw(IDictionary<string, object> args, string key, out string value)
        {
            value = string.Empty;
            if (args == null || !args.TryGetValue(key, out var raw) || raw == null)
            {
                return false;
            }

            value = raw.ToString() ?? string.Empty;
            return true;
        }

        static bool TryConvertToInstanceId(object value, out int instanceId)
        {
            instanceId = 0;
            switch (value)
            {
                case int intValue:
                    instanceId = intValue;
                    return true;
                case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                    instanceId = (int)longValue;
                    return true;
                case short shortValue:
                    instanceId = shortValue;
                    return true;
                case byte byteValue:
                    instanceId = byteValue;
                    return true;
                case string stringValue:
                    return int.TryParse(stringValue.Trim(), out instanceId);
                default:
                    return false;
            }
        }

        static bool IsSceneObject(GameObject gameObject)
        {
            return gameObject != null
                && gameObject.scene.IsValid()
                && !EditorUtility.IsPersistent(gameObject)
                && (gameObject.hideFlags & HideFlags.NotEditable) == 0
                && (gameObject.hideFlags & HideFlags.HideAndDontSave) == 0;
        }

        static string NormalizePath(string path)
        {
            var normalized = path.Replace('\\', '/').Trim();
            while (normalized.Contains("//", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
            }

            return normalized.Trim('/');
        }

        static string GetHierarchyPath(GameObject gameObject)
        {
            var names = new List<string>();
            var current = gameObject != null ? gameObject.transform : null;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }
    }
}
