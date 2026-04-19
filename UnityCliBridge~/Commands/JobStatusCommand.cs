using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityCli.Http;
using UnityCli.Output;

namespace UnityCli.Commands
{
    static class JobStatusCommand
    {
        public static async Task<int> RunAsync(string[] args)
        {
            if (!TryParseArgs(args, out var jobId, out var projectPath, out var errorPayload))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(errorPayload);
            }

            var client = new BridgeClient(projectPath);
            var result = await client.GetAsync($"/job/{Uri.EscapeDataString(jobId)}");
            return ResultFormatter.WritePayloadAndGetExitCode(result.Payload);
        }

        static bool TryParseArgs(string[] args, out string jobId, out string? projectPath, out object errorPayload)
        {
            jobId = string.Empty;
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
                            new { usage = CliUsage.JobStatus });
                        return false;
                    }

                    projectPath = args[++index];
                    continue;
                }

                if (args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    errorPayload = ResultFormatter.CreateErrorPayload(
                        "invalid_arguments",
                        $"job-status 不支持参数 '{args[index]}'。",
                        new { usage = CliUsage.JobStatus });
                    return false;
                }

                positionals.Add(args[index]);
            }

            if (positionals.Count != 1)
            {
                errorPayload = ResultFormatter.CreateErrorPayload(
                    "invalid_arguments",
                    "job-status 需要且仅需要一个 jobId 参数。",
                    new { usage = CliUsage.JobStatus });
                return false;
            }

            jobId = positionals[0];
            return true;
        }
    }
}
