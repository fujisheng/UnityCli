using System;
using System.Collections.Generic;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;

namespace UnityCli.Editor.Tools.BuiltIn
{
    [UnityCliTool("__test.sleep", Description = "Sleep for N milliseconds (async job)", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly, Category = "test")]
    public sealed class TestSleepTool : IUnityCliAsyncTool
    {
        const int DefaultMs = 1000;

        public string Id => "__test.sleep";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Sleep for N milliseconds (async job)",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "ms",
                        type = "integer",
                        description = "异步等待时长（毫秒）",
                        required = false,
                        defaultValue = DefaultMs
                    }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (context == null)
            {
                return ToolResult.Error("tool_execution_failed", "工具上下文不能为空。", Id);
            }

            var duration = ResolveDuration(args);
            var plannedMs = Convert.ToInt32(Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero));
            var payload = new SleepJobState
            {
                plannedMs = plannedMs
            };
            var jobId = context.CreateJob(duration, payload);
            return ToolResult.Pending(jobId, "sleep job accepted", new
            {
                plannedMs = plannedMs
            });
        }

        public ToolResult ContinueJob(UnityCliJob job, ToolContext context)
        {
            if (job == null)
            {
                return ToolResult.Error("tool_execution_failed", "Job 不能为空。", Id);
            }

            var plannedDuration = job.PlannedDuration ?? TimeSpan.FromMilliseconds(DefaultMs);
            if (job.Elapsed >= plannedDuration)
            {
                var sleptMs = Convert.ToInt32(Math.Round(plannedDuration.TotalMilliseconds, MidpointRounding.AwayFromZero));
                return ToolResult.Ok(new
                {
                    slept = sleptMs
                });
            }

            return ToolResult.Pending(job.JobId, "sleeping", new
            {
                elapsedMs = Convert.ToInt32(Math.Round(job.Elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero))
            });
        }

        static TimeSpan ResolveDuration(Dictionary<string, object> args)
        {
            var ms = DefaultMs;
            if (args != null && args.TryGetValue("ms", out var rawValue) && rawValue != null)
            {
                ms = ConvertToInt(rawValue, DefaultMs);
            }

            if (ms < 0)
            {
                ms = 0;
            }

            return TimeSpan.FromMilliseconds(ms);
        }

        static int ConvertToInt(object value, int fallback)
        {
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        [Serializable]
        sealed class SleepJobState
        {
            public int plannedMs;
        }
    }
}
