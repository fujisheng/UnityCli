using System;

namespace UnityCli.Protocol
{
    [Serializable]
    public class ParamDescriptor
    {
        public string name { get; set; }

        public string type { get; set; }

        public string description { get; set; }

        public bool required { get; set; }

        public object defaultValue { get; set; }
    }
}
