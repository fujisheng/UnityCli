using System;

namespace UnityCli.Editor.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class UnityCliParamAttribute : Attribute
    {
        public UnityCliParamAttribute(string description)
        {
            Description = description ?? string.Empty;
        }

        public string Description { get; }

        public bool Required { get; set; } = true;

        public object DefaultValue { get; set; }
    }
}
