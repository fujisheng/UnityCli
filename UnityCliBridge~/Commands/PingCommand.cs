using System;
using System.Threading.Tasks;
using UnityCli.Http;
using UnityCli.Output;

namespace UnityCli.Commands
{
    static class PingCommand
    {
        public static async Task<int> RunAsync(string[] args)
        {
            if (!TryParseArgs(args, out var projectPath, out var errorPayload))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(errorPayload);
            }

            var client = new BridgeClient(projectPath);
            var result = await client.GetAsync("/ping");
            return ResultFormatter.WritePayloadAndGetExitCode(result.Payload);
        }

        static bool TryParseArgs(string[] args, out string? projectPath, out object errorPayload)
        {
            projectPath = null;
            errorPayload = null!;

            for (var index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], "--project", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= args.Length)
                    {
                        errorPayload = ResultFormatter.CreateErrorPayload(
                            "invalid_arguments",
                            "--project 需要路径参数。",
                            new { usage = CliUsage.Ping });
                        return false;
                    }

                    projectPath = args[++index];
                    continue;
                }

                errorPayload = ResultFormatter.CreateErrorPayload(
                    "invalid_arguments",
                    $"ping 不支持参数 '{args[index]}'。",
                    new { usage = CliUsage.Ping });
                return false;
            }

            return true;
        }
    }
}
