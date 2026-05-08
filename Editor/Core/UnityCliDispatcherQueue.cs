using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
namespace UnityCli.Editor.Core
{
    public static class UnityCliDispatcherQueue
    {
        const int MaxPendingRequests = 100;
        // 与 UnityCliBridge~/Transport/BridgeClient.cs 默认值保持一致，避免 editor 默认等待晚于 CLI 断开。
        const int DefaultInvokeTimeoutMs = 30000;

        sealed class PendingInvokeResponse
        {
            public PendingInvokeResponse(TaskCompletionSource<InvokeResponse> completionSource, CancellationTokenSource timeoutCancellation)
            {
                CompletionSource = completionSource;
                TimeoutCancellation = timeoutCancellation;
                TimeoutRegistration = default;
            }

            public TaskCompletionSource<InvokeResponse> CompletionSource { get; }

            public CancellationTokenSource TimeoutCancellation { get; }

            public CancellationTokenRegistration TimeoutRegistration { get; private set; }

            public void SetTimeoutRegistration(CancellationTokenRegistration timeoutRegistration)
            {
                TimeoutRegistration = timeoutRegistration;
            }
        }

        static readonly ConcurrentQueue<InvokeRequest> queuedRequests = new ConcurrentQueue<InvokeRequest>();
        static readonly ConcurrentDictionary<string, PendingInvokeResponse> pendingResponses = new ConcurrentDictionary<string, PendingInvokeResponse>(StringComparer.Ordinal);
        static int pendingRequestCount;

        public static void EnsureInitializedOnMainThread()
        {
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
            var timeoutCancellation = CreateTimeoutCancellation(normalizedRequest.timeoutMs);
            var pendingResponse = new PendingInvokeResponse(completionSource, timeoutCancellation);
            if (!pendingResponses.TryAdd(normalizedRequest.requestId, pendingResponse))
            {
                Interlocked.Decrement(ref pendingRequestCount);
                timeoutCancellation.Dispose();
                return Task.FromResult(CreateErrorResponse(normalizedRequest.requestId, "tool_execution_failed", $"请求 Id '{normalizedRequest.requestId}' 已存在。"));
            }

            var timeoutRegistration = timeoutCancellation.Token.Register(() => HandleInvokeTimeout(normalizedRequest.requestId, normalizedRequest.tool, timeoutCancellation.Token));
            pendingResponse.SetTimeoutRegistration(timeoutRegistration);

            queuedRequests.Enqueue(normalizedRequest);
            UnityCliBootstrap.NotifyInvokeRequestQueued();
            return completionSource.Task;
        }

        internal static bool TryDequeue(out InvokeRequest request)
        {
            return queuedRequests.TryDequeue(out request);
        }

        internal static bool TryDequeuePending(out InvokeRequest request)
        {
            while (queuedRequests.TryDequeue(out request))
            {
                if (IsPending(request?.requestId))
                {
                    return true;
                }
            }

            request = null;
            return false;
        }

        internal static bool IsPending(string requestId)
        {
            return !string.IsNullOrWhiteSpace(requestId)
                && pendingResponses.ContainsKey(requestId);
        }

        internal static void Complete(InvokeRequest request, InvokeResponse response)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.requestId))
            {
                return;
            }

            if (pendingResponses.TryRemove(request.requestId, out var pendingResponse))
            {
                Interlocked.Decrement(ref pendingRequestCount);
                DisposePendingResponseResources(pendingResponse);
                pendingResponse.CompletionSource.TrySetResult(response ?? CreateErrorResponse(request.requestId, "tool_execution_failed", "调度器返回了空响应。"));
            }
        }

        public static void FailAllPending(string code, string message)
        {
            while (queuedRequests.TryDequeue(out _))
            {
            }

            foreach (var entry in pendingResponses)
            {
                if (!pendingResponses.TryRemove(entry.Key, out var pendingResponse))
                {
                    continue;
                }

                Interlocked.Decrement(ref pendingRequestCount);
                DisposePendingResponseResources(pendingResponse);
                pendingResponse.CompletionSource.TrySetResult(CreateErrorResponse(entry.Key, code, message));
            }
        }

        static void HandleInvokeTimeout(string requestId, string toolId, CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested
                || string.IsNullOrWhiteSpace(requestId)
                || !pendingResponses.TryRemove(requestId, out var pendingResponse))
            {
                return;
            }

            Interlocked.Decrement(ref pendingRequestCount);
            // 不能在自己的超时回调里释放已触发的 CTS，避免回调/释放互相等待。
            pendingResponse.CompletionSource.TrySetResult(CreateErrorResponse(requestId, "tool_timeout", $"工具 '{toolId ?? string.Empty}' 主线程调度超时。"));
        }

        static CancellationTokenSource CreateTimeoutCancellation(int? timeoutMs)
        {
            var effectiveTimeoutMs = timeoutMs.HasValue && timeoutMs.Value > 0
                ? timeoutMs.Value
                : DefaultInvokeTimeoutMs;
            return new CancellationTokenSource(effectiveTimeoutMs);
        }

        static void DisposePendingResponseResources(PendingInvokeResponse pendingResponse)
        {
            if (pendingResponse == null)
            {
                return;
            }

            DisposeTimeoutRegistration(pendingResponse.TimeoutRegistration);
            DisposeTimeoutCancellation(pendingResponse.TimeoutCancellation);
        }

        static void DisposeTimeoutCancellation(CancellationTokenSource cancellation)
        {
            if (cancellation == null)
            {
                return;
            }

            try
            {
                cancellation.Dispose();
            }
            catch
            {
            }
        }

        static void DisposeTimeoutRegistration(CancellationTokenRegistration registration)
        {
            try
            {
                registration.Dispose();
            }
            catch
            {
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
