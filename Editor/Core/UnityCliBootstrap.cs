using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;

namespace UnityCli.Editor.Core
{
    [InitializeOnLoad]
    public static class UnityCliBootstrap
    {
        sealed class UnityCliPlayerLoopDrainMarker
        {
        }

        const int MaxDispatchPumpBatchSize = 8;
        const double RetryIntervalSeconds = 2d;
        const double EndpointCheckIntervalSeconds = 5d;

        static readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        static SynchronizationContext mainThreadContext;
        static int mainThreadThreadId;
        static bool isPlayerLoopDrainInstalled;
        static bool startScheduled = true;
        static int wakeDrainScheduled;
        static double nextStartAttemptTime;
        static double nextEndpointCheckTime;

        static UnityCliBootstrap()
        {
        }

        [InitializeOnLoadMethod]
        static void InitializeOnLoadMainThread()
        {
            RecoverEditorCallbacksAfterReload();
        }

        [InitializeOnEnterPlayMode]
        static void HandleEnterPlayMode(EnterPlayModeOptions options)
        {
            RecoverEditorCallbacksAfterReload();
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        static void HandleScriptsReloaded()
        {
            RecoverEditorCallbacksAfterReload();
        }

        static void HandleEditorUpdate()
        {
            CaptureMainThreadContext();
            DrainPostedMainThreadActions();
            if (EditorApplication.isPlaying)
            {
                InstallPlayModePlayerLoopDrain();
            }

            UnityCliDispatcherQueue.EnsureInitializedOnMainThread();
            DrainQueuedRequestsOnMainThread();

            if (!startScheduled)
            {
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
            RemovePlayModePlayerLoopDrain();
            UnityCliDispatcherQueue.FailAllPending("bridge_reloaded", "UnityCli bridge 已重新加载，旧请求已失效。");

            // domain reload 时跳过 Dispose，因为线程池上可能还有 pending 的 IO 回调，
            // Dispose 后回调触发会导致 ObjectDisposedException 崩溃。
            // AppDomain 销毁时 GC 会自动清理。
            UnityCliServer.Stop(clearSessionState: false, disposeCancellation: false);
        }

        static void HandleEditorQuitting()
        {
            startScheduled = false;
            RemovePlayModePlayerLoopDrain();
            UnityCliDispatcherQueue.FailAllPending("bridge_shutdown", "UnityCli bridge 正在关闭，当前请求已取消。");
            UnityCliServer.Stop(clearSessionState: true);
        }

        static void HandlePlayModeStateChanged(PlayModeStateChange stateChange)
        {
            EnsureEditorCallbacksRegistered();
            CaptureMainThreadContext();

            switch (stateChange)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    InstallPlayModePlayerLoopDrain();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    RemovePlayModePlayerLoopDrain();
                    break;
            }
        }

        internal static void NotifyInvokeRequestQueued()
        {
            if (Interlocked.CompareExchange(ref wakeDrainScheduled, 1, 0) == 1)
            {
                return;
            }

            var context = mainThreadContext;
            if (context == null)
            {
                Interlocked.Exchange(ref wakeDrainScheduled, 0);
                return;
            }

            try
            {
                context.Post(_ => HandleQueuedInvokeWakeOnMainThread(), null);
            }
            catch
            {
                Interlocked.Exchange(ref wakeDrainScheduled, 0);
            }
        }

        internal static bool PostToMainThread(Action action)
        {
            if (action == null)
            {
                return false;
            }

            if (SynchronizationContext.Current != null
                && SynchronizationContext.Current == mainThreadContext
                && Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref mainThreadThreadId))
            {
                action();
                return true;
            }

            mainThreadActions.Enqueue(action);

            return true;
        }

        static void EnsureEditorCallbacksRegistered()
        {
            EditorApplication.update -= HandleEditorUpdate;
            EditorApplication.update += HandleEditorUpdate;
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            EditorApplication.quitting -= HandleEditorQuitting;
            EditorApplication.quitting += HandleEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
        }

        static void RecoverEditorCallbacksAfterReload()
        {
            Interlocked.Exchange(ref wakeDrainScheduled, 0);
            CaptureMainThreadContext();
            EnsureEditorCallbacksRegistered();

            if (EditorApplication.isPlaying)
            {
                InstallPlayModePlayerLoopDrain();
            }

            EditorApplication.QueuePlayerLoopUpdate();
        }

