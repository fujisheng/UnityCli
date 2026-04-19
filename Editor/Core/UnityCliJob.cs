using System;
using System.Collections.Generic;
using UnityCli.Protocol;

namespace UnityCli.Editor.Core
{
    public sealed class UnityCliJob
    {
        readonly Dictionary<string, object> arguments;

        internal UnityCliJob(
            string jobId,
            string requestId,
            string toolId,
            Dictionary<string, object> args,
            DateTime createdAtUtc,
            DateTime? deadlineUtc,
            TimeSpan? plannedDuration,
            object state)
        {
            JobId = jobId ?? throw new ArgumentNullException(nameof(jobId));
            RequestId = requestId ?? string.Empty;
            ToolId = toolId ?? string.Empty;
            arguments = args ?? new Dictionary<string, object>(StringComparer.Ordinal);
            CreatedAtUtc = createdAtUtc;
            DeadlineUtc = deadlineUtc;
            PlannedDuration = plannedDuration;
            State = state;
            Status = "pending";
        }

        public string JobId { get; }

        public string RequestId { get; }

        public string ToolId { get; }

        public IReadOnlyDictionary<string, object> Arguments => arguments;

        public DateTime CreatedAtUtc { get; }

        public DateTime? CompletedAtUtc { get; internal set; }

        public DateTime? DeadlineUtc { get; }

        public TimeSpan? PlannedDuration { get; }

        public string Status { get; internal set; }

        public object State { get; set; }

        public int StepCount { get; internal set; }

        public InvokeResponse LastResponse { get; internal set; }

        public TimeSpan Elapsed => GetReferenceTimeUtc() - CreatedAtUtc;

        public bool HasTimedOut(DateTime nowUtc)
        {
            return DeadlineUtc.HasValue && nowUtc >= DeadlineUtc.Value;
        }

        DateTime GetReferenceTimeUtc()
        {
            return CompletedAtUtc ?? DateTime.UtcNow;
        }

        internal JobStatus ToProtocolStatus()
        {
            return new JobStatus
            {
                jobId = JobId,
                status = Status,
                result = LastResponse,
                createdAt = CreatedAtUtc,
                completedAt = CompletedAtUtc
            };
        }
    }
}
