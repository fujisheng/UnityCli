using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Core
{
    [InitializeOnLoad]
    public static class UnityCliDispatcher
    {
        static UnityCliDispatcher()
        {
            EditorApplication.update -= HandleEditorUpdate;
            EditorApplication.update += HandleEditorUpdate;
        }

        public static Task<InvokeResponse> Enqueue(InvokeRequest request)
        {
            return UnityCliDispatcherQueue.Enqueue(request);
        }

        public static InvokeResponse Dispatch(InvokeRequest request)
        {
            var normalizedRequest = NormalizeRequest(request);
            UnityCliBridgeLogStore.AddInvokeReceived(normalizedRequest);
            Debug.Log($"<color=#4CAF50>[UnityCli]</color> 调用工具: {normalizedRequest.tool} (requestId={normalizedRequest.requestId})");

            if (!UnityCliAllowlist.IsAllowed(normalizedRequest.tool))
            {
                Debug.LogWarning($"<color=#FFC107>[UnityCli]</color> 工具 '{normalizedRequest.tool}' 未在白名单中启用。");
                return CreateLoggedErrorResponse(normalizedRequest, "tool_not_found", $"未找到工具 '{normalizedRequest.tool}'。");
            }

            if (!UnityCliRegistry.TryGetTool(normalizedRequest.tool, out var tool)
                || !UnityCliRegistry.TryGetDescriptor(normalizedRequest.tool, out var descriptor))
            {
                Debug.LogWarning($"<color=#FFC107>[UnityCli]</color> 工具 '{normalizedRequest.tool}' 未注册。");
                return CreateLoggedErrorResponse(normalizedRequest, "tool_not_found", $"未找到工具 '{normalizedRequest.tool}'。");
            }

            var editorState = ToolContext.EditorStateSnapshot.Capture();
            if (!IsModeAllowed(descriptor.mode, editorState.IsPlaying))
            {
                Debug.LogWarning($"<color=#FFC107>[UnityCli]</color> 工具 '{normalizedRequest.tool}' 模式不匹配 (mode={descriptor.mode}, isPlaying={editorState.IsPlaying})。");
                return CreateLoggedErrorResponse(normalizedRequest, "wrong_mode", $"工具 '{normalizedRequest.tool}' 当前运行模式不匹配。");
            }

            if (IsEditorBusy(editorState))
            {
                Debug.LogWarning($"<color=#FFC107>[UnityCli]</color> Editor 忙碌，跳过工具 '{normalizedRequest.tool}'。");
                return CreateLoggedErrorResponse(normalizedRequest, "editor_busy", "Unity Editor 当前忙碌，暂时无法执行请求。");
            }

            var args = normalizedRequest.args ?? new Dictionary<string, object>(StringComparer.Ordinal);
            var pendingJobRegistration = tool is IUnityCliAsyncTool ? UnityCliJobManager.CreatePendingJobRegistration() : null;
            var context = ToolContext.Create(editorState, null, pendingJobRegistration);

            ToolResult result;
            try
            {
                result = tool.Execute(args, context);
            }
            catch (Exception exception)
            {
                Debug.LogError($"<color=#F44336>[UnityCli]</color> 工具 '{normalizedRequest.tool}' 执行异常: {exception.Message}");
                Debug.LogException(exception);
                return CreateLoggedErrorResponse(normalizedRequest, "tool_execution_failed", $"工具 '{normalizedRequest.tool}' 执行失败。", exception.ToString());
            }

            if (result == null)
            {
                Debug.LogError($"<color=#F44336>[UnityCli]</color> 工具 '{normalizedRequest.tool}' 返回了空结果。");
                return CreateLoggedErrorResponse(normalizedRequest, "tool_execution_failed", $"工具 '{normalizedRequest.tool}' 返回了空结果。");
            }

            if (string.Equals(result.Status, "pending", StringComparison.Ordinal))
            {
                if (!(tool is IUnityCliAsyncTool asyncTool))
                {
                    return CreateLoggedErrorResponse(normalizedRequest, "tool_execution_failed", $"工具 '{normalizedRequest.tool}' 返回了 pending，但未实现 IUnityCliAsyncTool。");
                }

                if (!string.Equals(result.JobId, context.CurrentJobId, StringComparison.Ordinal))
                {
                    return CreateLoggedErrorResponse(normalizedRequest, "tool_execution_failed", $"工具 '{normalizedRequest.tool}' 返回了无效的 JobId。");
                }

                Debug.Log($"<color=#4CAF50>[UnityCli]</color> 工具 '{normalizedRequest.tool}' 返回异步 Job: {result.JobId}");
                UnityCliJobManager.RegisterPendingJob(normalizedRequest, asyncTool, context, result);
            }

            Debug.Log($"<color=#4CAF50>[UnityCli]</color> 工具 '{normalizedRequest.tool}' 完成 (status={result.Status})");
            return LogAndReturn(normalizedRequest, result.ToInvokeResponse(normalizedRequest.requestId));
        }

        public static JobStatus GetJobStatus(string jobId)
        {
            return UnityCliJobManager.GetJobStatus(jobId);
        }

        static void HandleEditorUpdate()
        {
            if (!UnityCliDispatcherQueue.TryDequeue(out var request))
            {
                return;
            }

            InvokeResponse response;
            try
            {
                response = Dispatch(request);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                response = CreateErrorResponse(request?.requestId ?? string.Empty, "tool_execution_failed", "主线程调度执行失败。", exception.ToString());
            }

            UnityCliDispatcherQueue.Complete(request, response);
        }

        static bool IsEditorBusy(ToolContext.EditorStateSnapshot editorState)
        {
            return editorState.IsCompiling || editorState.IsUpdating;
        }

        static bool IsModeAllowed(ToolMode mode, bool isPlaying)
        {
            switch (mode)
            {
                case ToolMode.EditOnly:
                    return !isPlaying;
                case ToolMode.PlayOnly:
                    return isPlaying;
                default:
                    return true;
            }
        }

        static InvokeRequest NormalizeRequest(InvokeRequest request)
        {
            return new InvokeRequest
            {
                requestId = string.IsNullOrWhiteSpace(request?.requestId) ? Guid.NewGuid().ToString("N") : request.requestId,
                tool = request?.tool ?? string.Empty,
                args = request?.args != null
                    ? new Dictionary<string, object>(request.args, StringComparer.Ordinal)
                    : new Dictionary<string, object>(StringComparer.Ordinal),
                timeoutMs = request?.timeoutMs
            };
        }

        static InvokeResponse CreateErrorResponse(string requestId, string code, string message, object details = null)
        {
            return ToolResult.Error(code, message, details).ToInvokeResponse(requestId ?? string.Empty);
        }

        static InvokeResponse CreateLoggedErrorResponse(InvokeRequest request, string code, string message, object details = null)
        {
            return LogAndReturn(request, CreateErrorResponse(request?.requestId, code, message, details));
        }

        static InvokeResponse LogAndReturn(InvokeRequest request, InvokeResponse response)
        {
            UnityCliBridgeLogStore.AddInvokeCompleted(request, response);
            return response;
        }
    }

    public sealed class UnityCliBridgeLogEntry
    {
        public UnityCliBridgeLogEntry(
            long sequence,
            DateTime timestampUtc,
            string category,
            string message,
            string toolId,
            string requestId,
            string status,
            string details,
            LogType logType)
        {
            Sequence = sequence;
            TimestampUtc = timestampUtc;
            Category = category ?? string.Empty;
            Message = message ?? string.Empty;
            ToolId = toolId ?? string.Empty;
            RequestId = requestId ?? string.Empty;
            Status = status ?? string.Empty;
            Details = details ?? string.Empty;
            LogType = logType;
        }

        public long Sequence { get; }

        public DateTime TimestampUtc { get; }

        public string Category { get; }

        public string Message { get; }

        public string ToolId { get; }

        public string RequestId { get; }

        public string Status { get; }

        public string Details { get; }

        public LogType LogType { get; }
    }

    public static class UnityCliBridgeLogStore
    {
        const int MaxInMemoryEntries = 20;
        const int MaxPersistedEntries = 100;
        const int SerializedFieldCount = 9;
        const string PersistedLogDirectory = "Library/UnityCliBridge";
        const string PersistedLogFileName = "bridge-log.tsv";
        const string EmptyFieldToken = "_";

        static readonly object syncRoot = new object();
        static readonly List<UnityCliBridgeLogEntry> recentEntries = new List<UnityCliBridgeLogEntry>(MaxInMemoryEntries);

        static long nextSequence;
        static bool isSequenceInitialized;
        static string persistedLogPath;
        static string lastStorageWarning;

        public static void AddSystem(string message, LogType logType = LogType.Log, string details = null)
        {
            Add("system", message, string.Empty, string.Empty, string.Empty, details, logType);
        }

        public static void AddInvokeReceived(InvokeRequest request)
        {
            if (request == null)
            {
                return;
            }

            Add(
                "invoke",
                "收到调用请求",
                request.tool,
                request.requestId,
                "received",
                BuildInvokeRequestDetails(request),
                LogType.Log);
        }

        public static void AddInvokeCompleted(InvokeRequest request, InvokeResponse response)
        {
            Add(
                "invoke",
                BuildInvokeResponseMessage(response),
                request?.tool,
                response?.requestId ?? request?.requestId,
                response?.status,
                BuildInvokeResponseDetails(response),
                ResolveInvokeLogType(response));
        }

        public static void AddJobQueued(string jobId, string toolId, string requestId)
        {
            Add("job", "异步 Job 已创建", toolId, requestId, "pending", BuildJobDetails(jobId), LogType.Warning);
        }

        public static void AddJobUpdated(string jobId, string toolId, string requestId, string status, bool isSuccess, string details = null)
        {
            Add(
                "job",
                BuildJobMessage(status, isSuccess),
                toolId,
                requestId,
                status,
                BuildJobDetails(jobId, details),
                ResolveJobLogType(status, isSuccess));
        }

        public static List<UnityCliBridgeLogEntry> GetEntries(int offset, int limit, out int totalCount)
        {
            lock (syncRoot)
            {
                var allEntries = LoadMergedEntriesUnsafe();
                totalCount = allEntries.Count;
                if (totalCount == 0 || limit <= 0 || offset >= totalCount)
                {
                    return new List<UnityCliBridgeLogEntry>();
                }

                var normalizedOffset = Math.Max(offset, 0);
                var count = Math.Min(limit, totalCount - normalizedOffset);
                var result = new List<UnityCliBridgeLogEntry>(count);
                var startIndex = totalCount - 1 - normalizedOffset;
                for (var index = 0; index < count; index++)
                {
                    result.Add(allEntries[startIndex - index]);
                }

                return result;
            }
        }

        public static List<UnityCliBridgeLogEntry> GetAllEntries(out int totalCount)
        {
            lock (syncRoot)
            {
                var allEntries = LoadMergedEntriesUnsafe();
                totalCount = allEntries.Count;
                return allEntries
                    .AsEnumerable()
                    .Reverse()
                    .ToList();
            }
        }

        public static void Clear()
        {
            lock (syncRoot)
            {
                recentEntries.Clear();
                nextSequence = 0;
                isSequenceInitialized = true;
                DeletePersistedEntriesUnsafe();
            }
        }

        static void Add(string category, string message, string toolId, string requestId, string status, string details, LogType logType)
        {
            lock (syncRoot)
            {
                EnsureSequenceInitializedUnsafe();

                var entry = new UnityCliBridgeLogEntry(
                    ++nextSequence,
                    DateTime.UtcNow,
                    category,
                    message,
                    toolId,
                    requestId,
                    status,
                    details,
                    logType);

                recentEntries.Add(entry);
                TrimRecentEntriesUnsafe();
                PersistEntryUnsafe(entry);
            }
        }

        static List<UnityCliBridgeLogEntry> LoadMergedEntriesUnsafe()
        {
            var persistedEntries = LoadPersistedEntriesUnsafe();
            if (recentEntries.Count == 0)
            {
                return persistedEntries;
            }

            var seenSequences = new HashSet<long>();
            var mergedEntries = new List<UnityCliBridgeLogEntry>(persistedEntries.Count + recentEntries.Count);

            foreach (var entry in persistedEntries)
            {
                if (seenSequences.Add(entry.Sequence))
                {
                    mergedEntries.Add(entry);
                }
            }

            foreach (var entry in recentEntries)
            {
                if (seenSequences.Add(entry.Sequence))
                {
                    mergedEntries.Add(entry);
                }
            }

            mergedEntries.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            return mergedEntries;
        }

        static void TrimRecentEntriesUnsafe()
        {
            var overflowCount = recentEntries.Count - MaxInMemoryEntries;
            if (overflowCount > 0)
            {
                recentEntries.RemoveRange(0, overflowCount);
            }
        }

        static void EnsureSequenceInitializedUnsafe()
        {
            if (isSequenceInitialized)
            {
                return;
            }

            var persistedEntries = LoadPersistedEntriesUnsafe();
            if (persistedEntries.Count > 0)
            {
                nextSequence = persistedEntries[persistedEntries.Count - 1].Sequence;
            }

            isSequenceInitialized = true;
        }

        static void PersistEntryUnsafe(UnityCliBridgeLogEntry entry)
        {
            var persistedEntries = LoadPersistedEntriesUnsafe();
            persistedEntries.Add(entry);

            var overflowCount = persistedEntries.Count - MaxPersistedEntries;
            if (overflowCount > 0)
            {
                persistedEntries.RemoveRange(0, overflowCount);
            }

            SavePersistedEntriesUnsafe(persistedEntries);
        }

        static List<UnityCliBridgeLogEntry> LoadPersistedEntriesUnsafe()
        {
            var path = GetPersistedLogPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return new List<UnityCliBridgeLogEntry>();
            }

            try
            {
                var persistedEntries = new List<UnityCliBridgeLogEntry>();
                foreach (var line in File.ReadLines(path))
                {
                    if (TryDeserializeEntry(line, out var entry))
                    {
                        persistedEntries.Add(entry);
                    }
                }

                lastStorageWarning = null;
                return persistedEntries;
            }
            catch (Exception exception)
            {
                ReportStorageWarning("读取 Bridge 日志文件失败", exception);
                return new List<UnityCliBridgeLogEntry>();
            }
        }

        static void SavePersistedEntriesUnsafe(List<UnityCliBridgeLogEntry> entries)
        {
            var path = GetPersistedLogPath();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (entries == null || entries.Count == 0)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    lastStorageWarning = null;
                    return;
                }

                var serializedLines = new string[entries.Count];
                for (var index = 0; index < entries.Count; index++)
                {
                    serializedLines[index] = SerializeEntry(entries[index]);
                }

                File.WriteAllLines(path, serializedLines, new UTF8Encoding(false));
                lastStorageWarning = null;
            }
            catch (Exception exception)
            {
                ReportStorageWarning("写入 Bridge 日志文件失败", exception);
            }
        }

        static void DeletePersistedEntriesUnsafe()
        {
            var path = GetPersistedLogPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                lastStorageWarning = null;
                return;
            }

            try
            {
                File.Delete(path);
                lastStorageWarning = null;
            }
            catch (Exception exception)
            {
                ReportStorageWarning("清理 Bridge 日志文件失败", exception);
            }
        }

        static string GetPersistedLogPath()
        {
            if (!string.IsNullOrEmpty(persistedLogPath))
            {
                return persistedLogPath;
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            if (string.IsNullOrEmpty(projectRoot))
            {
                return string.Empty;
            }

            persistedLogPath = Path.Combine(projectRoot, PersistedLogDirectory, PersistedLogFileName);
            return persistedLogPath;
        }

        static string SerializeEntry(UnityCliBridgeLogEntry entry)
        {
            return string.Join("\t", new[]
            {
                entry.Sequence.ToString(),
                entry.TimestampUtc.Ticks.ToString(),
                ((int)entry.LogType).ToString(),
                EncodeField(entry.Category),
                EncodeField(entry.Message),
                EncodeField(entry.ToolId),
                EncodeField(entry.RequestId),
                EncodeField(entry.Status),
                EncodeField(entry.Details)
            });
        }

        static bool TryDeserializeEntry(string line, out UnityCliBridgeLogEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var parts = line.Split(new[] { '\t' }, SerializedFieldCount, StringSplitOptions.None);
            if (parts.Length != SerializedFieldCount)
            {
                return false;
            }

            if (!long.TryParse(parts[0], out var sequence)
                || !long.TryParse(parts[1], out var timestampTicks)
                || !int.TryParse(parts[2], out var logTypeValue))
            {
                return false;
            }

            if (!TryDecodeField(parts[3], out var category)
                || !TryDecodeField(parts[4], out var message)
                || !TryDecodeField(parts[5], out var toolId)
                || !TryDecodeField(parts[6], out var requestId)
                || !TryDecodeField(parts[7], out var status)
                || !TryDecodeField(parts[8], out var details))
            {
                return false;
            }

            try
            {
                entry = new UnityCliBridgeLogEntry(
                    sequence,
                    new DateTime(timestampTicks, DateTimeKind.Utc),
                    category,
                    message,
                    toolId,
                    requestId,
                    status,
                    details,
                    (LogType)logTypeValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string EncodeField(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return EmptyFieldToken;
            }

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        static bool TryDecodeField(string value, out string decodedValue)
        {
            decodedValue = string.Empty;
            if (string.IsNullOrEmpty(value) || string.Equals(value, EmptyFieldToken, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                decodedValue = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void ReportStorageWarning(string action, Exception exception)
        {
            var message = $"<color=#FFC107>[UnityCli]</color> {action}: {exception.Message}";
            if (string.Equals(lastStorageWarning, message, StringComparison.Ordinal))
            {
                return;
            }

            lastStorageWarning = message;
            Debug.LogWarning(message);
        }

        static string BuildInvokeRequestDetails(InvokeRequest request)
        {
            var parts = new List<string>();
            if (request?.args != null)
            {
                parts.Add($"args={request.args.Count}");
            }

            if (request?.timeoutMs.HasValue == true)
            {
                parts.Add($"timeoutMs={request.timeoutMs.Value}");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }

        static string BuildInvokeResponseMessage(InvokeResponse response)
        {
            if (response == null)
            {
                return "调用未返回结果";
            }

            if (!string.IsNullOrWhiteSpace(response.error?.message))
            {
                return response.error.message;
            }

            if (!string.IsNullOrWhiteSpace(response.message))
            {
                return response.message;
            }

            switch (response.status)
            {
                case "pending":
                    return "调用已进入异步队列";
                case "completed":
                    return response.ok ? "调用完成" : "调用失败";
                case "failed":
                    return "调用失败";
                default:
                    return string.IsNullOrWhiteSpace(response.status)
                        ? "调用结束"
                        : $"调用结束 ({response.status})";
            }
        }

        static string BuildInvokeResponseDetails(InvokeResponse response)
        {
            if (response == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(response.status))
            {
                parts.Add($"status={response.status}");
            }

            if (!string.IsNullOrWhiteSpace(response.jobId))
            {
                parts.Add($"jobId={response.jobId}");
            }

            if (!string.IsNullOrWhiteSpace(response.error?.code))
            {
                parts.Add($"error={response.error.code}");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }

        static string BuildJobDetails(string jobId, string details = null)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return details ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(details)
                ? $"jobId={jobId}"
                : $"jobId={jobId} · {details}";
        }

        static string BuildJobMessage(string status, bool isSuccess)
        {
            switch (status)
            {
                case "running":
                    return "异步 Job 继续执行";
                case "completed":
                    return isSuccess ? "异步 Job 已完成" : "异步 Job 已结束";
                case "failed":
                    return "异步 Job 失败";
                default:
                    return string.IsNullOrWhiteSpace(status)
                        ? "异步 Job 状态更新"
                        : $"异步 Job 状态更新 ({status})";
            }
        }

        static LogType ResolveInvokeLogType(InvokeResponse response)
        {
            if (response == null)
            {
                return LogType.Error;
            }

            if (string.Equals(response.status, "pending", StringComparison.Ordinal))
            {
                return LogType.Warning;
            }

            return response.ok && response.error == null ? LogType.Log : LogType.Error;
        }

        static LogType ResolveJobLogType(string status, bool isSuccess)
        {
            if (string.Equals(status, "running", StringComparison.Ordinal))
            {
                return LogType.Warning;
            }

            return isSuccess ? LogType.Log : LogType.Error;
        }
    }
}
