using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool(
        "gameobject",
        Description = "Create and modify scene GameObjects",
        Mode = ToolMode.Both,
        Capabilities = ToolCapabilities.SceneMutation,
        Category = "editor")]
    public sealed class GameObjectTool : IUnityCliTool
    {
        static readonly string[] SupportedActions =
        {
            "create",
            "modify",
            "delete",
            "duplicate",
            "move_relative",
            "look_at"
        };

        static readonly string[] SupportedPrimitiveTypes =
        {
            "Cube",
            "Sphere",
            "Capsule",
            "Cylinder",
            "Plane",
            "Quad"
        };

        static readonly string[] SupportedDirections =
        {
            "left",
            "right",
            "up",
            "down",
            "forward",
            "back"
        };

        static readonly Type[] ComponentTypes = TypeCache.GetTypesDerivedFrom<Component>()
            .Where(type => type != null)
            .ToArray();

        public string Id => "gameobject";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Create and modify scene GameObjects",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.SceneMutation,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "action",
                        type = "string",
                        description = "GameObject action name",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "target",
                        type = "string",
                        description = "Target scene GameObject reference for non-create actions",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "name",
                        type = "string",
                        description = "Name for create, or rename alias for modify",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "new_name",
                        type = "string",
                        description = "Optional rename value for modify/duplicate",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "primitive_type",
                        type = "string",
                        description = "Primitive type for create: Cube/Sphere/Capsule/Cylinder/Plane/Quad",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "prefab_path",
                        type = "string",
                        description = "Prefab asset path under Assets/ for create",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "position",
                        type = "array",
                        description = "World position [x,y,z]",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "rotation",
                        type = "array",
                        description = "World rotation euler angles [x,y,z]",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "scale",
                        type = "array",
                        description = "Local scale [x,y,z]",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "parent",
                        type = "string",
                        description = "Optional parent scene GameObject reference for create",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "components_to_add",
                        type = "array",
                        description = "Component type names to add",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "components_to_remove",
                        type = "array",
                        description = "Component type names to remove",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "set_active",
                        type = "boolean",
                        description = "Optional active state for modify",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "layer",
                        type = "string",
                        description = "Layer index or layer name for modify",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "offset",
                        type = "array",
                        description = "World-space duplicate offset [x,y,z]",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "direction",
                        type = "string",
                        description = "Relative move direction: left/right/up/down/forward/back",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "distance",
                        type = "number",
                        description = "Relative move distance",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "look_at_target",
                        type = "string",
                        description = "Target scene GameObject reference for look_at",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "search_method",
                        type = "string",
                        description = "Optional target resolver override for target",
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
                case "create":
                    return HandleCreate(args, context);
                case "modify":
                    return HandleModify(args, context);
                case "delete":
                    return HandleDelete(args, context);
                case "duplicate":
                    return HandleDuplicate(args, context);
                case "move_relative":
                    return HandleMoveRelative(args, context);
                case "look_at":
                    return HandleLookAt(args, context);
                default:
                    return ToolResult.Error("invalid_parameter", $"不支持的 gameobject 操作 '{action}'。", new
                    {
                        parameter = "action",
                        value = action,
                        supportedActions = SupportedActions
                    });
            }
        }

        static ToolResult HandleCreate(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "name", out string name, out error))
            {
                return error;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return ToolResult.Error("invalid_parameter", "参数 'name' 不能为空。", new
                {
                    parameter = "name"
                });
            }

            if (!TryGetOptionalNonEmptyString(args, "primitive_type", out var hasPrimitiveType, out var primitiveTypeText, out error))
            {
                return error;
            }

            if (!TryGetOptionalNonEmptyString(args, "prefab_path", out var hasPrefabPath, out var prefabPath, out error))
            {
                return error;
            }

            if (hasPrimitiveType && hasPrefabPath)
            {
                return ToolResult.Error("invalid_parameter", "参数 'primitive_type' 与 'prefab_path' 不能同时提供。", new
                {
                    parameters = new[] { "primitive_type", "prefab_path" }
                });
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

            if (!TryResolveReference(args, "parent", false, null, out var parent, out error))
            {
                return error;
            }

            if (!TryGetOptionalStringArray(args, "components_to_add", out _, out var componentNamesToAdd, out error))
            {
                return error;
            }

            if (!TryResolveComponentTypes(componentNamesToAdd, true, out var componentTypesToAdd, out error))
            {
                return error;
            }

            var createMode = "empty";
            var primitiveType = PrimitiveType.Cube;
            if (hasPrimitiveType)
            {
                if (!TryResolvePrimitiveType(primitiveTypeText, out primitiveType, out error))
                {
                    return error;
                }

                createMode = "primitive";
            }

            GameObject prefabAsset = null;
            string normalizedPrefabPath = null;
            if (hasPrefabPath)
            {
                if (!PathGuard.TryNormalizeAssetPath(prefabPath, out normalizedPrefabPath, out error))
                {
                    return error;
                }

                prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(normalizedPrefabPath);
                if (prefabAsset == null)
                {
                    return ToolResult.Error("not_found", "未找到参数 'prefab_path' 指定的 GameObject 资源。", new
                    {
                        parameter = "prefab_path",
                        path = normalizedPrefabPath
                    });
                }

                createMode = "prefab";
            }

            var undoGroup = BeginUndoGroup("Create GameObject");
            try
            {
                GameObject gameObject;
                if (prefabAsset != null)
                {
                    gameObject = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                    if (gameObject == null)
                    {
                        Undo.RevertAllDownToGroup(undoGroup);
                        return ToolResult.Error("not_allowed", "Unity 未能实例化指定 Prefab。", new
                        {
                            action = "create",
                            prefab_path = normalizedPrefabPath
                        });
                    }

                    Undo.RegisterCreatedObjectUndo(gameObject, "Create GameObject");
                }
                else if (hasPrimitiveType)
                {
                    gameObject = GameObject.CreatePrimitive(primitiveType);
                    Undo.RegisterCreatedObjectUndo(gameObject, "Create GameObject");
                }
                else
                {
                    gameObject = new GameObject();
                    Undo.RegisterCreatedObjectUndo(gameObject, "Create GameObject");
                }

                gameObject.name = name.Trim();

                if (parent != null)
                {
                    Undo.SetTransformParent(gameObject.transform, parent.transform, "Create GameObject");
                }

                if (hasPosition)
                {
                    gameObject.transform.position = position;
                }

                if (hasRotation)
                {
                    gameObject.transform.rotation = Quaternion.Euler(rotation);
                }

                if (hasScale)
                {
                    gameObject.transform.localScale = scale;
                }

                var addedComponents = new List<object>();
                if (!TryAddComponents(gameObject, componentTypesToAdd, addedComponents, out error))
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    return error;
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "create",
                    create_mode = createMode,
                    primitive_type = hasPrimitiveType ? primitiveType.ToString() : null,
                    prefab_path = normalizedPrefabPath,
                    parent = parent != null ? CreateGameObjectReference(parent) : null,
                    added_components = addedComponents.ToArray(),
                    gameObject = CreateGameObjectSummary(gameObject)
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 create 时发生异常：{exception.Message}", new
                {
                    action = "create",
                    exception = exception.GetType().FullName
                });
            }
        }

        static ToolResult HandleModify(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveReference(args, "target", true, "search_method", out var gameObject, out error))
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

            if (!TryGetRenameValue(args, out var hasRename, out var renameValue, out error))
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

            if (!TryGetOptionalStringArray(args, "components_to_add", out _, out var componentNamesToAdd, out error))
            {
                return error;
            }

            if (!TryGetOptionalStringArray(args, "components_to_remove", out _, out var componentNamesToRemove, out error))
            {
                return error;
            }

            if (!TryResolveComponentTypes(componentNamesToAdd, true, out var componentTypesToAdd, out error))
            {
                return error;
            }

            if (!TryResolveComponentTypes(componentNamesToRemove, false, out var componentTypesToRemove, out error))
            {
                return error;
            }

            if (!TryCollectComponentsToRemove(gameObject, componentTypesToRemove, out var removableComponents, out var removedComponentSummaries, out error))
            {
                return error;
            }

            if (!hasPosition
                && !hasRotation
                && !hasScale
                && !hasSetActive
                && !hasRename
                && !hasLayer
                && componentTypesToAdd.Count == 0
                && removableComponents.Count == 0)
            {
                return ToolResult.Error("missing_parameter", "modify 至少需要一个修改参数。", new
                {
                    supported = new[]
                    {
                        "position",
                        "rotation",
                        "scale",
                        "set_active",
                        "name",
                        "new_name",
                        "layer",
                        "components_to_add",
                        "components_to_remove"
                    }
                });
            }

            var undoGroup = BeginUndoGroup("Modify GameObject");
            try
            {
                Undo.RecordObject(gameObject, "Modify GameObject");
                Undo.RecordObject(gameObject.transform, "Modify GameObject");

                if (hasRename)
                {
                    gameObject.name = renameValue;
                }

                if (hasSetActive)
                {
                    gameObject.SetActive(setActive);
                }

                if (hasLayer)
                {
                    gameObject.layer = layer;
                }

                if (hasPosition)
                {
                    gameObject.transform.position = position;
                }

                if (hasRotation)
                {
                    gameObject.transform.rotation = Quaternion.Euler(rotation);
                }

                if (hasScale)
                {
                    gameObject.transform.localScale = scale;
                }

                var addedComponents = new List<object>();
                if (!TryAddComponents(gameObject, componentTypesToAdd, addedComponents, out error))
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    return error;
                }

                foreach (var component in removableComponents)
                {
                    Undo.DestroyObjectImmediate(component);
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "modify",
                    added_components = addedComponents.ToArray(),
                    removed_components = removedComponentSummaries.ToArray(),
                    gameObject = CreateGameObjectSummary(gameObject)
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 modify 时发生异常：{exception.Message}", new
                {
                    action = "modify",
                    exception = exception.GetType().FullName,
                    target = CreateGameObjectReference(gameObject)
                });
            }
        }

        static ToolResult HandleDelete(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveReference(args, "target", true, "search_method", out var gameObject, out error))
            {
                return error;
            }

            var deletedSummary = CreateGameObjectSummary(gameObject);
            var undoGroup = BeginUndoGroup("Delete GameObject");
            try
            {
                Undo.DestroyObjectImmediate(gameObject);
                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "delete",
                    deleted = true,
                    gameObject = deletedSummary
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 delete 时发生异常：{exception.Message}", new
                {
                    action = "delete",
                    exception = exception.GetType().FullName,
                    target = deletedSummary
                });
            }
        }

        static ToolResult HandleDuplicate(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveReference(args, "target", true, "search_method", out var gameObject, out error))
            {
                return error;
            }

            if (!TryGetOptionalNonEmptyString(args, "new_name", out var hasNewName, out var newName, out error))
            {
                return error;
            }

            if (!TryGetOptionalVector3(args, "offset", out var hasOffset, out var offset, out error))
            {
                return error;
            }

            var undoGroup = BeginUndoGroup("Duplicate GameObject");
            try
            {
                var parent = gameObject.transform.parent;
                var duplicate = Object.Instantiate(gameObject, parent);
                Undo.RegisterCreatedObjectUndo(duplicate, "Duplicate GameObject");
                duplicate.transform.SetSiblingIndex(gameObject.transform.GetSiblingIndex() + 1);

                if (hasNewName)
                {
                    duplicate.name = newName;
                }

                if (hasOffset)
                {
                    duplicate.transform.position += offset;
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "duplicate",
                    offset = hasOffset ? CreateVector3Array(offset) : null,
                    source = CreateGameObjectReference(gameObject),
                    gameObject = CreateGameObjectSummary(duplicate)
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 duplicate 时发生异常：{exception.Message}", new
                {
                    action = "duplicate",
                    exception = exception.GetType().FullName,
                    target = CreateGameObjectReference(gameObject)
                });
            }
        }

        static ToolResult HandleMoveRelative(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveReference(args, "target", true, "search_method", out var gameObject, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "direction", out string direction, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "distance", out float distance, out error))
            {
                return error;
            }

            if (!TryGetMoveDelta(gameObject.transform, direction, distance, out var delta, out error))
            {
                return error;
            }

            var undoGroup = BeginUndoGroup("Move GameObject Relative");
            try
            {
                Undo.RecordObject(gameObject.transform, "Move GameObject Relative");
                gameObject.transform.position += delta;
                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "move_relative",
                    direction = direction.Trim().ToLowerInvariant(),
                    distance,
                    delta = CreateVector3Array(delta),
                    gameObject = CreateGameObjectSummary(gameObject)
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 move_relative 时发生异常：{exception.Message}", new
                {
                    action = "move_relative",
                    exception = exception.GetType().FullName,
                    target = CreateGameObjectReference(gameObject)
                });
            }
        }

        static ToolResult HandleLookAt(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveReference(args, "target", true, "search_method", out var gameObject, out error))
            {
                return error;
            }

            if (!TryResolveReference(args, "look_at_target", true, null, out var lookAtTarget, out error))
            {
                return error;
            }

            var undoGroup = BeginUndoGroup("Look At GameObject");
            try
            {
                Undo.RecordObject(gameObject.transform, "Look At GameObject");
                gameObject.transform.LookAt(lookAtTarget.transform.position);
                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "look_at",
                    look_at_target = CreateGameObjectReference(lookAtTarget),
                    gameObject = CreateGameObjectSummary(gameObject)
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 look_at 时发生异常：{exception.Message}", new
                {
                    action = "look_at",
                    exception = exception.GetType().FullName,
                    target = CreateGameObjectReference(gameObject),
                    look_at_target = CreateGameObjectReference(lookAtTarget)
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

        static int BeginUndoGroup(string name)
        {
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return undoGroup;
        }

        static bool TryResolveReference(
            IDictionary<string, object> args,
            string parameterName,
            bool required,
            string searchMethodParameterName,
            out GameObject gameObject,
            out ToolResult error)
        {
            gameObject = null;
            error = null;

            if (args == null)
            {
                error = ToolResult.Error("invalid_parameter", "参数字典不能为空。", nameof(args));
                return false;
            }

            if (!ArgsHelper.TryGetRawOptional(args, parameterName, out var hasReference, out var rawReference, out error))
            {
                return false;
            }

            if (!hasReference)
            {
                if (!required)
                {
                    return true;
                }

                error = ToolResult.Error("missing_parameter", $"缺少参数 '{parameterName}'。", new
                {
                    parameter = parameterName
                });
                return false;
            }

            Dictionary<string, object> resolverArgs;
            if (rawReference is IDictionary<string, object> nestedDictionary)
            {
                resolverArgs = new Dictionary<string, object>(nestedDictionary, StringComparer.Ordinal);
                if (!resolverArgs.ContainsKey("instanceID")
                    && resolverArgs.TryGetValue("instanceId", out var instanceIdValue)
                    && instanceIdValue != null)
                {
                    resolverArgs["instanceID"] = instanceIdValue;
                }
            }
            else
            {
                resolverArgs = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["target"] = rawReference
                };

                if (!string.IsNullOrWhiteSpace(searchMethodParameterName)
                    && ArgsHelper.TryGetRawOptional(args, searchMethodParameterName, out var hasSearchMethod, out var searchMethodValue, out error)
                    && hasSearchMethod)
                {
                    resolverArgs["search_method"] = searchMethodValue;
                }
            }

            if (!GameObjectResolver.TryResolve(resolverArgs, out gameObject, out error))
            {
                return false;
            }

            if (!IsSceneObject(gameObject))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 必须引用场景中的 GameObject。", new
                {
                    parameter = parameterName,
                    value = rawReference
                });
                gameObject = null;
                return false;
            }

            return true;
        }

        static bool TryGetOptionalVector3(IDictionary<string, object> args, string parameterName, out bool hasValue, out Vector3 value, out ToolResult error)
        {
            hasValue = false;
            value = default;
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, parameterName, out hasValue, out var rawValue, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            if (!ArgsHelper.TryGetArray(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [parameterName] = rawValue
                }, parameterName, out var elements, out error))
            {
                return false;
            }

            if (elements.Length != 3)
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 必须是长度为 3 的数值数组。", new
                {
                    parameter = parameterName,
                    length = elements.Length
                });
                return false;
            }

            if (!TryConvertToFloat(elements[0], out var x)
                || !TryConvertToFloat(elements[1], out var y)
                || !TryConvertToFloat(elements[2], out var z))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 必须仅包含数值。", new
                {
                    parameter = parameterName,
                    value = elements
                });
                return false;
            }

            value = new Vector3(x, y, z);
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

            return ArgsHelper.TryGetOptional(args, parameterName, false, out value, out error);
        }

        static bool TryGetOptionalNonEmptyString(IDictionary<string, object> args, string parameterName, out bool hasValue, out string value, out ToolResult error)
        {
            hasValue = false;
            value = string.Empty;
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, parameterName, out hasValue, out _, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            if (!ArgsHelper.TryGetOptional(args, parameterName, string.Empty, out value, out error))
            {
                return false;
            }

            value = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 不能为空字符串。", new
                {
                    parameter = parameterName
                });
                return false;
            }

            return true;
        }

        static bool TryGetOptionalStringArray(IDictionary<string, object> args, string parameterName, out bool hasValue, out List<string> values, out ToolResult error)
        {
            hasValue = false;
            values = new List<string>();
            error = null;

            if (!ArgsHelper.TryGetRawOptional(args, parameterName, out hasValue, out var rawValue, out error))
            {
                return false;
            }

            if (!hasValue)
            {
                return true;
            }

            if (!ArgsHelper.TryGetArray(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [parameterName] = rawValue
                }, parameterName, out var elements, out error))
            {
                return false;
            }

            for (var index = 0; index < elements.Length; index++)
            {
                var value = elements[index]?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 第 {index} 项不能为空。", new
                    {
                        parameter = parameterName,
                        index,
                        value = elements[index]
                    });
                    values.Clear();
                    return false;
                }

                values.Add(value);
            }

            return true;
        }

        static bool TryGetRenameValue(IDictionary<string, object> args, out bool hasValue, out string value, out ToolResult error)
        {
            hasValue = false;
            value = string.Empty;
            error = null;

            if (!TryGetOptionalNonEmptyString(args, "name", out var hasName, out var nameValue, out error))
            {
                return false;
            }

            if (!TryGetOptionalNonEmptyString(args, "new_name", out var hasNewName, out var newNameValue, out error))
            {
                return false;
            }

            if (!hasName && !hasNewName)
            {
                return true;
            }

            if (hasName && hasNewName && !string.Equals(nameValue, newNameValue, StringComparison.Ordinal))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'name' 与 'new_name' 同时提供时必须一致。", new
                {
                    name = nameValue,
                    new_name = newNameValue
                });
                return false;
            }

            hasValue = true;
            value = hasNewName ? newNameValue : nameValue;
            return true;
        }

        static bool TryResolvePrimitiveType(string primitiveTypeText, out PrimitiveType primitiveType, out ToolResult error)
        {
            primitiveType = PrimitiveType.Cube;
            error = null;

            if (!Enum.TryParse(primitiveTypeText, true, out primitiveType))
            {
                error = ToolResult.Error("invalid_parameter", $"不支持的 primitive_type: '{primitiveTypeText}'。", new
                {
                    parameter = "primitive_type",
                    value = primitiveTypeText,
                    supported = SupportedPrimitiveTypes
                });
                return false;
            }

            if (!SupportedPrimitiveTypes.Contains(primitiveType.ToString(), StringComparer.Ordinal))
            {
                error = ToolResult.Error("invalid_parameter", $"不支持的 primitive_type: '{primitiveTypeText}'。", new
                {
                    parameter = "primitive_type",
                    value = primitiveTypeText,
                    supported = SupportedPrimitiveTypes
                });
                return false;
            }

            return true;
        }

        static bool TryResolveLayer(string layerValue, out int layer, out ToolResult error)
        {
            layer = -1;
            error = null;

            if (int.TryParse(layerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericLayer))
            {
                if (numericLayer < 0 || numericLayer > 31)
                {
                    error = ToolResult.Error("invalid_parameter", "层号必须在 0~31 范围内。", new
                    {
                        parameter = "layer",
                        value = layerValue
                    });
                    return false;
                }

                layer = numericLayer;
                return true;
            }

            var resolvedLayer = LayerMask.NameToLayer(layerValue);
            if (resolvedLayer < 0)
            {
                error = ToolResult.Error("invalid_parameter", $"未找到层名 '{layerValue}'。", new
                {
                    parameter = "layer",
                    value = layerValue
                });
                return false;
            }

            layer = resolvedLayer;
            return true;
        }

        static bool TryResolveComponentTypes(IReadOnlyList<string> componentNames, bool forAdd, out List<Type> componentTypes, out ToolResult error)
        {
            componentTypes = new List<Type>();
            error = null;

            foreach (var componentName in componentNames)
            {
                if (!TryResolveComponentType(componentName, forAdd, out var componentType, out error))
                {
                    componentTypes.Clear();
                    return false;
                }

                componentTypes.Add(componentType);
            }

            return true;
        }

        static bool TryResolveComponentType(string componentName, bool forAdd, out Type componentType, out ToolResult error)
        {
            componentType = null;
            error = null;

            var matches = ComponentTypes
                .Where(type => string.Equals(type.FullName, componentName, StringComparison.Ordinal)
                    || string.Equals(type.Name, componentName, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length == 0)
            {
                error = ToolResult.Error("invalid_parameter", $"未找到组件类型 '{componentName}'。", new
                {
                    component_type = componentName
                });
                return false;
            }

            if (matches.Length > 1 && !matches.Any(type => string.Equals(type.FullName, componentName, StringComparison.Ordinal)))
            {
                error = ToolResult.Error("duplicate_name", $"组件类型名 '{componentName}' 对应多个候选，请改用完整类型名。", new
                {
                    component_type = componentName,
                    candidates = matches.Select(type => type.FullName).ToArray()
                });
                return false;
            }

            componentType = matches.FirstOrDefault(type => string.Equals(type.FullName, componentName, StringComparison.Ordinal))
                ?? matches[0];

            if (componentType == typeof(Transform))
            {
                error = ToolResult.Error("invalid_parameter", "不支持手动添加或移除 Transform 组件。", new
                {
                    component_type = componentName
                });
                componentType = null;
                return false;
            }

            if (componentType.IsAbstract || componentType.IsGenericTypeDefinition)
            {
                error = ToolResult.Error("invalid_parameter", forAdd
                    ? $"组件类型 '{componentName}' 不可实例化。"
                    : $"组件类型 '{componentName}' 不能用于移除，请提供具体组件类型。", new
                {
                    component_type = componentName,
                    resolved = componentType.FullName
                });
                componentType = null;
                return false;
            }

            return true;
        }

        static bool TryCollectComponentsToRemove(GameObject gameObject, IReadOnlyList<Type> componentTypes, out List<Component> components, out List<object> summaries, out ToolResult error)
        {
            components = new List<Component>();
            summaries = new List<object>();
            error = null;
            var componentIds = new HashSet<int>();

            foreach (var componentType in componentTypes)
            {
                var matches = gameObject.GetComponents(componentType)
                    .Where(component => component != null)
                    .Cast<Component>()
                    .ToArray();
                if (matches.Length == 0)
                {
                    error = ToolResult.Error("not_found", $"目标对象上未找到组件 '{componentType.FullName ?? componentType.Name}'。", new
                    {
                        target = CreateGameObjectReference(gameObject),
                        component_type = componentType.FullName ?? componentType.Name
                    });
                    components.Clear();
                    summaries.Clear();
                    return false;
                }

                foreach (var component in matches)
                {
                    if (!componentIds.Add(component.GetInstanceID()))
                    {
                        continue;
                    }

                    components.Add(component);
                    summaries.Add(CreateComponentSummary(component));
                }
            }

            return true;
        }

        static bool TryAddComponents(GameObject gameObject, IReadOnlyList<Type> componentTypes, ICollection<object> addedComponents, out ToolResult error)
        {
            error = null;
            foreach (var componentType in componentTypes)
            {
                if (!CanAddComponent(gameObject, componentType, out error))
                {
                    return false;
                }

                var component = Undo.AddComponent(gameObject, componentType);
                if (component == null)
                {
                    error = ToolResult.Error("not_allowed", $"Unity 未能添加组件 '{componentType.FullName ?? componentType.Name}'。", new
                    {
                        target = CreateGameObjectReference(gameObject),
                        component_type = componentType.FullName ?? componentType.Name
                    });
                    return false;
                }

                addedComponents.Add(CreateComponentSummary(component));
            }

            return true;
        }

        static bool CanAddComponent(GameObject gameObject, Type componentType, out ToolResult error)
        {
            error = null;
            if (componentType == null)
            {
                error = ToolResult.Error("invalid_parameter", "组件类型不能为空。", nameof(componentType));
                return false;
            }

            if (!typeof(Component).IsAssignableFrom(componentType))
            {
                error = ToolResult.Error("invalid_parameter", $"类型 '{componentType.FullName}' 不是 Unity 组件。", new
                {
                    component_type = componentType.FullName
                });
                return false;
            }

            if (Attribute.IsDefined(componentType, typeof(DisallowMultipleComponent)) && gameObject.GetComponent(componentType) != null)
            {
                error = ToolResult.Error("not_allowed", $"目标对象上已存在不允许重复的组件 '{componentType.FullName ?? componentType.Name}'。", new
                {
                    target = CreateGameObjectReference(gameObject),
                    component_type = componentType.FullName ?? componentType.Name
                });
                return false;
            }

            return true;
        }

        static bool TryGetMoveDelta(Transform transform, string direction, float distance, out Vector3 delta, out ToolResult error)
        {
            delta = default;
            error = null;

            if (!IsFinite(distance))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'distance' 必须是有限数值。", new
                {
                    parameter = "distance",
                    value = distance
                });
                return false;
            }

            if (string.IsNullOrWhiteSpace(direction))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'direction' 不能为空。", new
                {
                    parameter = "direction",
                    supported = SupportedDirections
                });
                return false;
            }

            var normalizedDirection = direction.Trim().ToLowerInvariant();
            switch (normalizedDirection)
            {
                case "left":
                    delta = -transform.right * distance;
                    return true;
                case "right":
                    delta = transform.right * distance;
                    return true;
                case "up":
                    delta = transform.up * distance;
                    return true;
                case "down":
                    delta = -transform.up * distance;
                    return true;
                case "forward":
                    delta = transform.forward * distance;
                    return true;
                case "back":
                    delta = -transform.forward * distance;
                    return true;
                default:
                    error = ToolResult.Error("invalid_parameter", $"不支持的 direction: '{direction}'。", new
                    {
                        parameter = "direction",
                        value = direction,
                        supported = SupportedDirections
                    });
                    return false;
            }
        }

        static bool TryConvertToFloat(object rawValue, out float value)
        {
            value = 0f;
            if (rawValue == null)
            {
                return false;
            }

            switch (rawValue)
            {
                case float floatValue:
                    value = floatValue;
                    return IsFinite(value);
                case double doubleValue:
                    value = (float)doubleValue;
                    return IsFinite(value);
                case int intValue:
                    value = intValue;
                    return IsFinite(value);
                case long longValue:
                    value = longValue;
                    return IsFinite(value);
                case short shortValue:
                    value = shortValue;
                    return IsFinite(value);
                case byte byteValue:
                    value = byteValue;
                    return IsFinite(value);
                case decimal decimalValue:
                    value = (float)decimalValue;
                    return IsFinite(value);
                case string stringValue:
                    return float.TryParse(stringValue.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
                        && IsFinite(value);
                default:
                    try
                    {
                        value = Convert.ToSingle(rawValue, CultureInfo.InvariantCulture);
                        return IsFinite(value);
                    }
                    catch
                    {
                        return false;
                    }
            }
        }

        static bool IsSceneObject(GameObject gameObject)
        {
            return gameObject != null
                && gameObject.scene.IsValid()
                && !EditorUtility.IsPersistent(gameObject)
                && StageUtility.GetStageHandle(gameObject) == StageUtility.GetMainStageHandle()
                && (gameObject.hideFlags & HideFlags.NotEditable) == 0
                && (gameObject.hideFlags & HideFlags.HideAndDontSave) == 0;
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static object CreateGameObjectSummary(GameObject gameObject)
        {
            var transform = gameObject.transform;
            return new
            {
                instanceId = gameObject.GetInstanceID(),
                instanceID = gameObject.GetInstanceID(),
                name = gameObject.name,
                path = GetHierarchyPath(gameObject),
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                tag = gameObject.tag,
                layer = gameObject.layer,
                layerName = LayerMask.LayerToName(gameObject.layer),
                parentInstanceId = transform.parent != null ? transform.parent.gameObject.GetInstanceID() : 0,
                parentInstanceID = transform.parent != null ? transform.parent.gameObject.GetInstanceID() : 0,
                scene = gameObject.scene.name,
                scenePath = gameObject.scene.path,
                position = CreateVector3Array(transform.position),
                rotation = CreateVector3Array(transform.eulerAngles),
                scale = CreateVector3Array(transform.localScale)
            };
        }

        static object CreateGameObjectReference(GameObject gameObject)
        {
            return new
            {
                instanceId = gameObject.GetInstanceID(),
                instanceID = gameObject.GetInstanceID(),
                name = gameObject.name,
                path = GetHierarchyPath(gameObject)
            };
        }

        static object CreateComponentSummary(Component component)
        {
            return new
            {
                instanceId = component.GetInstanceID(),
                instanceID = component.GetInstanceID(),
                type = component.GetType().FullName ?? component.GetType().Name,
                gameObject = CreateGameObjectReference(component.gameObject)
            };
        }

        static float[] CreateVector3Array(Vector3 value)
        {
            return new[] { value.x, value.y, value.z };
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
