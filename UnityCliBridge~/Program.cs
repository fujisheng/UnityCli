using System;
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
            if (args.Length == 0 || IsHelp(args[0]))
            {
                return PrintHelp();
            }

            var command = args[0].ToLowerInvariant();
            var commandArgs = args.Skip(1).ToArray();
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
                usage = CliUsage.All
            }, "UnityCli 命令参考"));
        }
    }

    static class CliUsage
    {
        public static readonly string[] All =
        {
            "unitycli ping [--project <path>]",
            "unitycli tools list [--project <path>]",
            "unitycli tools describe <toolId> [--project <path>]",
            "unitycli invoke --tool <toolId> --json <json> [--project <path>] [--wait] [--timeout <ms>]",
            "unitycli invoke --tool <toolId> --stdin [--project <path>] [--wait] [--timeout <ms>]",
            "unitycli invoke --tool <toolId> --in <file.json> [--project <path>] [--wait] [--timeout <ms>]",
            "unitycli job-status <jobId> [--project <path>]"
        };

        public static readonly string[] Tools =
        {
            "unitycli tools list [--project <path>]",
            "unitycli tools describe <toolId> [--project <path>]"
        };

        public static readonly string[] Invoke =
        {
            "unitycli invoke --tool <toolId> --json <json> [--project <path>] [--wait] [--timeout <ms>]",
            "unitycli invoke --tool <toolId> --stdin [--project <path>] [--wait] [--timeout <ms>]",
            "unitycli invoke --tool <toolId> --in <file.json> [--project <path>] [--wait] [--timeout <ms>]"
        };

        public static readonly string[] Ping =
        {
            "unitycli ping [--project <path>]"
        };

        public static readonly string[] JobStatus =
        {
            "unitycli job-status <jobId> [--project <path>]"
        };

        public static readonly string[] ToolsDescribe =
        {
            "unitycli tools describe <toolId> [--project <path>]"
        };

        public static readonly string[] ToolsList =
        {
            "unitycli tools list [--project <path>]"
        };
    }
}
