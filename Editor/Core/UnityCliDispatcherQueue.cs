using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCli.Editor.Core
{
    [InitializeOnLoad]
    public static class UnityCliDispatcherQueue
    {
        const int MaxPendingRequests = 100;

        static readonly ConcurrentQueue<InvokeRequest> queuedRequests = new ConcurrentQueue<InvokeRequest>();
        static readonly ConcurrentDictionary<string, TaskCompletionSource<InvokeResponse>> pendingResponses = new ConcurrentDictionary<string, TaskCompletionSource<InvokeResponse>>(StringComparer.Ordinal);
        static int pendingRequestCount;

        static UnityCliDispatcherQueue()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
        }

        public static int PendingCount => Volatile.Read(ref pendingRequestCount);

        public static Task<InvokeResponse> Enqueue(InvokeRequest request)
        {
            var normalizedRequest = NormalizeRequest(request);
            if (Interlocked.Increment(ref pendingRequestCount) > MaxPendingRequests)
            {
                Interlocked.Decrement(ref pendingRequestCount);
                return Task.FromResult(CreateErrorResponse(normalizedRequest.requestId, "editor_busy", "主线程调度队列已满。"));
            }

            var completionSource = new TaskCompletionSource<InvokeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pendingResponses.TryAdd(normalizedRequest.requestId, completionSource))
            {
                Interlocked.Decrement(ref pendingRequestCount);
                return Task.FromResult(CreateErrorResponse(normalizedRequest.requestId, "tool_execution_failed", $"请求 Id '{normalizedRequest.requestId}' 已存在。"));
            }

            queuedRequests.Enqueue(normalizedRequest);
            return completionSource.Task;
        }

        internal static bool TryDequeue(out InvokeRequest request)
        {
            return queuedRequests.TryDequeue(out request);
        }

        internal static void Complete(InvokeRequest request, InvokeResponse response)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.requestId))
            {
                return;
            }

            if (pendingResponses.TryRemove(request.requestId, out var completionSource))
            {
                Interlocked.Decrement(ref pendingRequestCount);
                completionSource.TrySetResult(response ?? CreateErrorResponse(request.requestId, "tool_execution_failed", "调度器返回了空响应。"));
            }
        }

        static void HandleBeforeAssemblyReload()
        {
            while (queuedRequests.TryDequeue(out _))
            {
            }

            foreach (var entry in pendingResponses)
            {
                if (!pendingResponses.TryRemove(entry.Key, out var completionSource))
                {
                    continue;
                }

                Interlocked.Decrement(ref pendingRequestCount);
                completionSource.TrySetResult(CreateErrorResponse(entry.Key, "bridge_reloaded", "UnityCli bridge 已重新加载，旧请求已失效。"));
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

        static InvokeResponse CreateErrorResponse(string requestId, string code, string message)
        {
            return ToolResult.Error(code, message).ToInvokeResponse(requestId ?? string.Empty);
        }
    }
}
