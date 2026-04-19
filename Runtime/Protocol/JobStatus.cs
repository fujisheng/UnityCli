using System;

namespace UnityCli.Protocol
{
    [Serializable]
    public class JobStatus
    {
        public string jobId { get; set; }

        public string status { get; set; }

        public InvokeResponse result { get; set; }

        public DateTime createdAt { get; set; }

        public DateTime? completedAt { get; set; }
    }
}
