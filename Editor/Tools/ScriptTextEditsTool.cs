using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("script.text_edits", Description = "Apply exact range-based text edits to a C# script under Assets/", Mode = ToolMode.Both, Capabilities = ToolCapabilities.WriteAssets, Category = "editor")]
    public sealed class ScriptTextEditsTool : IUnityCliTool
    {
        public string Id => "script.text_edits";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Apply exact range-based text edits to a C# script under Assets/",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "uri",
                        type = "string",
                        description = "Script asset path under Assets/ ending with .cs",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "edits",
                        type = "array",
                        description = "Text edit array with startLine/startCol/endLine/endCol/newText",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "precondition_sha256",
                        type = "string",
                        description = "Optional SHA256 of the current file content before applying edits",
                        required = false
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

            if (!ArgsHelper.TryGetRequired(args, "uri", out string rawPath, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetArray(args, "edits", out var rawEdits, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "precondition_sha256", string.Empty, out string preconditionSha256, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeScriptPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            var fullPath = GetFullPath(normalizedPath, context);
            if (!File.Exists(fullPath))
            {
                return ToolResult.Error("not_found", "脚本文件不存在。", new
                {
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
                var currentSha256 = ComputeSha256(contents);

                if (!string.IsNullOrWhiteSpace(preconditionSha256)
                    && !string.Equals(currentSha256, preconditionSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Error("precondition_failed", "文件内容哈希不匹配，已拒绝覆盖。", new
                    {
                        path = normalizedPath,
                        expected = preconditionSha256.Trim(),
                        actual = currentSha256
                    });
                }

                if (!TryResolveRanges(contents, edits, out var resolvedEdits, out error))
                {
                    return error;
                }

                var updatedContents = ApplyEdits(contents, resolvedEdits);
                File.WriteAllText(fullPath, updatedContents);
                AssetDatabase.ImportAsset(normalizedPath, ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(normalizedPath);

                return ToolResult.Ok(new
                {
                    action = "text_edits",
                    path = normalizedPath,
                    fullPath,
                    editCount = resolvedEdits.Count,
                    imported = importer != null,
                    sha256_before = currentSha256,
                    sha256_after = ComputeSha256(updatedContents)
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"编辑脚本失败：{exception.Message}", new
                {
                    action = "text_edits",
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

        static bool TryParseEdits(object[] rawEdits, out List<TextEdit> edits, out ToolResult error)
        {
            edits = new List<TextEdit>();
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

                if (!ArgsHelper.TryGetRequired(editArgs, "startLine", out int startLine, out error)
                    || !ArgsHelper.TryGetRequired(editArgs, "startCol", out int startCol, out error)
                    || !ArgsHelper.TryGetRequired(editArgs, "endLine", out int endLine, out error)
                    || !ArgsHelper.TryGetRequired(editArgs, "endCol", out int endCol, out error)
                    || !ArgsHelper.TryGetRequired(editArgs, "newText", out string newText, out error))
                {
                    return false;
                }

                if (startLine < 1 || startCol < 1 || endLine < 1 || endCol < 1)
                {
                    error = ToolResult.Error("invalid_parameter", "文本编辑坐标必须从 1 开始。", new
                    {
                        parameter = "edits",
                        index,
                        startLine,
                        startCol,
                        endLine,
                        endCol
                    });
                    return false;
                }

                edits.Add(new TextEdit(index, startLine, startCol, endLine, endCol, newText ?? string.Empty));
            }

            return true;
        }

        static bool TryResolveRanges(string contents, List<TextEdit> edits, out List<ResolvedTextEdit> resolvedEdits, out ToolResult error)
        {
            resolvedEdits = new List<ResolvedTextEdit>();
            error = null;
            var lineStarts = BuildLineStarts(contents);

            foreach (var edit in edits)
            {
                if (!TryGetAbsoluteIndex(contents, lineStarts, edit.StartLine, edit.StartCol, out var startIndex, out error)
                    || !TryGetAbsoluteIndex(contents, lineStarts, edit.EndLine, edit.EndCol, out var endIndex, out error))
                {
                    return false;
                }

                if (startIndex > endIndex)
                {
                    error = ToolResult.Error("invalid_parameter", "文本编辑范围无效：起始位置晚于结束位置。", new
                    {
                        index = edit.Index,
                        edit.StartLine,
                        edit.StartCol,
                        edit.EndLine,
                        edit.EndCol
                    });
                    return false;
                }

                resolvedEdits.Add(new ResolvedTextEdit(edit.Index, startIndex, endIndex, edit.NewText));
            }

            var ordered = resolvedEdits.OrderBy(item => item.StartIndex).ThenBy(item => item.EndIndex).ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                if (ordered[index - 1].EndIndex > ordered[index].StartIndex)
                {
                    error = ToolResult.Error("invalid_parameter", "文本编辑范围重叠，已拒绝执行。", new
                    {
                        firstIndex = ordered[index - 1].Index,
                        secondIndex = ordered[index].Index
                    });
                    return false;
                }
            }

            resolvedEdits = ordered;
            return true;
        }

        static bool TryGetAbsoluteIndex(string contents, List<int> lineStarts, int line, int column, out int absoluteIndex, out ToolResult error)
        {
            absoluteIndex = -1;
            error = null;

            if (line < 1 || line > lineStarts.Count)
            {
                error = ToolResult.Error("invalid_parameter", "文本编辑行号超出范围。", new
                {
                    line,
                    lineCount = lineStarts.Count
                });
                return false;
            }

            var lineStart = lineStarts[line - 1];
            var lineEnd = GetLineContentEnd(contents, lineStarts, line);
            var maxColumn = (lineEnd - lineStart) + 1;
            if (column < 1 || column > maxColumn)
            {
                error = ToolResult.Error("invalid_parameter", "文本编辑列号超出范围。", new
                {
                    line,
                    column,
                    maxColumn
                });
                return false;
            }

            absoluteIndex = lineStart + (column - 1);
            return true;
        }

        static List<int> BuildLineStarts(string contents)
        {
            var starts = new List<int> { 0 };
            for (var index = 0; index < contents.Length; index++)
            {
                if (contents[index] == '\n')
                {
                    starts.Add(index + 1);
                }
            }

            return starts;
        }

        static int GetLineContentEnd(string contents, List<int> lineStarts, int line)
        {
            var lineStart = lineStarts[line - 1];
            var nextLineStart = line < lineStarts.Count ? lineStarts[line] : contents.Length;
            var lineEnd = nextLineStart;
            if (lineEnd > lineStart && contents[lineEnd - 1] == '\n')
            {
                lineEnd--;
            }

            if (lineEnd > lineStart && contents[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            return lineEnd;
        }

        static string ApplyEdits(string contents, List<ResolvedTextEdit> edits)
        {
            var builder = new StringBuilder(contents);
            for (var index = edits.Count - 1; index >= 0; index--)
            {
                var edit = edits[index];
                builder.Remove(edit.StartIndex, edit.EndIndex - edit.StartIndex);
                builder.Insert(edit.StartIndex, edit.NewText);
            }

            return builder.ToString();
        }

        static string ComputeSha256(string contents)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(contents ?? string.Empty);
                var hash = sha256.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        static string GetFullPath(string assetPath, ToolContext context)
        {
            var projectPath = context?.EditorState?.ProjectPath ?? string.Empty;
            var relativePath = assetPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(projectPath, relativePath);
        }

        readonly struct TextEdit
        {
            public TextEdit(int index, int startLine, int startCol, int endLine, int endCol, string newText)
            {
                Index = index;
                StartLine = startLine;
                StartCol = startCol;
                EndLine = endLine;
                EndCol = endCol;
                NewText = newText ?? string.Empty;
            }

            public int Index { get; }

            public int StartLine { get; }

            public int StartCol { get; }

            public int EndLine { get; }

            public int EndCol { get; }

            public string NewText { get; }
        }

        readonly struct ResolvedTextEdit
        {
            public ResolvedTextEdit(int index, int startIndex, int endIndex, string newText)
            {
                Index = index;
                StartIndex = startIndex;
                EndIndex = endIndex;
                NewText = newText ?? string.Empty;
            }

            public int Index { get; }

            public int StartIndex { get; }

            public int EndIndex { get; }

            public string NewText { get; }
        }
    }
}
