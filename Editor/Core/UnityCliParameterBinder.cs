using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityCli.Editor.Attributes;

namespace UnityCli.Editor.Core
{
    /// <summary>
    /// UnityCli 工具参数绑定与类型转换辅助类。
    /// </summary>
    public static class UnityCliParameterBinder
    {
        static readonly Lazy<Type> unityCliJsonType = new Lazy<Type>(() =>
            Type.GetType("UnityCli.Editor.Core.UnityCliJson, com.fujisheng.unitycli.editor", throwOnError: false));

        /// <summary>
        /// 检查 Unity Editor 是否处于可执行状态（未在编译/刷新中）。
        /// </summary>
        public static bool EnsureReady(ToolContext context, out ToolResult error)
        {
            error = null;
            if (context == null)
            {
                error = ToolResult.Error("invalid_parameter", "工具上下文不能为空。", nameof(context));
                return false;
            }

            if (context.EditorState.IsCompiling || context.EditorState.IsUpdating)
            {
                error = ToolResult.Error("not_allowed", "Unity Editor 正在编译或刷新，当前不可执行。", new
                {
                    context.EditorState.IsCompiling,
                    context.EditorState.IsUpdating
                });
                return false;
            }

            return true;
        }

        /// <summary>
        /// 检查当前运行模式是否符合要求。
        /// </summary>
        public static bool EnsurePlayMode(ToolContext context, bool requiredPlaying, out ToolResult error)
        {
            error = null;
            if (context == null)
            {
                error = ToolResult.Error("invalid_parameter", "工具上下文不能为空。", nameof(context));
                return false;
            }

            if (context.IsPlaying != requiredPlaying)
            {
                var expected = requiredPlaying ? "Play" : "Edit";
                var current = context.IsPlaying ? "Play" : "Edit";
                error = ToolResult.Error("not_allowed", $"当前运行模式不允许。期望: {expected}，当前: {current}。", new
                {
                    expected,
                    current
                });
                return false;
            }

            return true;
        }

        /// <summary>
        /// 检查当前不在 PlayMode 中。
        /// </summary>
        public static bool EnsureNotPlaying(ToolContext context, out ToolResult error)
        {
            return EnsurePlayMode(context, false, out error);
        }

        /// <summary>
        /// 将 CLI 传入的参数字典绑定到强类型 POCO。
        /// </summary>
        public static bool TryBindParameters<T>(IDictionary<string, object> args, out T parameters, out ToolResult error)
            where T : new()
        {
            parameters = new T();
            error = null;

            var properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (var property in properties)
            {
                if (!property.CanWrite)
                {
                    continue;
                }

                var attribute = property.GetCustomAttribute<UnityCliParamAttribute>(true);
                var parameterName = property.Name;
                if (TryGetArgumentValue(args, parameterName, out var rawValue) && rawValue != null)
                {
                    if (!TryConvertValue(rawValue, property.PropertyType, out var convertedValue, out var errorMessage))
                    {
                        parameters = default;
                        error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 类型无效：{errorMessage}", new
                        {
                            parameter = parameterName,
                            expectedType = property.PropertyType.Name,
                            actualType = rawValue.GetType().Name,
                            actualValue = rawValue
                        });
                        return false;
                    }

                    property.SetValue(parameters, convertedValue);
                    continue;
                }

                var currentValue = property.GetValue(parameters);
                if (attribute?.DefaultValue != null && IsUnsetValue(currentValue, property.PropertyType))
                {
                    if (!TryConvertValue(attribute.DefaultValue, property.PropertyType, out var defaultValue, out var errorMessage))
                    {
                        parameters = default;
                        error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 默认值无效：{errorMessage}", new
                        {
                            parameter = parameterName,
                            expectedType = property.PropertyType.Name,
                            defaultValue = attribute.DefaultValue
                        });
                        return false;
                    }

                    property.SetValue(parameters, defaultValue);
                    continue;
                }

                continue;
            }

