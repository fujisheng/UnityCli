using System.Collections.Generic;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;

namespace UnityCli.Editor.Tools.BuiltIn
{
    [UnityCliTool("editor.status", Description = "Get current Unity Editor state", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly, Category = "editor")]
    public sealed class EditorStatusTool : IUnityCliTool
    {
        public string Id => "editor.status";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Get current Unity Editor state",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly,
                schemaVersion = "1.0"
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (context == null)
            {
                return ToolResult.Error("tool_execution_failed", "工具上下文不能为空。", Id);
            }

            return ToolResult.Ok(new
            {
                isPlaying = context.IsPlaying,
                isCompiling = context.IsCompiling,
                isBatchMode = context.EditorState.IsBatchMode,
                unityVersion = context.EditorState.UnityVersion,
                projectPath = context.EditorState.ProjectPath
            });
        }
    }
}
