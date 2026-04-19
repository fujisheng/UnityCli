using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace UnityCli.Editor.Core
{
    static class UnityCliJson
    {
        public static string Serialize(object value)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return SerializeValue(value, visited);
        }

        public static object Deserialize(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var parser = new Parser(json);
            var value = parser.ParseValue();
            parser.EnsureFullyConsumed();
            return value;
        }

        public static bool TryDeserializeObject(string json, out Dictionary<string, object> value, out string error)
        {
            value = null;
            error = null;

            try
            {
                var parsed = Deserialize(json) as Dictionary<string, object>;
                if (parsed == null)
                {
                    error = "JSON 根节点必须是对象。";
                    return false;
                }

                value = parsed;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        static string SerializeValue(object value, HashSet<object> visited)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is string stringValue)
            {
                return '"' + Escape(stringValue) + '"';
            }

            if (value is bool boolValue)
            {
                return boolValue ? "true" : "false";
            }

            if (value is DateTime dateTime)
            {
                return '"' + dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + '"';
            }

            if (value is DateTimeOffset dateTimeOffset)
            {
                return '"' + dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + '"';
            }

            if (value is Enum)
            {
                return '"' + Escape(value.ToString()) + '"';
            }

            if (IsNumeric(value))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (value is IDictionary dictionary)
            {
                return SerializeDictionary(dictionary, visited);
            }

            if (value is IEnumerable enumerable)
            {
                return SerializeArray(enumerable, visited);
            }

            var type = value.GetType();
            if (!type.IsValueType)
            {
                if (!visited.Add(value))
                {
                    return "null";
                }
            }

            try
            {
                return SerializeObject(value, visited);
            }
            finally
            {
                if (!type.IsValueType)
                {
                    visited.Remove(value);
                }
            }
        }

        static string SerializeDictionary(IDictionary dictionary, HashSet<object> visited)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            var isFirst = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!isFirst)
                {
                    builder.Append(',');
                }

                isFirst = false;
                builder.Append('"')
                    .Append(Escape(Convert.ToString(entry.Key, CultureInfo.InvariantCulture)))
                    .Append('"')
                    .Append(':')
                    .Append(SerializeValue(entry.Value, visited));
            }

            builder.Append('}');
            return builder.ToString();
        }

        static string SerializeArray(IEnumerable enumerable, HashSet<object> visited)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var isFirst = true;
            foreach (var item in enumerable)
            {
                if (!isFirst)
                {
                    builder.Append(',');
                }

                isFirst = false;
                builder.Append(SerializeValue(item, visited));
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string SerializeObject(object value, HashSet<object> visited)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            var isFirst = true;
            var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (var property in properties)
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var propertyValue = property.GetValue(value, null);
                if (propertyValue == null)
                {
                    continue;
                }

                AppendMember(builder, property.Name, propertyValue, visited, ref isFirst);
            }

            var fields = value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach (var field in fields)
            {
                var fieldValue = field.GetValue(value);
                if (fieldValue == null)
                {
                    continue;
                }

                AppendMember(builder, field.Name, fieldValue, visited, ref isFirst);
            }

            builder.Append('}');
            return builder.ToString();
        }

        static void AppendMember(StringBuilder builder, string name, object value, HashSet<object> visited, ref bool isFirst)
        {
            if (!isFirst)
            {
                builder.Append(',');
            }

            isFirst = false;
            builder.Append('"')
                .Append(Escape(name))
                .Append('"')
                .Append(':')
                .Append(SerializeValue(value, visited));
        }

        static bool IsNumeric(object value)
        {
            return value is byte
                || value is sbyte
                || value is short
                || value is ushort
                || value is int
                || value is uint
                || value is long
                || value is ulong
                || value is float
                || value is double
                || value is decimal;
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\b", "\\b")
                .Replace("\f", "\\f")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        sealed class Parser
        {
            readonly string json;
            int index;

            public Parser(string json)
            {
                this.json = json;
            }

            public object ParseValue()
            {
                SkipWhiteSpace();
                if (index >= json.Length)
                {
                    throw CreateException("JSON 意外结束。");
                }

                var current = json[index];
                switch (current)
                {
                    case '{':
                        return ParseObject();
                    case '[':
                        return ParseArray();
                    case '"':
                        return ParseString();
                    case 't':
                        ExpectKeyword("true");
                        return true;
                    case 'f':
                        ExpectKeyword("false");
                        return false;
                    case 'n':
                        ExpectKeyword("null");
                        return null;
                    default:
                        if (current == '-' || char.IsDigit(current))
                        {
                            return ParseNumber();
                        }

                        throw CreateException($"无法识别的 JSON 值起始字符 '{current}'。");
                }
            }

            public void EnsureFullyConsumed()
            {
                SkipWhiteSpace();
                if (index < json.Length)
                {
                    throw CreateException("JSON 尾部存在多余内容。");
                }
            }

            Dictionary<string, object> ParseObject()
            {
                Consume('{');
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWhiteSpace();
                if (TryConsume('}'))
                {
                    return result;
                }

                while (true)
                {
                    SkipWhiteSpace();
                    var key = ParseString();
                    SkipWhiteSpace();
                    Consume(':');
                    var value = ParseValue();
                    result[key] = value;
                    SkipWhiteSpace();
                    if (TryConsume('}'))
                    {
                        break;
                    }

                    Consume(',');
                }

                return result;
            }

            List<object> ParseArray()
            {
                Consume('[');
                var result = new List<object>();
                SkipWhiteSpace();
                if (TryConsume(']'))
                {
                    return result;
                }

                while (true)
                {
                    var value = ParseValue();
                    result.Add(value);
                    SkipWhiteSpace();
                    if (TryConsume(']'))
                    {
                        break;
                    }

                    Consume(',');
                }

                return result;
            }

            string ParseString()
            {
                Consume('"');
                var builder = new StringBuilder();
                while (index < json.Length)
                {
                    var current = json[index++];
                    if (current == '"')
                    {
                        return builder.ToString();
                    }

                    if (current != '\\')
                    {
                        builder.Append(current);
                        continue;
                    }

                    if (index >= json.Length)
                    {
                        throw CreateException("字符串转义序列不完整。");
                    }

                    var escaped = json[index++];
                    switch (escaped)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            builder.Append(ParseUnicodeEscape());
                            break;
                        default:
                            throw CreateException($"不支持的转义字符 '\\{escaped}'。");
                    }
                }

                throw CreateException("字符串缺少结束引号。");
            }

            object ParseNumber()
            {
                var start = index;
                if (json[index] == '-')
                {
                    index++;
                }

                ConsumeDigits();
                var isFloatingPoint = false;
                if (TryConsume('.'))
                {
                    isFloatingPoint = true;
                    ConsumeDigits();
                }

                if (TryConsume('e') || TryConsume('E'))
                {
                    isFloatingPoint = true;
                    if (TryConsume('+') || TryConsume('-'))
                    {
                    }

                    ConsumeDigits();
                }

                var token = json.Substring(start, index - start);
                if (isFloatingPoint)
                {
                    if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                    {
                        throw CreateException($"无法解析数字 '{token}'。");
                    }

                    return doubleValue;
                }

                if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    throw CreateException($"无法解析整数 '{token}'。");
                }

                return longValue;
            }

            char ParseUnicodeEscape()
            {
                if (index + 4 > json.Length)
                {
                    throw CreateException("Unicode 转义序列长度不足。");
                }

                var token = json.Substring(index, 4);
                index += 4;
                if (!ushort.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                {
                    throw CreateException($"无效的 Unicode 转义序列 '{token}'。");
                }

                return (char)codePoint;
            }

            void ExpectKeyword(string keyword)
            {
                if (index + keyword.Length > json.Length || !string.Equals(json.Substring(index, keyword.Length), keyword, StringComparison.Ordinal))
                {
                    throw CreateException($"期望关键字 '{keyword}'。");
                }

                index += keyword.Length;
            }

            void ConsumeDigits()
            {
                var start = index;
                while (index < json.Length && char.IsDigit(json[index]))
                {
                    index++;
                }

                if (start == index)
                {
                    throw CreateException("数字缺少有效的数字字符。");
                }
            }

            bool TryConsume(char expected)
            {
                if (index < json.Length && json[index] == expected)
                {
                    index++;
                    return true;
                }

                return false;
            }

            void Consume(char expected)
            {
                SkipWhiteSpace();
                if (index >= json.Length || json[index] != expected)
                {
                    throw CreateException($"期望字符 '{expected}'。");
                }

                index++;
            }

            void SkipWhiteSpace()
            {
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                {
                    index++;
                }
            }

            FormatException CreateException(string message)
            {
                return new FormatException($"{message} (position: {index})");
            }
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
