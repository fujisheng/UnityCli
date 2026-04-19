using System;

namespace UnityCli.Protocol
{
    [Serializable]
    public enum ToolMode
    {
        EditOnly = 0,
        PlayOnly = 1,
        Both = 2
    }
}
