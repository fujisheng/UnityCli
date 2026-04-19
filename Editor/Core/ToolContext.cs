using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Core
{
    public sealed class ToolContext
    {
        readonly PendingJobRegistration pendingJobRegistration;

        public ToolContext(bool isPlaying, bool isCompiling, EditorStateSnapshot editorState)
            : this(isPlaying, isCompiling, editorState, null, null)
        {
        }

        ToolContext(bool isPlaying, bool isCompiling, EditorStateSnapshot editorState, UnityCliJob currentJob, PendingJobRegistration pendingJobRegistration)
        {
            IsPlaying = isPlaying;
            IsCompiling = isCompiling;
            EditorState = editorState ?? throw new ArgumentNullException(nameof(editorState));
            CurrentJob = currentJob;
            this.pendingJobRegistration = pendingJobRegistration;
        }

        public bool IsPlaying { get; }

        public bool IsCompiling { get; }

        public EditorStateSnapshot EditorState { get; }

        public UnityCliJob CurrentJob { get; }

        public string CurrentJobId => CurrentJob?.JobId ?? pendingJobRegistration?.JobId ?? string.Empty;

        public static ToolContext CreateCurrent()
        {
            var editorState = EditorStateSnapshot.Capture();
            return Create(editorState);
        }

        internal static ToolContext Create(EditorStateSnapshot editorState, UnityCliJob currentJob = null, PendingJobRegistration pendingJobRegistration = null)
        {
            if (editorState == null)
            {
                throw new ArgumentNullException(nameof(editorState));
            }

            return new ToolContext(editorState.IsPlaying, editorState.IsCompiling, editorState, currentJob, pendingJobRegistration);
        }

        public string CreateJob(TimeSpan? plannedDuration = null, object state = null)
        {
            if (pendingJobRegistration == null)
            {
                throw new InvalidOperationException("当前上下文不支持创建异步 Job。");
            }

            return pendingJobRegistration.Configure(plannedDuration, state);
        }

        internal PendingJobRegistration GetPendingJobRegistration()
        {
            return pendingJobRegistration;
        }

        internal sealed class PendingJobRegistration
        {
            public PendingJobRegistration(string jobId)
            {
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    throw new ArgumentException("JobId 不能为空。", nameof(jobId));
                }

                JobId = jobId;
            }

            public string JobId { get; }

            public TimeSpan? PlannedDuration { get; private set; }

            public object State { get; private set; }

            public string Configure(TimeSpan? plannedDuration, object state)
            {
                PlannedDuration = plannedDuration;
                State = state;
                return JobId;
            }
        }

        public sealed class EditorStateSnapshot
        {
            public EditorStateSnapshot(
                bool isPlaying,
                bool isCompiling,
                bool isUpdating,
                bool isPlayingOrWillChangePlaymode,
                bool isBatchMode,
                string unityVersion,
                string projectPath)
            {
                IsPlaying = isPlaying;
                IsCompiling = isCompiling;
                IsUpdating = isUpdating;
                IsPlayingOrWillChangePlaymode = isPlayingOrWillChangePlaymode;
                IsBatchMode = isBatchMode;
                UnityVersion = unityVersion ?? string.Empty;
                ProjectPath = projectPath ?? string.Empty;
            }

            public bool IsPlaying { get; }

            public bool IsCompiling { get; }

            public bool IsUpdating { get; }

            public bool IsPlayingOrWillChangePlaymode { get; }

            public bool IsBatchMode { get; }

            public string UnityVersion { get; }

            public string ProjectPath { get; }

            public static EditorStateSnapshot Capture()
            {
                var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
                return new EditorStateSnapshot(
                    EditorApplication.isPlaying,
                    EditorApplication.isCompiling,
                    EditorApplication.isUpdating,
                    EditorApplication.isPlayingOrWillChangePlaymode,
                    Application.isBatchMode,
                    Application.unityVersion,
                    projectPath);
            }
        }
    }
}
