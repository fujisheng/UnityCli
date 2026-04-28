using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool(
        "prefab",
        Description = "Read and headlessly modify prefab assets",
        Mode = ToolMode.Both,
        Capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.SceneMutation | ToolCapabilities.WriteAssets,
        Category = "editor")]
    public sealed class PrefabTool : IUnityCliTool
    {
        static readonly string[] SupportedActions =
        {
            "get_info",
            "get_hierarchy",
            "create",
            "create_from_gameobject",
            "modify_contents"
        };

        static readonly Type[] ComponentTypes = TypeCache.GetTypesDerivedFrom<Component>()
            .Where(type => type != null)
            .ToArray();

        public string Id => "prefab";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Read and headlessly modify prefab assets",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.SceneMutation | ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "action",
                        type = "string",
                        description = "prefab action: get_info/get_hierarchy/create/create_from_gameobject/modify_contents",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "prefab_path",
                        type = "string",
                        description = "Prefab asset path under Assets/",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "target",
                        type = "string",
                        description = "Scene GameObject reference for create_from_gameobject",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "search_method",
                        type = "string",
                        description = "Optional target resolver override for create_from_gameobject",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "name",
                        type = "string",
                        description = "Optional root name for modify_contents",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "new_name",
                        type = "string",
                        description = "Optional root rename alias for modify_contents",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "position",
                        type = "array",
                        description = "Optional local position [x,y,z] for prefab root",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "rotation",
                        type = "array",
                        description = "Optional local rotation euler angles [x,y,z] for prefab root",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "scale",
                        type = "array",
                        description = "Optional local scale [x,y,z] for prefab root",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "set_active",
                        type = "boolean",
                        description = "Optional active state for prefab root",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "tag",
                        type = "string",
                        description = "Optional tag for prefab root",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "layer",
                        type = "string",
                        description = "Optional layer name or index for prefab root",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "components_to_add",
                        type = "array",
                        description = "Optional component type names to add on prefab root",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "components_to_remove",
                        type = "array",
                        description = "Optional component type names to remove from prefab root",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "create_child",
                        type = "object",
                        description = "Optional child spec or array of child specs for create / modify_contents",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "child_path",
                        type = "string",
                        description = "Optional child path in prefab for modify_contents (targets root if omitted)",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "delete_child",
                        type = "string",
                        description = "Optional child path or array of child paths for modify_contents",
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
                case "get_info":
                    return HandleGetInfo(args);
                case "get_hierarchy":
                    return HandleGetHierarchy(args);
                case "create":
                    return HandleCreate(args, context);
                case "create_from_gameobject":
                    return HandleCreateFromGameObject(args, context);
                case "modify_contents":
                    return HandleModifyContents(args, context);
                default:
                    return ToolResult.Error("invalid_parameter", $"不支持的 prefab 操作 '{action}'。", new
                    {
                        parameter = "action",
                        value = action,
                        supportedActions = SupportedActions
                    });
            }
        }

        static ToolResult HandleGetInfo(Dictionary<string, object> args)
        {
            if (!TryGetPrefabPath(args, out var prefabPath, out var error))
            {
                return error;
            }

            if (!TryLoadPrefabContents(prefabPath, out var prefabRoot, out error))
            {
                return error;
            }

            try
            {
                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                return ToolResult.Ok(new
                {
                    action = "get_info",
                    prefab = CreatePrefabSummary(prefabPath, prefabAsset, prefabRoot)
                });
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        static ToolResult HandleGetHierarchy(Dictionary<string, object> args)
        {
            if (!TryGetPrefabPath(args, out var prefabPath, out var error))
            {
                return error;
            }

            if (!TryLoadPrefabContents(prefabPath, out var prefabRoot, out error))
            {
                return error;
            }

            try
            {
                return ToolResult.Ok(new
                {
                    action = "get_hierarchy",
                    prefab_path = prefabPath,
                    hierarchy = CreateHierarchyNode(prefabRoot.transform, string.Empty)
                });
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        static ToolResult HandleCreate(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryGetPrefabPath(args, out var prefabPath, out error))
            {
                return error;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
            {
                return ToolResult.Error("invalid_parameter", "目标 prefab_path 已存在。", new
                {
                    prefab_path = prefabPath
                });
            }

            if (!TryEnsureAssetFolderExists(prefabPath, out error))
            {
                return error;
            }

            // Determine root name from prefab file name
            var rootName = System.IO.Path.GetFileNameWithoutExtension(prefabPath);
            ArgsHelper.TryGetOptional(args, "name", rootName, out var customName, out _);
            if (!string.IsNullOrWhiteSpace(customName))
            {
                rootName = customName;
            }

            // Parse optional position/rotation/scale
            TryGetOptionalVector3(args, "position", out var hasPos, out var pos, out error);
            if (error != null) return error;
            TryGetOptionalVector3(args, "rotation", out var hasRot, out var rot, out error);
            if (error != null) return error;
            TryGetOptionalVector3(args, "scale", out var hasScale, out var scl, out error);
            if (error != null) return error;

            // Parse optional components
            TryGetOptionalStringArray(args, "components_to_add", out _, out var componentNames, out error);
            if (error != null) return error;
            if (!TryResolveComponentTypes(componentNames, out var componentTypes, out error))
            {
                return error;
            }

            // Parse optional children
            if (!TryParseChildCreateSpecs(args, out var childCreateSpecs, out error))
            {
                return error;
            }

            try
            {
                // Create root GameObject
                var root = new GameObject(rootName, typeof(RectTransform));
                if (hasPos) root.transform.localPosition = pos;
                if (hasRot) root.transform.localRotation = Quaternion.Euler(rot);
                if (hasScale) root.transform.localScale = scl;

                // Add components
                var addedComponents = new List<object>();
                if (!TryAddComponents(root, componentTypes, addedComponents, out error))
                {
                    Object.DestroyImmediate(root);
                    return error;
                }

                // Create children
                var createdChildren = new List<object>();
                if (!TryCreateChildren(root, childCreateSpecs, createdChildren, out error))
                {
                    Object.DestroyImmediate(root);
                    return error;
                }

                // Save as prefab
                var prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out var success);
                if (!success || prefabAsset == null)
                {
                    Object.DestroyImmediate(root);
                    return ToolResult.Error("tool_execution_failed", "Unity 未能创建 Prefab 资源。", new
                    {
                        action = "create",
                        prefab_path = prefabPath
                    });
                }

                AssetDatabase.SaveAssets();
                return ToolResult.Ok(new
                {
                    action = "create",
                    prefab_path = prefabPath,
                    root_name = rootName,
                    added_components = addedComponents.ToArray(),
                    created_children = createdChildren.ToArray(),
                    prefab = CreatePrefabSummary(prefabPath, prefabAsset, root)
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"执行 create 时发生异常：{exception.Message}", new
                {
                    action = "create",
                    prefab_path = prefabPath,
                    exception = exception.GetType().FullName
                });
            }
        }

        static ToolResult HandleCreateFromGameObject(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryGetPrefabPath(args, out var prefabPath, out error))
            {
                return error;
            }

            if (!GameObjectResolver.TryResolve(args, out var target, out error))
            {
                return error;
            }

            if (target == null || !target.scene.IsValid() || EditorUtility.IsPersistent(target))
            {
                return ToolResult.Error("invalid_parameter", "create_from_gameobject 需要场景中的 GameObject 目标。", new
                {
                    target = target != null ? CreateGameObjectReference(target) : null
                });
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
            {
                return ToolResult.Error("invalid_parameter", "目标 prefab_path 已存在。", new
                {
                    prefab_path = prefabPath
                });
            }

            if (!TryEnsureAssetFolderExists(prefabPath, out error))
            {
                return error;
            }

            try
            {
                var prefabAsset = PrefabUtility.SaveAsPrefabAsset(target, prefabPath, out var success);
                if (!success || prefabAsset == null)
                {
                    return ToolResult.Error("tool_execution_failed", "Unity 未能创建 Prefab 资源。", new
                    {
                        action = "create_from_gameobject",
                        prefab_path = prefabPath,
                        target = CreateGameObjectReference(target)
                    });
                }

                AssetDatabase.SaveAssets();
                return ToolResult.Ok(new
                {
                    action = "create_from_gameobject",
                    prefab_path = prefabPath,
                    source = CreateGameObjectReference(target),
                    prefab = CreatePrefabSummary(prefabPath, prefabAsset, target)
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"执行 create_from_gameobject 时发生异常：{exception.Message}", new
                {
                    action = "create_from_gameobject",
                    prefab_path = prefabPath,
                    exception = exception.GetType().FullName
                });
            }
        }

        static ToolResult HandleModifyContents(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryGetPrefabPath(args, out var prefabPath, out error))
            {
                return error;
            }

            if (!TryGetRenameValue(args, out var hasRename, out var renameValue, out error))
            {
                return error;
            }

            if (!TryGetOptionalVector3(args, "position", out var hasPosition, out var position, out error))
            {
                return error;
            }

            if (!TryGetOptionalVector3(args, "rotation", out var hasRotation, out var rotation, out error))
            {
                return error;
            }

            if (!TryGetOptionalVector3(args, "scale", out var hasScale, out var scale, out error))
            {
                return error;
            }

            if (!TryGetOptionalBoolean(args, "set_active", out var hasSetActive, out var setActive, out error))
            {
                return error;
            }

            if (!TryGetOptionalNonEmptyString(args, "tag", out var hasTag, out var tagValue, out error))
            {
                return error;
            }

            if (!TryGetOptionalNonEmptyString(args, "layer", out var hasLayer, out var layerValue, out error))
            {
                return error;
            }

            var layer = 0;
            if (hasLayer && !TryResolveLayer(layerValue, out layer, out error))
            {
                return error;
            }

            if (hasTag && !TryValidateTag(tagValue, out error))
            {
                return error;
            }

            if (!TryGetOptionalStringArray(args, "components_to_add", out _, out var componentNamesToAdd, out error))
            {
                return error;
            }

            if (!TryGetOptionalStringArray(args, "components_to_remove", out _, out var componentNamesToRemove, out error))
            {
                return error;
            }

            if (!TryResolveComponentTypes(componentNamesToAdd, out var componentTypesToAdd, out error))
            {
                return error;
            }

            if (!TryResolveComponentTypes(componentNamesToRemove, out var componentTypesToRemove, out error))
            {
                return error;
            }

            if (!TryParseChildCreateSpecs(args, out var childCreateSpecs, out error))
            {
                return error;
            }

            if (!TryParseDeleteChildPaths(args, out var deleteChildPaths, out error))
            {
                return error;
            }

            if (!hasRename
                && !hasPosition
                && !hasRotation
                && !hasScale
                && !hasSetActive
                && !hasTag
                && !hasLayer
                && componentTypesToAdd.Count == 0
                && componentTypesToRemove.Count == 0
                && childCreateSpecs.Count == 0
                && deleteChildPaths.Count == 0)
            {
                return ToolResult.Error("missing_parameter", "modify_contents 至少需要一个修改参数。", new
                {
                    supported = new[]
                    {
                        "name",
                        "new_name",
                        "position",
                        "rotation",
                        "scale",
                        "set_active",
                        "tag",
                        "layer",
                        "components_to_add",
                        "components_to_remove",
                        "create_child",
                        "delete_child"
                    }
                });
            }

            if (!TryLoadPrefabContents(prefabPath, out var prefabRoot, out error))
            {
                return error;
            }

            // Navigate to child if specified
            var targetRoot = prefabRoot;
            if (ArgsHelper.TryGetOptional(args, "child_path", string.Empty, out var childPath, out _) && !string.IsNullOrWhiteSpace(childPath))
            {
                if (!TryFindChildTransform(prefabRoot.transform, childPath, out var childTransform, out error))
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                    return error;
                }
                targetRoot = childTransform.gameObject;
            }

            try
            {
                var addedComponents = new List<object>();
                var removedComponents = new List<object>();
                var createdChildren = new List<object>();
                var deletedChildren = new List<object>();

                if (hasRename)
                {
                    targetRoot.name = renameValue;
                }

                if (hasSetActive)
                {
                    targetRoot.SetActive(setActive);
                }

                if (hasTag)
                {
                    targetRoot.tag = tagValue;
                }

                if (hasLayer)
                {
                    targetRoot.layer = layer;
                }

                if (hasPosition)
                {
                    targetRoot.transform.localPosition = position;
                }

                if (hasRotation)
                {
                    targetRoot.transform.localRotation = Quaternion.Euler(rotation);
                }

                if (hasScale)
                {
                    targetRoot.transform.localScale = scale;
                }

                if (!TryAddComponents(targetRoot, componentTypesToAdd, addedComponents, out error))
                {
                    return error;
                }

                if (!TryRemoveComponents(prefabRoot, componentTypesToRemove, removedComponents, out error))
                {
                    return error;
                }

                if (!TryCreateChildren(prefabRoot, childCreateSpecs, createdChildren, out error))
                {
                    return error;
                }

                if (!TryDeleteChildren(prefabRoot, deleteChildPaths, deletedChildren, out error))
                {
                    return error;
                }

                var savedPrefab = PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath, out var success);
                if (!success || savedPrefab == null)
                {
                    return ToolResult.Error("tool_execution_failed", "Unity 未能保存 prefab 修改。", new
                    {
                        action = "modify_contents",
                        prefab_path = prefabPath
                    });
                }

                AssetDatabase.SaveAssets();
                return ToolResult.Ok(new
                {
                    action = "modify_contents",
                    prefab_path = prefabPath,
                    added_components = addedComponents.ToArray(),
                    removed_components = removedComponents.ToArray(),
                    created_children = createdChildren.ToArray(),
                    deleted_children = deletedChildren.ToArray(),
                    prefab = CreatePrefabSummary(prefabPath, savedPrefab, prefabRoot)
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"执行 modify_contents 时发生异常：{exception.Message}", new
                {
                    action = "modify_contents",
                    prefab_path = prefabPath,
                    exception = exception.GetType().FullName
                });
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
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

        static bool TryGetPrefabPath(IDictionary<string, object> args, out string prefabPath, out ToolResult error)
        {
            prefabPath = string.Empty;
            error = null;
            if (!ArgsHelper.TryGetRequired(args, "prefab_path", out string rawPath, out error))
            {
                return false;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out prefabPath, out error))
            {
                return false;
            }

            if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolResult.Error("path_violation", "prefab_path 必须是 .prefab 文件。", new
                {
                    prefab_path = rawPath,
                    normalized_path = prefabPath
                });
                prefabPath = string.Empty;
                return false;
            }

            return true;
        }

        static bool TryLoadPrefabContents(string prefabPath, out GameObject prefabRoot, out ToolResult error)
        {
            prefabRoot = null;
            error = null;

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                error = ToolResult.Error("not_found", "未找到 prefab 资源。", new
                {
                    prefab_path = prefabPath
                });
                return false;
            }

            try
            {
                prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                if (prefabRoot == null)
                {
                    error = ToolResult.Error("tool_execution_failed", "Unity 未能加载 prefab 内容。", new
                    {
                        prefab_path = prefabPath
                    });
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = ToolResult.Error("tool_execution_failed", $"加载 prefab 内容失败：{exception.Message}", new
                {
                    prefab_path = prefabPath,
                    exception = exception.GetType().FullName
                });
                return false;
            }
        }

        static bool TryEnsureAssetFolderExists(string assetPath, out ToolResult error)
        {
            error = null;
            var folderPath = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(folderPath) || string.Equals(folderPath, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!folderPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolResult.Error("path_violation", "Prefab 目录必须位于 Assets/ 下。", new
                {
                    path = assetPath,
                    folder = folderPath
                });
                return false;
            }

            var segments = folderPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = segments[0];
            for (var index = 1; index < segments.Length; index++)
            {
                var next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    var guid = AssetDatabase.CreateFolder(current, segments[index]);
                    if (string.IsNullOrWhiteSpace(guid))
                    {
                        error = ToolResult.Error("tool_execution_failed", "创建 prefab 目录失败。", new
                        {
                            path = assetPath,
                            folder = next
                        });
                        return false;
                    }
                }

                current = next;
            }

            return true;
        }

        static object CreatePrefabSummary(string prefabPath, GameObject prefabAsset, GameObject root)
        {
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            var nodes = root.GetComponentsInChildren<Transform>(true);
            var components = root.GetComponentsInChildren<Component>(true)
                .Where(component => component != null)
                .ToArray();
            var assetType = prefabAsset != null ? PrefabUtility.GetPrefabAssetType(prefabAsset).ToString() : string.Empty;

            return new
            {
                path = prefabPath,
                guid,
                asset_type = assetType,
                node_count = nodes.Length,
                component_count = components.Length,
                root = CreateGameObjectSummary(root, string.Empty)
            };
        }

        static object CreateHierarchyNode(Transform transform, string parentPath)
        {
            var path = string.IsNullOrWhiteSpace(parentPath) ? transform.name : parentPath + "/" + transform.name;
            var children = new object[transform.childCount];
            for (var index = 0; index < transform.childCount; index++)
            {
                children[index] = CreateHierarchyNode(transform.GetChild(index), path);
            }

            return new
            {
                name = transform.name,
                path,
                activeSelf = transform.gameObject.activeSelf,
                localPosition = CreateVector3Summary(transform.localPosition),
                localRotation = CreateVector3Summary(transform.localEulerAngles),
                localScale = CreateVector3Summary(transform.localScale),
                components = transform.gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(component => GetComponentTypeName(component.GetType()))
                    .ToArray(),
                childCount = transform.childCount,
                children
            };
        }

        static object CreateGameObjectSummary(GameObject gameObject, string parentPath)
        {
            var path = string.IsNullOrWhiteSpace(parentPath) ? gameObject.name : parentPath + "/" + gameObject.name;
            return new
            {
                name = gameObject.name,
                path,
                activeSelf = gameObject.activeSelf,
                tag = gameObject.tag,
                layer = gameObject.layer,
                localPosition = CreateVector3Summary(gameObject.transform.localPosition),
                localRotation = CreateVector3Summary(gameObject.transform.localEulerAngles),
                localScale = CreateVector3Summary(gameObject.transform.localScale),
                childCount = gameObject.transform.childCount,
                components = gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(component => GetComponentTypeName(component.GetType()))
                    .ToArray()
            };
        }

        static object CreateGameObjectReference(GameObject gameObject)
        {
            return new
            {
                name = gameObject.name,
                instanceID = gameObject.GetInstanceID(),
                path = GetHierarchyPath(gameObject.transform)
            };
        }

        static object CreateVector3Summary(Vector3 value)
        {
            return new[]
            {
                (double)value.x,
                (double)value.y,
                (double)value.z
            };
        }

        static bool TryGetRenameValue(IDictionary<string, object> args, out bool hasRename, out string renameValue, out ToolResult error)
        {
            hasRename = false;
            renameValue = string.Empty;
            error = null;

            if (!TryGetOptionalNonEmptyString(args, "new_name", out var hasNewName, out var newName, out error))
            {
                return false;
            }

            if (!TryGetOptionalNonEmptyString(args, "name", out var hasName, out var name, out error))
            {
                return false;
            }

            if (hasNewName && hasName)
            {
                error = ToolResult.Error("invalid_parameter", "参数 'name' 与 'new_name' 不能同时提供。", new
                {
                    parameters = new[] { "name", "new_name" }
                });
                return false;
            }

            hasRename = hasNewName || hasName;
            renameValue = hasNewName ? newName : name;
            return true;
        }

        static bool TryGetOptionalNonEmptyString(IDictionary<string, object> args, string parameterName, out bool hasValue, out string value, out ToolResult error)
        {
            hasValue = false;
            value = string.Empty;
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, parameterName, out hasValue, out var rawValue, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            value = rawValue?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 不能为空字符串。", new
                {
                    parameter = parameterName
                });
                value = string.Empty;
                return false;
            }

            return true;
        }

        static bool TryGetOptionalBoolean(IDictionary<string, object> args, string parameterName, out bool hasValue, out bool value, out ToolResult error)
        {
            hasValue = false;
            value = false;
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, parameterName, out hasValue, out _, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            if (!ArgsHelper.TryGetOptional(args, parameterName, false, out value, out error))
            {
                return false;
            }

            return true;
        }

        static bool TryGetOptionalVector3(IDictionary<string, object> args, string parameterName, out bool hasValue, out Vector3 value, out ToolResult error)
        {
            hasValue = false;
            value = default;
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, parameterName, out hasValue, out _, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            if (!ArgsHelper.TryGetArray(args, parameterName, out var rawArray, out error))
            {
                return false;
            }

            if (rawArray.Length != 3)
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 必须是长度为 3 的数组。", new
                {
                    parameter = parameterName,
                    length = rawArray.Length
                });
                return false;
            }

            if (!TryConvertToFloat(rawArray[0], out var x)
                || !TryConvertToFloat(rawArray[1], out var y)
                || !TryConvertToFloat(rawArray[2], out var z))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 必须是数字数组。", new
                {
                    parameter = parameterName,
                    value = rawArray
                });
                return false;
            }

            value = new Vector3(x, y, z);
            return true;
        }

        static bool TryGetOptionalStringArray(IDictionary<string, object> args, string parameterName, out bool hasValue, out List<string> values, out ToolResult error)
        {
            hasValue = false;
            values = new List<string>();
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, parameterName, out hasValue, out _, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            if (!ArgsHelper.TryGetArray(args, parameterName, out var rawArray, out error))
            {
                return false;
            }

            for (var index = 0; index < rawArray.Length; index++)
            {
                var text = rawArray[index]?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 包含空字符串。", new
                    {
                        parameter = parameterName,
                        index
                    });
                    values.Clear();
                    return false;
                }

                values.Add(text);
            }

            return true;
        }

        static bool TryResolveComponentTypes(IEnumerable<string> componentNames, out List<Type> componentTypes, out ToolResult error)
        {
            componentTypes = new List<Type>();
            error = null;

            foreach (var componentName in componentNames)
            {
                if (!TryResolveComponentType(componentName, out var componentType, out error))
                {
                    componentTypes.Clear();
                    return false;
                }

                componentTypes.Add(componentType);
            }

            return true;
        }

        static bool TryResolveComponentType(string componentName, out Type componentType, out ToolResult error)
        {
            componentType = null;
            error = null;
            if (string.IsNullOrWhiteSpace(componentName))
            {
                error = ToolResult.Error("missing_parameter", "缺少组件类型。", new
                {
                    parameter = "component_type"
                });
                return false;
            }

            var normalizedName = componentName.Trim();
            var matches = ComponentTypes
                .Where(type => string.Equals(type.Name, normalizedName, StringComparison.Ordinal)
                    || string.Equals(type.FullName, normalizedName, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length == 0)
            {
                error = ToolResult.Error("invalid_parameter", $"未找到组件类型 '{normalizedName}'。", new
                {
                    component_type = normalizedName
                });
                return false;
            }

            if (matches.Length > 1 && !matches.Any(type => string.Equals(type.FullName, normalizedName, StringComparison.Ordinal)))
            {
                error = ToolResult.Error("duplicate_name", $"组件类型名 '{normalizedName}' 对应多个候选，请改用完整类型名。", new
                {
                    component_type = normalizedName,
                    candidates = matches.Select(GetComponentTypeName).ToArray()
                });
                return false;
            }

            componentType = matches.FirstOrDefault(type => string.Equals(type.FullName, normalizedName, StringComparison.Ordinal)) ?? matches[0];
            if (componentType == typeof(Transform) || componentType.IsAbstract || componentType.IsGenericTypeDefinition)
            {
                error = ToolResult.Error("invalid_parameter", $"组件类型 '{normalizedName}' 不能用于当前操作。", new
                {
                    component_type = normalizedName,
                    resolved = GetComponentTypeName(componentType)
                });
                componentType = null;
                return false;
            }

            return true;
        }

        static bool TryAddComponents(GameObject gameObject, IEnumerable<Type> componentTypes, List<object> summaries, out ToolResult error)
        {
            error = null;
            foreach (var componentType in componentTypes)
            {
                if (!CanAddComponent(gameObject, componentType, out error))
                {
                    return false;
                }

                var component = gameObject.AddComponent(componentType);
                if (component == null)
                {
                    error = ToolResult.Error("tool_execution_failed", $"添加组件 '{GetComponentTypeName(componentType)}' 失败。", new
                    {
                        target = GetHierarchyPath(gameObject.transform),
                        component_type = GetComponentTypeName(componentType)
                    });
                    return false;
                }

                summaries.Add(CreateComponentSummary(component));
            }

            return true;
        }

        static bool TryRemoveComponents(GameObject gameObject, IEnumerable<Type> componentTypes, List<object> summaries, out ToolResult error)
        {
            error = null;
            foreach (var componentType in componentTypes)
            {
                var components = gameObject.GetComponents(componentType)
                    .Where(component => component != null)
                    .Cast<Component>()
                    .ToArray();
                if (components.Length == 0)
                {
                    error = ToolResult.Error("not_found", $"目标对象上未找到组件 '{GetComponentTypeName(componentType)}'。", new
                    {
                        target = GetHierarchyPath(gameObject.transform),
                        component_type = GetComponentTypeName(componentType)
                    });
                    return false;
                }

                foreach (var component in components)
                {
                    summaries.Add(CreateComponentSummary(component));
                    Object.DestroyImmediate(component);
                }
            }

            return true;
        }

        static bool CanAddComponent(GameObject gameObject, Type componentType, out ToolResult error)
        {
            error = null;
            if (Attribute.IsDefined(componentType, typeof(DisallowMultipleComponent)) && gameObject.GetComponent(componentType) != null)
            {
                error = ToolResult.Error("not_allowed", $"目标对象上已存在不允许重复的组件 '{GetComponentTypeName(componentType)}'。", new
                {
                    target = GetHierarchyPath(gameObject.transform),
                    component_type = GetComponentTypeName(componentType)
                });
                return false;
            }

            return true;
        }

        static bool TryParseChildCreateSpecs(IDictionary<string, object> args, out List<ChildCreateSpec> specs, out ToolResult error)
        {
            specs = new List<ChildCreateSpec>();
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, "create_child", out var hasValue, out var rawValue, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            if (rawValue is IDictionary<string, object> dictionary)
            {
                return TryParseChildCreateSpec(dictionary, 0, specs, out error);
            }

            if (rawValue is IList list)
            {
                for (var index = 0; index < list.Count; index++)
                {
                    if (!(list[index] is IDictionary<string, object> item))
                    {
                        error = ToolResult.Error("invalid_parameter", "参数 'create_child' 数组项必须是对象。", new
                        {
                            parameter = "create_child",
                            index
                        });
                        specs.Clear();
                        return false;
                    }

                    if (!TryParseChildCreateSpec(item, index, specs, out error))
                    {
                        specs.Clear();
                        return false;
                    }
                }

                return true;
            }

            error = ToolResult.Error("invalid_parameter", "参数 'create_child' 必须是对象或对象数组。", new
            {
                parameter = "create_child",
                actual_type = rawValue.GetType().Name
            });
            return false;
        }

        static bool TryParseChildCreateSpec(IDictionary<string, object> dictionary, int index, List<ChildCreateSpec> specs, out ToolResult error)
        {
            error = null;
            var spec = new ChildCreateSpec();

            if (!ArgsHelper.TryGetRequired(dictionary, "name", out string name, out error))
            {
                return false;
            }

            spec.Name = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(spec.Name))
            {
                error = ToolResult.Error("invalid_parameter", "create_child.name 不能为空。", new
                {
                    parameter = "create_child",
                    index
                });
                return false;
            }

            if (!TryGetOptionalNonEmptyString(dictionary, "parent", out _, out var parentPath, out error))
            {
                return false;
            }

            spec.ParentPath = parentPath;

            if (!TryGetOptionalVector3(dictionary, "position", out var hasPosition, out var position, out error))
            {
                return false;
            }

            spec.HasPosition = hasPosition;
            spec.Position = position;

            if (!TryGetOptionalVector3(dictionary, "rotation", out var hasRotation, out var rotation, out error))
            {
                return false;
            }

            spec.HasRotation = hasRotation;
            spec.Rotation = rotation;

            if (!TryGetOptionalVector3(dictionary, "scale", out var hasScale, out var scale, out error))
            {
                return false;
            }

            spec.HasScale = hasScale;
            spec.Scale = scale;

            if (!TryGetOptionalBoolean(dictionary, "set_active", out var hasSetActive, out var setActive, out error))
            {
                return false;
            }

            spec.HasSetActive = hasSetActive;
            spec.SetActive = setActive;

            if (!TryGetOptionalNonEmptyString(dictionary, "tag", out var hasTag, out var tagValue, out error))
            {
                return false;
            }

            if (hasTag)
            {
                if (!TryValidateTag(tagValue, out error))
                {
                    return false;
                }

                spec.Tag = tagValue;
            }

            if (!TryGetOptionalNonEmptyString(dictionary, "layer", out var hasLayer, out var layerValue, out error))
            {
                return false;
            }

            if (hasLayer)
            {
                if (!TryResolveLayer(layerValue, out var layer, out error))
                {
                    return false;
                }

                spec.Layer = layer;
                spec.HasLayer = true;
            }

            if (!TryGetOptionalStringArray(dictionary, "components_to_add", out _, out var componentNames, out error))
            {
                return false;
            }

            spec.ComponentNamesToAdd = componentNames;

            if (!TryResolveComponentTypes(componentNames, out var componentTypes, out error))
            {
                return false;
            }

            spec.ComponentTypesToAdd = componentTypes;

            specs.Add(spec);
            return true;
        }

        static bool TryParseDeleteChildPaths(IDictionary<string, object> args, out List<string> childPaths, out ToolResult error)
        {
            childPaths = new List<string>();
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, "delete_child", out var hasValue, out var rawValue, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            if (rawValue is string text)
            {
                if (!TryAddDeleteChildPath(text, childPaths, out error))
                {
                    childPaths.Clear();
                    return false;
                }

                return true;
            }

            if (rawValue is IList list)
            {
                for (var index = 0; index < list.Count; index++)
                {
                    if (!TryAddDeleteChildPath(list[index]?.ToString(), childPaths, out error))
                    {
                        childPaths.Clear();
                        return false;
                    }
                }

                return true;
            }

            error = ToolResult.Error("invalid_parameter", "参数 'delete_child' 必须是字符串或字符串数组。", new
            {
                parameter = "delete_child",
                actual_type = rawValue.GetType().Name
            });
            return false;
        }

        static bool TryAddDeleteChildPath(string value, List<string> childPaths, out ToolResult error)
        {
            error = null;
            var normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = ToolResult.Error("invalid_parameter", "delete_child 包含空路径。", new
                {
                    parameter = "delete_child"
                });
                return false;
            }

            childPaths.Add(normalized);
            return true;
        }

        static bool TryCreateChildren(GameObject prefabRoot, IEnumerable<ChildCreateSpec> specs, List<object> createdChildren, out ToolResult error)
        {
            error = null;
            foreach (var spec in specs)
            {
                var parent = prefabRoot.transform;
                if (!string.IsNullOrWhiteSpace(spec.ParentPath))
                {
                    if (!TryFindChildTransform(prefabRoot.transform, spec.ParentPath, out parent, out error))
                    {
                        return false;
                    }
                }

                var child = new GameObject(spec.Name);
                child.transform.SetParent(parent, false);

                if (spec.HasPosition)
                {
                    child.transform.localPosition = spec.Position;
                }

                if (spec.HasRotation)
                {
                    child.transform.localRotation = Quaternion.Euler(spec.Rotation);
                }

                if (spec.HasScale)
                {
                    child.transform.localScale = spec.Scale;
                }

                if (spec.HasSetActive)
                {
                    child.SetActive(spec.SetActive);
                }

                if (!string.IsNullOrWhiteSpace(spec.Tag))
                {
                    child.tag = spec.Tag;
                }

                if (spec.HasLayer)
                {
                    child.layer = spec.Layer;
                }

                var addedComponents = new List<object>();
                if (!TryAddComponents(child, spec.ComponentTypesToAdd, addedComponents, out error))
                {
                    Object.DestroyImmediate(child);
                    return false;
                }

                createdChildren.Add(new
                {
                    name = child.name,
                    path = GetHierarchyPath(child.transform),
                    parent = GetHierarchyPath(parent),
                    added_components = addedComponents.ToArray()
                });
            }

            return true;
        }

        static bool TryDeleteChildren(GameObject prefabRoot, IEnumerable<string> childPaths, List<object> deletedChildren, out ToolResult error)
        {
            error = null;
            foreach (var childPath in childPaths)
            {
                if (!TryFindChildTransform(prefabRoot.transform, childPath, out var child, out error))
                {
                    return false;
                }

                if (child == prefabRoot.transform)
                {
                    error = ToolResult.Error("not_allowed", "不允许删除 prefab 根节点。", new
                    {
                        path = childPath
                    });
                    return false;
                }

                deletedChildren.Add(new
                {
                    name = child.name,
                    path = GetHierarchyPath(child)
                });
                Object.DestroyImmediate(child.gameObject);
            }

            return true;
        }

        static bool TryFindChildTransform(Transform root, string path, out Transform result, out ToolResult error)
        {
            result = null;
            error = null;
            var normalizedPath = NormalizeHierarchyPath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                error = ToolResult.Error("invalid_parameter", "层级路径不能为空。", new
                {
                    path
                });
                return false;
            }

            var rootPath = NormalizeHierarchyPath(GetHierarchyPath(root));
            if (string.Equals(normalizedPath, rootPath, StringComparison.Ordinal))
            {
                result = root;
                return true;
            }

            if (normalizedPath.StartsWith(rootPath + "/", StringComparison.Ordinal))
            {
                normalizedPath = normalizedPath.Substring(rootPath.Length + 1);
            }

            var segments = normalizedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                result = root;
                return true;
            }

            if (segments.Length == 1)
            {
                var matches = root.GetComponentsInChildren<Transform>(true)
                    .Where(candidate => candidate != null && candidate != root)
                    .Where(candidate => string.Equals(candidate.name, segments[0], StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length == 1)
                {
                    result = matches[0];
                    return true;
                }

                if (matches.Length > 1)
                {
                    error = ToolResult.Error("duplicate_name", $"子节点名 '{segments[0]}' 对应多个候选，请提供完整层级路径。", new
                    {
                        path,
                        candidates = matches.Select(GetHierarchyPath).ToArray()
                    });
                    return false;
                }
            }

            var current = root;
            foreach (var segment in segments)
            {
                current = current.Find(segment);
                if (current == null)
                {
                    error = ToolResult.Error("not_found", $"未找到子节点路径 '{path}'。", new
                    {
                        path,
                        root = rootPath
                    });
                    return false;
                }
            }

            result = current;
            return true;
        }

        static string NormalizeHierarchyPath(string path)
        {
            var normalized = path?.Replace('\\', '/').Trim() ?? string.Empty;
            while (normalized.Contains("//", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
            }

            return normalized.Trim('/');
        }

        static string GetHierarchyPath(Transform transform)
        {
            var names = new List<string>();
            var current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        static string GetHierarchyPath(GameObject gameObject)
        {
            return gameObject == null ? string.Empty : GetHierarchyPath(gameObject.transform);
        }

        static object CreateComponentSummary(Component component)
        {
            return new
            {
                type = GetComponentTypeName(component.GetType())
            };
        }

        static string GetComponentTypeName(Type componentType)
        {
            return componentType.FullName ?? componentType.Name;
        }

        static bool TryResolveLayer(string layerValue, out int layer, out ToolResult error)
        {
            layer = 0;
            error = null;

            if (int.TryParse(layerValue, out layer))
            {
                if (layer < 0 || layer > 31)
                {
                    error = ToolResult.Error("invalid_parameter", "Layer 索引必须在 0 到 31 之间。", new
                    {
                        layer = layerValue
                    });
                    return false;
                }

                return true;
            }

            layer = LayerMask.NameToLayer(layerValue);
            if (layer < 0)
            {
                error = ToolResult.Error("invalid_parameter", $"未找到 Layer '{layerValue}'。", new
                {
                    layer = layerValue
                });
                return false;
            }

            return true;
        }

        static bool TryValidateTag(string tagValue, out ToolResult error)
        {
            error = null;
            if (InternalEditorUtility.tags.Contains(tagValue))
            {
                return true;
            }

            error = ToolResult.Error("invalid_parameter", $"未找到 Tag '{tagValue}'。", new
            {
                tag = tagValue
            });
            return false;
        }

        static bool TryConvertToFloat(object value, out float result)
        {
            try
            {
                result = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                result = 0F;
                return false;
            }
        }

        sealed class ChildCreateSpec
        {
            public string Name { get; set; } = string.Empty;

            public string ParentPath { get; set; } = string.Empty;

            public bool HasPosition { get; set; }

            public Vector3 Position { get; set; }

            public bool HasRotation { get; set; }

            public Vector3 Rotation { get; set; }

            public bool HasScale { get; set; }

            public Vector3 Scale { get; set; }

            public bool HasSetActive { get; set; }

            public bool SetActive { get; set; }

            public string Tag { get; set; } = string.Empty;

            public bool HasLayer { get; set; }

            public int Layer { get; set; }

            public List<string> ComponentNamesToAdd { get; set; } = new List<string>();

            public List<Type> ComponentTypesToAdd { get; set; } = new List<Type>();
        }
    }
}
