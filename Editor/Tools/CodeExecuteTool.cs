using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool(
        "code.execute",
        Description = "Execute arbitrary C# code inside Unity Editor",
        Mode = ToolMode.Both,
        Capabilities = ToolCapabilities.Dangerous,
        Category = "editor")]
    public sealed class CodeExecuteTool : IUnityCliAsyncTool
    {
        const string DefaultCompiler = "auto";
        const string RoslynCompiler = "roslyn";
        const string CodeDomCompiler = "codedom";

        static readonly string[] SupportedCompilers =
        {
            DefaultCompiler,
            RoslynCompiler,
            CodeDomCompiler
        };

        static readonly SafetyRule[] SafetyRules =
        {
            new SafetyRule(@"\bFile\s*\.\s*Delete\s*\(", "检测到 File.Delete，默认安全检查已阻止该调用。"),
            new SafetyRule(@"\bDirectory\s*\.\s*Delete\s*\(", "检测到 Directory.Delete，默认安全检查已阻止该调用。"),
            new SafetyRule(@"\bProcess\s*\.\s*Start\s*\(", "检测到 Process.Start，默认安全检查已阻止该调用。"),
            new SafetyRule(@"\bwhile\s*\(\s*true\s*\)", "检测到 while(true)，默认安全检查已阻止该调用。"),
            new SafetyRule(@"\bfor\s*\(\s*;\s*;\s*\)", "检测到 for(;;)，默认安全检查已阻止该调用。"),
            new SafetyRule(@"\bfor\s*\(\s*;\s*true\s*;", "检测到 for(; true; ...)，默认安全检查已阻止该调用。")
        };

        public string Id => "code.execute";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Execute arbitrary C# code inside Unity Editor",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.Dangerous,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "code",
                        type = "string",
                        description = "C# method body to compile and execute",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "compiler",
                        type = "string",
                        description = "Optional compiler backend: auto, roslyn, codedom",
                        required = false,
                        defaultValue = DefaultCompiler
                    },
                    new ParamDescriptor
                    {
                        name = "safety_checks",
                        type = "boolean",
                        description = "Enable minimal dangerous-pattern checks before execution",
                        required = false,
                        defaultValue = true
                    }
                }
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (context == null)
            {
                return ToolResult.Error("invalid_parameter", "工具上下文不能为空。", nameof(context));
            }

            if (!EnsureExecutable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "code", out string code, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "compiler", DefaultCompiler, out string compiler, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "safety_checks", true, out bool safetyChecks, out error))
            {
                return error;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolResult.Error("invalid_parameter", "参数 'code' 不能为空。", new
                {
                    parameter = "code"
                });
            }

            compiler = NormalizeCompiler(compiler);
            if (!SupportedCompilers.Contains(compiler, StringComparer.Ordinal))
            {
                return ToolResult.Error("invalid_parameter", $"不支持的 compiler '{compiler}'。", new
                {
                    parameter = "compiler",
                    value = compiler,
                    supported = SupportedCompilers
                });
            }

            if (safetyChecks && !TryValidateSafety(code, out error))
            {
                return error;
            }

            var state = new CodeExecuteJobState
            {
                code = code,
                compiler = compiler,
                safetyChecks = safetyChecks
            };

            var jobId = context.CreateJob(TimeSpan.FromSeconds(1), state);
            return ToolResult.Pending(jobId, "code execution scheduled", new
            {
                compiler,
                safety_checks = safetyChecks
            });
        }

        public ToolResult ContinueJob(UnityCliJob job, ToolContext context)
        {
            if (job == null)
            {
                return ToolResult.Error("tool_execution_failed", "Job 不能为空。", Id);
            }

            if (!EnsureExecutable(context, out var error))
            {
                return error;
            }

            var state = job.State as CodeExecuteJobState;
            if (state == null)
            {
                return ToolResult.Error("tool_execution_failed", "Job 状态缺失或类型无效。", new
                {
                    jobId = job.JobId
                });
            }

            var executionResult = ExecuteCode(state);
            return ToolResult.Ok(new
            {
                output = executionResult.Output,
                exception = executionResult.Exception,
                compiler = executionResult.Compiler,
                safety_checks = state.safetyChecks
            });
        }

        static bool EnsureExecutable(ToolContext context, out ToolResult error)
        {
            error = null;
            if (!StateGuard.EnsureReady(context, out error))
            {
                return false;
            }

            if (!StateGuard.EnsureNotPlaying(context, out error))
            {
                return false;
            }

            return true;
        }

        static string NormalizeCompiler(string compiler)
        {
            return string.IsNullOrWhiteSpace(compiler)
                ? DefaultCompiler
                : compiler.Trim().ToLowerInvariant();
        }

        static bool TryValidateSafety(string code, out ToolResult error)
        {
            error = null;
            foreach (var rule in SafetyRules)
            {
                if (!rule.Regex.IsMatch(code))
                {
                    continue;
                }

                error = ToolResult.Error("not_allowed", rule.Message, new
                {
                    pattern = rule.Pattern
                });
                return false;
            }

            return true;
        }

        static ExecutionResult ExecuteCode(CodeExecuteJobState state)
        {
            var source = BuildSource(state.code ?? string.Empty);
            switch (state.compiler)
            {
                case RoslynCompiler:
                    return ExecuteWithRoslyn(source, RoslynCompiler);
                case CodeDomCompiler:
                    return ExecuteWithCodeDom(source, CodeDomCompiler);
                default:
                    var roslynResult = ExecuteWithRoslyn(source, RoslynCompiler);
                    if (string.IsNullOrEmpty(roslynResult.Exception))
                    {
                        return roslynResult;
                    }

                    var codeDomResult = ExecuteWithCodeDom(source, CodeDomCompiler);
                    if (string.IsNullOrEmpty(codeDomResult.Exception))
                    {
                        return codeDomResult;
                    }

                    return new ExecutionResult(
                        string.Empty,
                        string.Join(Environment.NewLine + Environment.NewLine, new[]
                        {
                            PrefixCompilerFailure(RoslynCompiler, roslynResult.Exception),
                            PrefixCompilerFailure(CodeDomCompiler, codeDomResult.Exception)
                        }.Where(value => !string.IsNullOrWhiteSpace(value))),
                        DefaultCompiler);
            }
        }

        static string PrefixCompilerFailure(string compiler, string exception)
        {
            if (string.IsNullOrWhiteSpace(exception))
            {
                return string.Empty;
            }

            return $"[{compiler}] {exception}";
        }

        static ExecutionResult ExecuteWithRoslyn(string source, string compiler)
        {
            try
            {
                var codeAnalysisAssembly = ResolveAssembly(
                    "Microsoft.CodeAnalysis",
                    Path.Combine(EditorApplication.applicationContentsPath, "Tools", "Roslyn", "Microsoft.CodeAnalysis.dll"));
                var csharpAssembly = ResolveAssembly(
                    "Microsoft.CodeAnalysis.CSharp",
                    Path.Combine(EditorApplication.applicationContentsPath, "Tools", "Roslyn", "Microsoft.CodeAnalysis.CSharp.dll"));

                if (codeAnalysisAssembly == null || csharpAssembly == null)
                {
                    return new ExecutionResult(string.Empty, "Roslyn 编译器不可用。", compiler);
                }

                var metadataReferenceType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.MetadataReference", true);
                var syntaxTreeType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.SyntaxTree", true);
                var outputKindType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.OutputKind", true);
                var csharpCompilationType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation", true);
                var csharpCompilationOptionsType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions", true);
                var csharpSyntaxTreeType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree", true);

                var parseTextMethod = csharpSyntaxTreeType.GetMethod("ParseText", new[] { typeof(string) });
                var createReferenceMethod = metadataReferenceType.GetMethod("CreateFromFile", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                var createCompilationMethod = csharpCompilationType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method =>
                    {
                        if (!string.Equals(method.Name, "Create", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        var parameters = method.GetParameters();
                        return parameters.Length == 4 && parameters[0].ParameterType == typeof(string);
                    });

                if (parseTextMethod == null || createReferenceMethod == null || createCompilationMethod == null)
                {
                    return new ExecutionResult(string.Empty, "Roslyn 反射入口缺失。", compiler);
                }

                var syntaxTree = parseTextMethod.Invoke(null, new object[] { source });
                var references = CreateMetadataReferenceArray(metadataReferenceType, createReferenceMethod);
                var outputKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
                var compilationOptions = Activator.CreateInstance(csharpCompilationOptionsType, outputKind);
                var syntaxTrees = Array.CreateInstance(syntaxTreeType, 1);
                syntaxTrees.SetValue(syntaxTree, 0);
                var compilation = createCompilationMethod.Invoke(null, new object[]
                {
                    "UnityCli.DynamicExecution",
                    syntaxTrees,
                    references,
                    compilationOptions
                });

                using (var stream = new MemoryStream())
                {
                    var emitMethod = compilation.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(method =>
                        {
                            if (!string.Equals(method.Name, "Emit", StringComparison.Ordinal))
                            {
                                return false;
                            }

                            var parameters = method.GetParameters();
                            return parameters.Length > 0 && parameters[0].ParameterType == typeof(Stream);
                        });

                    if (emitMethod == null)
                    {
                        return new ExecutionResult(string.Empty, "Roslyn Emit 入口缺失。", compiler);
                    }

                    var emitArguments = BuildEmitArguments(emitMethod, stream);
                    var emitResult = emitMethod.Invoke(compilation, emitArguments);
                    var success = (bool)(emitResult.GetType().GetProperty("Success")?.GetValue(emitResult) ?? false);
                    if (!success)
                    {
                        return new ExecutionResult(string.Empty, BuildDiagnosticMessage(emitResult.GetType().GetProperty("Diagnostics")?.GetValue(emitResult) as IEnumerable), compiler);
                    }

                    return ExecuteCompiledAssembly(stream.ToArray(), compiler);
                }
            }
            catch (Exception exception)
            {
                return new ExecutionResult(string.Empty, exception.ToString(), compiler);
            }
        }

        static object[] BuildEmitArguments(MethodInfo emitMethod, MemoryStream stream)
        {
            var parameters = emitMethod.GetParameters();
            var values = new object[parameters.Length];
            values[0] = stream;
            for (var index = 1; index < values.Length; index++)
            {
                values[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : null;
            }

            return values;
        }

        static Array CreateMetadataReferenceArray(Type metadataReferenceType, MethodInfo createReferenceMethod)
        {
            var referencePaths = GetReferencePaths().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var references = Array.CreateInstance(metadataReferenceType, referencePaths.Length);
            for (var index = 0; index < referencePaths.Length; index++)
            {
                var reference = createReferenceMethod.Invoke(null, new object[] { referencePaths[index] });
                references.SetValue(reference, index);
            }

            return references;
        }

        static IEnumerable<string> GetReferencePaths()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.IsDynamic)
                {
                    continue;
                }

                var location = string.Empty;
                try
                {
                    location = assembly.Location;
                }
                catch
                {
                    location = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
                {
                    continue;
                }

                yield return location;
            }
        }

        static ExecutionResult ExecuteWithCodeDom(string source, string compiler)
        {
            try
            {
                var microsoftCSharpAssembly = ResolveAssembly("Microsoft.CSharp", null);
                var codeDomAssembly = ResolveAssembly("System.CodeDom", null);
                if (microsoftCSharpAssembly == null)
                {
                    return new ExecutionResult(string.Empty, "CodeDom 编译器不可用。", compiler);
                }

                var providerType = microsoftCSharpAssembly.GetType("Microsoft.CSharp.CSharpCodeProvider", false);
                var compilerParametersType = codeDomAssembly?.GetType("System.CodeDom.Compiler.CompilerParameters", false)
                    ?? Type.GetType("System.CodeDom.Compiler.CompilerParameters, System.CodeDom", false);
                if (providerType == null || compilerParametersType == null)
                {
                    return new ExecutionResult(string.Empty, "CodeDom 编译器不可用。", compiler);
                }

                using (var provider = Activator.CreateInstance(providerType) as IDisposable)
                {
                    if (provider == null)
                    {
                        return new ExecutionResult(string.Empty, "CodeDom 编译器创建失败。", compiler);
                    }

                    var compilerParameters = Activator.CreateInstance(compilerParametersType);
                    compilerParametersType.GetProperty("GenerateExecutable")?.SetValue(compilerParameters, false);
                    compilerParametersType.GetProperty("GenerateInMemory")?.SetValue(compilerParameters, true);
                    compilerParametersType.GetProperty("TreatWarningsAsErrors")?.SetValue(compilerParameters, false);

                    var referencedAssemblies = compilerParametersType.GetProperty("ReferencedAssemblies")?.GetValue(compilerParameters);
                    var addReferenceMethod = referencedAssemblies?.GetType().GetMethod("Add", new[] { typeof(string) });
                    foreach (var referencePath in GetReferencePaths().Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        addReferenceMethod?.Invoke(referencedAssemblies, new object[] { referencePath });
                    }

                    var compileMethod = providerType.GetMethod("CompileAssemblyFromSource", new[] { compilerParametersType, typeof(string[]) });
                    if (compileMethod == null)
                    {
                        return new ExecutionResult(string.Empty, "CodeDom 编译入口缺失。", compiler);
                    }

                    var compilerResults = compileMethod.Invoke(provider, new object[]
                    {
                        compilerParameters,
                        new[] { source }
                    });
                    var errors = compilerResults?.GetType().GetProperty("Errors")?.GetValue(compilerResults) as IEnumerable;
                    var diagnosticMessage = BuildCompilerErrorMessage(errors);
                    if (!string.IsNullOrEmpty(diagnosticMessage))
                    {
                        return new ExecutionResult(string.Empty, diagnosticMessage, compiler);
                    }

                    var assembly = compilerResults?.GetType().GetProperty("CompiledAssembly")?.GetValue(compilerResults) as Assembly;
                    if (assembly == null)
                    {
                        return new ExecutionResult(string.Empty, "CodeDom 未返回已编译程序集。", compiler);
                    }

                    return InvokeCompiledAssembly(assembly, compiler);
                }
            }
            catch (Exception exception)
            {
                return new ExecutionResult(string.Empty, exception.ToString(), compiler);
            }
        }

        static string BuildCompilerErrorMessage(IEnumerable errors)
        {
            if (errors == null)
            {
                return string.Empty;
            }

            var messages = new List<string>();
            foreach (var item in errors)
            {
                var errorType = item.GetType();
                var isWarning = (bool)(errorType.GetProperty("IsWarning")?.GetValue(item) ?? false);
                if (isWarning)
                {
                    continue;
                }

                messages.Add(item.ToString());
            }

            return messages.Count == 0
                ? string.Empty
                : "编译失败：" + Environment.NewLine + string.Join(Environment.NewLine, messages);
        }

        static string BuildDiagnosticMessage(IEnumerable diagnostics)
        {
            if (diagnostics == null)
            {
                return "编译失败。";
            }

            var messages = new List<string>();
            foreach (var item in diagnostics)
            {
                var severity = item.GetType().GetProperty("Severity")?.GetValue(item)?.ToString();
                if (!string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                messages.Add(item.ToString());
            }

            return messages.Count == 0
                ? "编译失败。"
                : "编译失败：" + Environment.NewLine + string.Join(Environment.NewLine, messages);
        }

        static ExecutionResult ExecuteCompiledAssembly(byte[] bytes, string compiler)
        {
            var assembly = Assembly.Load(bytes);
            return InvokeCompiledAssembly(assembly, compiler);
        }

        static ExecutionResult InvokeCompiledAssembly(Assembly assembly, string compiler)
        {
            if (assembly == null)
            {
                return new ExecutionResult(string.Empty, "编译结果为空。", compiler);
            }

            var runnerType = assembly.GetType("UnityCli.Editor.RuntimeExecution.CodeExecuteRunner", true);
            var method = runnerType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                return new ExecutionResult(string.Empty, "未找到入口方法 CodeExecuteRunner.Execute。", compiler);
            }

            var originalOut = Console.Out;
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                try
                {
                    Console.SetOut(writer);
                    var result = method.Invoke(null, null);
                    return new ExecutionResult(ComposeOutput(writer.ToString(), result), string.Empty, compiler);
                }
                catch (TargetInvocationException exception)
                {
                    var actual = exception.InnerException ?? exception;
                    return new ExecutionResult(writer.ToString(), actual.ToString(), compiler);
                }
                catch (Exception exception)
                {
                    return new ExecutionResult(writer.ToString(), exception.ToString(), compiler);
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }

        static string ComposeOutput(string consoleOutput, object result)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(consoleOutput))
            {
                builder.Append(consoleOutput.TrimEnd('\r', '\n'));
            }

            if (result != null)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(result);
            }

            return builder.ToString();
        }

        static Assembly ResolveAssembly(string assemblyName, string filePath)
        {
            var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (loadedAssembly != null)
            {
                return loadedAssembly;
            }

            try
            {
                return Assembly.Load(assemblyName);
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                try
                {
                    return Assembly.LoadFrom(filePath);
                }
                catch
                {
                }
            }

            return null;
        }

        static string BuildSource(string code)
        {
            return @"using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.RuntimeExecution
{
    public static class CodeExecuteRunner
    {
        public static object Execute()
        {
" + Indent(code, 3) + @"
        }
    }
}";
        }

        static string Indent(string text, int level)
        {
            var indent = new string(' ', level * 4);
            var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            return string.Join(Environment.NewLine, lines.Select(line => indent + line));
        }

        sealed class SafetyRule
        {
            public SafetyRule(string pattern, string message)
            {
                Pattern = pattern;
                Message = message;
                Regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }

            public string Pattern { get; }

            public string Message { get; }

            public Regex Regex { get; }
        }

        [Serializable]
        sealed class CodeExecuteJobState
        {
            public string code;
            public string compiler;
            public bool safetyChecks;
        }

        readonly struct ExecutionResult
        {
            public ExecutionResult(string output, string exception, string compiler)
            {
                Output = output ?? string.Empty;
                Exception = exception ?? string.Empty;
                Compiler = compiler ?? string.Empty;
            }

            public string Output { get; }

            public string Exception { get; }

            public string Compiler { get; }
        }
    }
}
