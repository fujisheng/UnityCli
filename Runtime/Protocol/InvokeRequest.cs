using System;
using System.Collections.Generic;

namespace UnityCli.Protocol
{
    [Serializable]
    public class InvokeRequest
    {
        public string requestId { get; set; }

        public string tool { get; set; }

        public Dictionary<string, object> args { get; set; }

        public int? timeoutMs { get; set; }
    }
}
