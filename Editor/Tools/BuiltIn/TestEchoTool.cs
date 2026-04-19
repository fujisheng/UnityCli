using System.Collections.Generic;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;

namespace UnityCli.Editor.Tools.BuiltIn
{
    [UnityCliTool("__test.echo", Description = "Echo back the input text", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly, Category = "test")]
    public sealed class TestEchoTool : IUnityCliTool
    {
        public string Id => "__test.echo";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Echo back the input text",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "text",
                        type = "string",
                        description = "需要回显的文本",
                        required = false,
                        defaultValue = string.Empty
                    }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            var text = ReadText(args);
            return ToolResult.Ok(new { echo = text });
        }

        static string ReadText(Dictionary<string, object> args)
        {
            if (args == null)
            {
                return string.Empty;
            }

            if (!args.TryGetValue("text", out var rawValue) || rawValue == null)
            {
                return string.Empty;
            }

            return rawValue.ToString() ?? string.Empty;
        }
    }
}
