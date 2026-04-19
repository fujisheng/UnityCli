using System;
using System.Collections;
using System.Collections.Generic;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("batch", Description = "Execute multiple tool commands sequentially", Mode = ToolMode.EditOnly, Capabilities = ToolCapabilities.Dangerous, Category = "editor")]
    public sealed class BatchTool : IUnityCliTool
    {
        public string Id => "batch";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Execute multiple tool commands sequentially",
                mode = ToolMode.EditOnly,
                capabilities = ToolCapabilities.Dangerous,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "commands",
                        type = "array",
                        description = "Array of {tool, args} commands",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "fail_fast",
                        type = "boolean",
                        description = "Stop on first failure",
                        required = false,
                        defaultValue = false
                    }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            // 1. 校验 context + StateGuard
            if (!StateGuard.EnsureReady(context, out var error))
            {
                return error;
            }

            if (!StateGuard.EnsureNotPlaying(context, out error))
            {
                return error;
            }

            // 2. 提取必填参数 commands
            if (!ArgsHelper.TryGetArray(args, "commands", out var commandsRaw, out error))
            {
                return error;
            }

            if (commandsRaw.Length == 0)
            {
                return ToolResult.Error("invalid_parameter", "参数 'commands' 不能为空数组。", new
                {
                    parameter = "commands"
                });
            }

            // 3. 提取可选参数 fail_fast
            if (!ArgsHelper.TryGetOptional(args, "fail_fast", false, out bool failFast, out error))
            {
                return error;
            }

            // 4. 遍历执行
            var results = new List<object>();
            var failedCount = 0;

            for (var i = 0; i < commandsRaw.Length; i++)
            {
                var commandResult = ExecuteCommand(commandsRaw[i], i, context);
                results.Add(commandResult);

                if (!commandResult.ok)
                {
                    failedCount++;
                    if (failFast)
                    {
                        break;
                    }
                }
            }

            // 5. 返回汇总
            return ToolResult.Ok(new
            {
                results,
                failed_count = failedCount,
                total_count = commandsRaw.Length
            }, "batch completed");
        }

        BatchCommandResult ExecuteCommand(object commandRaw, int index, ToolContext context)
        {
            // 提取 command dict
            if (!TryGetCommandDict(commandRaw, out var commandDict))
            {
                return new BatchCommandResult
                {
                    tool = $"command[{index}]",
                    ok = false,
                    status = "error",
                    data = null,
                    error = new { message = "命令项必须是对象。", index }
                };
            }

            // 提取 tool 名称
            if (!commandDict.TryGetValue("tool", out var toolRaw) || !(toolRaw is string toolId) || string.IsNullOrWhiteSpace(toolId))
            {
                return new BatchCommandResult
                {
                    tool = $"command[{index}]",
                    ok = false,
                    status = "error",
                    data = null,
                    error = new { message = "命令缺少必填字段 'tool'。", index }
                };
            }

            // 禁止嵌套 batch
            if (string.Equals(toolId, "batch", StringComparison.Ordinal))
            {
                return new BatchCommandResult
                {
                    tool = toolId,
                    ok = false,
                    status = "error",
                    data = null,
                    error = new { message = "禁止嵌套 batch。", code = "invalid_parameter", index }
                };
            }

            // 提取 args
            var commandArgs = new Dictionary<string, object>();
            if (commandDict.TryGetValue("args", out var argsRaw) && argsRaw != null)
            {
                if (argsRaw is IDictionary<string, object> argsDict)
                {
                    commandArgs = new Dictionary<string, object>(argsDict);
                }
                else if (argsRaw is IDictionary genericDict)
                {
                    foreach (DictionaryEntry entry in genericDict)
                    {
                        if (entry.Key is string key)
                        {
                            commandArgs[key] = entry.Value;
                        }
                    }
                }
            }

            // 获取注册工具
            if (!UnityCliRegistry.TryGetTool(toolId, out var tool))
            {
                return new BatchCommandResult
                {
                    tool = toolId,
                    ok = false,
                    status = "error",
                    data = null,
                    error = new { message = $"工具 '{toolId}' 未注册。", code = "not_found", index }
                };
            }

            // 检查白名单
            if (!UnityCliAllowlist.IsAllowed(toolId))
            {
                return new BatchCommandResult
                {
                    tool = toolId,
                    ok = false,
                    status = "error",
                    data = null,
                    error = new { message = $"工具 '{toolId}' 未在白名单中。", code = "not_allowed", index }
                };
            }

            // 检查异步工具
            if (tool is IUnityCliAsyncTool)
            {
                return new BatchCommandResult
                {
                    tool = toolId,
                    ok = false,
                    status = "error",
                    data = null,
                    error = new { message = $"batch 不支持异步工具 '{toolId}'。", code = "invalid_parameter", index }
                };
            }

            // 执行工具
            try
            {
                var result = tool.Execute(commandArgs, context);
                return new BatchCommandResult
                {
                    tool = toolId,
                    ok = result.IsOk,
                    status = result.IsOk ? "completed" : "error",
                    data = result.Data,
                    error = result.IsOk ? null : result.ErrorInfo
                };
            }
            catch (Exception exception)
            {
                return new BatchCommandResult
                {
                    tool = toolId,
                    ok = false,
                    status = "error",
                    data = null,
                    error = new { message = exception.Message, code = "tool_execution_failed", index }
                };
            }
        }

        static bool TryGetCommandDict(object raw, out Dictionary<string, object> dict)
        {
            dict = null;

            if (raw is Dictionary<string, object> directDict)
            {
                dict = directDict;
                return true;
            }

            if (raw is IDictionary genericDict)
            {
                dict = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in genericDict)
                {
                    if (entry.Key is string key)
                    {
                        dict[key] = entry.Value;
                    }
                }

                return true;
            }

            return false;
        }

        sealed class BatchCommandResult
        {
            public string tool;
            public bool ok;
            public string status;
            public object data;
            public object error;
        }
    }
}
