using System;

namespace UnityCli.Protocol
{
    [Serializable]
    public class InvokeResponse
    {
        public string requestId { get; set; }

        public bool ok { get; set; }

        public string status { get; set; }

        public string message { get; set; }

        public object data { get; set; }

        public string jobId { get; set; }

        public ToolError error { get; set; }
    }
}
