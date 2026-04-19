using System;
using System.Collections.Generic;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCli.Editor.Core
{
    [InitializeOnLoad]
    public static class UnityCliJobManager
    {
        sealed class JobEntry
        {
            public JobEntry(UnityCliJob job, IUnityCliAsyncTool tool)
            {
                Job = job;
                Tool = tool;
            }

            public UnityCliJob Job { get; }

            public IUnityCliAsyncTool Tool { get; }
        }

        static readonly object syncRoot = new object();
        static readonly Dictionary<string, JobEntry> jobs = new Dictionary<string, JobEntry>(StringComparer.Ordinal);
        static readonly Queue<string> scheduledJobIds = new Queue<string>();
        static readonly string sessionId = Guid.NewGuid().ToString("N");

        static int nextJobSequence;

        static UnityCliJobManager()
        {
            EditorApplication.update -= HandleEditorUpdate;
            EditorApplication.update += HandleEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
        }

        public static JobStatus GetJobStatus(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return CreateErrorStatus(string.Empty, "tool_execution_failed", "JobId 不能为空。");
            }

            if (!IsCurrentSessionJob(jobId))
            {
                return CreateErrorStatus(jobId, "bridge_reloaded", "UnityCli bridge 已重新加载，旧 Job 已失效。");
            }

            lock (syncRoot)
            {
                if (jobs.TryGetValue(jobId, out var entry))
                {
                    return CloneStatus(entry.Job.ToProtocolStatus());
                }
            }

            return CreateErrorStatus(jobId, "tool_execution_failed", $"未找到 Job '{jobId}'。", jobId);
        }

        internal static ToolContext.PendingJobRegistration CreatePendingJobRegistration()
        {
            return new ToolContext.PendingJobRegistration(CreateJobId());
        }

        internal static void RegisterPendingJob(InvokeRequest request, IUnityCliAsyncTool tool, ToolContext context, ToolResult pendingResult)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (pendingResult == null)
            {
                throw new ArgumentNullException(nameof(pendingResult));
            }

            var registration = context.GetPendingJobRegistration();
            var jobId = !string.IsNullOrWhiteSpace(registration?.JobId) ? registration.JobId : pendingResult.JobId;
            var createdAtUtc = DateTime.UtcNow;
            var timeout = ResolveTimeout(request.timeoutMs);
            var deadlineUtc = timeout.HasValue ? createdAtUtc + timeout.Value : (DateTime?)null;
            var job = new UnityCliJob(
                jobId,
                request.requestId ?? string.Empty,
                request.tool ?? string.Empty,
                CloneArguments(request.args),
                createdAtUtc,
                deadlineUtc,
                registration?.PlannedDuration,
                registration?.State);
            job.LastResponse = pendingResult.ToInvokeResponse(job.RequestId);

            lock (syncRoot)
            {
                jobs[jobId] = new JobEntry(job, tool);
                scheduledJobIds.Enqueue(jobId);
            }
        }

        static void HandleEditorUpdate()
        {
            JobEntry jobEntry = null;

            lock (syncRoot)
            {
                while (scheduledJobIds.Count > 0)
                {
                    var jobId = scheduledJobIds.Dequeue();
                    if (!jobs.TryGetValue(jobId, out var candidate))
                    {
                        continue;
                    }

                    if (IsTerminal(candidate.Job.Status))
                    {
                        continue;
                    }

                    candidate.Job.Status = "running";
                    jobEntry = candidate;
                    break;
                }
            }

            if (jobEntry == null)
            {
                return;
            }

            var job = jobEntry.Job;
            var nowUtc = DateTime.UtcNow;
            ToolResult stepResult;
            if (job.HasTimedOut(nowUtc))
            {
                stepResult = ToolResult.Error("tool_execution_failed", $"Job '{job.JobId}' 已超时。");
            }
            else
            {
                var editorState = ToolContext.EditorStateSnapshot.Capture();
                var context = ToolContext.Create(editorState, job);
                try
                {
                    stepResult = jobEntry.Tool.ContinueJob(job, context);
                }
                catch (Exception exception)
                {
                    stepResult = ToolResult.Error("tool_execution_failed", $"工具 '{job.ToolId}' 的 Job 执行失败。", exception.ToString());
                }
            }

            if (stepResult == null)
            {
                stepResult = ToolResult.Error("tool_execution_failed", $"工具 '{job.ToolId}' 的 Job 返回了空结果。");
            }

            ApplyStepResult(jobEntry, stepResult, DateTime.UtcNow);
        }

        static void ApplyStepResult(JobEntry jobEntry, ToolResult stepResult, DateTime nowUtc)
        {
            var job = jobEntry.Job;
            lock (syncRoot)
            {
                job.StepCount++;
                if (string.Equals(stepResult.Status, "pending", StringComparison.Ordinal))
                {
                    if (!string.Equals(stepResult.JobId, job.JobId, StringComparison.Ordinal))
                    {
                        MarkJobFailed(job, ToolResult.Error("tool_execution_failed", $"工具 '{job.ToolId}' 返回了不匹配的 JobId。"), nowUtc);
                        return;
                    }

                    if (job.HasTimedOut(nowUtc))
                    {
                        MarkJobFailed(job, ToolResult.Error("tool_execution_failed", $"Job '{job.JobId}' 已超时。"), nowUtc);
                        return;
                    }

                    job.Status = "running";
                    job.LastResponse = stepResult.ToInvokeResponse(job.RequestId);
                    scheduledJobIds.Enqueue(job.JobId);
                    return;
                }

                if (stepResult.IsOk)
                {
                    job.Status = "completed";
                    job.CompletedAtUtc = nowUtc;
                    job.LastResponse = stepResult.ToInvokeResponse(job.RequestId);
                    return;
                }

                MarkJobFailed(job, stepResult, nowUtc);
            }
        }

        static void MarkJobFailed(UnityCliJob job, ToolResult result, DateTime nowUtc)
        {
            job.Status = "failed";
            job.CompletedAtUtc = nowUtc;
            job.LastResponse = result.ToInvokeResponse(job.RequestId);
        }

        static bool IsTerminal(string status)
        {
            return string.Equals(status, "completed", StringComparison.Ordinal)
                || string.Equals(status, "failed", StringComparison.Ordinal);
        }

        static bool IsCurrentSessionJob(string jobId)
        {
            return jobId.StartsWith(sessionId + ":", StringComparison.Ordinal);
        }

        static string CreateJobId()
        {
            var sequence = System.Threading.Interlocked.Increment(ref nextJobSequence);
            return $"{sessionId}:{sequence}";
        }

        static TimeSpan? ResolveTimeout(int? timeoutMs)
        {
            if (!timeoutMs.HasValue || timeoutMs.Value <= 0)
            {
                return null;
            }

            return TimeSpan.FromMilliseconds(timeoutMs.Value);
        }

        static Dictionary<string, object> CloneArguments(Dictionary<string, object> args)
        {
            return args != null
                ? new Dictionary<string, object>(args, StringComparer.Ordinal)
                : new Dictionary<string, object>(StringComparer.Ordinal);
        }

        static JobStatus CreateErrorStatus(string jobId, string code, string message, string requestId = "")
        {
            var nowUtc = DateTime.UtcNow;
            return new JobStatus
            {
                jobId = jobId ?? string.Empty,
                status = "failed",
                createdAt = nowUtc,
                completedAt = nowUtc,
                result = ToolResult.Error(code, message).ToInvokeResponse(requestId ?? string.Empty)
            };
        }

        static JobStatus CloneStatus(JobStatus status)
        {
            if (status == null)
            {
                return null;
            }

            return new JobStatus
            {
                jobId = status.jobId,
                status = status.status,
                createdAt = status.createdAt,
                completedAt = status.completedAt,
                result = CloneInvokeResponse(status.result)
            };
        }

        static InvokeResponse CloneInvokeResponse(InvokeResponse response)
        {
            if (response == null)
            {
                return null;
            }

            return new InvokeResponse
            {
                requestId = response.requestId,
                ok = response.ok,
                status = response.status,
                message = response.message,
                data = response.data,
                jobId = response.jobId,
                error = CloneError(response.error)
            };
        }

        static ToolError CloneError(ToolError error)
        {
            if (error == null)
            {
                return null;
            }

            return new ToolError
            {
                code = error.code,
                message = error.message,
                details = error.details
            };
        }

        static void HandleBeforeAssemblyReload()
        {
            lock (syncRoot)
            {
                jobs.Clear();
                scheduledJobIds.Clear();
            }
        }
    }
}
