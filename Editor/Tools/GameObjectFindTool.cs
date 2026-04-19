using System;
using System.Collections.Generic;
using System.Linq;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("gameobject.find", Description = "Find scene GameObjects by search criteria", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly, Category = "editor")]
    public sealed class GameObjectFindTool : IUnityCliTool
    {
        const int DefaultPageSize = 50;
        static readonly string[] SupportedSearchMethods =
        {
            "by_name",
            "by_tag",
            "by_layer",
            "by_component",
            "by_path",
            "by_id"
        };

        public string Id => "gameobject.find";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Find scene GameObjects by search criteria",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "search_term",
                        type = "string",
                        description = "Search term value",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "search_method",
                        type = "string",
                        description = "Search method: by_name/by_tag/by_layer/by_component/by_path/by_id",
                        required = false,
                        defaultValue = "by_name"
                    },
                    new ParamDescriptor
                    {
                        name = "include_inactive",
                        type = "boolean",
                        description = "Whether to include inactive objects in hierarchy",
                        required = false,
                        defaultValue = false
                    },
                    new ParamDescriptor
                    {
                        name = "page_size",
                        type = "integer",
                        description = "Maximum number of results to return",
                        required = false,
                        defaultValue = DefaultPageSize
                    }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (!ArgsHelper.TryGetRequired(args, "search_term", out string searchTerm, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "search_method", "by_name", out string searchMethod, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "include_inactive", false, out bool includeInactive, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "page_size", DefaultPageSize, out int pageSize, out error))
            {
                return error;
            }

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return ToolResult.Error("invalid_parameter", "参数 'search_term' 不能为空。", new
                {
                    parameter = "search_term"
                });
            }

            if (pageSize <= 0)
            {
                return ToolResult.Error("invalid_parameter", "参数 'page_size' 必须大于 0。", new
                {
                    parameter = "page_size",
                    value = pageSize
                });
            }

            var normalizedSearchMethod = (searchMethod ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedSearchMethod))
            {
                normalizedSearchMethod = "by_name";
            }

            if (!TryBuildMatcher(normalizedSearchMethod, searchTerm.Trim(), out var matcher, out error))
            {
                return error;
            }

            var candidates = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(IsSceneObject)
                .Where(gameObject => includeInactive || gameObject.activeInHierarchy)
                .Where(matcher)
                .Take(pageSize)
                .Select(gameObject => new
                {
                    instanceId = gameObject.GetInstanceID(),
                    name = gameObject.name,
                    activeSelf = gameObject.activeSelf,
                    activeInHierarchy = gameObject.activeInHierarchy,
                    tag = gameObject.tag,
                    layer = gameObject.layer,
                    path = GetHierarchyPath(gameObject)
                })
                .Cast<object>()
                .ToArray();

            return ToolResult.Ok(new
            {
                search_term = searchTerm,
                search_method = normalizedSearchMethod,
                include_inactive = includeInactive,
                page_size = pageSize,
                count = candidates.Length,
                results = candidates
            });
        }

        static bool TryBuildMatcher(string searchMethod, string searchTerm, out Func<GameObject, bool> matcher, out ToolResult error)
        {
            matcher = null;
            error = null;

            switch (searchMethod)
            {
                case "by_name":
                    matcher = gameObject => string.Equals(gameObject.name, searchTerm, StringComparison.Ordinal);
                    return true;

                case "by_tag":
                    matcher = gameObject => string.Equals(gameObject.tag, searchTerm, StringComparison.Ordinal);
                    return true;

                case "by_layer":
                    if (!TryResolveLayer(searchTerm, out var layer, out error))
                    {
                        return false;
                    }

                    matcher = gameObject => gameObject.layer == layer;
                    return true;

                case "by_component":
                    matcher = gameObject => HasComponentType(gameObject, searchTerm);
                    return true;

                case "by_path":
                    var normalizedPath = NormalizePath(searchTerm);
                    matcher = gameObject => string.Equals(GetHierarchyPath(gameObject), normalizedPath, StringComparison.Ordinal);
                    return true;

                case "by_id":
                    if (!int.TryParse(searchTerm, out var instanceId))
                    {
                        error = ToolResult.Error("invalid_parameter", "search_method=by_id 时，'search_term' 必须是整数 instanceID。", new
                        {
                            parameter = "search_term",
                            search_method = "by_id",
                            value = searchTerm
                        });
                        return false;
                    }

                    matcher = gameObject => gameObject.GetInstanceID() == instanceId;
                    return true;

                default:
                    error = ToolResult.Error("invalid_parameter", $"不支持的 search_method: '{searchMethod}'。", new
                    {
                        parameter = "search_method",
                        value = searchMethod,
                        supported = SupportedSearchMethods
                    });
                    return false;
            }
        }

        static bool TryResolveLayer(string layerTerm, out int layer, out ToolResult error)
        {
            layer = -1;
            error = null;

            if (int.TryParse(layerTerm, out var numericLayer))
            {
                if (numericLayer < 0 || numericLayer > 31)
                {
                    error = ToolResult.Error("invalid_parameter", "层号必须在 0~31 范围内。", new
                    {
                        parameter = "search_term",
                        search_method = "by_layer",
                        value = layerTerm
                    });
                    return false;
                }

                layer = numericLayer;
                return true;
            }

            var layerByName = LayerMask.NameToLayer(layerTerm);
            if (layerByName < 0)
            {
                error = ToolResult.Error("invalid_parameter", $"未找到层名 '{layerTerm}'。", new
                {
                    parameter = "search_term",
                    search_method = "by_layer",
                    value = layerTerm
                });
                return false;
            }

            layer = layerByName;
            return true;
        }

        static bool HasComponentType(GameObject gameObject, string componentTypeName)
        {
            var components = gameObject.GetComponents<Component>();
            foreach (var component in components)
            {
                if (component == null)
                {
                    continue;
                }

                var componentType = component.GetType();
                if (string.Equals(componentType.Name, componentTypeName, StringComparison.Ordinal)
                    || string.Equals(componentType.FullName, componentTypeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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
