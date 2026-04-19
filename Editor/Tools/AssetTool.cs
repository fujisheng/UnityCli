using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("asset", Description = "Search, create, delete, move, and rename Unity assets", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets, Category = "editor")]
    public sealed class AssetTool : IUnityCliTool
    {
        const int DefaultPageSize = 25;

        static readonly string[] SupportedActions =
        {
            "search",
            "get_info",
            "create_folder",
            "create",
            "delete",
            "move",
            "rename"
        };

        public string Id => "asset";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Search, create, delete, move, and rename Unity assets",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "action",
                        type = "string",
                        description = "asset action: search/get_info/create_folder/create/delete/move/rename",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "path",
                        type = "string",
                        description = "Asset path or folder scope under Assets/",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "search_pattern",
                        type = "string",
                        description = "Optional wildcard or substring pattern for search",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "filter_type",
                        type = "string",
                        description = "Optional Unity asset type filter, such as Material, MonoScript, SceneAsset or Folder",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "page_size",
                        type = "integer",
                        description = "Maximum number of search results to return",
                        required = false,
                        defaultValue = DefaultPageSize
                    },
                    new ParamDescriptor
                    {
                        name = "asset_type",
                        type = "string",
                        description = "Asset type for create action, currently only Material is supported",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "destination",
                        type = "string",
                        description = "Destination path for move action, or new asset name for rename action",
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

            if (!ArgsHelper.TryGetRequired(args, "action", out string action, out var error))
            {
                return error;
            }

            switch (action)
            {
                case "search":
                    return HandleSearch(args);
                case "get_info":
                    return HandleGetInfo(args);
                case "create_folder":
                    return HandleCreateFolder(args, context);
                case "create":
                    return HandleCreate(args, context);
                case "delete":
                    return HandleDelete(args, context);
                case "move":
                    return HandleMove(args, context);
                case "rename":
                    return HandleRename(args, context);
                default:
                    return ToolResult.Error("invalid_parameter", $"不支持的 asset 操作 '{action}'。", new
                    {
                        parameter = "action",
                        value = action,
                        supportedActions = SupportedActions
                    });
            }
        }

        static ToolResult HandleSearch(Dictionary<string, object> args)
        {
            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "search_pattern", string.Empty, out string searchPattern, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "filter_type", string.Empty, out string filterType, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetOptional(args, "page_size", DefaultPageSize, out int pageSize, out error))
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

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            if (!TryEnsureFolderScopeExists(normalizedPath, out error))
            {
                return error;
            }

            var matcher = CreateSearchPatternMatcher(searchPattern);
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searchFilter = BuildAssetDatabaseSearchFilter(filterType);
            foreach (var guid in AssetDatabase.FindAssets(searchFilter, new[] { normalizedPath }))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrWhiteSpace(assetPath))
                {
                    candidates.Add(assetPath);
                }
            }

            AppendSubFolders(normalizedPath, candidates);

            var filteredPaths = candidates
                .Where(matcher)
                .Where(path => MatchesFilterType(path, filterType))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var assets = filteredPaths
                .Take(pageSize)
                .Select(CreateAssetSummary)
                .Cast<object>()
                .ToArray();

            return ToolResult.Ok(new
            {
                action = "search",
                path = normalizedPath,
                searchPattern = NormalizeOptionalValue(searchPattern),
                filterType = NormalizeOptionalValue(filterType),
                pageSize,
                totalCount = filteredPaths.Length,
                returnedCount = assets.Length,
                assets
            });
        }

        static ToolResult HandleGetInfo(Dictionary<string, object> args)
        {
            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out var error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            if (!TryEnsureAssetExists(normalizedPath, out error))
            {
                return error;
            }

            return ToolResult.Ok(new
            {
                action = "get_info",
                asset = CreateAssetSummary(normalizedPath)
            });
        }

        static ToolResult HandleCreateFolder(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            if (!TryCreateFolderPath(normalizedPath, out error))
            {
                return error;
            }

            return ToolResult.Ok(new
            {
                action = "create_folder",
                created = true,
                folder = CreateAssetSummary(normalizedPath)
            });
        }

        static readonly string[] SupportedCreateAssetTypes =
        {
            "Material"
        };

        static ToolResult HandleCreate(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "asset_type", out string assetType, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            if (!TryNormalizeCreateAssetType(assetType, out var normalizedAssetType, out error))
            {
                return error;
            }

            if (TryAssetExists(normalizedPath))
            {
                return ToolResult.Error("invalid_parameter", "目标资源已存在。", new
                {
                    parameter = "path",
                    path = normalizedPath
                });
            }

            if (!TryEnsureParentFolderExists(normalizedPath, out error))
            {
                return error;
            }

            if (!normalizedPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Error("invalid_parameter", "Material 资源路径必须以 .mat 结尾。", new
                {
                    parameter = "path",
                    path = normalizedPath,
                    asset_type = normalizedAssetType
                });
            }

            var shader = Shader.Find("Standard");
            if (shader == null)
            {
                return ToolResult.Error("tool_execution_failed", "Unity 未能找到创建 Material 所需的 Standard Shader。", new
                {
                    asset_type = normalizedAssetType,
                    path = normalizedPath
                });
            }

            var material = new Material(shader);
            try
            {
                AssetDatabase.CreateAsset(material, normalizedPath);
            }
            catch (Exception exception)
            {
                UnityEngine.Object.DestroyImmediate(material);
                return ToolResult.Error("tool_execution_failed", $"创建资源失败: {exception.Message}", new
                {
                    asset_type = normalizedAssetType,
                    path = normalizedPath
                });
            }

            AssetDatabase.SaveAssets();

            return ToolResult.Ok(new
            {
                action = "create",
                created = true,
                assetType = normalizedAssetType,
                asset = CreateAssetSummary(normalizedPath)
            });
        }

        static ToolResult HandleDelete(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            if (!TryEnsureAssetExists(normalizedPath, out error))
            {
                return error;
            }

            var deletedAsset = CreateAssetSummary(normalizedPath);
            if (!AssetDatabase.DeleteAsset(normalizedPath))
            {
                return ToolResult.Error("tool_execution_failed", "删除资源失败，Unity 未能完成删除操作。", new
                {
                    path = normalizedPath
                });
            }

            AssetDatabase.SaveAssets();

            return ToolResult.Ok(new
            {
                action = "delete",
                deleted = true,
                asset = deletedAsset
            });
        }

        static ToolResult HandleMove(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawSourcePath, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "destination", out string rawDestPath, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawSourcePath, out var sourcePath, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawDestPath, out var destPath, out error))
            {
                return error;
            }

            if (!TryEnsureAssetExists(sourcePath, out error))
            {
                return error;
            }

            if (TryAssetExists(destPath))
            {
                return ToolResult.Error("invalid_parameter", "目标路径已存在。", new
                {
                    parameter = "destination",
                    path = destPath
                });
            }

            if (!TryEnsureParentFolderExists(destPath, out error))
            {
                return error;
            }

            var result = AssetDatabase.MoveAsset(sourcePath, destPath);
            if (!string.IsNullOrEmpty(result))
            {
                return ToolResult.Error("tool_execution_failed", $"移动资源失败: {result}", new
                {
                    source = sourcePath,
                    destination = destPath,
                    unityError = result
                });
            }

            AssetDatabase.SaveAssets();

            return ToolResult.Ok(new
            {
                action = "move",
                moved = true,
                source = sourcePath,
                destination = destPath,
                asset = CreateAssetSummary(destPath)
            });
        }

        static ToolResult HandleRename(Dictionary<string, object> args, ToolContext context)
        {
            if (!EnsureWritable(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "path", out string rawPath, out error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "destination", out string newName, out error))
            {
                return error;
            }

            if (!PathGuard.TryNormalizeAssetPath(rawPath, out var normalizedPath, out error))
            {
                return error;
            }

            if (!TryEnsureAssetExists(normalizedPath, out error))
            {
                return error;
            }

            if (!TryNormalizeAssetName(newName, "destination", out var normalizedNewName, out error))
            {
                return error;
            }

            var parentFolder = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(parentFolder))
            {
                parentFolder = "Assets";
            }

            var extension = Path.GetExtension(normalizedPath);
            var destPath = $"{parentFolder}/{normalizedNewName}{extension}";

            if (TryAssetExists(destPath))
            {
                return ToolResult.Error("invalid_parameter", "同名资源已存在。", new
                {
                    parameter = "destination",
                    path = destPath
                });
            }

            var result = AssetDatabase.RenameAsset(normalizedPath, normalizedNewName);
            if (!string.IsNullOrEmpty(result))
            {
                return ToolResult.Error("tool_execution_failed", $"重命名资源失败: {result}", new
                {
                    path = normalizedPath,
                    newName = normalizedNewName,
                    unityError = result
                });
            }

            AssetDatabase.SaveAssets();

            return ToolResult.Ok(new
            {
                action = "rename",
                renamed = true,
                oldPath = normalizedPath,
                newName = normalizedNewName,
                asset = CreateAssetSummary(destPath)
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

        static bool TryEnsureFolderScopeExists(string path, out ToolResult error)
        {
            error = null;
            if (AssetDatabase.IsValidFolder(path))
            {
                return true;
            }

            if (TryAssetExists(path))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'path' 必须指向 Assets/ 下已存在的文件夹。", new
                {
                    parameter = "path",
                    path
                });
                return false;
            }

            error = ToolResult.Error("not_found", "未找到目标搜索目录。", new
            {
                path
            });
            return false;
        }

        static bool TryEnsureAssetExists(string path, out ToolResult error)
        {
            error = null;
            if (TryAssetExists(path))
            {
                return true;
            }

            error = ToolResult.Error("not_found", "未找到目标资源。", new
            {
                path
            });
            return false;
        }

        static bool TryEnsureParentFolderExists(string assetPath, out ToolResult error)
        {
            error = null;
            var parentFolder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(parentFolder))
            {
                parentFolder = "Assets";
            }

            if (AssetDatabase.IsValidFolder(parentFolder))
            {
                return true;
            }

            error = ToolResult.Error("not_found", "父文件夹不存在。", new
            {
                path = assetPath,
                parent = parentFolder
            });
            return false;
        }

        static bool TryCreateFolderPath(string folderPath, out ToolResult error)
        {
            error = null;
            if (string.Equals(folderPath, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolResult.Error("invalid_parameter", "参数 'path' 不能是 Assets 根目录。", new
                {
                    parameter = "path",
                    path = folderPath
                });
                return false;
            }

            if (AssetDatabase.IsValidFolder(folderPath))
            {
                error = ToolResult.Error("invalid_parameter", "目标文件夹已存在。", new
                {
                    parameter = "path",
                    path = folderPath
                });
                return false;
            }

            if (!TryValidateFolderPath(folderPath, out error))
            {
                return false;
            }

            var segments = folderPath.Split('/');
            var currentPath = "Assets";
            for (var index = 1; index < segments.Length; index++)
            {
                var segment = segments[index];
                var nextPath = $"{currentPath}/{segment}";
                if (AssetDatabase.IsValidFolder(nextPath))
                {
                    currentPath = nextPath;
                    continue;
                }

                if (TryAssetExists(nextPath))
                {
                    error = ToolResult.Error("invalid_parameter", "目标路径已存在且不是文件夹。", new
                    {
                        parameter = "path",
                        path = nextPath
                    });
                    return false;
                }

                var guid = AssetDatabase.CreateFolder(currentPath, segment);
                if (string.IsNullOrWhiteSpace(guid))
                {
                    error = ToolResult.Error("tool_execution_failed", "Unity 未能创建目标文件夹。", new
                    {
                        parent = currentPath,
                        name = segment,
                        path = nextPath
                    });
                    return false;
                }

                currentPath = nextPath;
            }

            return true;
        }

        static bool TryValidateFolderPath(string folderPath, out ToolResult error)
        {
            error = null;
            var segments = folderPath.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (string.IsNullOrWhiteSpace(segment))
                {
                    error = ToolResult.Error("invalid_parameter", "参数 'path' 包含空文件夹名。", new
                    {
                        parameter = "path",
                        path = folderPath
                    });
                    return false;
                }

                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    error = ToolResult.Error("invalid_parameter", "参数 'path' 包含非法文件夹名字符。", new
                    {
                        parameter = "path",
                        path = folderPath,
                        folderName = segment
                    });
                    return false;
                }
            }

            return true;
        }

        static bool TryNormalizeCreateAssetType(string assetType, out string normalizedAssetType, out ToolResult error)
        {
            normalizedAssetType = NormalizeOptionalValue(assetType);
            error = null;
            if (string.Equals(normalizedAssetType, "Material", StringComparison.OrdinalIgnoreCase))
            {
                normalizedAssetType = "Material";
                return true;
            }

            error = ToolResult.Error("invalid_parameter", $"不支持的资源类型 '{assetType}'，create 操作目前仅支持 'Material'。", new
            {
                parameter = "asset_type",
                value = assetType,
                supportedTypes = SupportedCreateAssetTypes
            });
            normalizedAssetType = string.Empty;
            return false;
        }

        static bool TryNormalizeAssetName(string value, string parameterName, out string normalizedName, out ToolResult error)
        {
            normalizedName = NormalizeOptionalValue(value);
            error = null;
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                error = ToolResult.Error("missing_parameter", $"参数 '{parameterName}' 不能为空。", new
                {
                    parameter = parameterName
                });
                return false;
            }

            if (normalizedName.Contains("/", StringComparison.Ordinal) || normalizedName.Contains("\\", StringComparison.Ordinal))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 必须是资源名称，不能包含路径分隔符。", new
                {
                    parameter = parameterName,
                    value
                });
                normalizedName = string.Empty;
                return false;
            }

            if (normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 包含非法文件名字符。", new
                {
                    parameter = parameterName,
                    value
                });
                normalizedName = string.Empty;
                return false;
            }

            normalizedName = Path.GetFileNameWithoutExtension(normalizedName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 无法解析为有效资源名称。", new
                {
                    parameter = parameterName,
                    value
                });
                return false;
            }

            return true;
        }

        static string BuildAssetDatabaseSearchFilter(string filterType)
        {
            var normalizedFilterType = NormalizeOptionalValue(filterType);
            if (string.IsNullOrWhiteSpace(normalizedFilterType)
                || string.Equals(normalizedFilterType, "Folder", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return $"t:{normalizedFilterType}";
        }

        static Func<string, bool> CreateSearchPatternMatcher(string searchPattern)
        {
            var normalizedPattern = NormalizeOptionalValue(searchPattern).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalizedPattern))
            {
                return _ => true;
            }

            if (normalizedPattern.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                var regexPattern = "^" + Regex.Escape(normalizedPattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return assetPath => regex.IsMatch(assetPath) || regex.IsMatch(Path.GetFileName(assetPath));
            }

            return assetPath => assetPath.IndexOf(normalizedPattern, StringComparison.OrdinalIgnoreCase) >= 0
                || Path.GetFileName(assetPath).IndexOf(normalizedPattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool MatchesFilterType(string assetPath, string filterType)
        {
            var normalizedFilterType = NormalizeOptionalValue(filterType);
            if (string.IsNullOrWhiteSpace(normalizedFilterType))
            {
                return true;
            }

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return string.Equals(normalizedFilterType, "Folder", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedFilterType, "DefaultAsset", StringComparison.OrdinalIgnoreCase);
            }

            var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (assetType == null)
            {
                return false;
            }

            return string.Equals(assetType.Name, normalizedFilterType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(assetType.FullName, normalizedFilterType, StringComparison.OrdinalIgnoreCase);
        }

        static bool TryAssetExists(string assetPath)
        {
            return AssetDatabase.IsValidFolder(assetPath)
                || !string.IsNullOrWhiteSpace(AssetDatabase.AssetPathToGUID(assetPath));
        }

        static void AppendSubFolders(string rootFolder, ISet<string> results)
        {
            foreach (var subFolder in AssetDatabase.GetSubFolders(rootFolder))
            {
                results.Add(subFolder);
                AppendSubFolders(subFolder, results);
            }
        }

        static object CreateAssetSummary(string assetPath)
        {
            var isFolder = AssetDatabase.IsValidFolder(assetPath);
            var fileName = Path.GetFileName(assetPath);
            var typeName = isFolder
                ? "Folder"
                : ResolveAssetTypeName(assetPath);

            return new
            {
                name = isFolder ? fileName : Path.GetFileNameWithoutExtension(assetPath),
                path = assetPath,
                type = typeName,
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                isFolder,
                fileName,
                extension = isFolder ? string.Empty : Path.GetExtension(assetPath)
            };
        }

        static string ResolveAssetTypeName(string assetPath)
        {
            var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (assetType != null)
            {
                return assetType.Name;
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return asset != null ? asset.GetType().Name : "Unknown";
        }

        static string NormalizeOptionalValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
