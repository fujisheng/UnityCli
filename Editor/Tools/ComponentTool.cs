using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
        "component",
        Description = "Add, remove and inspect components on scene GameObjects",
        Mode = ToolMode.Both,
        Capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.SceneMutation,
        Category = "editor")]
    public sealed class ComponentTool : IUnityCliTool
    {
        static readonly string[] SupportedActions =
        {
            "add",
            "remove",
            "set_property",
            "get_info"
        };

        static readonly string[] SupportedWritableTypes =
        {
            "int",
            "float",
            "bool",
            "string",
            "Vector2",
            "Vector3",
            "Color",
            "Enum"
        };

        static readonly Type[] ComponentTypes = TypeCache.GetTypesDerivedFrom<Component>()
            .Where(type => type != null)
            .Append(typeof(Transform))
            .Distinct()
            .ToArray();

        public string Id => "component";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Add, remove and inspect components on scene GameObjects",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.SceneMutation,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "action",
                        type = "string",
                        description = "Component action: add/remove/set_property/get_info",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "target",
                        type = "string",
                        description = "Target scene GameObject reference",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "component_type",
                        type = "string",
                        description = "Component type name or full type name",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "property",
                        type = "string",
                        description = "Single property name for set_property",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "value",
                        type = "object",
                        description = "Single property value for set_property",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "properties",
                        type = "object",
                        description = "Batch property values for set_property",
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
                case "add":
                    return HandleAdd(args, context);
                case "remove":
                    return HandleRemove(args, context);
                case "set_property":
                    return HandleSetProperty(args, context);
                case "get_info":
                    return HandleGetInfo(args);
                default:
                    return ToolResult.Error("invalid_parameter", $"不支持的 component 操作 '{action}'。", new
                    {
                        parameter = "action",
                        value = action,
                        supportedActions = SupportedActions
                    });
            }
        }

        static ToolResult HandleAdd(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveTarget(args, out var target, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "component_type", out string componentTypeName, out error))
            {
                return error;
            }

            if (!TryResolveComponentType(componentTypeName, false, false, false, out var componentType, out error))
            {
                return error;
            }

            if (!CanAddComponent(target, componentType, out error))
            {
                return error;
            }

            var undoGroup = BeginUndoGroup("Add Component");
            try
            {
                var component = Undo.AddComponent(target, componentType);
                if (component == null)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    return ToolResult.Error("not_allowed", $"Unity 未能添加组件 '{GetComponentTypeName(componentType)}'。", new
                    {
                        action = "add",
                        target = CreateGameObjectReference(target),
                        component_type = GetComponentTypeName(componentType)
                    });
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "add",
                    target = CreateGameObjectReference(target),
                    component_type = GetComponentTypeName(componentType),
                    added_components = new[]
                    {
                        CreateComponentSummary(component)
                    }
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 add 时发生异常：{exception.Message}", new
                {
                    action = "add",
                    exception = exception.GetType().FullName
                });
            }
        }

        static ToolResult HandleRemove(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveTarget(args, out var target, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "component_type", out string componentTypeName, out error))
            {
                return error;
            }

            if (!TryResolveComponentType(componentTypeName, false, false, false, out var componentType, out error))
            {
                return error;
            }

            if (!TryCollectMatchingComponents(target, componentType, out var components, out var removedSummaries, out error))
            {
                return error;
            }

            var undoGroup = BeginUndoGroup("Remove Component");
            try
            {
                foreach (var component in components)
                {
                    Undo.DestroyObjectImmediate(component);
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "remove",
                    target = CreateGameObjectReference(target),
                    component_type = GetComponentTypeName(componentType),
                    removed_count = removedSummaries.Count,
                    removed_components = removedSummaries.ToArray()
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 remove 时发生异常：{exception.Message}", new
                {
                    action = "remove",
                    exception = exception.GetType().FullName
                });
            }
        }

        static ToolResult HandleSetProperty(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveTarget(args, out var target, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "component_type", out string componentTypeName, out error))
            {
                return error;
            }

            if (!TryResolveComponentType(componentTypeName, true, false, false, out var componentType, out error))
            {
                return error;
            }

            if (!TryCollectMatchingComponents(target, componentType, out var components, out var componentSummaries, out error))
            {
                return error;
            }

            if (!TryBuildAssignments(args, componentType, out var assignments, out error))
            {
                return error;
            }

            var undoGroup = BeginUndoGroup("Set Component Property");
            try
            {
                Undo.RecordObjects(components.Cast<Object>().ToArray(), "Set Component Property");

                foreach (var component in components)
                {
                    foreach (var assignment in assignments)
                    {
                        assignment.Member.SetValue(component, assignment.Value);
                    }

                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                    EditorUtility.SetDirty(component);
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResult.Ok(new
                {
                    action = "set_property",
                    target = CreateGameObjectReference(target),
                    component_type = GetComponentTypeName(componentType),
                    component_count = components.Count,
                    updated_components = componentSummaries.ToArray(),
                    updated_properties = assignments
                        .Select(assignment => CreatePropertySummary(assignment.Member))
                        .ToArray()
                });
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return ToolResult.Error("tool_execution_failed", $"执行 set_property 时发生异常：{exception.Message}", new
                {
                    action = "set_property",
                    exception = exception.GetType().FullName
                });
            }
        }

        static ToolResult HandleGetInfo(Dictionary<string, object> args)
        {
            if (!TryResolveTarget(args, out var target, out var error))
            {
                return error;
            }

            if (!TryGetOptionalNonEmptyString(args, "component_type", out var hasComponentType, out var componentTypeName, out error))
            {
                return error;
            }

            Type filterType = null;
            if (hasComponentType)
            {
                if (!TryResolveComponentType(componentTypeName, true, true, true, out filterType, out error))
                {
                    return error;
                }
            }

            var components = GetComponentsForInfo(target, filterType);
            if (components.Length == 0)
            {
                return ToolResult.Error("not_found", hasComponentType
                    ? $"目标对象上未找到组件 '{GetComponentTypeName(filterType)}'。"
                    : "目标对象上未找到可用组件。", new
                {
                    target = CreateGameObjectReference(target),
                    component_type = filterType != null ? GetComponentTypeName(filterType) : null
                });
            }

            var memberCache = new Dictionary<Type, IReadOnlyList<ComponentMember>>();
            var componentInfos = new List<object>(components.Length);
            foreach (var component in components)
            {
                var componentRuntimeType = component.GetType();
                if (!memberCache.TryGetValue(componentRuntimeType, out var members))
                {
                    members = GetComponentMembers(componentRuntimeType);
                    memberCache[componentRuntimeType] = members;
                }

                componentInfos.Add(new
                {
                    instanceId = component.GetInstanceID(),
                    instanceID = component.GetInstanceID(),
                    type = GetComponentTypeName(componentRuntimeType),
                    gameObject = CreateGameObjectReference(component.gameObject),
                    propertyCount = members.Count,
                    properties = members.Select(CreatePropertySummary).ToArray()
                });
            }

            return ToolResult.Ok(new
            {
                action = "get_info",
                target = CreateGameObjectReference(target),
                component_type = filterType != null ? GetComponentTypeName(filterType) : null,
                count = componentInfos.Count,
                components = componentInfos.ToArray()
            });
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

        static bool TryResolveTarget(IDictionary<string, object> args, out GameObject gameObject, out ToolResult error)
        {
            return TryResolveReference(args, "target", true, "search_method", out gameObject, out error);
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

        static bool TryResolveComponentType(string componentName, bool allowTransform, bool allowAbstract, bool allowGenericTypeDefinition, out Type componentType, out ToolResult error)
        {
            componentType = null;
            error = null;

            if (string.IsNullOrWhiteSpace(componentName))
            {
                error = ToolResult.Error("missing_parameter", "缺少参数 'component_type'。", new
                {
                    parameter = "component_type"
                });
                return false;
            }

            var normalizedName = componentName.Trim();
            var matches = ComponentTypes
                .Where(type => string.Equals(type.FullName, normalizedName, StringComparison.Ordinal)
                    || string.Equals(type.Name, normalizedName, StringComparison.Ordinal))
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

            componentType = matches.FirstOrDefault(type => string.Equals(type.FullName, normalizedName, StringComparison.Ordinal))
                ?? matches[0];

            if (!allowTransform && componentType == typeof(Transform))
            {
                error = ToolResult.Error("invalid_parameter", "不支持手动添加或移除 Transform 组件。", new
                {
                    component_type = normalizedName
                });
                componentType = null;
                return false;
            }

            if (!allowAbstract && componentType.IsAbstract)
            {
                error = ToolResult.Error("invalid_parameter", $"组件类型 '{normalizedName}' 不能用于当前操作，请提供具体组件类型。", new
                {
                    component_type = normalizedName,
                    resolved = GetComponentTypeName(componentType)
                });
                componentType = null;
                return false;
            }

            if (!allowGenericTypeDefinition && componentType.IsGenericTypeDefinition)
            {
                error = ToolResult.Error("invalid_parameter", $"组件类型 '{normalizedName}' 不是可直接使用的具体类型。", new
                {
                    component_type = normalizedName,
                    resolved = GetComponentTypeName(componentType)
                });
                componentType = null;
                return false;
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
                error = ToolResult.Error("not_allowed", $"目标对象上已存在不允许重复的组件 '{GetComponentTypeName(componentType)}'。", new
                {
                    target = CreateGameObjectReference(gameObject),
                    component_type = GetComponentTypeName(componentType)
                });
                return false;
            }

            return true;
        }

        static bool TryCollectMatchingComponents(GameObject gameObject, Type componentType, out List<Component> components, out List<object> summaries, out ToolResult error)
        {
            components = gameObject.GetComponents(componentType)
                .Where(component => component != null)
                .Cast<Component>()
                .ToList();
            summaries = components
                .Select(CreateComponentSummary)
                .Cast<object>()
                .ToList();
            error = null;

            if (components.Count > 0)
            {
                return true;
            }

            error = ToolResult.Error("not_found", $"目标对象上未找到组件 '{GetComponentTypeName(componentType)}'。", new
            {
                target = CreateGameObjectReference(gameObject),
                component_type = GetComponentTypeName(componentType)
            });
            return false;
        }

        static Component[] GetComponentsForInfo(GameObject gameObject, Type filterType)
        {
            if (filterType == null)
            {
                return gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .ToArray();
            }

            return gameObject.GetComponents(filterType)
                .Where(component => component != null)
                .Cast<Component>()
                .ToArray();
        }

        static bool TryBuildAssignments(IDictionary<string, object> args, Type componentType, out List<PropertyAssignment> assignments, out ToolResult error)
        {
            assignments = new List<PropertyAssignment>();
            error = null;

            var members = GetComponentMembers(componentType);
            if (!TryGetOptionalNonEmptyString(args, "property", out var hasSingleProperty, out var propertyName, out error))
            {
                return false;
            }

            if (!ArgsHelper.TryGetRawOptional(args, "value", out var hasSingleValue, out var rawSingleValue, out error))
            {
                return false;
            }

            if (!ArgsHelper.TryGetRawOptional(args, "properties", out var hasBatchProperties, out var rawBatchProperties, out error))
            {
                return false;
            }

            if (hasSingleProperty && hasBatchProperties)
            {
                error = ToolResult.Error("invalid_parameter", "参数 'property' 与 'properties' 不能同时提供。", new
                {
                    parameters = new[] { "property", "properties" }
                });
                return false;
            }

            if (hasSingleProperty)
            {
                if (!hasSingleValue)
                {
                    error = ToolResult.Error("missing_parameter", "使用单属性模式时缺少参数 'value'。", new
                    {
                        parameter = "value"
                    });
                    return false;
                }

                if (!TryCreateAssignment(members, componentType, propertyName, rawSingleValue, out var assignment, out error))
                {
                    return false;
                }

                assignments.Add(assignment);
                return true;
            }

            if (hasSingleValue)
            {
                error = ToolResult.Error("missing_parameter", "提供参数 'value' 时必须同时提供 'property'。", new
                {
                    parameter = "property"
                });
                return false;
            }

            if (!hasBatchProperties)
            {
                error = ToolResult.Error("missing_parameter", "缺少属性设置参数，请提供 'property'+'value' 或 'properties'。", new
                {
                    supported = new[] { "property", "value", "properties" }
                });
                return false;
            }

            if (!(rawBatchProperties is IDictionary<string, object> batchDictionary))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'properties' 必须是对象。", new
                {
                    parameter = "properties",
                    actualType = rawBatchProperties != null ? rawBatchProperties.GetType().Name : "null"
                });
                return false;
            }

            if (batchDictionary.Count == 0)
            {
                error = ToolResult.Error("invalid_parameter", "参数 'properties' 不能为空对象。", new
                {
                    parameter = "properties"
                });
                return false;
            }

            foreach (var entry in batchDictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    error = ToolResult.Error("invalid_parameter", "参数 'properties' 包含空属性名。", new
                    {
                        parameter = "properties"
                    });
                    assignments.Clear();
                    return false;
                }

                if (!TryCreateAssignment(members, componentType, entry.Key.Trim(), entry.Value, out var assignment, out error))
                {
                    assignments.Clear();
                    return false;
                }

                assignments.Add(assignment);
            }

            return true;
        }

        static bool TryCreateAssignment(IReadOnlyList<ComponentMember> members, Type componentType, string propertyName, object rawValue, out PropertyAssignment assignment, out ToolResult error)
        {
            assignment = null;
            error = null;

            if (!TryResolveMember(members, componentType, propertyName, out var member, out error))
            {
                return false;
            }

            if (!TryConvertMemberValue(rawValue, member, out var convertedValue, out error))
            {
                return false;
            }

            assignment = new PropertyAssignment(member, convertedValue);
            return true;
        }

        static bool TryResolveMember(IReadOnlyList<ComponentMember> members, Type componentType, string propertyName, out ComponentMember member, out ToolResult error)
        {
            member = null;
            error = null;

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'property' 不能为空字符串。", new
                {
                    parameter = "property"
                });
                return false;
            }

            var normalizedName = propertyName.Trim();
            member = members.FirstOrDefault(candidate => string.Equals(candidate.Name, normalizedName, StringComparison.Ordinal));
            if (member == null)
            {
                var caseInsensitiveMatches = members
                    .Where(candidate => string.Equals(candidate.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (caseInsensitiveMatches.Length > 1)
                {
                    error = ToolResult.Error("duplicate_name", $"属性名 '{normalizedName}' 匹配到多个成员，请使用精确大小写。", new
                    {
                        property = normalizedName,
                        component_type = GetComponentTypeName(componentType),
                        candidates = caseInsensitiveMatches.Select(candidate => candidate.Name).ToArray()
                    });
                    return false;
                }

                member = caseInsensitiveMatches.FirstOrDefault();
            }

            if (member == null)
            {
                error = ToolResult.Error("not_found", $"组件 '{GetComponentTypeName(componentType)}' 上不存在属性 '{normalizedName}'。", new
                {
                    component_type = GetComponentTypeName(componentType),
                    property = normalizedName
                });
                return false;
            }

            if (!member.Supported)
            {
                error = ToolResult.Error("invalid_parameter", $"属性 '{member.Name}' 的类型 '{member.TypeName}' 不受支持。", new
                {
                    component_type = GetComponentTypeName(componentType),
                    property = member.Name,
                    property_type = member.TypeName,
                    supported_types = SupportedWritableTypes
                });
                member = null;
                return false;
            }

            if (!member.Writable)
            {
                error = ToolResult.Error("invalid_parameter", $"属性 '{member.Name}' 是只读的，无法修改。", new
                {
                    component_type = GetComponentTypeName(componentType),
                    property = member.Name
                });
                member = null;
                return false;
            }

            return true;
        }

        static IReadOnlyList<ComponentMember> GetComponentMembers(Type componentType)
        {
            var members = new Dictionary<string, ComponentMember>(StringComparer.Ordinal);
            for (var currentType = componentType; currentType != null && currentType != typeof(Component) && currentType != typeof(Object); currentType = currentType.BaseType)
            {
                foreach (var property in currentType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    if (members.ContainsKey(property.Name))
                    {
                        continue;
                    }

                    members[property.Name] = CreatePropertyMember(property);
                }

                foreach (var field in currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (members.ContainsKey(field.Name))
                    {
                        continue;
                    }

                    members[field.Name] = CreateFieldMember(field);
                }
            }

            return members.Values
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .ToArray();
        }

        static ComponentMember CreatePropertyMember(PropertyInfo property)
        {
            var writable = property.SetMethod != null && property.SetMethod.IsPublic;
            var supported = TryDescribeMemberType(property.PropertyType, out var typeName, out var enumType);
            return new ComponentMember(property.Name, property.PropertyType, typeName, enumType, "property", supported, writable, property, null);
        }

        static ComponentMember CreateFieldMember(FieldInfo field)
        {
            var writable = !field.IsInitOnly && !field.IsLiteral;
            var supported = TryDescribeMemberType(field.FieldType, out var typeName, out var enumType);
            return new ComponentMember(field.Name, field.FieldType, typeName, enumType, "field", supported, writable, null, field);
        }

        static bool TryDescribeMemberType(Type memberType, out string typeName, out string enumType)
        {
            enumType = null;
            var nonNullableType = Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (nonNullableType == typeof(int))
            {
                typeName = "int";
                return true;
            }

            if (nonNullableType == typeof(float))
            {
                typeName = "float";
                return true;
            }

            if (nonNullableType == typeof(bool))
            {
                typeName = "bool";
                return true;
            }

            if (nonNullableType == typeof(string))
            {
                typeName = "string";
                return true;
            }

            if (nonNullableType == typeof(Vector2))
            {
                typeName = "Vector2";
                return true;
            }

            if (nonNullableType == typeof(Vector3))
            {
                typeName = "Vector3";
                return true;
            }

            if (nonNullableType == typeof(Color))
            {
                typeName = "Color";
                return true;
            }

            if (nonNullableType.IsEnum)
            {
                typeName = "Enum";
                enumType = nonNullableType.FullName ?? nonNullableType.Name;
                return true;
            }

            typeName = nonNullableType.FullName ?? nonNullableType.Name;
            return false;
        }

        static bool TryConvertMemberValue(object rawValue, ComponentMember member, out object convertedValue, out ToolResult error)
        {
            convertedValue = null;
            error = null;

            var memberType = Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType;
            if (memberType == typeof(int))
            {
                if (!TryConvertToInt(rawValue, out var intValue))
                {
                    error = CreateInvalidValueError(member, rawValue, "需要整数值。", "int");
                    return false;
                }

                convertedValue = intValue;
                return true;
            }

            if (memberType == typeof(float))
            {
                if (!TryConvertToFloat(rawValue, out var floatValue))
                {
                    error = CreateInvalidValueError(member, rawValue, "需要有限浮点数。", "float");
                    return false;
                }

                convertedValue = floatValue;
                return true;
            }

            if (memberType == typeof(bool))
            {
                if (!TryConvertToBoolean(rawValue, out var boolValue))
                {
                    error = CreateInvalidValueError(member, rawValue, "需要布尔值 true/false 或 1/0。", "bool");
                    return false;
                }

                convertedValue = boolValue;
                return true;
            }

            if (memberType == typeof(string))
            {
                if (!TryConvertToString(rawValue, out var stringValue))
                {
                    error = CreateInvalidValueError(member, rawValue, "需要字符串或基础值。", "string");
                    return false;
                }

                convertedValue = stringValue;
                return true;
            }

            if (memberType == typeof(Vector2))
            {
                if (!TryConvertToVector2(rawValue, out var vector2Value))
                {
                    error = CreateInvalidValueError(member, rawValue, "需要 [x,y] 数组或 {x,y} 对象。", "Vector2");
                    return false;
                }

                convertedValue = vector2Value;
                return true;
            }

            if (memberType == typeof(Vector3))
            {
                if (!TryConvertToVector3(rawValue, out var vector3Value))
                {
                    error = CreateInvalidValueError(member, rawValue, "需要 [x,y,z] 数组或 {x,y,z} 对象。", "Vector3");
                    return false;
                }

                convertedValue = vector3Value;
                return true;
            }

            if (memberType == typeof(Color))
            {
                if (!TryConvertToColor(rawValue, out var colorValue))
                {
                    error = CreateInvalidValueError(member, rawValue, "需要 [r,g,b,a] / [r,g,b] 数组或 {r,g,b,a} 对象。", "Color");
                    return false;
                }

                convertedValue = colorValue;
                return true;
            }

            if (memberType.IsEnum)
            {
                if (!TryConvertToEnum(rawValue, memberType, out var enumValue))
                {
                    error = CreateInvalidValueError(member, rawValue, $"需要枚举 {member.EnumType ?? memberType.Name} 的名称或整数值。", "Enum");
                    return false;
                }

                convertedValue = enumValue;
                return true;
            }

            error = ToolResult.Error("invalid_parameter", $"属性 '{member.Name}' 的类型 '{member.TypeName}' 不受支持。", new
            {
                property = member.Name,
                property_type = member.TypeName,
                supported_types = SupportedWritableTypes
            });
            return false;
        }

        static ToolResult CreateInvalidValueError(ComponentMember member, object rawValue, string reason, string expectedType)
        {
            return ToolResult.Error("invalid_parameter", $"属性 '{member.Name}' 的值无效：{reason}", new
            {
                property = member.Name,
                expected_type = expectedType,
                actual_type = rawValue != null ? rawValue.GetType().Name : "null",
                actual_value = rawValue
            });
        }

        static bool TryConvertToInt(object rawValue, out int value)
        {
            value = 0;
            switch (rawValue)
            {
                case int intValue:
                    value = intValue;
                    return true;
                case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                    value = (int)longValue;
                    return true;
                case short shortValue:
                    value = shortValue;
                    return true;
                case byte byteValue:
                    value = byteValue;
                    return true;
                case sbyte sbyteValue:
                    value = sbyteValue;
                    return true;
                case string stringValue:
                    return int.TryParse(stringValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                case float floatValue when IsFinite(floatValue) && Mathf.Approximately(floatValue, Mathf.Round(floatValue)):
                    value = (int)floatValue;
                    return true;
                case double doubleValue when !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue) && Math.Abs(doubleValue - Math.Round(doubleValue)) < 0.00001d:
                    value = (int)Math.Round(doubleValue);
                    return true;
                default:
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
                    return true;
                case long longValue:
                    value = longValue;
                    return IsFinite(value);
                case short shortValue:
                    value = shortValue;
                    return true;
                case byte byteValue:
                    value = byteValue;
                    return true;
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

        static bool TryConvertToBoolean(object rawValue, out bool value)
        {
            value = false;
            switch (rawValue)
            {
                case bool boolValue:
                    value = boolValue;
                    return true;
                case string stringValue:
                    var normalized = stringValue.Trim();
                    if (string.Equals(normalized, "1", StringComparison.Ordinal))
                    {
                        value = true;
                        return true;
                    }

                    if (string.Equals(normalized, "0", StringComparison.Ordinal))
                    {
                        value = false;
                        return true;
                    }

                    return bool.TryParse(normalized, out value);
                default:
                    try
                    {
                        value = Convert.ToInt32(rawValue, CultureInfo.InvariantCulture) != 0;
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
            }
        }

        static bool TryConvertToString(object rawValue, out string value)
        {
            value = string.Empty;
            switch (rawValue)
            {
                case null:
                    return false;
                case string stringValue:
                    value = stringValue;
                    return true;
                case bool boolValue:
                    value = boolValue ? "True" : "False";
                    return true;
                case Enum enumValue:
                    value = enumValue.ToString();
                    return true;
                case IFormattable formattable:
                    value = formattable.ToString(null, CultureInfo.InvariantCulture);
                    return true;
                default:
                    if (rawValue is IList || rawValue is IDictionary)
                    {
                        return false;
                    }

                    value = rawValue.ToString() ?? string.Empty;
                    return true;
            }
        }

        static bool TryConvertToVector2(object rawValue, out Vector2 value)
        {
            value = default;
            if (TryGetNumericArray(rawValue, 2, out var elements))
            {
                value = new Vector2(elements[0], elements[1]);
                return true;
            }

            if (!TryGetObjectDictionary(rawValue, out var dictionary))
            {
                return false;
            }

            if (!TryGetRequiredFloat(dictionary, "x", out var x) || !TryGetRequiredFloat(dictionary, "y", out var y))
            {
                return false;
            }

            value = new Vector2(x, y);
            return true;
        }

        static bool TryConvertToVector3(object rawValue, out Vector3 value)
        {
            value = default;
            if (TryGetNumericArray(rawValue, 3, out var elements))
            {
                value = new Vector3(elements[0], elements[1], elements[2]);
                return true;
            }

            if (!TryGetObjectDictionary(rawValue, out var dictionary))
            {
                return false;
            }

            if (!TryGetRequiredFloat(dictionary, "x", out var x)
                || !TryGetRequiredFloat(dictionary, "y", out var y)
                || !TryGetRequiredFloat(dictionary, "z", out var z))
            {
                return false;
            }

            value = new Vector3(x, y, z);
            return true;
        }

        static bool TryConvertToColor(object rawValue, out Color value)
        {
            value = default;
            if (TryGetNumericArray(rawValue, 3, out var rgbElements))
            {
                value = new Color(rgbElements[0], rgbElements[1], rgbElements[2], 1f);
                return true;
            }

            if (TryGetNumericArray(rawValue, 4, out var rgbaElements))
            {
                value = new Color(rgbaElements[0], rgbaElements[1], rgbaElements[2], rgbaElements[3]);
                return true;
            }

            if (!TryGetObjectDictionary(rawValue, out var dictionary))
            {
                return false;
            }

            if (!TryGetRequiredFloat(dictionary, "r", out var r)
                || !TryGetRequiredFloat(dictionary, "g", out var g)
                || !TryGetRequiredFloat(dictionary, "b", out var b))
            {
                return false;
            }

            var a = 1f;
            if (TryGetOptionalFloat(dictionary, "a", out var alpha))
            {
                a = alpha;
            }

            value = new Color(r, g, b, a);
            return true;
        }

        static bool TryConvertToEnum(object rawValue, Type enumType, out object value)
        {
            value = null;
            if (rawValue == null)
            {
                return false;
            }

            if (rawValue is string stringValue)
            {
                try
                {
                    value = Enum.Parse(enumType, stringValue.Trim(), true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (!TryConvertToInt(rawValue, out var intValue))
            {
                return false;
            }

            try
            {
                value = Enum.ToObject(enumType, intValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool TryGetNumericArray(object rawValue, int expectedLength, out float[] values)
        {
            values = Array.Empty<float>();
            if (!TryConvertToObjectArray(rawValue, out var elements) || elements.Length != expectedLength)
            {
                return false;
            }

            var converted = new float[elements.Length];
            for (var index = 0; index < elements.Length; index++)
            {
                if (!TryConvertToFloat(elements[index], out converted[index]))
                {
                    values = Array.Empty<float>();
                    return false;
                }
            }

            values = converted;
            return true;
        }

        static bool TryConvertToObjectArray(object rawValue, out object[] values)
        {
            values = Array.Empty<object>();
            switch (rawValue)
            {
                case object[] directArray:
                    values = directArray;
                    return true;
                case IList list:
                    values = new object[list.Count];
                    for (var index = 0; index < list.Count; index++)
                    {
                        values[index] = list[index];
                    }

                    return true;
                default:
                    return false;
            }
        }

        static bool TryGetObjectDictionary(object rawValue, out Dictionary<string, object> dictionary)
        {
            dictionary = null;
            if (!(rawValue is IDictionary<string, object> rawDictionary))
            {
                return false;
            }

            dictionary = new Dictionary<string, object>(rawDictionary, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        static bool TryGetRequiredFloat(IDictionary<string, object> dictionary, string key, out float value)
        {
            value = 0f;
            return dictionary.TryGetValue(key, out var rawValue)
                && rawValue != null
                && TryConvertToFloat(rawValue, out value);
        }

        static bool TryGetOptionalFloat(IDictionary<string, object> dictionary, string key, out float value)
        {
            value = 0f;
            if (!dictionary.TryGetValue(key, out var rawValue) || rawValue == null)
            {
                return false;
            }

            return TryConvertToFloat(rawValue, out value);
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

        static string GetComponentTypeName(Type componentType)
        {
            return componentType?.FullName ?? componentType?.Name ?? string.Empty;
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
                type = GetComponentTypeName(component.GetType()),
                gameObject = CreateGameObjectReference(component.gameObject)
            };
        }

        static object CreatePropertySummary(ComponentMember member)
        {
            return new
            {
                name = member.Name,
                type = member.TypeName,
                enumType = member.EnumType,
                memberKind = member.MemberKind,
                writable = member.Writable,
                supported = member.Supported
            };
        }

        sealed class ComponentMember
        {
            public ComponentMember(
                string name,
                Type memberType,
                string typeName,
                string enumType,
                string memberKind,
                bool supported,
                bool writable,
                PropertyInfo property,
                FieldInfo field)
            {
                Name = name;
                MemberType = memberType;
                TypeName = typeName;
                EnumType = enumType;
                MemberKind = memberKind;
                Supported = supported;
                Writable = writable;
                Property = property;
                Field = field;
            }

            public string Name { get; }

            public Type MemberType { get; }

            public string TypeName { get; }

            public string EnumType { get; }

            public string MemberKind { get; }

            public bool Supported { get; }

            public bool Writable { get; }

            PropertyInfo Property { get; }

            FieldInfo Field { get; }

            public void SetValue(object target, object value)
            {
                if (Property != null)
                {
                    Property.SetValue(target, value, null);
                    return;
                }

                Field?.SetValue(target, value);
            }
        }

        sealed class PropertyAssignment
        {
            public PropertyAssignment(ComponentMember member, object value)
            {
                Member = member;
                Value = value;
            }

            public ComponentMember Member { get; }

            public object Value { get; }
        }
    }
}
