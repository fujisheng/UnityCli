using System;

namespace UnityCli.Protocol
{
    [Serializable]
    [Flags]
    public enum ToolCapabilities
    {
        None = 0,
        ReadOnly = 1 << 0,
        WriteAssets = 1 << 1,
        PlayMode = 1 << 2,
        SceneMutation = 1 << 3,
        ExternalProcess = 1 << 4,
        Dangerous = 1 << 5
    }
}
