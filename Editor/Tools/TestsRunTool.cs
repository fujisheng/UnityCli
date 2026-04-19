using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool(
        "tests",
        Description = "Run Unity tests and query test job status",
        Mode = ToolMode.EditOnly,
        Capabilities = ToolCapabilities.Dangerous,
        Category = "editor")]
    public sealed class TestsRunTool : IUnityCliAsyncTool
    {
        const string ActionRun = "run";

        static readonly string[] SupportedModes = { "EditMode", "PlayMode" };

        public string Id => "tests";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Run Unity tests and query test job status",
                mode = ToolMode.EditOnly,
                capabilities = ToolCapabilities.Dangerous,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor { name = "action", type = "string", description = "Action to perform: run", required = true },
                    new ParamDescriptor { name = "mode", type = "string", description = "Test mode: EditMode or PlayMode", required = false, defaultValue = "EditMode" },
                    new ParamDescriptor { name = "test_names", type = "array", description = "Specific test names to run", required = false },
                    new ParamDescriptor { name = "group_names", type = "array", description = "Test group names to run", required = false },
                    new ParamDescriptor { name = "category_names", type = "array", description = "Test category names", required = false },
                    new ParamDescriptor { name = "assembly_names", type = "array", description = "Test assembly names", required = false }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (context == null)
            {
                return ToolResult.Error("invalid_parameter", "工具上下文不能为空。", nameof(context));
            }

            if (!StateGuard.EnsureReady(context, out var error))
            {
                return error;
            }

            if (!StateGuard.EnsureNotPlaying(context, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "action", out string action, out error))
            {
                return error;
            }

            if (!string.Equals(action, ActionRun, StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Error("invalid_action", $"不支持的操作 '{action}'。测试结果通过 job polling 机制返回，无需 action=status。", new
                {
                    action,
                    supported = new[] { ActionRun }
                });
            }

            if (!ArgsHelper.TryGetOptional(args, "mode", "EditMode", out string mode, out error))
            {
                return error;
            }

            if (!SupportedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                return ToolResult.Error("invalid_parameter", $"不支持的测试模式 '{mode}'。", new
                {
                    parameter = "mode",
                    value = mode,
                    supported = SupportedModes
                });
            }

            mode = SupportedModes.First(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));

            string[] testNames = null;
            if (args != null && args.TryGetValue("test_names", out var testNamesRaw) && testNamesRaw != null)
            {
                testNames = ToStringArray(testNamesRaw);
            }

            string[] groupNames = null;
            if (args != null && args.TryGetValue("group_names", out var groupNamesRaw) && groupNamesRaw != null)
            {
                groupNames = ToStringArray(groupNamesRaw);
            }

            string[] categoryNames = null;
            if (args != null && args.TryGetValue("category_names", out var categoryNamesRaw) && categoryNamesRaw != null)
            {
                categoryNames = ToStringArray(categoryNamesRaw);
            }

            string[] assemblyNames = null;
            if (args != null && args.TryGetValue("assembly_names", out var assemblyNamesRaw) && assemblyNamesRaw != null)
            {
                assemblyNames = ToStringArray(assemblyNamesRaw);
            }

            var state = new TestsRunJobState
            {
                mode = mode,
                testNames = testNames,
                groupNames = groupNames,
                categoryNames = categoryNames,
                assemblyNames = assemblyNames
            };

            var jobId = context.CreateJob(TimeSpan.FromSeconds(300), state);
            var testCount = testNames?.Length ?? 0;
            return ToolResult.Pending(jobId, "test run scheduled", new
            {
                mode,
                test_count = testCount
            });
        }

        public ToolResult ContinueJob(UnityCliJob job, ToolContext context)
        {
            if (job == null)
            {
                return ToolResult.Error("tool_execution_failed", "Job 不能为空。", Id);
            }

            var state = job.State as TestsRunJobState;
            if (state == null)
            {
                return ToolResult.Error("tool_execution_failed", "Job 状态缺失或类型无效。", new
                {
                    jobId = job.JobId
                });
            }

            // 检查测试是否已完成
            if (state.isCompleted)
            {
                return ToolResult.Ok(state.summary, "test run completed");
            }

            // 如果测试已启动但尚未完成，继续等待
            if (state.testStarted)
            {
                return ToolResult.Pending(job.JobId, "test run in progress", new
                {
                    mode = state.mode,
                    elapsed = job.Elapsed.TotalSeconds
                });
            }

            // 首次调用：启动测试
            var startResult = StartTestRun(state, job);
            if (!startResult)
            {
                return ToolResult.Error("tool_execution_failed", "TestRunner API 不可用或启动失败。", new
                {
                    mode = state.mode
                });
            }

            return ToolResult.Pending(job.JobId, "test run started", new
            {
                mode = state.mode
            });
        }

        bool StartTestRun(TestsRunJobState state, UnityCliJob job)
        {
            var testRunnerApiType = Type.GetType("UnityEditor.TestTools.TestRunner.TestRunnerApi, UnityEditor.TestRunner");
            if (testRunnerApiType == null)
            {
                return false;
            }

            var executionSettingsType = Type.GetType("UnityEditor.TestTools.TestRunner.ExecutionSettings, UnityEditor.TestRunner");
            var filterType = Type.GetType("UnityEditor.TestTools.TestRunner.Filter, UnityEditor.TestRunner");
            var testModeType = Type.GetType("UnityEditor.TestTools.TestRunner.TestMode, UnityEditor.TestRunner");
            var callbacksType = Type.GetType("UnityEditor.TestTools.TestRunner.ICallbacks, UnityEditor.TestRunner");

            if (executionSettingsType == null || filterType == null || testModeType == null)
            {
                return false;
            }

            // 创建 TestRunnerApi 实例
            var api = ScriptableObject.CreateInstance(testRunnerApiType);
            if (api == null)
            {
                return false;
            }

            // 解析 TestMode 枚举
            object testMode;
            if (string.Equals(state.mode, "PlayMode", StringComparison.OrdinalIgnoreCase))
            {
                testMode = Enum.Parse(testModeType, "PlayMode");
            }
            else
            {
                testMode = Enum.Parse(testModeType, "EditMode");
            }

            // 创建 Filter
            var filter = Activator.CreateInstance(filterType);
            if (filter == null)
            {
                return false;
            }

            // 设置 filter 属性
            if (state.testNames != null && state.testNames.Length > 0)
            {
                var namesProperty = filterType.GetProperty("testNames");
                namesProperty?.SetValue(filter, state.testNames);
            }

            if (state.groupNames != null && state.groupNames.Length > 0)
            {
                var groupProperty = filterType.GetProperty("groupNames");
                groupProperty?.SetValue(filter, state.groupNames);
            }

            if (state.categoryNames != null && state.categoryNames.Length > 0)
            {
                var categoryProperty = filterType.GetProperty("categoryNames");
                categoryProperty?.SetValue(filter, state.categoryNames);
            }

            if (state.assemblyNames != null && state.assemblyNames.Length > 0)
            {
                var assemblyProperty = filterType.GetProperty("assemblyNames");
                assemblyProperty?.SetValue(filter, state.assemblyNames);
            }

            // 创建 ExecutionSettings
            object executionSettings;
            var executionSettingsConstructor = executionSettingsType.GetConstructor(new[] { filterType.MakeArrayType(), testModeType });
            if (executionSettingsConstructor != null)
            {
                var filters = Array.CreateInstance(filterType, 1);
                filters.SetValue(filter, 0);
                executionSettings = executionSettingsConstructor.Invoke(new object[] { filters, testMode });
            }
            else
            {
                executionSettings = Activator.CreateInstance(executionSettingsType);
                if (executionSettings == null)
                {
                    return false;
                }

                // 尝试通过属性设置
                var filtersProperty = executionSettingsType.GetProperty("filters");
                if (filtersProperty != null)
                {
                    var filters = Array.CreateInstance(filterType, 1);
                    filters.SetValue(filter, 0);
                    filtersProperty.SetValue(executionSettings, filters);
                }

                var overwriteProperty = executionSettingsType.GetProperty("overwriteTestResultsFile");
                overwriteProperty?.SetValue(executionSettings, false);
            }

            // 注册回调
            if (callbacksType != null)
            {
                var callback = new TestRunCallback(state);
                var registerMethod = testRunnerApiType.GetMethod("RegisterCallbacks", new[] { callbacksType });
                if (registerMethod != null)
                {
                    registerMethod.Invoke(api, new object[] { callback });
                }
            }

            // 执行测试
            var executeMethod = testRunnerApiType.GetMethod("Execute", new[] { executionSettingsType });
            if (executeMethod == null)
            {
                return false;
            }

            executeMethod.Invoke(api, new object[] { executionSettings });
            state.testStarted = true;
            return true;
        }

        static string[] ToStringArray(object raw)
        {
            if (raw == null)
            {
                return null;
            }

            if (raw is string[] stringArray)
            {
                return stringArray;
            }

            if (raw is object[] objArray)
            {
                var result = new string[objArray.Length];
                for (var i = 0; i < objArray.Length; i++)
                {
                    result[i] = objArray[i]?.ToString() ?? string.Empty;
                }

                return result;
            }

            if (raw is IList<string> list)
            {
                return list.ToArray();
            }

            if (raw is System.Collections.IList iList)
            {
                var result = new string[iList.Count];
                for (var i = 0; i < iList.Count; i++)
                {
                    result[i] = iList[i]?.ToString() ?? string.Empty;
                }

                return result;
            }

            return null;
        }

        // 测试回调实现
        sealed class TestRunCallback
        {
            readonly TestsRunJobState state;

            public TestRunCallback(TestsRunJobState state)
            {
                this.state = state;
            }

            // 通过反射调用 — ICallbacks.RunStarted
            public void RunStarted(string testsToRun)
            {
                // 测试开始运行
            }

            // 通过反射调用 — ICallbacks.RunFinished
            public void RunFinished(object result)
            {
                if (result == null)
                {
                    state.isCompleted = true;
                    state.summary = new TestRunSummary();
                    return;
                }

                try
                {
                    var resultType = result.GetType();
                    // ITestResultAdaptor 有 PassCount, FailCount, SkipCount, InconclusiveCount, Duration
                    var passCount = (int)(resultType.GetProperty("PassCount")?.GetValue(result) ?? 0);
                    var failCount = (int)(resultType.GetProperty("FailCount")?.GetValue(result) ?? 0);
                    var skipCount = (int)(resultType.GetProperty("SkipCount")?.GetValue(result) ?? 0);
                    var inconclusiveCount = (int)(resultType.GetProperty("InconclusiveCount")?.GetValue(result) ?? 0);
                    var duration = (double)(resultType.GetProperty("Duration")?.GetValue(result) ?? 0.0);

                    state.summary = new TestRunSummary
                    {
                        total = passCount + failCount + skipCount + inconclusiveCount,
                        passed = passCount,
                        failed = failCount,
                        skipped = skipCount + inconclusiveCount,
                        duration = duration
                    };

                    // 收集子结果
                    CollectResults(result);
                }
                catch
                {
                    state.summary = new TestRunSummary();
                }

                state.isCompleted = true;
            }

            // 通过反射调用 — ICallbacks.TestStarted
            public void TestStarted(object test)
            {
                // 单个测试开始
            }

            // 通过反射调用 — ICallbacks.TestFinished
            public void TestFinished(object result)
            {
                if (result == null)
                {
                    return;
                }

                try
                {
                    var resultType = result.GetType();
                    var name = resultType.GetProperty("FullName")?.GetValue(result)?.ToString()
                        ?? resultType.GetProperty("Name")?.GetValue(result)?.ToString()
                        ?? string.Empty;
                    var status = resultType.GetProperty("TestStatus")?.GetValue(result)?.ToString()
                        ?? "Inconclusive";
                    var duration = (double)(resultType.GetProperty("Duration")?.GetValue(result) ?? 0.0);
                    var message = resultType.GetProperty("Message")?.GetValue(result)?.ToString()
                        ?? string.Empty;

                    state.summary.results.Add(new TestResultEntry
                    {
                        name = name,
                        status = status,
                        duration = duration,
                        message = message
                    });
                }
                catch
                {
                    // 忽略单个结果的收集错误
                }
            }

            void CollectResults(object result)
            {
                if (result == null)
                {
                    return;
                }

                try
                {
                    var resultType = result.GetType();
                    var hasChildrenProperty = resultType.GetProperty("HasChildren");
                    var childrenProperty = resultType.GetProperty("Children");

                    if (hasChildrenProperty == null || childrenProperty == null)
                    {
                        return;
                    }

                    var hasChildren = (bool)(hasChildrenProperty.GetValue(result) ?? false);
                    if (!hasChildren)
                    {
                        // 叶子节点 — 收集结果
                        var name = resultType.GetProperty("FullName")?.GetValue(result)?.ToString()
                            ?? resultType.GetProperty("Name")?.GetValue(result)?.ToString()
                            ?? string.Empty;
                        var status = resultType.GetProperty("TestStatus")?.GetValue(result)?.ToString()
                            ?? "Inconclusive";
                        var duration = (double)(resultType.GetProperty("Duration")?.GetValue(result) ?? 0.0);
                        var message = resultType.GetProperty("Message")?.GetValue(result)?.ToString()
                            ?? string.Empty;

                        // 避免重复（TestFinished 可能已添加）
                        if (!state.summary.results.Any(r => string.Equals(r.name, name, StringComparison.Ordinal)))
                        {
                            state.summary.results.Add(new TestResultEntry
                            {
                                name = name,
                                status = status,
                                duration = duration,
                                message = message
                            });
                        }

                        return;
                    }

                    var children = childrenProperty.GetValue(result) as System.Collections.IEnumerable;
                    if (children == null)
                    {
                        return;
                    }

                    foreach (var child in children)
                    {
                        CollectResults(child);
                    }
                }
                catch
                {
                    // 忽略递归收集的错误
                }
            }
        }

        [Serializable]
        sealed class TestsRunJobState
        {
            public string mode;
            public string[] testNames;
            public string[] groupNames;
            public string[] categoryNames;
            public string[] assemblyNames;
            public bool testStarted;
            public bool isCompleted;
            public TestRunSummary summary;
        }

        sealed class TestRunSummary
        {
            public int total;
            public int passed;
            public int failed;
            public int skipped;
            public double duration;
            public List<TestResultEntry> results = new List<TestResultEntry>();
        }

        sealed class TestResultEntry
        {
            public string name;
            public string status;
            public double duration;
            public string message;
        }
    }
}
