using System;

namespace UnityCli.Editor.Core
{
    internal static class StateGuard
    {
        public static bool EnsureReady(ToolContext context, out ToolResult error)
        {
            error = null;
            if (context == null)
            {
                error = ToolResult.Error("invalid_parameter", "工具上下文不能为空。", nameof(context));
                return false;
            }

            if (context.EditorState.IsCompiling || context.EditorState.IsUpdating)
            {
                error = ToolResult.Error("not_allowed", "Unity Editor 正在编译或刷新，当前不可执行。", new
                {
                    context.EditorState.IsCompiling,
                    context.EditorState.IsUpdating
                });
                return false;
            }

            return true;
        }

        public static bool EnsureNotCompiling(ToolContext context, out ToolResult error)
        {
            error = null;
            if (context == null)
            {
                error = ToolResult.Error("invalid_parameter", "工具上下文不能为空。", nameof(context));
                return false;
            }

            if (context.EditorState.IsCompiling)
            {
                error = ToolResult.Error("not_allowed", "Unity Editor 正在编译，当前操作不允许。", new
                {
                    context.EditorState.IsCompiling
                });
                return false;
            }

            return true;
        }

        public static bool EnsurePlayMode(ToolContext context, bool requiredPlaying, out ToolResult error)
        {
            error = null;
            if (context == null)
            {
                error = ToolResult.Error("invalid_parameter", "工具上下文不能为空。", nameof(context));
                return false;
            }

            if (context.IsPlaying != requiredPlaying)
            {
                var expected = requiredPlaying ? "Play" : "Edit";
                var current = context.IsPlaying ? "Play" : "Edit";
                error = ToolResult.Error("not_allowed", $"当前运行模式不允许。期望: {expected}，当前: {current}。", new
                {
                    expected,
                    current
                });
                return false;
            }

            return true;
        }

        public static bool EnsureNotPlaying(ToolContext context, out ToolResult error)
        {
            return EnsurePlayMode(context, false, out error);
        }
    }
}
