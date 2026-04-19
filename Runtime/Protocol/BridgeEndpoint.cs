using System;

namespace UnityCli.Protocol
{
    [Serializable]
    public class BridgeEndpoint
    {
        public const string TransportNamedPipe = "named_pipe";

        public string protocolVersion { get; set; }

        public string transport { get; set; }

        public string pipeName { get; set; }

        public int pid { get; set; }

        public string instanceId { get; set; }

        public long generation { get; set; }

        public string token { get; set; }

        public DateTime startedAt { get; set; }
    }
}
