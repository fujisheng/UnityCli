using System.Collections.Generic;
using UnityCli.Protocol;

namespace UnityCli.Editor.Core
{
    public interface IUnityCliTool
    {
        string Id { get; }

        ToolDescriptor GetDescriptor();

        ToolResult Execute(Dictionary<string, object> args, ToolContext context);
    }

    public interface IUnityCliAsyncTool : IUnityCliTool
    {
        ToolResult ContinueJob(UnityCliJob job, ToolContext context);
    }
}
