using System;
using System.Collections.Generic;
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
            if (!UnityCliAllowlist.IsAllowed(normalizedRequest.tool))
            {
                return CreateErrorResponse(normalizedRequest.requestId, "tool_not_found", $"未找到工具 '{normalizedRequest.tool}'。");
            }

            if (!UnityCliRegistry.TryGetTool(normalizedRequest.tool, out var tool)
                || !UnityCliRegistry.TryGetDescriptor(normalizedRequest.tool, out var descriptor))
            {
                return CreateErrorResponse(normalizedRequest.requestId, "tool_not_found", $"未找到工具 '{normalizedRequest.tool}'。");
            }

            var editorState = ToolContext.EditorStateSnapshot.Capture();
            if (!IsModeAllowed(descriptor.mode, editorState.IsPlaying))
            {
                return CreateErrorResponse(normalizedRequest.requestId, "wrong_mode", $"工具 '{normalizedRequest.tool}' 当前运行模式不匹配。");
            }

            if (IsEditorBusy(editorState))
            {
                return CreateErrorResponse(normalizedRequest.requestId, "editor_busy", "Unity Editor 当前忙碌，暂时无法执行请求。");
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
                Debug.LogException(exception);
                return CreateErrorResponse(normalizedRequest.requestId, "tool_execution_failed", $"工具 '{normalizedRequest.tool}' 执行失败。", exception.ToString());
            }

            if (result == null)
            {
                return CreateErrorResponse(normalizedRequest.requestId, "tool_execution_failed", $"工具 '{normalizedRequest.tool}' 返回了空结果。");
            }

            if (string.Equals(result.Status, "pending", StringComparison.Ordinal))
            {
                if (!(tool is IUnityCliAsyncTool asyncTool))
                {
                    return CreateErrorResponse(normalizedRequest.requestId, "tool_execution_failed", $"工具 '{normalizedRequest.tool}' 返回了 pending，但未实现 IUnityCliAsyncTool。");
                }

                if (!string.Equals(result.JobId, context.CurrentJobId, StringComparison.Ordinal))
                {
                    return CreateErrorResponse(normalizedRequest.requestId, "tool_execution_failed", $"工具 '{normalizedRequest.tool}' 返回了无效的 JobId。");
                }

                UnityCliJobManager.RegisterPendingJob(normalizedRequest, asyncTool, context, result);
            }

            return result.ToInvokeResponse(normalizedRequest.requestId);
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
    }
}
