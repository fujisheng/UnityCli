using System;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Core
{
    [InitializeOnLoad]
    public static class UnityCliBootstrap
    {
        const double RetryIntervalSeconds = 2d;
        const double EndpointCheckIntervalSeconds = 5d;

        static bool startScheduled = true;
        static double nextStartAttemptTime;
        static double nextEndpointCheckTime;

        static UnityCliBootstrap()
        {
            EditorApplication.update -= HandleEditorUpdate;
            EditorApplication.update += HandleEditorUpdate;
            EditorApplication.quitting -= HandleEditorQuitting;
            EditorApplication.quitting += HandleEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
        }

        static void HandleEditorUpdate()
        {
            if (!startScheduled)
            {
                // 检测服务器崩溃后自动恢复
                if (!UnityCliServer.IsRunning && IsEditorSafeToStart())
                {
                    startScheduled = true;
                }

                if (EditorApplication.timeSinceStartup >= nextEndpointCheckTime)
                {
                    nextEndpointCheckTime = EditorApplication.timeSinceStartup + EndpointCheckIntervalSeconds;
                    UnityCliServer.EnsureEndpointFileWritten();
                }

                if (!startScheduled)
                {
                    return;
                }
            }

            if (EditorApplication.timeSinceStartup < nextStartAttemptTime)
            {
                return;
            }

            if (!IsEditorSafeToStart())
            {
                return;
            }

            try
            {
                UnityCliServer.EnsureRunning();
                if (UnityCliServer.IsRunning)
                {
                    startScheduled = false;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                nextStartAttemptTime = EditorApplication.timeSinceStartup + RetryIntervalSeconds;
            }
        }

        static bool IsEditorSafeToStart()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return false;
            }

            if (EditorApplication.isPlaying)
            {
                return true;
            }

            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        static void HandleBeforeAssemblyReload()
        {
            // domain reload 时跳过 Dispose，因为线程池上可能还有 pending 的 IO 回调，
            // Dispose 后回调触发会导致 ObjectDisposedException 崩溃。
            // AppDomain 销毁时 GC 会自动清理。
            UnityCliServer.Stop(clearSessionState: false, disposeCancellation: false);
        }

        static void HandleEditorQuitting()
        {
            startScheduled = false;
            UnityCliServer.Stop(clearSessionState: true);
        }
    }
}
