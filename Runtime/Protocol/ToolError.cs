using System;

namespace UnityCli.Protocol
{
    [Serializable]
    public class ToolError
    {
        public string code { get; set; }

        public string message { get; set; }

        public object details { get; set; }
    }
}
