using System;
using System.Collections.Generic;
using UnityCli.Protocol;

namespace UnityCli.Editor.Core
{
    /// <summary>
    /// 支持强类型参数绑定的 UnityCli 工具基类。
    /// </summary>
    public abstract class ParameterizedUnityCliTool<TParameters> : IUnityCliTool
        where TParameters : new()
    {
        public abstract string Id { get; }

        public virtual ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor();
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            if (!ValidateContext(context, out var error))
            {
                return error;
            }

            if (!UnityCliParameterBinder.TryBindParameters(args, out TParameters parameters, out error))
            {
                return error;
            }

            try
            {
                return UnityCliParameterBinder.ToToolResult(ExecuteCommand(parameters, context, args), DefaultErrorCode, DefaultSuccessMessage);
            }
            catch (Exception exception)
            {
                return ToolResult.Error("tool_execution_failed", $"工具 '{Id}' 执行失败。", exception.ToString());
            }
        }

        protected virtual string DefaultErrorCode => "tool_execution_failed";

        protected virtual string DefaultSuccessMessage => null;

        protected virtual bool ValidateContext(ToolContext context, out ToolResult error)
        {
            return UnityCliParameterBinder.EnsureReady(context, out error);
        }

        protected abstract object ExecuteCommand(TParameters parameters, ToolContext context, Dictionary<string, object> args);
    }

    /// <summary>
    /// 要求必须在 PlayMode 下运行的 UnityCli 工具基类。
    /// </summary>
    public abstract class PlayModeUnityCliTool<TParameters> : ParameterizedUnityCliTool<TParameters>
        where TParameters : new()
    {
        protected override bool ValidateContext(ToolContext context, out ToolResult error)
        {
            if (!UnityCliParameterBinder.EnsureReady(context, out error))
            {
                return false;
            }

            return UnityCliParameterBinder.EnsurePlayMode(context, true, out error);
        }
    }

    /// <summary>
    /// 要求必须在 EditMode 下运行的 UnityCli 工具基类。
    /// </summary>
    public abstract class EditModeUnityCliTool<TParameters> : ParameterizedUnityCliTool<TParameters>
        where TParameters : new()
    {
        protected override bool ValidateContext(ToolContext context, out ToolResult error)
        {
            if (!UnityCliParameterBinder.EnsureReady(context, out error))
            {
                return false;
            }

            return UnityCliParameterBinder.EnsureNotPlaying(context, out error);
        }
    }
}
