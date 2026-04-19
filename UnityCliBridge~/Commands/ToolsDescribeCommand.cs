using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityCli.Transport;
using UnityCli.Output;

namespace UnityCli.Commands
{
    static class ToolsDescribeCommand
    {
        public static async Task<int> RunAsync(string[] args)
        {
            if (!TryParseArgs(args, out var toolId, out var projectPath, out var errorPayload))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(errorPayload);
            }

            var client = new BridgeClient(projectPath);
            var result = await client.GetAsync($"/tools/{Uri.EscapeDataString(toolId)}");
            return ResultFormatter.WritePayloadAndGetExitCode(result.Payload);
        }

        static bool TryParseArgs(string[] args, out string toolId, out string? projectPath, out object errorPayload)
        {
            toolId = string.Empty;
            projectPath = null;
            errorPayload = null!;
            var positionals = new List<string>();

            for (var index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], "--project", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= args.Length)
                    {
                        errorPayload = ResultFormatter.CreateErrorPayload(
                            "invalid_arguments",
                            "--project 需要路径参数。",
                            new { usage = CliUsage.ToolsDescribe });
                        return false;
                    }

                    projectPath = args[++index];
                    continue;
                }

                if (args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    errorPayload = ResultFormatter.CreateErrorPayload(
                        "invalid_arguments",
                        $"tools describe 不支持参数 '{args[index]}'。",
                        new { usage = CliUsage.ToolsDescribe });
                    return false;
                }

                positionals.Add(args[index]);
            }

            if (positionals.Count != 1)
            {
                errorPayload = ResultFormatter.CreateErrorPayload(
                    "invalid_arguments",
                    "tools describe 需要且仅需要一个 toolId 参数。",
                    new { usage = CliUsage.ToolsDescribe });
                return false;
            }

            toolId = positionals[0];
            return true;
        }
    }
}
