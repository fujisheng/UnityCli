using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool(
        "scene",
        Description = "Manage Unity scenes",
        Mode = ToolMode.Both,
        Capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.SceneMutation | ToolCapabilities.WriteAssets,
        Category = "editor")]
    public sealed class SceneTool : IUnityCliTool
    {
        const int DefaultPageSize = 50;
        static readonly string[] SupportedActions =
        {
            "get_active",
            "get_hierarchy",
            "get_build_settings",
            "get_loaded_scenes",
            "create",
            "load",
            "save",
            "close_scene",
            "set_active_scene"
        };

        public string Id => "scene";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Manage Unity scenes",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.SceneMutation | ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "action",
                        type = "string",
                        description = "Scene action name",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "name",
                        type = "string",
                        description = "Scene name for create",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "path",
                        type = "string",
                        description = "Scene asset path or create folder path under Assets/",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "additive",
                        type = "boolean",
                        description = "Whether to load scene additively",
                        required = false,
                        defaultValue = false
                    },
                    new ParamDescriptor
                    {
                        name = "scene_name",
                        type = "string",
                        description = "Loaded scene name selector",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "build_index",
                        type = "integer",
                        description = "Loaded scene build index selector",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "page_size",
                        type = "integer",
                        description = "Hierarchy page size",
                        required = false,
                        defaultValue = DefaultPageSize
                    },
                    new ParamDescriptor
                    {
                        name = "cursor",
                        type = "integer",
                        description = "Hierarchy page cursor offset",
                        required = false,
                        defaultValue = 0
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

            if (!ArgsHelper.TryGetRequired(args, "action", out string action, out var error))
            {
                return error;
            }

            switch (action)
            {
                case "get_active":
                    return HandleGetActive();
                case "get_hierarchy":
                    return HandleGetHierarchy(args);
                case "get_build_settings":
                    return HandleGetBuildSettings();
                case "get_loaded_scenes":
                    return HandleGetLoadedScenes();
                case "create":
                    return HandleCreate(args, context);
                case "load":
                    return HandleLoad(args, context);
                case "save":
                    return HandleSave(context);
                case "close_scene":
                    return HandleCloseScene(args, context);
                case "set_active_scene":
                    return HandleSetActiveScene(args, context);
                default:
                    return ToolResult.Error("invalid_parameter", $"不支持的场景操作 '{action}'。", new
                    {
                        parameter = "action",
                        value = action,
                        supportedActions = SupportedActions
                    });
            }
        }

        ToolResult HandleGetActive()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return ToolResult.Error("not_found", "当前没有激活场景。", new
                {
                    action = "get_active"
                });
            }

            return ToolResult.Ok(new
            {
                action = "get_active",
                scene = CreateSceneSummary(scene)
            });
        }

        ToolResult HandleGetHierarchy(Dictionary<string, object> args)
        {
            if (!ArgsHelper.TryGetOptional(args, "page_size", DefaultPageSize, out int pageSize, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "cursor", 0, out int cursor, out error))
            {
                return error;
            }

            if (pageSize <= 0)
            {
                return ToolResult.Error("invalid_parameter", "参数 'page_size' 必须大于 0。", new
                {
                    parameter = "page_size",
                    value = pageSize
                });
            }

            if (cursor < 0)
            {
                return ToolResult.Error("invalid_parameter", "参数 'cursor' 不能小于 0。", new
                {
                    parameter = "cursor",
                    value = cursor
                });
            }

            if (!TryGetHierarchyScene(args, out var scene, out error))
            {
                return error;
            }

            var nodes = BuildHierarchyNodes(scene);
            var page = nodes.Skip(cursor).Take(pageSize).ToArray();

            return ToolResult.Ok(new
            {
                action = "get_hierarchy",
                scene = CreateSceneSummary(scene),
                totalCount = nodes.Count,
                cursor,
                pageSize,
                returnedCount = page.Length,
                hasMore = cursor + page.Length < nodes.Count,
                nodes = page
            });
        }

        ToolResult HandleGetBuildSettings()
        {
            var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var loadedScene = SceneManager.GetSceneAt(index);
                if (loadedScene.IsValid() && !string.IsNullOrEmpty(loadedScene.path))
                {
                    loadedPaths.Add(loadedScene.path);
                }
            }

            var scenes = (EditorBuildSettings.scenes ?? Array.Empty<EditorBuildSettingsScene>())
                .Select((scene, index) => new
                {
                    name = Path.GetFileNameWithoutExtension(scene.path),
                    path = scene.path,
                    enabled = scene.enabled,
                    buildIndex = index,
                    isLoaded = loadedPaths.Contains(scene.path)
                })
                .ToArray();

            return ToolResult.Ok(new
            {
                action = "get_build_settings",
                count = scenes.Length,
                scenes
            });
        }

        ToolResult HandleGetLoadedScenes()
        {
            var scenes = new List<object>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid())
                {
                    continue;
                }

                scenes.Add(CreateSceneSummary(scene));
            }

            return ToolResult.Ok(new
            {
                action = "get_loaded_scenes",
                count = scenes.Count,
                scenes = scenes.ToArray()
            });
        }

        ToolResult HandleCreate(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "name", out string rawName, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error))
            {
                return error;
            }

            if (!TryNormalizeSceneName(rawName, out var sceneName, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            if (!TryBuildSceneAssetPath(sceneName, normalizedPath, out var sceneAssetPath, out error))
            {
                return error;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(sceneAssetPath) != null)
            {
                return ToolResult.Error("invalid_parameter", "目标场景已存在。", new
                {
                    parameter = "path",
                    path = sceneAssetPath
                });
            }

            if (!TryEnsureParentFolderExists(sceneAssetPath, context, out error))
            {
                return error;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            if (!scene.IsValid())
            {
                return ToolResult.Error("not_allowed", "Unity 未能创建新场景。", new
                {
                    action = "create",
                    path = sceneAssetPath
                });
            }

            if (!EditorSceneManager.SaveScene(scene, sceneAssetPath, false))
            {
                return ToolResult.Error("not_allowed", "Unity 未能保存新场景。", new
                {
                    action = "create",
                    path = sceneAssetPath
                });
            }

            return ToolResult.Ok(new
            {
                action = "create",
                scene = CreateSceneSummary(scene)
            });
        }

        ToolResult HandleLoad(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "additive", false, out bool additive, out error))
            {
                return error;
            }

            if (!TryNormalizeSceneAssetPath(rawPath, out var sceneAssetPath, out error))
            {
                return error;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(sceneAssetPath) == null)
            {
                return ToolResult.Error("not_found", "未找到目标场景资源。", new
                {
                    path = sceneAssetPath
                });
            }

            var openMode = additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
            var scene = EditorSceneManager.OpenScene(sceneAssetPath, openMode);
            if (!scene.IsValid())
            {
                return ToolResult.Error("not_allowed", "Unity 未能加载目标场景。", new
                {
                    action = "load",
                    path = sceneAssetPath,
                    additive
                });
            }

            return ToolResult.Ok(new
            {
                action = "load",
                additive,
                scene = CreateSceneSummary(scene)
            });
        }

        ToolResult HandleSave(ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return ToolResult.Error("not_found", "当前没有激活场景可保存。", new
                {
                    action = "save"
                });
            }

            if (string.IsNullOrWhiteSpace(scene.path))
            {
                return ToolResult.Error("not_allowed", "当前激活场景尚未保存到 Assets/，请先使用 create 指定路径。", new
                {
                    action = "save",
                    scene = scene.name
                });
            }

            if (!EditorSceneManager.SaveScene(scene))
            {
                return ToolResult.Error("not_allowed", "Unity 未能保存当前激活场景。", new
                {
                    action = "save",
                    path = scene.path
                });
            }

            return ToolResult.Ok(new
            {
                action = "save",
                scene = CreateSceneSummary(scene)
            });
        }

        ToolResult HandleCloseScene(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveLoadedScene(args, true, out var scene, out error))
            {
                return error;
            }

            var closedSceneSummary = CreateSceneSummary(scene);
            if (!EditorSceneManager.CloseScene(scene, true))
            {
                return ToolResult.Error("not_allowed", "Unity 未能关闭目标场景。", new
                {
                    action = "close_scene",
                    scene = closedSceneSummary
                });
            }

            var activeScene = EditorSceneManager.GetActiveScene();
            return ToolResult.Ok(new
            {
                action = "close_scene",
                closed = true,
                scene = closedSceneSummary,
                activeScene = activeScene.IsValid() ? CreateSceneSummary(activeScene) : null
            });
        }

        ToolResult HandleSetActiveScene(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!TryResolveLoadedScene(args, true, out var scene, out error))
            {
                return error;
            }

            if (!EditorSceneManager.SetActiveScene(scene))
            {
                return ToolResult.Error("not_allowed", "Unity 未能设置激活场景。", new
                {
                    action = "set_active_scene",
                    scene = CreateSceneSummary(scene)
                });
            }

            return ToolResult.Ok(new
            {
                action = "set_active_scene",
                scene = CreateSceneSummary(scene)
            });
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

        static bool TryGetHierarchyScene(Dictionary<string, object> args, out Scene scene, out ToolResult error)
        {
            error = null;
            if (!TryResolveLoadedScene(args, false, out scene, out error))
            {
                return false;
            }

            if (!scene.IsValid())
            {
                scene = EditorSceneManager.GetActiveScene();
            }

            if (!scene.IsValid())
            {
                error = ToolResult.Error("not_found", "当前没有可读取层级的场景。", new
                {
                    action = "get_hierarchy"
                });
                return false;
            }

            return true;
        }

        static bool TryResolveLoadedScene(Dictionary<string, object> args, bool requireSelector, out Scene scene, out ToolResult error)
        {
            scene = default;
            error = null;

            if (!ArgsHelper.TryGetOptional(args, "scene_name", string.Empty, out string sceneName, out error))
            {
                return false;
            }

            if (!ArgsHelper.TryGetOptional(args, "build_index", int.MinValue, out int buildIndex, out error))
            {
                return false;
            }

            var hasSceneName = !string.IsNullOrWhiteSpace(sceneName);
            var hasBuildIndex = buildIndex != int.MinValue;

            if (!hasSceneName && !hasBuildIndex)
            {
                if (requireSelector)
                {
                    error = ToolResult.Error("missing_parameter", "缺少场景标识参数 'scene_name' 或 'build_index'。", new
                    {
                        parameters = new[] { "scene_name", "build_index" }
                    });
                    return false;
                }

                scene = default;
                return true;
            }

            if (hasBuildIndex)
            {
                for (var index = 0; index < SceneManager.sceneCount; index++)
                {
                    var loadedScene = SceneManager.GetSceneAt(index);
                    if (loadedScene.IsValid() && loadedScene.buildIndex == buildIndex)
                    {
                        scene = loadedScene;
                        return true;
                    }
                }

                error = ToolResult.Error("not_found", "未找到指定 build_index 的已加载场景。", new
                {
                    buildIndex
                });
                return false;
            }

            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var loadedScene = SceneManager.GetSceneAt(index);
                if (loadedScene.IsValid() && string.Equals(loadedScene.name, sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    scene = loadedScene;
                    return true;
                }
            }

            error = ToolResult.Error("not_found", "未找到指定 scene_name 的已加载场景。", new
            {
                sceneName
            });
            return false;
        }

        static List<object> BuildHierarchyNodes(Scene scene)
        {
            var nodes = new List<object>();
            foreach (var root in scene.GetRootGameObjects())
            {
                AppendHierarchyNode(root.transform, 0, null, nodes);
            }

            return nodes;
        }

        static void AppendHierarchyNode(Transform current, int depth, int? parentInstanceId, ICollection<object> nodes)
        {
            nodes.Add(new
            {
                name = current.name,
                instanceId = current.gameObject.GetInstanceID(),
                childCount = current.childCount,
                depth,
                parentInstanceId,
                siblingIndex = current.GetSiblingIndex(),
                activeSelf = current.gameObject.activeSelf,
                activeInHierarchy = current.gameObject.activeInHierarchy
            });

            var currentInstanceId = current.gameObject.GetInstanceID();
            for (var index = 0; index < current.childCount; index++)
            {
                AppendHierarchyNode(current.GetChild(index), depth + 1, currentInstanceId, nodes);
            }
        }

        static bool TryNormalizeSceneName(string rawName, out string sceneName, out ToolResult error)
        {
            sceneName = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(rawName))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'name' 不能为空。", new
                {
                    parameter = "name"
                });
                return false;
            }

            var trimmed = rawName.Trim();
            if (trimmed.Contains("/", StringComparison.Ordinal) || trimmed.Contains("\\", StringComparison.Ordinal))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'name' 不能包含路径分隔符。", new
                {
                    parameter = "name",
                    value = rawName
                });
                return false;
            }

            sceneName = Path.GetFileNameWithoutExtension(trimmed);
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'name' 无法解析为有效场景名。", new
                {
                    parameter = "name",
                    value = rawName
                });
                return false;
            }

            if (sceneName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = ToolResult.Error("invalid_parameter", "参数 'name' 包含非法文件名字符。", new
                {
                    parameter = "name",
                    value = rawName
                });
                sceneName = string.Empty;
                return false;
            }

            return true;
        }

        static bool TryBuildSceneAssetPath(string sceneName, string normalizedPath, out string sceneAssetPath, out ToolResult error)
        {
            sceneAssetPath = string.Empty;
            error = null;

            if (normalizedPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
                if (!string.Equals(fileName, sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    error = ToolResult.Error("invalid_parameter", "当 'path' 指向具体场景文件时，文件名必须与 'name' 一致。", new
                    {
                        parameter = "path",
                        path = normalizedPath,
                        name = sceneName
                    });
                    return false;
                }

                sceneAssetPath = normalizedPath;
                return true;
            }

            sceneAssetPath = string.Equals(normalizedPath, "Assets", StringComparison.OrdinalIgnoreCase)
                ? $"Assets/{sceneName}.unity"
                : $"{normalizedPath}/{sceneName}.unity";
            return true;
        }

        static bool TryNormalizeSceneAssetPath(string rawPath, out string sceneAssetPath, out ToolResult error)
        {
            sceneAssetPath = string.Empty;
            error = null;

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out sceneAssetPath, out error))
            {
                return false;
            }

            if (!sceneAssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'path' 必须指向 .unity 场景资源。", new
                {
                    parameter = "path",
                    value = rawPath,
                    normalizedPath = sceneAssetPath
                });
                sceneAssetPath = string.Empty;
                return false;
            }

            return true;
        }

        static bool TryEnsureParentFolderExists(string assetPath, ToolContext context, out ToolResult error)
        {
            error = null;

            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || string.Equals(directory, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var projectPath = context.EditorState.ProjectPath ?? string.Empty;
            var fullDirectoryPath = Path.Combine(projectPath, directory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(fullDirectoryPath);
            AssetDatabase.Refresh();
            return true;
        }

        static object CreateSceneSummary(Scene scene)
        {
            return new
            {
                name = scene.name,
                path = scene.path,
                isDirty = scene.isDirty,
                buildIndex = scene.buildIndex,
                isLoaded = scene.isLoaded,
                rootCount = scene.rootCount
            };
        }
    }
}
