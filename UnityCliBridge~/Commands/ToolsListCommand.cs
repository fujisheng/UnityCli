using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityCli.Http;
using UnityCli.Output;

namespace UnityCli.Commands
{
    static class ToolsListCommand
    {
        sealed class ToolsListOptions
        {
            public string? ProjectPath { get; set; }
            public string? Category { get; set; }
            public bool Verbose { get; set; }
        }

        public static async Task<int> RunAsync(string[] args)
        {
            if (!TryParseArgs(args, out var options, out var errorPayload))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(errorPayload);
            }

            var client = new BridgeClient(options.ProjectPath);
            var result = await client.GetAsync("/tools");

            // /tools 路由返回裸 ToolDescriptor[]（无 ok 包装）
            var descriptors = result.Payload as List<object>;
            if (descriptors == null)
            {
                return ResultFormatter.WritePayloadAndGetExitCode(result.Payload);
            }

            var typedDescriptors = descriptors.OfType<Dictionary<string, object>>().ToList();
            var filtered = ApplyCategoryFilter(typedDescriptors, options.Category);
            var output = options.Verbose
                ? (object)filtered
                : filtered.Select(d =>
                {
                    CliObjectAccessor.TryGetString(d, "id", out var id);
                    CliObjectAccessor.TryGetString(d, "description", out var desc);
                    CliObjectAccessor.TryGetString(d, "category", out var cat);
                    return new { id, description = desc, category = cat };
                }).ToList();

            ResultFormatter.WritePayload(output);
            return 0;
        }

        static List<Dictionary<string, object>> ApplyCategoryFilter(List<Dictionary<string, object>> descriptors, string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return descriptors;
            }

            return descriptors.Where(d =>
            {
                if (!CliObjectAccessor.TryGetString(d, "category", out var cat))
                {
                    return false;
                }

                return string.Equals(cat, category, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        static bool TryParseArgs(string[] args, out ToolsListOptions options, out object errorPayload)
        {
            options = new ToolsListOptions();
            errorPayload = null!;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "--project":
                        if (index + 1 >= args.Length)
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                "--project 需要路径参数。",
                                new { usage = CliUsage.ToolsList });
                            return false;
                        }

                        options.ProjectPath = args[++index];
                        break;

                    case "--category":
                        if (index + 1 >= args.Length)
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                "--category 需要分类参数。可用分类由工具注册时声明，常见分类：ui, config, editor, game, test。",
                                new { usage = CliUsage.ToolsList });
                            return false;
                        }

                        options.Category = args[++index];
                        break;

                    case "--verbose":
                        options.Verbose = true;
                        break;

                    default:
                        errorPayload = ResultFormatter.CreateErrorPayload(
                            "invalid_arguments",
                            $"tools list 不支持参数 '{args[index]}'。",
                            new { usage = CliUsage.ToolsList });
                        return false;
                }
            }

            return true;
        }
    }
}
