using System;
using UnityCli.Protocol;

namespace UnityCli.Editor.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class UnityCliToolAttribute : Attribute
    {
        public UnityCliToolAttribute(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("工具 Id 不能为空。", nameof(id));
            }

            Id = id;
        }

        public string Id { get; }

        public string Description { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public ToolMode Mode { get; set; } = ToolMode.Both;

        public ToolCapabilities Capabilities { get; set; } = ToolCapabilities.ReadOnly;

        public string SchemaVersion { get; set; } = "1.0";
    }
}
