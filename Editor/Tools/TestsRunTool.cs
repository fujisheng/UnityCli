using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
                if (!HasValidSummary(state))
                {
                    return ToolResult.Error("test_results_empty", "测试运行结束，但未返回有效结果。", CreateCompletionDetails(state));
                }

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
            var testRunnerApiType = ResolveType(
                "UnityEditor.TestTools.TestRunner.Api.TestRunnerApi, UnityEditor.TestRunner",
                "UnityEditor.TestTools.TestRunner.TestRunnerApi, UnityEditor.TestRunner");
            if (testRunnerApiType == null)
            {
                return false;
            }

            var executionSettingsType = ResolveType(
                "UnityEditor.TestTools.TestRunner.Api.ExecutionSettings, UnityEditor.TestRunner",
                "UnityEditor.TestTools.TestRunner.ExecutionSettings, UnityEditor.TestRunner");
            var filterType = ResolveType(
                "UnityEditor.TestTools.TestRunner.Api.Filter, UnityEditor.TestRunner",
                "UnityEditor.TestTools.TestRunner.Filter, UnityEditor.TestRunner");
            var testModeType = ResolveType(
                "UnityEditor.TestTools.TestRunner.Api.TestMode, UnityEditor.TestRunner",
                "UnityEditor.TestTools.TestRunner.TestMode, UnityEditor.TestRunner");
            var callbacksType = ResolveType(
                "UnityEditor.TestTools.TestRunner.Api.ICallbacks, UnityEditor.TestRunner",
                "UnityEditor.TestTools.TestRunner.ICallbacks, UnityEditor.TestRunner");

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

            SetMemberValue(filterType, filter, "testMode", testMode);

            // 设置 filter 属性
            var resolvedGroupNames = MergeGroupFilters(state.groupNames, state.testNames);
            if (state.testNames != null && state.testNames.Length > 0)
            {
                SetMemberValue(filterType, filter, "testNames", null);
            }

            if (resolvedGroupNames != null && resolvedGroupNames.Length > 0)
            {
                SetMemberValue(filterType, filter, "groupNames", resolvedGroupNames);
            }

            if (state.categoryNames != null && state.categoryNames.Length > 0)
            {
                SetMemberValue(filterType, filter, "categoryNames", state.categoryNames);
            }

            if (state.assemblyNames != null && state.assemblyNames.Length > 0)
            {
                SetMemberValue(filterType, filter, "assemblyNames", state.assemblyNames);
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
                var filtersConstructor = executionSettingsType.GetConstructor(new[] { filterType.MakeArrayType() });
                if (filtersConstructor != null)
                {
                    var filters = Array.CreateInstance(filterType, 1);
                    filters.SetValue(filter, 0);
                    executionSettings = filtersConstructor.Invoke(new object[] { filters });
                }
                else
                {
                    executionSettings = Activator.CreateInstance(executionSettingsType);
                    if (executionSettings == null)
                    {
                        return false;
                    }

                    // 尝试通过属性设置
                    var filters = Array.CreateInstance(filterType, 1);
                    filters.SetValue(filter, 0);
                    SetMemberValue(executionSettingsType, executionSettings, "filters", filters);
                }

                SetMemberValue(executionSettingsType, executionSettings, "overwriteTestResultsFile", false);
            }

            if (string.Equals(state.mode, "EditMode", StringComparison.OrdinalIgnoreCase))
            {
                SetMemberValue(executionSettingsType, executionSettings, "runSynchronously", true);
            }

            // 注册回调
            if (callbacksType != null)
            {
                var callback = CreateCallbackProxy(callbacksType, state);
                if (callback == null)
                {
                    return false;
                }

                var registerMethod = testRunnerApiType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(method => method.Name == "RegisterCallbacks"
                        && method.IsGenericMethodDefinition
                        && method.GetGenericArguments().Length == 1
                        && (method.GetParameters().Length == 1 || method.GetParameters().Length == 2));
                if (registerMethod != null)
                {
                    var genericMethod = registerMethod.MakeGenericMethod(callbacksType);
                    var parameters = registerMethod.GetParameters();
                    if (parameters.Length == 1)
                    {
                        genericMethod.Invoke(api, new[] { callback });
                    }
                    else
                    {
                        genericMethod.Invoke(api, new object[] { callback, 0 });
                    }
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

        static Type ResolveType(params string[] typeNames)
        {
            if (typeNames == null || typeNames.Length == 0)
            {
                return null;
            }

            foreach (var typeName in typeNames)
            {
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }

                var type = Type.GetType(typeName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        static object CreateCallbackProxy(Type callbacksType, TestsRunJobState state)
        {
            if (callbacksType == null || state == null)
            {
                return null;
            }

            var createMethod = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "Create" && method.IsGenericMethodDefinition);
            if (createMethod == null)
            {
                return null;
            }

            var proxy = createMethod.MakeGenericMethod(callbacksType, typeof(TestsRunCallbackDispatchProxy)).Invoke(null, null);
            var typedProxy = proxy as TestsRunCallbackDispatchProxy;
            if (typedProxy == null)
            {
                return null;
            }

            var callback = new TestRunCallback(state);
            typedProxy.dispatcher = (methodName, args) =>
            {
                var argument = args != null && args.Length > 0 ? args[0] : null;
                switch (methodName)
                {
                    case "RunStarted":
                        callback.RunStarted(argument);
                        break;
                    case "RunFinished":
                        callback.RunFinished(argument);
                        break;
                    case "TestStarted":
                        callback.TestStarted(argument);
                        break;
                    case "TestFinished":
                        callback.TestFinished(argument);
                        break;
                }
            };

            return proxy;
        }

        static void SetMemberValue(Type type, object target, string memberName, object value)
        {
            if (type == null || target == null || string.IsNullOrWhiteSpace(memberName))
            {
                return;
            }

            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                property.SetValue(target, value);
                return;
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            field?.SetValue(target, value);
        }

        static bool HasValidSummary(TestsRunJobState state)
        {
            if (state?.summary == null)
            {
                return false;
            }

            if (state.summary.total > 0)
            {
                return true;
            }

            if (!HasFilters(state))
            {
                return state.receivedRunFinished;
            }

            return state.summary.results != null && state.summary.results.Count > 0;
        }

        static bool HasFilters(TestsRunJobState state)
        {
            if (state == null)
            {
                return false;
            }

            return HasAnyValues(state.testNames)
                || HasAnyValues(state.groupNames)
                || HasAnyValues(state.categoryNames)
                || HasAnyValues(state.assemblyNames);
        }

        static bool HasAnyValues(string[] values)
        {
            return values != null && values.Any(value => !string.IsNullOrWhiteSpace(value));
        }

        static string[] MergeGroupFilters(string[] groupNames, string[] testNames)
        {
            var values = new List<string>();
            if (groupNames != null)
            {
                values.AddRange(groupNames.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            if (testNames != null)
            {
                foreach (var testName in testNames)
                {
                    if (string.IsNullOrWhiteSpace(testName))
                    {
                        continue;
                    }

                    values.Add($"(^|.*\\.){Regex.Escape(testName)}$");
                }
            }

            return values.Count == 0 ? null : values.ToArray();
        }

        static object CreateCompletionDetails(TestsRunJobState state)
        {
            return new
            {
                mode = state?.mode,
                test_names = state?.testNames,
                group_names = state?.groupNames,
                category_names = state?.categoryNames,
                assembly_names = state?.assemblyNames,
                receivedRunStarted = state?.receivedRunStarted ?? false,
                receivedRunFinished = state?.receivedRunFinished ?? false,
                receivedTestFinished = state?.receivedTestFinished ?? false,
                total = state?.summary?.total ?? 0,
                results = state?.summary?.results?.Count ?? 0
            };
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
            public void RunStarted(object testsToRun)
            {
                // 测试开始运行
                state.receivedRunStarted = true;
            }

            // 通过反射调用 — ICallbacks.RunFinished
            public void RunFinished(object result)
            {
                if (result == null)
                {
                    state.summary ??= new TestRunSummary();
                    state.receivedRunFinished = true;
                    state.isCompleted = true;
                    return;
                }

                try
                {
                    state.summary ??= new TestRunSummary();
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

                state.receivedRunFinished = true;
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
                    state.summary ??= new TestRunSummary();
                    state.receivedTestFinished = true;
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
            public bool receivedRunStarted;
            public bool receivedRunFinished;
            public bool receivedTestFinished;
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

    public class TestsRunCallbackDispatchProxy : DispatchProxy
    {
        public Action<string, object[]> dispatcher;

        public TestsRunCallbackDispatchProxy()
        {
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            dispatcher?.Invoke(targetMethod?.Name, args);
            return null;
        }
    }
}
