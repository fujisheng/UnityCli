using System;
using System.Collections.Generic;

namespace UnityCli.Protocol
{
    [Serializable]
    public class ToolDescriptor
    {
        public string id { get; set; }

        public string description { get; set; }

        public string category { get; set; }

        public ToolMode mode { get; set; }

        public ToolCapabilities capabilities { get; set; }

        public string schemaVersion { get; set; }

        public List<ParamDescriptor> parameters { get; set; }
    }
}
