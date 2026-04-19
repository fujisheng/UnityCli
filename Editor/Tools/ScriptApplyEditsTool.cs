using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("script.apply_edits", Description = "Apply text-based structured edits to a C# script under Assets/", Mode = ToolMode.Both, Capabilities = ToolCapabilities.WriteAssets, Category = "editor")]
    public sealed class ScriptApplyEditsTool : IUnityCliTool
    {
        static readonly string[] SupportedOps = { "replace_method", "insert_method", "delete_method", "anchor_insert", "anchor_replace" };

        public string Id => "script.apply_edits";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Apply text-based structured edits to a C# script under Assets/",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "name",
                        type = "string",
                        description = "Script file name without .cs",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "path",
                        type = "string",
                        description = "Script folder path under Assets/",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "edits",
                        type = "array",
                        description = "Structured edit array",
                        required = true
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

            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "name", out string name, out error)
                || !ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error)
                || !ArgsHelper.TryGetArray(args, "edits", out var rawEdits, out error))
            {
                return error;
            }

            if (!TryBuildScriptPath(name, rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            var fullPath = GetFullPath(normalizedPath, context);
            if (!File.Exists(fullPath))
            {
                return ToolResult.Error("not_found", "脚本文件不存在。", new
                {
                    name,
                    path = rawPath,
                    normalizedPath,
                    fullPath
                });
            }

            if (!TryParseEdits(rawEdits, out var edits, out error))
            {
                return error;
            }

            try
            {
                var contents = File.ReadAllText(fullPath);
                var updatedContents = contents;
                var applied = new List<object>();

                for (var index = 0; index < edits.Count; index++)
                {
                    if (!TryApplyEdit(updatedContents, edits[index], out updatedContents, out error))
                    {
                        return error;
                    }

                    applied.Add(new
                    {
                        op = edits[index].Op,
                        className = edits[index].ClassName,
                        methodName = edits[index].MethodName,
                        anchor = edits[index].Anchor,
                        position = edits[index].Position
                    });
                }

                File.WriteAllText(fullPath, updatedContents);
                AssetDatabase.ImportAsset(normalizedPath, ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(normalizedPath);

                return ToolResult.Ok(new
                {
                    action = "apply_edits",
                    path = normalizedPath,
                    fullPath,
                    editCount = edits.Count,
                    imported = importer != null,
                    applied
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"编辑脚本失败：{exception.Message}", new
                {
                    action = "apply_edits",
                    path = normalizedPath,
                    exception = exception.GetType().FullName
                });
            }
        }

        static bool EnsureWritable(ToolContext context, out ToolResult error)
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

        static bool TryBuildScriptPath(string name, string rawPath, out string normalizedPath, out ToolResult error)
        {
            normalizedPath = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(name))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'name' 不能为空。", new
                {
                    parameter = "name"
                });
                return false;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out var normalizedFolder, out error))
            {
                return false;
            }

            if (normalizedFolder.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'path' 必须是脚本目录而不是脚本文件。", new
                {
                    parameter = "path",
                    path = rawPath
                });
                return false;
            }

            var trimmedName = name.Trim();
            var fileName = trimmedName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? trimmedName : $"{trimmedName}.cs";
            var combined = string.Equals(normalizedFolder, "Assets", StringComparison.OrdinalIgnoreCase)
                ? $"Assets/{fileName}"
                : $"{normalizedFolder}/{fileName}";

            return PathGuard.TryNormalizeScriptPath(combined, out normalizedPath, out error);
        }

        static bool TryParseEdits(object[] rawEdits, out List<StructuredEdit> edits, out ToolResult error)
        {
            edits = new List<StructuredEdit>();
            error = null;

            if (rawEdits == null || rawEdits.Length == 0)
            {
                error = ToolResult.Error("invalid_parameter", "参数 'edits' 不能为空数组。", new
                {
                    parameter = "edits"
                });
                return false;
            }

            for (var index = 0; index < rawEdits.Length; index++)
            {
                if (!(rawEdits[index] is IDictionary<string, object> editArgs))
                {
                    error = ToolResult.Error("invalid_parameter", "参数 'edits' 的每一项都必须是对象。", new
                    {
                        parameter = "edits",
                        index
                    });
                    return false;
                }

                if (!ArgsHelper.TryGetRequired(editArgs, "op", out string op, out error))
                {
                    return false;
                }

                if (!SupportedOps.Contains(op, StringComparer.Ordinal))
                {
                    error = ToolResult.Error("invalid_parameter", "不支持的编辑操作。", new
                    {
                        parameter = "op",
                        value = op,
                        supported = SupportedOps
                    });
                    return false;
                }

                if (!ArgsHelper.TryGetRequired(editArgs, "className", out string className, out error))
                {
                    return false;
                }

                ArgsHelper.TryGetOptional(editArgs, "methodName", string.Empty, out string methodName, out _);
                ArgsHelper.TryGetOptional(editArgs, "replacement", string.Empty, out string replacement, out _);
                ArgsHelper.TryGetOptional(editArgs, "anchor", string.Empty, out string anchor, out _);
                ArgsHelper.TryGetOptional(editArgs, "text", string.Empty, out string text, out _);
                ArgsHelper.TryGetOptional(editArgs, "position", "end", out string position, out _);
                ArgsHelper.TryGetOptional(editArgs, "afterMethodName", string.Empty, out string afterMethodName, out _);
                ArgsHelper.TryGetOptional(editArgs, "beforeMethodName", string.Empty, out string beforeMethodName, out _);

                if ((op == "replace_method" || op == "delete_method") && string.IsNullOrWhiteSpace(methodName))
                {
                    error = ToolResult.Error("missing_parameter", "方法编辑缺少 'methodName'。", new
                    {
                        index,
                        op,
                        parameter = "methodName"
                    });
                    return false;
                }

                if (op == "insert_method")
                {
                    if (string.IsNullOrWhiteSpace(replacement))
                    {
                        error = ToolResult.Error("missing_parameter", "插入方法缺少 'replacement'。", new
                        {
                            index,
                            op,
                            parameter = "replacement"
                        });
                        return false;
                    }

                    if (!IsSupportedInsertPosition(position))
                    {
                        error = ToolResult.Error("invalid_parameter", "不支持的 insert_method position。", new
                        {
                            index,
                            op,
                            position,
                            supported = new[] { "start", "end", "after", "before" }
                        });
                        return false;
                    }

                    if (string.Equals(position, "after", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(afterMethodName))
                    {
                        error = ToolResult.Error("missing_parameter", "position=after 时缺少 'afterMethodName'。", new
                        {
                            index,
                            op,
                            parameter = "afterMethodName"
                        });
                        return false;
                    }

                    if (string.Equals(position, "before", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(beforeMethodName))
                    {
                        error = ToolResult.Error("missing_parameter", "position=before 时缺少 'beforeMethodName'。", new
                        {
                            index,
                            op,
                            parameter = "beforeMethodName"
                        });
                        return false;
                    }
                }

                if ((op == "anchor_insert" || op == "anchor_replace") && string.IsNullOrWhiteSpace(anchor))
                {
                    error = ToolResult.Error("missing_parameter", "锚点编辑缺少 'anchor'。", new
                    {
                        index,
                        op,
                        parameter = "anchor"
                    });
                    return false;
                }

                if (op == "anchor_insert" && string.IsNullOrEmpty(text))
                {
                    error = ToolResult.Error("missing_parameter", "anchor_insert 缺少 'text'。", new
                    {
                        index,
                        op,
                        parameter = "text"
                    });
                    return false;
                }

                if ((op == "anchor_replace" || op == "replace_method") && string.IsNullOrEmpty(replacement))
                {
                    error = ToolResult.Error("missing_parameter", $"{op} 缺少 'replacement'。", new
                    {
                        index,
                        op,
                        parameter = "replacement"
                    });
                    return false;
                }

                edits.Add(new StructuredEdit(index, op, className, methodName, replacement, anchor, text, position, afterMethodName, beforeMethodName));
            }

            return true;
        }

        static bool TryApplyEdit(string contents, StructuredEdit edit, out string updatedContents, out ToolResult error)
        {
            updatedContents = contents;
            error = null;

            if (!TryFindClassRange(contents, edit.ClassName, out var classRange, out error))
            {
                return false;
            }

            switch (edit.Op)
            {
                case "replace_method":
                    return TryReplaceMethod(contents, edit, classRange, out updatedContents, out error);
                case "insert_method":
                    return TryInsertMethod(contents, edit, classRange, out updatedContents, out error);
                case "delete_method":
                    return TryDeleteMethod(contents, edit, classRange, out updatedContents, out error);
                case "anchor_insert":
                    return TryAnchorInsert(contents, edit, classRange, out updatedContents, out error);
                case "anchor_replace":
                    return TryAnchorReplace(contents, edit, classRange, out updatedContents, out error);
                default:
                    error = ToolResult.Error("invalid_parameter", "不支持的编辑操作。", new
                    {
                        op = edit.Op,
                        supported = SupportedOps
                    });
                    return false;
            }
        }

        static bool TryReplaceMethod(string contents, StructuredEdit edit, Range classRange, out string updatedContents, out ToolResult error)
        {
            updatedContents = contents;
            error = null;
            if (!TryFindMethodRange(contents, edit.ClassName, edit.MethodName, classRange, out var methodRange, out error))
            {
                return false;
            }

            updatedContents = ReplaceRange(contents, methodRange.Start, methodRange.End, NormalizeInsertionText(edit.Replacement));
            return true;
        }

        static bool TryDeleteMethod(string contents, StructuredEdit edit, Range classRange, out string updatedContents, out ToolResult error)
        {
            updatedContents = contents;
            error = null;
            if (!TryFindMethodRange(contents, edit.ClassName, edit.MethodName, classRange, out var methodRange, out error))
            {
                return false;
            }

            updatedContents = ReplaceRange(contents, methodRange.Start, methodRange.End, string.Empty);
            return true;
        }

        static bool TryInsertMethod(string contents, StructuredEdit edit, Range classRange, out string updatedContents, out ToolResult error)
        {
            updatedContents = contents;
            error = null;
            var insertionIndex = classRange.End - 1;

            switch (edit.Position)
            {
                case "start":
                    insertionIndex = FindClassBodyStart(contents, classRange);
                    break;
                case "end":
                    insertionIndex = classRange.End - 1;
                    break;
                case "after":
                    if (!TryFindMethodRange(contents, edit.ClassName, edit.AfterMethodName, classRange, out var afterRange, out error))
                    {
                        return false;
                    }

                    insertionIndex = afterRange.End;
                    break;
                case "before":
                    if (!TryFindMethodRange(contents, edit.ClassName, edit.BeforeMethodName, classRange, out var beforeRange, out error))
                    {
                        return false;
                    }

                    insertionIndex = beforeRange.Start;
                    break;
            }

            var insertionText = BuildMethodInsertion(contents, insertionIndex, classRange, edit.Replacement);
            updatedContents = contents.Insert(insertionIndex, insertionText);
            return true;
        }

        static bool TryAnchorInsert(string contents, StructuredEdit edit, Range classRange, out string updatedContents, out ToolResult error)
        {
            updatedContents = contents;
            error = null;
            if (!TryFindAnchorRange(contents, edit, classRange, out var anchorRange, out error))
            {
                return false;
            }

            updatedContents = contents.Insert(anchorRange.End, edit.Text);
            return true;
        }

        static bool TryAnchorReplace(string contents, StructuredEdit edit, Range classRange, out string updatedContents, out ToolResult error)
        {
            updatedContents = contents;
            error = null;
            if (!TryFindAnchorRange(contents, edit, classRange, out var anchorRange, out error))
            {
                return false;
            }

            updatedContents = ReplaceRange(contents, anchorRange.Start, anchorRange.End, edit.Replacement);
            return true;
        }

        static bool TryFindClassRange(string contents, string className, out Range range, out ToolResult error)
        {
            range = default;
            error = null;
            var pattern = $@"\b(class|struct|record)\s+{Regex.Escape(className)}\b";
            var matches = Regex.Matches(contents, pattern, RegexOptions.CultureInvariant);
            if (matches.Count == 0)
            {
                error = ToolResult.Error("not_found", "未找到目标类型。", new
                {
                    className
                });
                return false;
            }

            if (matches.Count > 1)
            {
                error = ToolResult.Error("duplicate_name", "找到多个同名类型，无法确定编辑目标。", new
                {
                    className,
                    matchCount = matches.Count
                });
                return false;
            }

            var match = matches[0];
            var braceIndex = contents.IndexOf('{', match.Index + match.Length);
            if (braceIndex < 0)
            {
                error = ToolResult.Error("tool_execution_failed", "目标类型缺少可匹配的大括号。", new
                {
                    className
                });
                return false;
            }

            if (!TryFindMatchingBrace(contents, braceIndex, out var endBraceIndex))
            {
                error = ToolResult.Error("tool_execution_failed", "目标类型的大括号不平衡。", new
                {
                    className
                });
                return false;
            }

            range = new Range(match.Index, endBraceIndex + 1);
            return true;
        }

        static bool TryFindMethodRange(string contents, string className, string methodName, Range classRange, out Range range, out ToolResult error)
        {
            range = default;
            error = null;
            var classBodyStart = FindClassBodyStart(contents, classRange);
            var classBodyText = contents.Substring(classBodyStart, classRange.End - 1 - classBodyStart);
            var pattern = $@"(?m)^[ \t]*(?:\[[^\]]+\][ \t]*\r?\n[ \t]*)*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|new|extern|unsafe|partial|readonly)\s+)*(?:[\w<>,\[\]\.?]+\s+)+{Regex.Escape(methodName)}\s*\(";
            var matches = Regex.Matches(classBodyText, pattern, RegexOptions.CultureInvariant);

            if (matches.Count == 0)
            {
                error = ToolResult.Error("not_found", "未找到目标方法。", new
                {
                    className,
                    methodName
                });
                return false;
            }

            if (matches.Count > 1)
            {
                error = ToolResult.Error("duplicate_name", "找到多个同名方法，无法确定编辑目标。", new
                {
                    className,
                    methodName,
                    matchCount = matches.Count
                });
                return false;
            }

            var match = matches[0];
            var methodStart = classBodyStart + match.Index;
            var bodyOpenIndex = contents.IndexOf('{', methodStart + match.Length);
            if (bodyOpenIndex < 0 || bodyOpenIndex >= classRange.End)
            {
                error = ToolResult.Error("tool_execution_failed", "目标方法缺少可匹配的大括号。", new
                {
                    className,
                    methodName
                });
                return false;
            }

            if (!TryFindMatchingBrace(contents, bodyOpenIndex, out var bodyCloseIndex))
            {
                error = ToolResult.Error("tool_execution_failed", "目标方法的大括号不平衡。", new
                {
                    className,
                    methodName
                });
                return false;
            }

            var methodEnd = ExtendTrailingWhitespace(contents, bodyCloseIndex + 1, classRange.End - 1);
            range = new Range(methodStart, methodEnd);
            return true;
        }

        static bool TryFindAnchorRange(string contents, StructuredEdit edit, Range classRange, out Range range, out ToolResult error)
        {
            range = default;
            error = null;
            var classText = contents.Substring(classRange.Start, classRange.End - classRange.Start);
            MatchCollection matches;

            try
            {
                matches = Regex.Matches(classText, edit.Anchor, RegexOptions.Multiline | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException exception)
            {
                error = ToolResult.Error("invalid_parameter", $"anchor 正则无效：{exception.Message}", new
                {
                    edit.Index,
                    edit.Anchor
                });
                return false;
            }

            if (matches.Count == 0)
            {
                error = ToolResult.Error("not_found", "未找到目标锚点。", new
                {
                    edit.ClassName,
                    edit.Anchor
                });
                return false;
            }

            if (matches.Count > 1)
            {
                error = ToolResult.Error("duplicate_name", "找到多个锚点匹配，无法确定编辑目标。", new
                {
                    edit.ClassName,
                    edit.Anchor,
                    matchCount = matches.Count
                });
                return false;
            }

            var match = matches[0];
            range = new Range(classRange.Start + match.Index, classRange.Start + match.Index + match.Length);
            return true;
        }

        static bool TryFindMatchingBrace(string contents, int openBraceIndex, out int closeBraceIndex)
        {
            closeBraceIndex = -1;
            var depth = 0;
            for (var index = openBraceIndex; index < contents.Length; index++)
            {
                var current = contents[index];
                if (current == '{')
                {
                    depth++;
                }
                else if (current == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBraceIndex = index;
                        return true;
                    }
                }
            }

            return false;
        }

        static int FindClassBodyStart(string contents, Range classRange)
        {
            return contents.IndexOf('{', classRange.Start) + 1;
        }

        static int ExtendTrailingWhitespace(string contents, int index, int maxExclusive)
        {
            var current = index;
            while (current < maxExclusive)
            {
                var character = contents[current];
                if (character != ' ' && character != '\t' && character != '\r' && character != '\n')
                {
                    break;
                }

                current++;
            }

            return current;
        }

        static string BuildMethodInsertion(string contents, int insertionIndex, Range classRange, string replacement)
        {
            var lineStart = FindLineStart(contents, insertionIndex);
            var indent = GetIndentation(contents, lineStart);
            var normalizedReplacement = NormalizeInsertionText(replacement).Trim('\r', '\n');
            var text = normalizedReplacement.Replace("\n", Environment.NewLine + indent);
            var needsLeadingNewLine = insertionIndex > 0 && contents[insertionIndex - 1] != '\n' && contents[insertionIndex - 1] != '\r';
            var prefix = needsLeadingNewLine ? Environment.NewLine : string.Empty;
            var suffix = insertionIndex < classRange.End - 1 ? Environment.NewLine : Environment.NewLine + indent;
            return prefix + indent + text + suffix;
        }

        static int FindLineStart(string contents, int index)
        {
            var current = Math.Max(0, Math.Min(index, contents.Length));
            while (current > 0)
            {
                var character = contents[current - 1];
                if (character == '\n')
                {
                    break;
                }

                current--;
            }

            return current;
        }

        static string GetIndentation(string contents, int lineStart)
        {
            var builder = new System.Text.StringBuilder();
            for (var index = lineStart; index < contents.Length; index++)
            {
                var character = contents[index];
                if (character == ' ' || character == '\t')
                {
                    builder.Append(character);
                    continue;
                }

                break;
            }

            return builder.ToString();
        }

        static string NormalizeInsertionText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
        }

        static string ReplaceRange(string contents, int start, int end, string replacement)
        {
            return contents.Substring(0, start) + replacement + contents.Substring(end);
        }

        static bool IsSupportedInsertPosition(string position)
        {
            return string.Equals(position, "start", StringComparison.Ordinal)
                || string.Equals(position, "end", StringComparison.Ordinal)
                || string.Equals(position, "after", StringComparison.Ordinal)
                || string.Equals(position, "before", StringComparison.Ordinal);
        }

        static string GetFullPath(string assetPath, ToolContext context)
        {
            var projectPath = context?.EditorState?.ProjectPath ?? string.Empty;
            var relativePath = assetPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(projectPath, relativePath);
        }

        readonly struct StructuredEdit
        {
            public StructuredEdit(int index, string op, string className, string methodName, string replacement, string anchor, string text, string position, string afterMethodName, string beforeMethodName)
            {
                Index = index;
                Op = op ?? string.Empty;
                ClassName = className ?? string.Empty;
                MethodName = methodName ?? string.Empty;
                Replacement = replacement ?? string.Empty;
                Anchor = anchor ?? string.Empty;
                Text = text ?? string.Empty;
                Position = string.IsNullOrWhiteSpace(position) ? "end" : position.Trim();
                AfterMethodName = afterMethodName ?? string.Empty;
                BeforeMethodName = beforeMethodName ?? string.Empty;
            }

            public int Index { get; }

            public string Op { get; }

            public string ClassName { get; }

            public string MethodName { get; }

            public string Replacement { get; }

            public string Anchor { get; }

            public string Text { get; }

            public string Position { get; }

            public string AfterMethodName { get; }

            public string BeforeMethodName { get; }
        }

        readonly struct Range
        {
            public Range(int start, int end)
            {
                Start = start;
                End = end;
            }

            public int Start { get; }

            public int End { get; }
        }
    }
}
