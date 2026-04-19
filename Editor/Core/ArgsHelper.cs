using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityCli.Editor.Core
{
    internal static class ArgsHelper
    {
        public static bool TryGetRequired<T>(IDictionary<string, object> args, string parameterName, out T value, out ToolResult error)
        {
            value = default;
            error = null;

            if (string.IsNullOrWhiteSpace(parameterName))
            {
                error = ToolResult.Error("invalid_parameter", "参数名不能为空。", nameof(parameterName));
                return false;
            }

            if (args == null || !args.TryGetValue(parameterName, out var rawValue) || rawValue == null)
            {
                error = ToolResult.Error("missing_parameter", $"缺少必需参数 '{parameterName}'。", new
                {
                    parameter = parameterName
                });
                return false;
            }

            if (!TryConvert(rawValue, out value, out var convertErrorMessage))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 类型无效：{convertErrorMessage}", new
                {
                    parameter = parameterName,
                    expectedType = GetFriendlyTypeName(typeof(T)),
                    actualType = rawValue.GetType().Name,
                    actualValue = rawValue
                });
                return false;
            }

            return true;
        }

        public static bool TryGetOptional<T>(IDictionary<string, object> args, string parameterName, T defaultValue, out T value, out ToolResult error)
        {
            value = defaultValue;
            error = null;

            if (string.IsNullOrWhiteSpace(parameterName))
            {
                error = ToolResult.Error("invalid_parameter", "参数名不能为空。", nameof(parameterName));
                return false;
            }

            if (args == null || !args.TryGetValue(parameterName, out var rawValue) || rawValue == null)
            {
                return true;
            }

            if (!TryConvert(rawValue, out value, out var convertErrorMessage))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 类型无效：{convertErrorMessage}", new
                {
                    parameter = parameterName,
                    expectedType = GetFriendlyTypeName(typeof(T)),
                    actualType = rawValue.GetType().Name,
                    actualValue = rawValue
                });
                return false;
            }

            return true;
        }

        public static bool TryGetArray(IDictionary<string, object> args, string parameterName, out object[] value, out ToolResult error)
        {
            value = Array.Empty<object>();
            error = null;

            if (string.IsNullOrWhiteSpace(parameterName))
            {
                error = ToolResult.Error("invalid_parameter", "参数名不能为空。", nameof(parameterName));
                return false;
            }

            if (args == null || !args.TryGetValue(parameterName, out var rawValue) || rawValue == null)
            {
                error = ToolResult.Error("missing_parameter", $"缺少必需参数 '{parameterName}'。", new
                {
                    parameter = parameterName
                });
                return false;
            }

            if (rawValue is object[] directArray)
            {
                value = directArray;
                return true;
            }

            if (rawValue is IList list)
            {
                var converted = new object[list.Count];
                for (var index = 0; index < list.Count; index++)
                {
                    converted[index] = list[index];
                }

                value = converted;
                return true;
            }

            if (rawValue is IEnumerable enumerable && !(rawValue is string))
            {
                value = enumerable.Cast<object>().ToArray();
                return true;
            }

            error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 不是数组。", new
            {
                parameter = parameterName,
                actualType = rawValue.GetType().Name
            });
            return false;
        }

        public static bool TryGetObject(IDictionary<string, object> args, string parameterName, out IDictionary<string, object> value, out ToolResult error)
        {
            value = null;
            error = null;

            if (string.IsNullOrWhiteSpace(parameterName))
            {
                error = ToolResult.Error("invalid_parameter", "参数名不能为空。", nameof(parameterName));
                return false;
            }

            if (args == null || !args.TryGetValue(parameterName, out var rawValue) || rawValue == null)
            {
                error = ToolResult.Error("missing_parameter", $"缺少必需参数 '{parameterName}'。", new
                {
                    parameter = parameterName
                });
                return false;
            }

            if (rawValue is IDictionary<string, object> dictionary)
            {
                value = dictionary;
                return true;
            }

            error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 不是对象。", new
            {
                parameter = parameterName,
                actualType = rawValue.GetType().Name
            });
            return false;
        }

        public static bool TryGetRawOptional(IDictionary<string, object> args, string parameterName, out bool hasValue, out object value, out ToolResult error)
        {
            hasValue = false;
            value = null;
            error = null;

            if (string.IsNullOrWhiteSpace(parameterName))
            {
                error = ToolResult.Error("invalid_parameter", "参数名不能为空。", nameof(parameterName));
                return false;
            }

            if (args == null || !args.TryGetValue(parameterName, out var rawValue) || rawValue == null)
            {
                return true;
            }

            hasValue = true;
            value = rawValue;
            return true;
        }

        static bool TryConvert<T>(object rawValue, out T value, out string errorMessage)
        {
            value = default;
            errorMessage = string.Empty;

            if (rawValue is T targetValue)
            {
                value = targetValue;
                return true;
            }

            var targetType = typeof(T);
            var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (nonNullableType.IsEnum)
            {
                if (rawValue is string enumString && Enum.TryParse(nonNullableType, enumString, true, out var enumParsed))
                {
                    value = (T)enumParsed;
                    return true;
                }

                errorMessage = $"需要枚举值 {nonNullableType.Name}。";
                return false;
            }

            if (nonNullableType == typeof(string))
            {
                value = (T)(object)(rawValue.ToString() ?? string.Empty);
                return true;
            }

            if (nonNullableType == typeof(bool))
            {
                if (TryConvertToBoolean(rawValue, out var boolValue))
                {
                    value = (T)(object)boolValue;
                    return true;
                }

                errorMessage = "需要布尔值 true/false 或 1/0。";
                return false;
            }

            if (nonNullableType == typeof(Guid))
            {
                if (Guid.TryParse(rawValue.ToString(), out var guidValue))
                {
                    value = (T)(object)guidValue;
                    return true;
                }

                errorMessage = "需要有效的 Guid 字符串。";
                return false;
            }

            try
            {
                var converted = Convert.ChangeType(rawValue, nonNullableType, CultureInfo.InvariantCulture);
                value = (T)converted;
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                return false;
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

        static string GetFriendlyTypeName(Type type)
        {
            var nonNullableType = Nullable.GetUnderlyingType(type) ?? type;
            return nonNullableType.Name;
        }
    }
}
