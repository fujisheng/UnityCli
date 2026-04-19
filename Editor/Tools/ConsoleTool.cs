using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("console", Description = "Read or clear Unity console logs", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly, Category = "editor")]
    public sealed class ConsoleTool : IUnityCliTool
    {
        const int DefaultCount = 10;

        static readonly string[] SupportedActions =
        {
            "get",
            "clear"
        };

        static readonly string[] SupportedTypes =
        {
            "error",
            "warning",
            "log"
        };

        public string Id => "console";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Read or clear Unity console logs",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "action",
                        type = "string",
                        description = "console action: get or clear",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "types",
                        type = "array",
                        description = "log type filters: error/warning/log",
                        required = false,
                        defaultValue = new[] { "error", "warning", "log" }
                    },
                    new ParamDescriptor
                    {
                        name = "count",
                        type = "integer",
                        description = "max entries to return",
                        required = false,
                        defaultValue = DefaultCount
                    },
                    new ParamDescriptor
                    {
                        name = "include_stacktrace",
                        type = "boolean",
                        description = "whether include stack trace",
                        required = false,
                        defaultValue = false
                    }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (!ArgsHelper.TryGetRequired(args, "action", out string action, out var error))
            {
                return error;
            }

            switch (action)
            {
                case "get":
                    return HandleGet(args);
                case "clear":
                    return HandleClear();
                default:
                    return ToolResult.Error("invalid_parameter", $"不支持的控制台操作 '{action}'。", new
                    {
                        parameter = "action",
                        value = action,
                        supportedActions = SupportedActions
                    });
            }
        }

        static ToolResult HandleGet(Dictionary<string, object> args)
        {
            if (!ArgsHelper.TryGetOptional(args, "count", DefaultCount, out int count, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "include_stacktrace", false, out bool includeStackTrace, out error))
            {
                return error;
            }

            if (count < 0)
            {
                return ToolResult.Error("invalid_parameter", "参数 'count' 不能小于 0。", new
                {
                    parameter = "count",
                    value = count
                });
            }

            if (!TryResolveTypeFilter(args, out var typeFilter, out error))
            {
                return error;
            }

            if (!ConsoleReflection.TryEnsureReady(out error))
            {
                return error;
            }

            if (!ConsoleReflection.TryStartGettingEntries(out error))
            {
                return error;
            }

            try
            {
                if (!ConsoleReflection.TryGetCount(out int totalCount, out error))
                {
                    return error;
                }

                var startIndex = Math.Max(totalCount - count, 0);
                var entries = new List<object>();
                for (var index = totalCount - 1; index >= startIndex; index--)
                {
                    if (!ConsoleReflection.TryReadEntry(index, out var rawEntry, out error))
                    {
                        return error;
                    }

                    var type = ResolveType(rawEntry.mode);
                    if (!typeFilter.Contains(type))
                    {
                        continue;
                    }

                    entries.Add(new
                    {
                        type,
                        message = rawEntry.message,
                        stackTrace = includeStackTrace ? rawEntry.stackTrace : string.Empty,
                        index
                    });
                }

                return ToolResult.Ok(new
                {
                    action = "get",
                    totalCount,
                    requestedCount = count,
                    returnedCount = entries.Count,
                    entries = entries.ToArray()
                });
            }
            finally
            {
                ConsoleReflection.EndGettingEntries();
            }
        }

        static ToolResult HandleClear()
        {
            if (!ConsoleReflection.TryEnsureReady(out var error))
            {
                return error;
            }

            if (!ConsoleReflection.TryClear(out error))
            {
                return error;
            }

            return ToolResult.Ok(new
            {
                action = "clear",
                cleared = true
            });
        }

        static bool TryResolveTypeFilter(Dictionary<string, object> args, out HashSet<string> filter, out ToolResult error)
        {
            error = null;
            filter = new HashSet<string>(SupportedTypes, StringComparer.Ordinal);
            if (args == null || !args.TryGetValue("types", out var rawTypes) || rawTypes == null)
            {
                return true;
            }

            if (!TryEnumerateTypeValues(rawTypes, out var values, out error))
            {
                return false;
            }

            filter.Clear();
            foreach (var value in values)
            {
                var normalized = value.Trim().ToLowerInvariant();
                if (!SupportedTypes.Contains(normalized, StringComparer.Ordinal))
                {
                    error = ToolResult.Error("invalid_parameter", "参数 'types' 仅支持 error/warning/log。", new
                    {
                        parameter = "types",
                        value
                    });
                    return false;
                }

                filter.Add(normalized);
            }

            return true;
        }

        static bool TryEnumerateTypeValues(object rawTypes, out IEnumerable<string> values, out ToolResult error)
        {
            error = null;
            values = Array.Empty<string>();

            if (rawTypes is string singleType)
            {
                values = new[] { singleType };
                return true;
            }

            if (rawTypes is IEnumerable enumerable)
            {
                var results = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        error = ToolResult.Error("invalid_parameter", "参数 'types' 不能包含 null。", new
                        {
                            parameter = "types"
                        });
                        return false;
                    }

                    results.Add(item.ToString() ?? string.Empty);
                }

                values = results;
                return true;
            }

            error = ToolResult.Error("invalid_parameter", "参数 'types' 必须是字符串或字符串数组。", new
            {
                parameter = "types",
                actualType = rawTypes.GetType().Name
            });
            return false;
        }

        static string ResolveType(int mode)
        {
            if ((mode & ErrorModeMask) != 0)
            {
                return "error";
            }

            if ((mode & WarningModeMask) != 0)
            {
                return "warning";
            }

            return "log";
        }

        const int ErrorModeMask =
            (1 << 0)
            | (1 << 1)
            | (1 << 4)
            | (1 << 6)
            | (1 << 8)
            | (1 << 11)
            | (1 << 12);

        const int WarningModeMask =
            (1 << 7)
            | (1 << 9)
            | (1 << 13);

        static class ConsoleReflection
        {
            static bool isInitialized;
            static Type logEntriesType;
            static Type logEntryType;
            static MethodInfo getCountMethod;
            static MethodInfo clearMethod;
            static MethodInfo getEntryInternalMethod;
            static MethodInfo startGettingEntriesMethod;
            static MethodInfo endGettingEntriesMethod;
            static FieldInfo messageField;
            static FieldInfo conditionField;
            static FieldInfo stackTraceField;
            static FieldInfo modeField;

            public static bool TryEnsureReady(out ToolResult error)
            {
                error = null;
                if (isInitialized)
                {
                    return true;
                }

                var editorAssembly = typeof(UnityEditor.Editor).Assembly;
                logEntriesType = editorAssembly.GetType("UnityEditor.LogEntries", false);
                logEntryType = editorAssembly.GetType("UnityEditor.LogEntry", false);
                if (logEntriesType == null || logEntryType == null)
                {
                    error = ToolResult.Error("tool_execution_failed", "无法访问 Unity 控制台内部类型。", new
                    {
                        logEntriesType = logEntriesType?.FullName,
                        logEntryType = logEntryType?.FullName
                    });
                    return false;
                }

                var staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                getCountMethod = logEntriesType.GetMethod("GetCount", staticFlags);
                clearMethod = logEntriesType.GetMethod("Clear", staticFlags);
                getEntryInternalMethod = logEntriesType.GetMethod("GetEntryInternal", staticFlags);
                startGettingEntriesMethod = logEntriesType.GetMethod("StartGettingEntries", staticFlags);
                endGettingEntriesMethod = logEntriesType.GetMethod("EndGettingEntries", staticFlags);
                if (getCountMethod == null || clearMethod == null || getEntryInternalMethod == null)
                {
                    error = ToolResult.Error("tool_execution_failed", "控制台反射方法解析失败。", new
                    {
                        getCount = getCountMethod != null,
                        clear = clearMethod != null,
                        getEntryInternal = getEntryInternalMethod != null,
                        startGettingEntries = startGettingEntriesMethod != null,
                        endGettingEntries = endGettingEntriesMethod != null
                    });
                    return false;
                }

                var instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                messageField = logEntryType.GetField("message", instanceFlags);
                conditionField = logEntryType.GetField("condition", instanceFlags);
                stackTraceField = logEntryType.GetField("callstack", instanceFlags)
                    ?? logEntryType.GetField("stackTrace", instanceFlags);
                modeField = logEntryType.GetField("mode", instanceFlags);
                // message 和 mode 是必须字段；condition 和 stackTrace 在 Unity 2022.3 中可能不存在
                // Unity 2022.3 的 LogEntry.message 包含完整消息+堆栈，没有独立的 stackTrace 字段
                if (messageField == null && conditionField == null)
                {
                    error = ToolResult.Error("tool_execution_failed", "控制台日志消息字段反射失败。", new
                    {
                        hasMessage = messageField != null,
                        hasCondition = conditionField != null,
                        hasMode = modeField != null
                    });
                    return false;
                }

                if (modeField == null)
                {
                    error = ToolResult.Error("tool_execution_failed", "控制台日志模式字段反射失败。", new
                    {
                        hasMode = false
                    });
                    return false;
                }

                isInitialized = true;
                return true;
            }

            public static bool TryStartGettingEntries(out ToolResult error)
            {
                error = null;
                if (startGettingEntriesMethod == null)
                {
                    // 无 StartGettingEntries 则跳过（兼容旧版 Unity）
                    return true;
                }

                try
                {
                    startGettingEntriesMethod.Invoke(null, null);
                    return true;
                }
                catch (Exception exception)
                {
                    error = ToolResult.Error("tool_execution_failed", "StartGettingEntries 调用失败。", exception.ToString());
                    return false;
                }
            }

            public static void EndGettingEntries()
            {
                if (endGettingEntriesMethod == null)
                {
                    return;
                }

                try
                {
                    endGettingEntriesMethod.Invoke(null, null);
                }
                catch
                {
                    // EndGettingEntries 失败不应阻断已有结果
                }
            }

            public static bool TryGetCount(out int totalCount, out ToolResult error)
            {
                totalCount = 0;
                error = null;

                try
                {
                    var rawCount = getCountMethod.Invoke(null, null);
                    totalCount = Convert.ToInt32(rawCount);
                    if (totalCount < 0)
                    {
                        totalCount = 0;
                    }

                    return true;
                }
                catch (Exception exception)
                {
                    error = ToolResult.Error("tool_execution_failed", "读取控制台日志数量失败。", exception.ToString());
                    return false;
                }
            }

            public static bool TryClear(out ToolResult error)
            {
                error = null;
                try
                {
                    clearMethod.Invoke(null, null);
                    return true;
                }
                catch (Exception exception)
                {
                    error = ToolResult.Error("tool_execution_failed", "清空控制台失败。", exception.ToString());
                    return false;
                }
            }

            public static bool TryReadEntry(int index, out ConsoleEntry entry, out ToolResult error)
            {
                entry = default;
                error = null;

                object logEntry;
                try
                {
                    logEntry = Activator.CreateInstance(logEntryType);
                }
                catch (Exception exception)
                {
                    error = ToolResult.Error("tool_execution_failed", "创建控制台日志对象失败。", exception.ToString());
                    return false;
                }

                try
                {
                    var invokeResult = getEntryInternalMethod.Invoke(null, new[] { (object)index, logEntry });
                    var success = getEntryInternalMethod.ReturnType == typeof(void)
                        || Convert.ToBoolean(invokeResult);
                    if (!success)
                    {
                        error = ToolResult.Error("tool_execution_failed", "读取控制台日志失败。", new
                        {
                            index
                        });
                        return false;
                    }

                    // 优先从 message 字段读取，回退到 condition 字段
                    var message = messageField != null
                        ? messageField.GetValue(logEntry)?.ToString()
                        : conditionField?.GetValue(logEntry)?.ToString();
                    if (string.IsNullOrEmpty(message))
                    {
                        message = conditionField?.GetValue(logEntry)?.ToString() ?? string.Empty;
                    }

                    // 堆栈跟踪：优先使用独立字段，不存在时从 message 内容中提取
                    var stackTrace = stackTraceField != null
                        ? (stackTraceField.GetValue(logEntry)?.ToString() ?? string.Empty)
                        : ExtractStackTrace(message);

                    entry = new ConsoleEntry
                    {
                        message = message,
                        stackTrace = stackTrace,
                        mode = Convert.ToInt32(modeField.GetValue(logEntry) ?? 0)
                    };
                    return true;
                }
                catch (Exception exception)
                {
                    error = ToolResult.Error("tool_execution_failed", "解析控制台日志失败。", new
                    {
                        index,
                        exception = exception.ToString()
                    });
                    return false;
                }
            }

            /// <summary>
            /// 从完整消息中提取堆栈跟踪部分。
            /// Unity 2022.3 的 LogEntry.message 包含消息+堆栈，没有独立的 stackTrace 字段。
            /// </summary>
            static string ExtractStackTrace(string fullMessage)
            {
                if (string.IsNullOrEmpty(fullMessage))
                {
                    return string.Empty;
                }

                var lines = fullMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length <= 1)
                {
                    return string.Empty;
                }

                var stackStartIndex = -1;
                for (var i = 1; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("at ")
                        || trimmed.StartsWith("UnityEngine.")
                        || trimmed.StartsWith("UnityEditor.")
                        || trimmed.Contains("(at "))
                    {
                        stackStartIndex = i;
                        break;
                    }
                }

                if (stackStartIndex > 0)
                {
                    return string.Join("\n", lines, stackStartIndex, lines.Length - stackStartIndex);
                }

                return string.Empty;
            }
        }

        struct ConsoleEntry
        {
            public string message;
            public string stackTrace;
            public int mode;
        }
    }
}
