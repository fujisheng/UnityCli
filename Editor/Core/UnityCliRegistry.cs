using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityCli.Editor.Attributes;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Core
{
    [InitializeOnLoad]
    public static class UnityCliRegistry
    {
        static readonly Dictionary<string, IUnityCliTool> registeredTools = new Dictionary<string, IUnityCliTool>(StringComparer.Ordinal);
        static readonly Dictionary<string, ToolDescriptor> registeredDescriptors = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);

        static UnityCliRegistry()
        {
            UnityCliAllowlist.Reload();
            Reload();
        }

        public static void Reload()
        {
            UnityCliAllowlist.Reload();
            registeredTools.Clear();
            registeredDescriptors.Clear();

            foreach (var toolType in DiscoverTools())
            {
                RegisterTool(toolType);
            }
        }

        public static IReadOnlyList<Type> DiscoverTools()
        {
            var toolTypes = new List<Type>();
            try
            {
                foreach (var type in TypeCache.GetTypesWithAttribute<UnityCliToolAttribute>())
                {
                    if (type == null || !type.IsClass || type.IsAbstract)
                    {
                        continue;
                    }

                    if (type.GetCustomAttribute<UnityCliToolAttribute>(false) == null)
                    {
                        continue;
                    }

                    toolTypes.Add(type);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[UnityCli] 通过 TypeCache 发现工具失败，将回退到程序集扫描。\n{exception}");
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in GetLoadableTypes(assembly))
                    {
                        if (type == null || !type.IsClass || type.IsAbstract)
                        {
                            continue;
                        }

                        if (type.GetCustomAttribute<UnityCliToolAttribute>(false) == null)
                        {
                            continue;
                        }

                        toolTypes.Add(type);
                    }
                }
            }

            toolTypes = toolTypes.Distinct().ToList();
            toolTypes.Sort(CompareToolTypes);
            return toolTypes;
        }

        public static IReadOnlyList<string> GetRegisteredToolIds()
        {
            return registeredTools.Keys.OrderBy(toolId => toolId, StringComparer.Ordinal).ToArray();
        }

        public static IReadOnlyList<IUnityCliTool> GetRegisteredTools()
        {
            return GetRegisteredToolIds()
                .Select(toolId => registeredTools[toolId])
                .ToArray();
        }

        public static IReadOnlyList<ToolDescriptor> GetRegisteredDescriptors()
        {
            return GetRegisteredToolIds()
                .Select(toolId => registeredDescriptors[toolId])
                .ToArray();
        }

        public static bool TryGetTool(string toolId, out IUnityCliTool tool)
        {
            return registeredTools.TryGetValue(toolId, out tool);
        }

        public static bool TryGetDescriptor(string toolId, out ToolDescriptor descriptor)
        {
            return registeredDescriptors.TryGetValue(toolId, out descriptor);
        }

        static void RegisterTool(Type toolType)
        {
            var attribute = toolType.GetCustomAttribute<UnityCliToolAttribute>(false);
            if (attribute == null)
            {
                return;
            }

            if (!typeof(IUnityCliTool).IsAssignableFrom(toolType))
            {
                Debug.LogWarning($"[UnityCli] 工具 '{toolType.FullName}' 标记了 [UnityCliTool]，但未实现 IUnityCliTool，已跳过注册。");
                return;
            }

            IUnityCliTool tool;
            try
            {
                tool = Activator.CreateInstance(toolType) as IUnityCliTool;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[UnityCli] 创建工具实例失败：{toolType.FullName}\n{exception}");
                return;
            }

            if (tool == null)
            {
                Debug.LogWarning($"[UnityCli] 工具 '{toolType.FullName}' 创建结果为空，已跳过注册。");
                return;
            }

            if (!string.IsNullOrWhiteSpace(tool.Id) && !string.Equals(tool.Id, attribute.Id, StringComparison.Ordinal))
            {
                Debug.LogWarning($"[UnityCli] 工具 '{toolType.FullName}' 的属性 Id '{attribute.Id}' 与实例 Id '{tool.Id}' 不一致，将以属性 Id 为准。");
            }

            ToolDescriptor descriptor;
            try
            {
                descriptor = BuildDescriptor(toolType, tool, attribute);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[UnityCli] 构建工具描述失败：{toolType.FullName}\n{exception}");
                return;
            }

            if (registeredTools.TryGetValue(attribute.Id, out var existingTool))
            {
                Debug.LogWarning($"[UnityCli] 检测到重复工具 Id '{attribute.Id}'，将使用 '{toolType.FullName}' 覆盖 '{existingTool.GetType().FullName}'。");
            }

            registeredTools[attribute.Id] = tool;
            registeredDescriptors[attribute.Id] = descriptor;
        }

        static ToolDescriptor BuildDescriptor(Type toolType, IUnityCliTool tool, UnityCliToolAttribute attribute)
        {
            var descriptor = tool.GetDescriptor() ?? new ToolDescriptor();
            descriptor.id = attribute.Id;
            descriptor.category = !string.IsNullOrWhiteSpace(attribute.Category)
                ? attribute.Category
                : "other";
            descriptor.description = !string.IsNullOrWhiteSpace(attribute.Description)
                ? attribute.Description
                : descriptor.description ?? string.Empty;
            descriptor.mode = attribute.Mode;
            descriptor.capabilities = attribute.Capabilities;
            descriptor.schemaVersion = !string.IsNullOrWhiteSpace(attribute.SchemaVersion)
                ? attribute.SchemaVersion
                : string.IsNullOrWhiteSpace(descriptor.schemaVersion) ? "1.0" : descriptor.schemaVersion;
            descriptor.parameters = BuildParameterDescriptors(toolType, descriptor.parameters);
            return descriptor;
        }

        static List<ParamDescriptor> BuildParameterDescriptors(Type toolType, List<ParamDescriptor> fallbackParameters)
        {
            var inferredParameters = InferParameterDescriptors(toolType);
            if (inferredParameters.Count > 0)
            {
                return inferredParameters;
            }

            return fallbackParameters ?? new List<ParamDescriptor>();
        }

        static List<ParamDescriptor> InferParameterDescriptors(Type toolType)
        {
            var parametersType = toolType.GetNestedType("Parameters", BindingFlags.Public | BindingFlags.NonPublic);
            if (parametersType != null)
            {
                return parametersType
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Select(CreateParamDescriptor)
                    .ToList();
            }

            return toolType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetCustomAttribute<UnityCliParamAttribute>(true) != null)
                .Select(CreateParamDescriptor)
                .ToList();
        }

        static ParamDescriptor CreateParamDescriptor(PropertyInfo property)
        {
            var attribute = property.GetCustomAttribute<UnityCliParamAttribute>(true);
            return new ParamDescriptor
            {
                name = property.Name,
                type = MapParameterType(property.PropertyType),
                description = attribute?.Description ?? string.Empty,
                required = attribute?.Required ?? IsRequired(property.PropertyType),
                defaultValue = attribute?.DefaultValue
            };
        }

        static bool IsRequired(Type propertyType)
        {
            if (!propertyType.IsValueType)
            {
                return false;
            }

            return Nullable.GetUnderlyingType(propertyType) == null;
        }

        static string MapParameterType(Type propertyType)
        {
            var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (underlyingType.IsEnum)
            {
                return underlyingType.Name;
            }

            if (underlyingType == typeof(string) || underlyingType == typeof(char) || underlyingType == typeof(Guid))
            {
                return "string";
            }

            if (underlyingType == typeof(bool))
            {
                return "boolean";
            }

            if (underlyingType == typeof(byte)
                || underlyingType == typeof(sbyte)
                || underlyingType == typeof(short)
                || underlyingType == typeof(ushort)
                || underlyingType == typeof(int)
                || underlyingType == typeof(uint)
                || underlyingType == typeof(long)
                || underlyingType == typeof(ulong))
            {
                return "integer";
            }

            if (underlyingType == typeof(float)
                || underlyingType == typeof(double)
                || underlyingType == typeof(decimal))
            {
                return "number";
            }

            if (underlyingType.IsArray)
            {
                return "array";
            }

            return "object";
        }

        static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                foreach (var loaderException in exception.LoaderExceptions.Where(item => item != null))
                {
                    Debug.LogWarning($"[UnityCli] 程序集类型加载异常：{assembly.FullName}\n{loaderException}");
                }

                return exception.Types.Where(type => type != null);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[UnityCli] 读取程序集类型失败：{assembly.FullName}\n{exception}");
                return Array.Empty<Type>();
            }
        }

        static int CompareToolTypes(Type left, Type right)
        {
            var leftKey = $"{left.Assembly.FullName}:{left.FullName}";
            var rightKey = $"{right.Assembly.FullName}:{right.FullName}";
            return StringComparer.Ordinal.Compare(leftKey, rightKey);
        }
    }
}
