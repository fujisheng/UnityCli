using System;
using UnityCli.Protocol;

namespace UnityCli.Editor.Core
{
    public sealed class ToolResult
    {
        ToolResult(bool isOk, string status, object data, string message, string jobId, ToolError error)
        {
            IsOk = isOk;
            Status = status;
            Data = data;
            Message = message;
            JobId = jobId;
            ErrorInfo = error;
        }

        public bool IsOk { get; }

        public string Status { get; }

        public object Data { get; }

        public string Message { get; }

        public string JobId { get; }

        public ToolError ErrorInfo { get; }

        public static ToolResult Ok(object data = null, string message = null)
        {
            return new ToolResult(true, "completed", data, message, null, null);
        }

        public static ToolResult Error(string code, string message, object details = null)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("错误码不能为空。", nameof(code));
            }

            return new ToolResult(false, "error", null, null, null, new ToolError
            {
                code = code,
                message = message,
                details = details
            });
        }

        public static ToolResult Pending(string jobId, string message = null, object data = null)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Pending 结果必须提供 jobId。", nameof(jobId));
            }

            return new ToolResult(true, "pending", data, message, jobId, null);
        }

        public InvokeResponse ToInvokeResponse(string requestId)
        {
            return new InvokeResponse
            {
                requestId = requestId,
                ok = IsOk,
                status = Status,
                message = Message,
                data = Data,
                jobId = JobId,
                error = ErrorInfo
            };
        }
    }
}
