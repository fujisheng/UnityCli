using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityCli.Commands;
using UnityCli.Output;

namespace UnityCli
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (!TryParseGlobalOptions(args, out var normalizedArgs, out var errorPayload))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(errorPayload);
            }

            if (normalizedArgs.Length == 0 || IsHelp(normalizedArgs[0]))
            {
                return PrintHelp();
            }

            var command = normalizedArgs[0].ToLowerInvariant();
            var commandArgs = normalizedArgs.Skip(1).ToArray();
            switch (command)
            {
                case "ping":
                    return await PingCommand.RunAsync(commandArgs);
                case "tools":
                    return await RunToolsCommand(commandArgs);
                case "invoke":
                    return await InvokeCommand.RunAsync(commandArgs);
                case "job-status":
                    return await JobStatusCommand.RunAsync(commandArgs);
                default:
                    return ResultFormatter.WritePayloadAndGetExitCode(ResultFormatter.CreateErrorPayload(
                        "invalid_command",
                        $"未知命令: {command}",
                        new
                        {
                            usage = CliUsage.All
                        }));
            }

                static bool TryParseGlobalOptions(string[] args, out string[] normalizedArgs, out object errorPayload)
                {
                    normalizedArgs = Array.Empty<string>();
                    errorPayload = null!;

                    var outputFormat = CliOutputFormat.Human;
                    var remainingArgs = new List<string>(args.Length);
                    for (var index = 0; index < args.Length; index++)
                    {
                        if (!string.Equals(args[index], "--format", StringComparison.OrdinalIgnoreCase))
                        {
                            remainingArgs.Add(args[index]);
                            continue;
                        }

                        if (index + 1 >= args.Length)
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                "--format 需要输出格式参数。",
                                new
                                {
                                    usage = CliUsage.All,
                                    outputFormats = ResultFormatter.SupportedOutputFormatNames
                                });
                            return false;
                        }

                        var rawFormat = args[++index];
                        if (!ResultFormatter.TryParseOutputFormat(rawFormat, out outputFormat))
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                $"不支持的输出格式: {rawFormat}。",
                                new
                                {
                                    usage = CliUsage.All,
                                    outputFormats = ResultFormatter.SupportedOutputFormatNames
                                });
                            return false;
                        }
                    }

                    ResultFormatter.SetOutputFormat(outputFormat);
                    normalizedArgs = remainingArgs.ToArray();
                    return true;
                }
        }

        static Task<int> RunToolsCommand(string[] args)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(ResultFormatter.WritePayloadAndGetExitCode(ResultFormatter.CreateErrorPayload(
                    "invalid_command",
                    "tools 需要子命令: list | describe",
                    new
                    {
                        usage = CliUsage.Tools
                    })));
            }

            var sub = args[0].ToLowerInvariant();
            var subArgs = args.Skip(1).ToArray();
            return sub switch
            {
                "list" => ToolsListCommand.RunAsync(subArgs),
                "describe" => ToolsDescribeCommand.RunAsync(subArgs),
                _ => UnknownToolsSubCommand(sub)
            };
        }

        static Task<int> UnknownToolsSubCommand(string sub)
        {
            return Task.FromResult(ResultFormatter.WritePayloadAndGetExitCode(ResultFormatter.CreateErrorPayload(
                "invalid_command",
                $"未知 tools 子命令: {sub}",
                new
                {
                    usage = CliUsage.Tools
                })));
        }

        static bool IsHelp(string arg)
        {
            return arg is "-h" or "--help" or "help";
        }

        static int PrintHelp()
        {
            return ResultFormatter.WritePayloadAndGetExitCode(ResultFormatter.CreateSuccessPayload(new
            {
                usage = CliUsage.All,
                outputFormats = ResultFormatter.SupportedOutputFormatNames,
                defaultOutputFormat = "human"
            }, "UnityCli 命令参考"));
        }
    }

    static class CliUsage
    {
        const string FormatOption = "[--format <json|pretty-json|human>]";

        public static readonly string[] All =
        {
            $"unitycli {FormatOption} ping [--project <path>]",
            $"unitycli {FormatOption} tools list [--project <path>]",
            $"unitycli {FormatOption} tools describe <toolId> [--project <path>]",
            $"unitycli {FormatOption} invoke --tool <toolId> --json <json> [--project <path>] [--wait] [--timeout <ms>]",
            $"unitycli {FormatOption} invoke --tool <toolId> --stdin [--project <path>] [--wait] [--timeout <ms>]",
            $"unitycli {FormatOption} invoke --tool <toolId> --in <file.json> [--project <path>] [--wait] [--timeout <ms>]",
            $"unitycli {FormatOption} job-status <jobId> [--project <path>]"
        };

        public static readonly string[] Tools =
        {
            $"unitycli {FormatOption} tools list [--project <path>]",
            $"unitycli {FormatOption} tools describe <toolId> [--project <path>]"
        };

        public static readonly string[] Invoke =
        {
            $"unitycli {FormatOption} invoke --tool <toolId> --json <json> [--project <path>] [--wait] [--timeout <ms>]",
            $"unitycli {FormatOption} invoke --tool <toolId> --stdin [--project <path>] [--wait] [--timeout <ms>]",
            $"unitycli {FormatOption} invoke --tool <toolId> --in <file.json> [--project <path>] [--wait] [--timeout <ms>]"
        };

        public static readonly string[] Ping =
        {
            $"unitycli {FormatOption} ping [--project <path>]"
        };

        public static readonly string[] JobStatus =
        {
            $"unitycli {FormatOption} job-status <jobId> [--project <path>]"
        };

        public static readonly string[] ToolsDescribe =
        {
            $"unitycli {FormatOption} tools describe <toolId> [--project <path>]"
        };

        public static readonly string[] ToolsList =
        {
            $"unitycli {FormatOption} tools list [--project <path>]"
        };
    }
}
