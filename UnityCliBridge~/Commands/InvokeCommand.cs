using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityCli.Transport;
using UnityCli.Output;

namespace UnityCli.Commands
{
    static class InvokeCommand
    {
        const int PollDelayMs = 250;

        sealed class InvokeOptions
        {
            public string? ProjectPath { get; set; }

            public string ToolId { get; set; } = string.Empty;

            public string? InlineJson { get; set; }

            public bool UseStdin { get; set; }

            public string? InputFile { get; set; }

            public bool WaitForCompletion { get; set; }

            public int? TimeoutMs { get; set; }
        }

        public static async Task<int> RunAsync(string[] args)
        {
            if (!TryParseArgs(args, out var options, out var errorPayload))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(errorPayload);
            }

            var inputJson = await ReadInputJsonAsync(options);
            if (inputJson.ErrorPayload != null)
            {
                return ResultFormatter.WritePayloadAndGetExitCode(inputJson.ErrorPayload);
            }

            if (!TryBuildRequest(options, inputJson.JsonText!, out var request, out errorPayload))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(errorPayload);
            }

            var client = new BridgeClient(options.ProjectPath);
            var invokeResult = await client.PostAsync("/invoke", request, options.TimeoutMs);
            if (!options.WaitForCompletion || !IsPendingInvokeResponse(invokeResult.Payload, out var jobId))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(invokeResult.Payload);
            }

            if (string.IsNullOrWhiteSpace(jobId))
            {
                return ResultFormatter.WritePayloadAndGetExitCode(ResultFormatter.CreateErrorPayload(
                    "invalid_response",
                    "invoke 返回 pending 但缺少 jobId。",
                    new
                    {
                        toolId = options.ToolId
                    }));
            }

            var waitPayload = await WaitForCompletionAsync(client, jobId, options.TimeoutMs);
            return ResultFormatter.WritePayloadAndGetExitCode(waitPayload);
        }

        static bool TryParseArgs(string[] args, out InvokeOptions options, out object errorPayload)
        {
            options = new InvokeOptions();
            errorPayload = null!;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--tool":
                        if (!TryReadValue(args, ref index, out var toolId))
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                "--tool 需要 toolId 参数。",
                                new { usage = CliUsage.Invoke });
                            return false;
                        }

                        options.ToolId = toolId;
                        break;

                    case "--json":
                        if (!TryReadValue(args, ref index, out var json))
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                "--json 需要 JSON 字符串参数。",
                                new { usage = CliUsage.Invoke });
                            return false;
                        }

                        options.InlineJson = json;
                        break;

                    case "--stdin":
                        options.UseStdin = true;
                        break;

                    case "--in":
                        if (!TryReadValue(args, ref index, out var inputFile))
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                "--in 需要 JSON 文件路径。",
                                new { usage = CliUsage.Invoke });
                            return false;
                        }

                        options.InputFile = inputFile;
                        break;

                    case "--project":
                        if (!TryReadValue(args, ref index, out var projectPath))
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                "--project 需要路径参数。",
                                new { usage = CliUsage.Invoke });
                            return false;
                        }

                        options.ProjectPath = projectPath;
                        break;

                    case "--wait":
                        options.WaitForCompletion = true;
                        break;

                    case "--timeout":
                        if (!TryReadValue(args, ref index, out var timeoutText) || !int.TryParse(timeoutText, out var timeoutMs) || timeoutMs <= 0)
                        {
                            errorPayload = ResultFormatter.CreateErrorPayload(
                                "invalid_arguments",
                                "--timeout 需要正整数毫秒值。",
                                new { usage = CliUsage.Invoke });
                            return false;
                        }

                        options.TimeoutMs = timeoutMs;
                        break;

                    default:
                        errorPayload = ResultFormatter.CreateErrorPayload(
                            "invalid_arguments",
                            $"invoke 不支持参数 '{args[index]}'。",
                            new { usage = CliUsage.Invoke });
                        return false;
                }
            }

            if (string.IsNullOrWhiteSpace(options.ToolId))
            {
                errorPayload = ResultFormatter.CreateErrorPayload(
                    "invalid_arguments",
                    "invoke 需要 --tool <toolId>。",
                    new { usage = CliUsage.Invoke });
                return false;
            }

            var inputModeCount = 0;
            if (!string.IsNullOrWhiteSpace(options.InlineJson))
            {
                inputModeCount++;
            }

            if (options.UseStdin)
            {
                inputModeCount++;
            }

            if (!string.IsNullOrWhiteSpace(options.InputFile))
            {
                inputModeCount++;
            }

            if (inputModeCount != 1)
            {
                errorPayload = ResultFormatter.CreateErrorPayload(
                    "invalid_arguments",
                    "invoke 必须且只能使用一种输入模式：--json / --stdin / --in。",
                    new { usage = CliUsage.Invoke });
                return false;
            }

            return true;
        }

        static async Task<(string? JsonText, object? ErrorPayload)> ReadInputJsonAsync(InvokeOptions options)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(options.InlineJson))
                {
                    return (options.InlineJson, null);
                }

                if (options.UseStdin)
                {
                    var stdin = await Console.In.ReadToEndAsync();
                    if (string.IsNullOrWhiteSpace(stdin))
                    {
                        return (null, ResultFormatter.CreateErrorPayload(
                            "invalid_arguments",
                            "--stdin 未读取到任何 JSON 内容。",
                            new { usage = CliUsage.Invoke }));
                    }

                    return (stdin, null);
                }

                var filePath = Path.GetFullPath(options.InputFile!);
                if (!File.Exists(filePath))
                {
                    return (null, ResultFormatter.CreateErrorPayload(
                        "invalid_arguments",
                        "指定的 JSON 文件不存在。",
                        new { file = filePath }));
                }

                return (await File.ReadAllTextAsync(filePath), null);
            }
            catch (Exception exception)
            {
                return (null, ResultFormatter.CreateErrorPayload(
                    "invalid_arguments",
                    "读取 invoke 输入失败。",
                    new
                    {
                        reason = exception.Message
                    }));
            }
        }

        static bool TryBuildRequest(InvokeOptions options, string inputJson, out InvokeRequest request, out object errorPayload)
        {
            request = null!;
            errorPayload = null!;
            if (!CliJson.TryDeserializeObject(inputJson, out var root, out var parseError))
            {
                errorPayload = ResultFormatter.CreateErrorPayload(
                    "invalid_arguments",
                    "invoke 输入必须是 JSON 对象。",
                    new
                    {
                        reason = parseError
                    });
                return false;
            }

            Dictionary<string, object> args;
            if (CliObjectAccessor.TryGetMember(root, "args", out var argsPayload) && argsPayload != null)
            {
                if (argsPayload is not Dictionary<string, object> nestedArgs)
                {
                    errorPayload = ResultFormatter.CreateErrorPayload(
                        "invalid_arguments",
                        "args 字段必须是 JSON 对象。");
                    return false;
                }

                args = CloneDictionary(nestedArgs);
            }
            else
            {
                args = CloneDictionary(root);
                args.Remove("requestId");
                args.Remove("tool");
                args.Remove("timeoutMs");
            }

            var timeoutMs = options.TimeoutMs;
            if (!timeoutMs.HasValue && CliObjectAccessor.TryGetInt(root, "timeoutMs", out var requestTimeout) && requestTimeout > 0)
            {
                timeoutMs = requestTimeout;
            }

            request = new InvokeRequest
            {
                requestId = CliObjectAccessor.TryGetString(root, "requestId", out var requestId) ? requestId : string.Empty,
                tool = options.ToolId,
                args = args,
                timeoutMs = timeoutMs
            };
            return true;
        }

        static async Task<object> WaitForCompletionAsync(BridgeClient client, string jobId, int? timeoutMs)
        {
            var startedAtUtc = DateTime.UtcNow;
            while (true)
            {
                if (timeoutMs.HasValue)
                {
                    var elapsedMs = (int)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds;
                    if (elapsedMs >= timeoutMs.Value)
                    {
                        return ResultFormatter.CreateErrorPayload(
                            "wait_timeout",
                            $"等待 Job '{jobId}' 完成超时。",
                            new
                            {
                                jobId,
                                timeoutMs = timeoutMs.Value
                            });
                    }
                }

                var remainingTimeout = timeoutMs.HasValue
                    ? Math.Max(1, timeoutMs.Value - (int)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds)
                    : (int?)null;
                var statusResult = await client.GetAsync($"/job/{Uri.EscapeDataString(jobId)}", remainingTimeout);
                if (ResultFormatter.GetExitCode(statusResult.Payload) == 2)
                {
                    return statusResult.Payload;
                }

                if (!CliObjectAccessor.TryGetString(statusResult.Payload, "status", out var status))
                {
                    return ResultFormatter.CreateErrorPayload(
                        "invalid_response",
                        "job-status 响应缺少 status 字段。",
                        new { jobId });
                }

                if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(PollDelayMs);
                    continue;
                }

                if (CliObjectAccessor.TryGetMember(statusResult.Payload, "result", out var invokeResult) && invokeResult != null)
                {
                    return invokeResult;
                }

                return ResultFormatter.CreateErrorPayload(
                    "invalid_response",
                    "job-status 响应缺少 result 字段。",
                    new
                    {
                        jobId,
                        status
                    });
            }
        }

        static bool IsPendingInvokeResponse(object payload, out string jobId)
        {
            jobId = string.Empty;
            if (!CliObjectAccessor.TryGetString(payload, "status", out var status)
                || !string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return CliObjectAccessor.TryGetString(payload, "jobId", out jobId) && !string.IsNullOrWhiteSpace(jobId);
        }

        static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            var clone = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                clone[pair.Key] = pair.Value;
            }

            return clone;
        }

        static bool TryReadValue(string[] args, ref int index, out string value)
        {
            value = string.Empty;
            if (index + 1 >= args.Length)
            {
                return false;
            }

            value = args[++index];
            return true;
        }
    }
}
