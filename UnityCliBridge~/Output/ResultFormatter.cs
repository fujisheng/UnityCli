using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace UnityCli.Output
{
    enum CliOutputFormat
    {
        Json,
        PrettyJson,
        Human
    }

    static class ResultFormatter
    {
        static readonly string[] supportedOutputFormatNames = { "json", "pretty-json", "human" };

        static CliOutputFormat outputFormat = CliOutputFormat.Human;

        public static IReadOnlyList<string> SupportedOutputFormatNames => supportedOutputFormatNames;

        public static void SetOutputFormat(CliOutputFormat format)
        {
            outputFormat = format;
        }

        public static bool TryParseOutputFormat(string? rawFormat, out CliOutputFormat format)
        {
            switch ((rawFormat ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "json":
                    format = CliOutputFormat.Json;
                    return true;
                case "pretty":
                case "pretty-json":
                    format = CliOutputFormat.PrettyJson;
                    return true;
                case "human":
                    format = CliOutputFormat.Human;
                    return true;
                default:
                    format = CliOutputFormat.Json;
                    return false;
            }
        }

        public static int WritePayloadAndGetExitCode(object? payload)
        {
            WritePayload(payload);
            return GetExitCode(payload);
        }

        public static void WritePayload(object? payload)
        {
            Console.Out.WriteLine(FormatPayload(payload));
        }

        public static void WriteSuccess(object? data, string? message = null)
        {
            WritePayload(CreateSuccessPayload(data, message));
        }

        public static void WriteError(string code, string message, object? details = null)
        {
            WritePayload(CreateErrorPayload(code, message, details));
        }

        public static object CreateSuccessPayload(object? data, string? message = null)
        {
            return new
            {
                ok = true,
                message,
                data
            };
        }

        public static object CreateErrorPayload(string code, string message, object? details = null)
        {
            return new
            {
                ok = false,
                error = new
                {
                    code,
                    message,
                    details
                }
            };
        }

        public static int GetExitCode(object? payload)
        {
            if (payload == null)
            {
                return 1;
            }

            if (CliObjectAccessor.TryGetBoolean(payload, "ok", out var ok))
            {
                if (ok)
                {
                    return 0;
                }

                return MapExitCode(TryGetErrorCode(payload));
            }

            if (CliObjectAccessor.TryGetMember(payload, "result", out var nestedResult) && nestedResult != null)
            {
                return GetExitCode(nestedResult);
            }

            if (CliObjectAccessor.TryGetString(payload, "status", out var status)
                && string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (TryGetErrorCode(payload) != null)
            {
                return MapExitCode(TryGetErrorCode(payload));
            }

            return 0;
        }

        public static int GetExitCodeForError(string code)
        {
            return MapExitCode(code);
        }

        static int MapExitCode(string? code)
        {
            return string.Equals(code, "bridge_unavailable", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }

        static string? TryGetErrorCode(object payload)
        {
            if (!CliObjectAccessor.TryGetMember(payload, "error", out var errorPayload) || errorPayload == null)
            {
                return null;
            }

            return CliObjectAccessor.TryGetString(errorPayload, "code", out var code) ? code : null;
        }

        static string FormatPayload(object? payload)
        {
            var json = CliJson.Serialize(payload);
            return outputFormat switch
            {
                CliOutputFormat.PrettyJson => PrettyPrintJson(json),
                CliOutputFormat.Human => HumanizePayload(json),
                _ => json
            };
        }

        static string PrettyPrintJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            var builder = new StringBuilder(json.Length + 64);
            var indent = 0;
            var inString = false;
            var escaping = false;

            foreach (var current in json)
            {
                if (escaping)
                {
                    builder.Append(current);
                    escaping = false;
                    continue;
                }

                if (inString)
                {
                    builder.Append(current);
                    if (current == '\\')
                    {
                        escaping = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (current)
                {
                    case '"':
                        inString = true;
                        builder.Append(current);
                        break;

                    case '{':
                    case '[':
                        builder.Append(current);
                        builder.AppendLine();
                        indent++;
                        AppendIndent(builder, indent);
                        break;

                    case '}':
                    case ']':
                        builder.AppendLine();
                        indent = Math.Max(0, indent - 1);
                        AppendIndent(builder, indent);
                        builder.Append(current);
                        break;

                    case ',':
                        builder.Append(current);
                        builder.AppendLine();
                        AppendIndent(builder, indent);
                        break;

                    case ':':
                        builder.Append(": ");
                        break;

                    default:
                        if (!char.IsWhiteSpace(current))
                        {
                            builder.Append(current);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        static string HumanizePayload(string json)
        {
            if (!CliJson.TryDeserialize(json, out var normalizedPayload, out _))
            {
                return PrettyPrintJson(json);
            }

            var builder = new StringBuilder();
            WriteHumanValue(builder, normalizedPayload, 0, null, isListItem: false);
            return builder.ToString().TrimEnd();
        }

        static void WriteHumanValue(StringBuilder builder, object? value, int indent, string? label, bool isListItem)
        {
            if (value is string stringValue && stringValue.Contains("\n", StringComparison.Ordinal))
            {
                WriteHumanMultilineScalar(builder, stringValue, indent, label, isListItem);
                return;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                WriteHumanDictionary(builder, dictionary, indent, label, isListItem);
                return;
            }

            if (value is IDictionary<string, object> genericDictionary)
            {
                WriteHumanDictionary(builder, genericDictionary, indent, label, isListItem);
                return;
            }

            if (value is IList<object> list)
            {
                WriteHumanList(builder, list, indent, label, isListItem);
                return;
            }

            if (value is IList rawList && value is not string)
            {
                WriteHumanList(builder, rawList, indent, label, isListItem);
                return;
            }

            WriteHumanScalar(builder, FormatHumanScalar(value), indent, label, isListItem);
        }

        static void WriteHumanDictionary(StringBuilder builder, IEnumerable<KeyValuePair<string, object>> members, int indent, string? label, bool isListItem)
        {
            var orderedMembers = new List<KeyValuePair<string, object>>(members);
            if (orderedMembers.Count == 0)
            {
                WriteHumanScalar(builder, "{}", indent, label, isListItem);
                return;
            }

            var childIndent = WriteHumanContainerHeader(builder, indent, label, isListItem);
            foreach (var pair in EnumerateHumanMembers(orderedMembers))
            {
                WriteHumanValue(builder, pair.Value, childIndent, pair.Key, isListItem: false);
            }
        }

        static void WriteHumanList(StringBuilder builder, IEnumerable items, int indent, string? label, bool isListItem)
        {
            var materializedItems = new List<object?>();
            foreach (var item in items)
            {
                materializedItems.Add(item);
            }

            if (materializedItems.Count == 0)
            {
                WriteHumanScalar(builder, "[]", indent, label, isListItem);
                return;
            }

            var childIndent = WriteHumanContainerHeader(builder, indent, label, isListItem);
            foreach (var item in materializedItems)
            {
                WriteHumanValue(builder, item, childIndent, null, isListItem: true);
            }
        }

        static int WriteHumanContainerHeader(StringBuilder builder, int indent, string? label, bool isListItem)
        {
            if (!string.IsNullOrEmpty(label))
            {
                AppendIndent(builder, indent);
                builder.Append(label).Append(':').AppendLine();
                return indent + 2;
            }

            if (isListItem)
            {
                AppendIndent(builder, indent);
                builder.Append('-').AppendLine();
                return indent + 2;
            }

            return indent;
        }

        static void WriteHumanScalar(StringBuilder builder, string valueText, int indent, string? label, bool isListItem)
        {
            AppendIndent(builder, indent);
            if (isListItem)
            {
                builder.Append("- ");
            }

            if (!string.IsNullOrEmpty(label))
            {
                builder.Append(label).Append(": ");
            }

            builder.Append(valueText).AppendLine();
        }

        static void WriteHumanMultilineScalar(StringBuilder builder, string value, int indent, string? label, bool isListItem)
        {
            AppendIndent(builder, indent);
            if (isListItem)
            {
                builder.Append("- ");
            }

            if (!string.IsNullOrEmpty(label))
            {
                builder.Append(label).Append(": ");
            }

            builder.Append('|').AppendLine();
            var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            foreach (var line in lines)
            {
                AppendIndent(builder, indent + 2);
                builder.AppendLine(line);
            }
        }

        static IEnumerable<KeyValuePair<string, object>> EnumerateHumanMembers(List<KeyValuePair<string, object>> members)
        {
            var preferredOrder = new[]
            {
                "ok",
                "status",
                "message",
                "tool",
                "jobId",
                "requestId",
                "error",
                "data",
                "result",
                "details",
                "usage",
                "outputFormats"
            };

            var yieldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preferredKey in preferredOrder)
            {
                for (var index = 0; index < members.Count; index++)
                {
                    var pair = members[index];
                    if (!string.Equals(pair.Key, preferredKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (yieldedKeys.Add(pair.Key))
                    {
                        yield return pair;
                    }
                }
            }

            foreach (var pair in members)
            {
                if (yieldedKeys.Add(pair.Key))
                {
                    yield return pair;
                }
            }
        }

        static string FormatHumanScalar(object? value)
        {
            if (value == null)
            {
                return "null";
            }

            return value switch
            {
                bool boolValue => boolValue ? "true" : "false",
                string stringValue => FormatHumanString(stringValue),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => FormatHumanString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
            };
        }

        static string FormatHumanString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (!NeedsHumanQuotes(value))
            {
                return value;
            }

            return '"' + value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
        }

        static bool NeedsHumanQuotes(string value)
        {
            if (value.Length == 0)
            {
                return true;
            }

            if (value.StartsWith(" ", StringComparison.Ordinal)
                || value.EndsWith(" ", StringComparison.Ordinal)
                || value.StartsWith("-", StringComparison.Ordinal)
                || value.StartsWith("{", StringComparison.Ordinal)
                || value.StartsWith("[", StringComparison.Ordinal)
                || value.Contains(": ", StringComparison.Ordinal)
                || value.Contains('#', StringComparison.Ordinal)
                || value.Contains('\t', StringComparison.Ordinal)
                || value.Contains('\r', StringComparison.Ordinal)
                || value.Contains('\n', StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        static void AppendIndent(StringBuilder builder, int indent)
        {
            builder.Append(' ', Math.Max(0, indent));
        }
    }

    static class CliObjectAccessor
    {
        public static bool TryGetMember(object? source, string name, out object? value)
        {
            value = null;
            if (source == null || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (source is IDictionary<string, object> typedDictionary)
            {
                if (typedDictionary.TryGetValue(name, out value))
                {
                    return true;
                }

                foreach (var pair in typedDictionary)
                {
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value;
                        return true;
                    }
                }

                return false;
            }

            if (source is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string key && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = entry.Value;
                        return true;
                    }
                }

                return false;
            }

            var properties = source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (var property in properties)
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = property.GetValue(source, null);
                return true;
            }

            var fields = source.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach (var field in fields)
            {
                if (!string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = field.GetValue(source);
                return true;
            }

            return false;
        }

        public static bool TryGetString(object? source, string name, out string value)
        {
            value = string.Empty;
            if (!TryGetMember(source, name, out var raw) || raw == null)
            {
                return false;
            }

            value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            return true;
        }

        public static bool TryGetBoolean(object? source, string name, out bool value)
        {
            value = false;
            if (!TryGetMember(source, name, out var raw) || raw == null)
            {
                return false;
            }

            switch (raw)
            {
                case bool boolValue:
                    value = boolValue;
                    return true;
                case string stringValue when bool.TryParse(stringValue, out var parsedBool):
                    value = parsedBool;
                    return true;
                case byte or sbyte or short or ushort or int or uint or long or ulong:
                    value = Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0L;
                    return true;
                case float or double or decimal:
                    value = Math.Abs(Convert.ToDouble(raw, CultureInfo.InvariantCulture)) > double.Epsilon;
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryGetInt(object? source, string name, out int value)
        {
            value = 0;
            if (!TryGetMember(source, name, out var raw) || raw == null)
            {
                return false;
            }

            switch (raw)
            {
                case int intValue:
                    value = intValue;
                    return true;
                case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                    value = (int)longValue;
                    return true;
                case double doubleValue when doubleValue >= int.MinValue && doubleValue <= int.MaxValue:
                    value = (int)doubleValue;
                    return true;
                case string stringValue when int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt):
                    value = parsedInt;
                    return true;
                default:
                    return false;
            }
        }
    }

    static class CliJson
    {
        public static string Serialize(object? value)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return SerializeValue(value, visited);
        }

        public static bool TryDeserialize(string json, out object? value, out string? error)
        {
            value = null;
            error = null;

            try
            {
                value = Deserialize(json);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static bool TryDeserializeObject(string json, out Dictionary<string, object> value, out string? error)
        {
            value = null!;
            error = null;
            if (!TryDeserialize(json, out var raw, out error))
            {
                return false;
            }

            if (raw is Dictionary<string, object> dictionary)
            {
                value = dictionary;
                return true;
            }

            error = "JSON 根节点必须是对象。";
            return false;
        }

        static object? Deserialize(string json)
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

        static string SerializeValue(object? value, HashSet<object> visited)
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
                return '"' + Escape(value.ToString() ?? string.Empty) + '"';
            }

            if (IsNumeric(value))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
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
            var first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append('"')
                    .Append(Escape(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty))
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
            var first = true;
            foreach (var item in enumerable)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(SerializeValue(item, visited));
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string SerializeObject(object value, HashSet<object> visited)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            var first = true;

            foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
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

                AppendMember(builder, property.Name, propertyValue, visited, ref first);
            }

            foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var fieldValue = field.GetValue(value);
                if (fieldValue == null)
                {
                    continue;
                }

                AppendMember(builder, field.Name, fieldValue, visited, ref first);
            }

            builder.Append('}');
            return builder.ToString();
        }

        static void AppendMember(StringBuilder builder, string name, object value, HashSet<object> visited, ref bool first)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append('"')
                .Append(Escape(name))
                .Append('"')
                .Append(':')
                .Append(SerializeValue(value, visited));
        }

        static bool IsNumeric(object value)
        {
            return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
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

            public object? ParseValue()
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
                    result[key] = ParseValue()!;
                    SkipWhiteSpace();
                    if (TryConsume('}'))
                    {
                        return result;
                    }

                    Consume(',');
                }
            }

            List<object?> ParseArray()
            {
                Consume('[');
                var result = new List<object?>();
                SkipWhiteSpace();
                if (TryConsume(']'))
                {
                    return result;
                }

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhiteSpace();
                    if (TryConsume(']'))
                    {
                        return result;
                    }

                    Consume(',');
                }
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
            public static readonly ReferenceEqualityComparer Instance = new();

            public new bool Equals(object? x, object? y)
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