        static void CaptureMainThreadContext()
        {
            mainThreadContext = SynchronizationContext.Current;
            mainThreadThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        static void DrainPostedMainThreadActions()
        {
            while (mainThreadActions.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        static void HandleQueuedInvokeWakeOnMainThread()
        {
            Interlocked.Exchange(ref wakeDrainScheduled, 0);
            CaptureMainThreadContext();
            DrainQueuedRequestsOnMainThread();
        }

        static void DrainQueuedRequestsFromPlayerLoop()
        {
            if (UnityCliDispatcherQueue.PendingCount <= 0)
            {
                return;
            }

            CaptureMainThreadContext();
            DrainQueuedRequestsOnMainThread();
        }

        static void DrainQueuedRequestsOnMainThread()
        {
            UnityCliDispatcher.PumpQueuedRequestsForEditorUpdate(MaxDispatchPumpBatchSize);

            if (UnityCliDispatcherQueue.PendingCount > 0)
            {
                if (EditorApplication.isPlaying)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                }

                NotifyInvokeRequestQueued();
            }
        }

        static void InstallPlayModePlayerLoopDrain()
        {
            var playerLoop = PlayerLoop.GetCurrentPlayerLoop();
            var markerType = typeof(UnityCliPlayerLoopDrainMarker);

            RemovePlayerLoopSystem(ref playerLoop, markerType);

            var drainSystem = new PlayerLoopSystem
            {
                type = markerType,
                updateDelegate = DrainQueuedRequestsFromPlayerLoop
            };

            if (!InsertPlayerLoopSystem(ref playerLoop, typeof(UnityEngine.PlayerLoop.Update), drainSystem))
            {
                AppendTopLevelPlayerLoopSystem(ref playerLoop, drainSystem);
            }

            PlayerLoop.SetPlayerLoop(playerLoop);
            isPlayerLoopDrainInstalled = true;
        }

        static void RemovePlayModePlayerLoopDrain()
        {
            if (!isPlayerLoopDrainInstalled)
            {
                return;
            }

            var playerLoop = PlayerLoop.GetCurrentPlayerLoop();
            if (RemovePlayerLoopSystem(ref playerLoop, typeof(UnityCliPlayerLoopDrainMarker)))
            {
                PlayerLoop.SetPlayerLoop(playerLoop);
            }

            isPlayerLoopDrainInstalled = false;
        }

        static bool InsertPlayerLoopSystem(ref PlayerLoopSystem playerLoop, Type targetType, PlayerLoopSystem system)
        {
            var subSystems = playerLoop.subSystemList;
            if (subSystems == null || subSystems.Length <= 0)
            {
                return false;
            }

            for (var i = 0; i < subSystems.Length; i++)
            {
                if (subSystems[i].type == targetType)
                {
                    var targetSubSystems = subSystems[i].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                    var updatedSubSystems = new PlayerLoopSystem[targetSubSystems.Length + 1];
                    Array.Copy(targetSubSystems, updatedSubSystems, targetSubSystems.Length);
                    updatedSubSystems[targetSubSystems.Length] = system;
                    subSystems[i].subSystemList = updatedSubSystems;
                    playerLoop.subSystemList = subSystems;
                    return true;
                }

                var child = subSystems[i];
                if (InsertPlayerLoopSystem(ref child, targetType, system))
                {
                    subSystems[i] = child;
                    playerLoop.subSystemList = subSystems;
                    return true;
                }
            }

            return false;
        }

        static void AppendTopLevelPlayerLoopSystem(ref PlayerLoopSystem playerLoop, PlayerLoopSystem system)
        {
            var subSystems = playerLoop.subSystemList ?? Array.Empty<PlayerLoopSystem>();
            var updatedSubSystems = new PlayerLoopSystem[subSystems.Length + 1];
            Array.Copy(subSystems, updatedSubSystems, subSystems.Length);
            updatedSubSystems[subSystems.Length] = system;
            playerLoop.subSystemList = updatedSubSystems;
        }

        static bool RemovePlayerLoopSystem(ref PlayerLoopSystem playerLoop, Type markerType)
        {
            var subSystems = playerLoop.subSystemList;
            if (subSystems == null || subSystems.Length <= 0)
            {
                return false;
            }

            var removed = false;
            var keptCount = 0;
            var updatedSubSystems = new PlayerLoopSystem[subSystems.Length];

            for (var i = 0; i < subSystems.Length; i++)
            {
                var child = subSystems[i];
                if (child.type == markerType)
                {
                    removed = true;
                    continue;
                }

                if (RemovePlayerLoopSystem(ref child, markerType))
                {
                    removed = true;
                }

                updatedSubSystems[keptCount++] = child;
            }

            if (!removed)
            {
                return false;
            }

            if (keptCount == 0)
            {
                playerLoop.subSystemList = Array.Empty<PlayerLoopSystem>();
                return true;
            }

            if (keptCount != updatedSubSystems.Length)
            {
                var resized = new PlayerLoopSystem[keptCount];
                Array.Copy(updatedSubSystems, resized, keptCount);
                updatedSubSystems = resized;
            }

            playerLoop.subSystemList = updatedSubSystems;
            return true;
        }
    }
}