            return true;
        }

        /// <summary>
        /// 把各种返回结果统一包装成 <see cref="ToolResult"/>。
        /// </summary>
        public static ToolResult ToToolResult(object response, string defaultErrorCode = "tool_execution_failed", string defaultSuccessMessage = null)
        {
            if (response is ToolResult toolResult)
            {
                return toolResult;
            }

            if (response == null)
            {
                return ToolResult.Error(defaultErrorCode, "工具返回了空结果。");
            }

            if (TryReadBoolean(response, "Success", out var success)
                || TryReadBoolean(response, "success", out success))
            {
                TryReadString(response, "Message", out var message);
                if (string.IsNullOrWhiteSpace(message))
                {
                    TryReadString(response, "message", out message);
                }

                TryReadString(response, "Error", out var errorMessage);
                if (string.IsNullOrWhiteSpace(errorMessage))
                {
                    TryReadString(response, "error", out errorMessage);
                }

                TryReadObject(response, "Data", out var data);
                if (data == null)
                {
                    TryReadObject(response, "data", out data);
                }

                if (success)
                {
                    return ToolResult.Ok(data ?? response, string.IsNullOrWhiteSpace(message) ? defaultSuccessMessage : message);
                }

                var finalError = !string.IsNullOrWhiteSpace(errorMessage)
                    ? errorMessage
                    : !string.IsNullOrWhiteSpace(message)
                        ? message
                        : "工具执行失败。";
                return ToolResult.Error(ResolveErrorCode(finalError, defaultErrorCode), finalError, data);
            }

            return ToolResult.Ok(response, defaultSuccessMessage);
        }

        /// <summary>
        /// 检查参数是否存在且非空。
        /// </summary>
        public static bool HasArgument(IDictionary<string, object> args, string parameterName)
        {
            return TryGetArgumentValue(args, parameterName, out var value) && value != null;
        }

        /// <summary>
        /// 尝试将 JSON 字符串反序列化为对象。
        /// </summary>
        public static bool TryDeserializeJson(string json, out object value, out string error)
        {
            value = null;
            error = null;

            var type = unityCliJsonType.Value;
            if (type == null)
            {
                error = "未找到 UnityCliJson 解析器。";
                return false;
            }

            var method = type.GetMethod("Deserialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                error = "UnityCliJson.Deserialize 不可用。";
                return false;
            }

            try
            {
                value = method.Invoke(null, new object[] { json });
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = exception.InnerException?.Message ?? exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 尝试将 JSON 字符串反序列化为字典对象。
        /// </summary>
        public static bool TryDeserializeJsonObject(string json, out Dictionary<string, object> value, out string error)
        {
            value = null;
            if (!TryDeserializeJson(json, out var rawValue, out error))
            {
                return false;
            }

            value = rawValue as Dictionary<string, object>;
            if (value != null)
            {
                return true;
            }

            error = "JSON 根节点必须是对象。";
            return false;
        }

        /// <summary>
        /// 尝试将 JSON 字符串反序列化为数组。
        /// </summary>
        public static bool TryDeserializeJsonArray(string json, out IList<object> value, out string error)
        {
            value = null;
            if (!TryDeserializeJson(json, out var rawValue, out error))
            {
                return false;
            }

            if (rawValue is IList<object> objectList)
            {
                value = objectList;
                return true;
            }

            if (rawValue is IList list)
            {
                var converted = new List<object>(list.Count);
                foreach (var item in list)
                {
                    converted.Add(item);
                }

                value = converted;
                return true;
            }

            error = "JSON 根节点必须是数组。";
            return false;
        }

        static bool TryGetArgumentValue(IDictionary<string, object> args, string parameterName, out object value)
        {
            value = null;
            if (args == null || string.IsNullOrWhiteSpace(parameterName))
            {
                return false;
            }

            if (args.TryGetValue(parameterName, out value))
            {
                return true;
            }

            foreach (var pair in args)
            {
                if (!string.Equals(pair.Key, parameterName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = pair.Value;
                return true;
            }

            return false;
        }

        static bool IsUnsetValue(object value, Type propertyType)
        {
            if (value == null)
            {
                return true;
            }

            var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (underlyingType == typeof(string))
            {
                return string.IsNullOrEmpty((string)value);
            }

            if (!underlyingType.IsValueType)
            {
                return false;
            }

            var defaultValue = Activator.CreateInstance(underlyingType);
            return Equals(value, defaultValue);
        }

        static bool TryConvertValue(object rawValue, Type targetType, out object convertedValue, out string errorMessage)
        {
            convertedValue = null;
            errorMessage = string.Empty;

            if (rawValue == null)
            {
                return false;
            }

            var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullableType.IsInstanceOfType(rawValue))
            {
                convertedValue = rawValue;
                return true;
            }

            if (nonNullableType == typeof(string))
            {
                convertedValue = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;
            }

            if (nonNullableType == typeof(bool))
            {
                if (TryConvertBoolean(rawValue, out var boolValue))
                {
                    convertedValue = boolValue;
                    return true;
                }

                errorMessage = "需要布尔值 true/false 或 1/0。";
                return false;
            }

            if (nonNullableType.IsEnum)
            {
                if (rawValue is string enumString && Enum.TryParse(nonNullableType, enumString, true, out var enumValue))
                {
                    convertedValue = enumValue;
                    return true;
                }

                try
                {
                    var numericValue = Convert.ChangeType(rawValue, Enum.GetUnderlyingType(nonNullableType), CultureInfo.InvariantCulture);
                    convertedValue = Enum.ToObject(nonNullableType, numericValue);
                    return true;
                }
                catch (Exception exception)
                {
                    errorMessage = exception.Message;
                    return false;
                }
            }

            if (nonNullableType.IsArray && rawValue is IEnumerable enumerable && !(rawValue is string))
            {
                var elementType = nonNullableType.GetElementType();
                var items = new List<object>();
                foreach (var item in enumerable)
                {
                    if (!TryConvertValue(item, elementType, out var convertedItem, out errorMessage))
                    {
                        return false;
                    }

                    items.Add(convertedItem);
                }

                var array = Array.CreateInstance(elementType, items.Count);
                for (var index = 0; index < items.Count; index++)
                {
                    array.SetValue(items[index], index);
                }

                convertedValue = array;
                return true;
            }

            try
            {
                convertedValue = Convert.ChangeType(rawValue, nonNullableType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                return false;
            }
        }

        static bool TryConvertBoolean(object rawValue, out bool value)
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

        static string ResolveErrorCode(string errorMessage, string defaultErrorCode)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                return defaultErrorCode;
            }

            if (errorMessage.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                || errorMessage.IndexOf("未找到", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "not_found";
            }

            if (errorMessage.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0
                || errorMessage.IndexOf("required", StringComparison.OrdinalIgnoreCase) >= 0
                || errorMessage.IndexOf("参数", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "invalid_parameter";
            }

            if (errorMessage.IndexOf("play mode", StringComparison.OrdinalIgnoreCase) >= 0
                || errorMessage.IndexOf("运行模式", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "not_allowed";
            }

            return defaultErrorCode;
        }

        static bool TryReadBoolean(object source, string memberName, out bool value)
        {
            value = false;
            if (!TryReadMember(source, memberName, out var memberValue) || memberValue == null)
            {
                return false;
            }

            return TryConvertBoolean(memberValue, out value);
        }

        static bool TryReadString(object source, string memberName, out string value)
        {
            value = string.Empty;
            if (!TryReadMember(source, memberName, out var memberValue) || memberValue == null)
            {
                return false;
            }

            value = Convert.ToString(memberValue, CultureInfo.InvariantCulture) ?? string.Empty;
            return true;
        }

        static bool TryReadObject(object source, string memberName, out object value)
        {
            value = null;
            return TryReadMember(source, memberName, out value);
        }

        static bool TryReadMember(object source, string memberName, out object value)
        {
            value = null;
            if (source == null || string.IsNullOrWhiteSpace(memberName))
            {
                return false;
            }

            if (source is IDictionary<string, object> dictionary && dictionary.TryGetValue(memberName, out var dictionaryValue))
            {
                value = dictionaryValue;
                return true;
            }

            var property = source.GetType().GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property != null)
            {
                value = property.GetValue(source, null);
                return true;
            }

            var field = source.GetType().GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (field != null)
            {
                value = field.GetValue(source);
                return true;
            }

            return false;
        }
    }
}
