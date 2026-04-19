using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace UnityCli.Editor.Tools
{
    [UnityCliTool("packages", Description = "List and query Unity packages", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets, Category = "editor")]
    public sealed class PackagesTool : IUnityCliTool
    {
        const int RequestPollIntervalMs = 20;
        const int RequestTimeoutMs = 30000;

        static readonly string[] SupportedActions =
        {
            "list",
            "search",
            "get_info",
            "add_package",
            "remove_package"
        };

        public string Id => "packages";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "List and query Unity packages",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets,
                schemaVersion = "1.0",
                parameters = new List<ParamDescriptor>
                {
                    new ParamDescriptor
                    {
                        name = "action",
                        type = "string",
                        description = "packages action: list/search/get_info/add_package/remove_package",
                        required = true
                    },
                    new ParamDescriptor
                    {
                        name = "query",
                        type = "string",
                        description = "Registry search query for action=search",
                        required = false
                    },
                    new ParamDescriptor
                    {
                        name = "package",
                        type = "string",
                        description = "Package name or id for action=get_info/add_package/remove_package",
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

            if (!StateGuard.EnsureReady(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "action", out string action, out error))
            {
                return error;
            }

            switch (action)
            {
                case "list":
                    return HandleList();
                case "search":
                    return HandleSearch(args);
                case "get_info":
                    return HandleGetInfo(args);
                case "add_package":
                    return HandleAddPackage(args, context);
                case "remove_package":
                    return HandleRemovePackage(args, context);
                default:
                    return ToolResult.Error("invalid_parameter", $"不支持的 packages 操作 '{action}'。", new
                    {
                        parameter = "action",
                        value = action,
                        supportedActions = SupportedActions
                    });
            }
        }

        static ToolResult HandleList()
        {
            if (!TryListInstalledPackages(out var installedPackages, out var error))
            {
                return error;
            }

            var packages = installedPackages
                .OrderBy(GetSortKey, StringComparer.Ordinal)
                .Select(package => CreatePackageSummary(package, true))
                .Cast<object>()
                .ToArray();

            return ToolResult.Ok(new
            {
                action = "list",
                offlineMode = true,
                count = packages.Length,
                packages
            });
        }

        static ToolResult HandleSearch(Dictionary<string, object> args)
        {
            if (!ArgsHelper.TryGetRequired(args, "query", out string query, out var error))
            {
                return error;
            }

            var normalizedQuery = NormalizeRequiredValue(query, "query", out error);
            if (error != null)
            {
                return error;
            }

            if (!TrySearchPackages(normalizedQuery, out var searchResults, out error))
            {
                return error;
            }

            var packages = searchResults
                .OrderBy(GetSortKey, StringComparer.Ordinal)
                .Select(CreateSearchPackageSummary)
                .Cast<object>()
                .ToArray();

            return ToolResult.Ok(new
            {
                action = "search",
                query = normalizedQuery,
                offlineMode = false,
                count = packages.Length,
                packages
            });
        }

        static ToolResult HandleGetInfo(Dictionary<string, object> args)
        {
            if (!ArgsHelper.TryGetRequired(args, "package", out string packageId, out var error))
            {
                return error;
            }

            var normalizedPackageId = NormalizeRequiredValue(packageId, "package", out error);
            if (error != null)
            {
                return error;
            }

            if (!TryListInstalledPackages(out var installedPackages, out error))
            {
                return error;
            }

            var installedPackage = FindExactPackage(installedPackages, normalizedPackageId);

            if (!TrySearchPackages(normalizedPackageId, out var registryPackages, out var searchError))
            {
                return searchError;
            }

            var registryPackage = FindExactPackage(registryPackages, normalizedPackageId);

            if (installedPackage == null && registryPackage == null)
            {
                return ToolResult.Error("not_found", "未找到指定包。", new
                {
                    package = normalizedPackageId
                });
            }

            return ToolResult.Ok(new
            {
                action = "get_info",
                package = normalizedPackageId,
                isInstalled = installedPackage != null,
                foundInRegistry = registryPackage != null,
                info = CreatePackageDetail(registryPackage, installedPackage),
                installed = installedPackage != null ? CreatePackageSummary(installedPackage, true) : null,
                registry = registryPackage != null ? CreateSearchPackageSummary(registryPackage) : null
            });
        }

        static ToolResult HandleAddPackage(Dictionary<string, object> args, ToolContext context)
        {
            if (!StateGuard.EnsureNotPlaying(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "package", out string packageId, out error))
            {
                return error;
            }

            var normalizedPackageId = NormalizeRequiredValue(packageId, "package", out error);
            if (error != null)
            {
                return error;
            }

            if (!TryCreateAddRequest(normalizedPackageId, out var request, out error))
            {
                return error;
            }

            if (!TryCompleteRequest(request, "add_package", out error))
            {
                return error;
            }

            return ToolResult.Ok(new
            {
                action = "add_package",
                package = normalizedPackageId,
                result = CreateMutationResultSummary(GetMemberValue(request, "Result"), normalizedPackageId)
            });
        }

        static ToolResult HandleRemovePackage(Dictionary<string, object> args, ToolContext context)
        {
            if (!StateGuard.EnsureNotPlaying(context, out var error))
            {
                return error;
            }

            if (!ArgsHelper.TryGetRequired(args, "package", out string packageId, out error))
            {
                return error;
            }

            var normalizedPackageId = NormalizeRequiredValue(packageId, "package", out error);
            if (error != null)
            {
                return error;
            }

            if (!TryCreateRemoveRequest(normalizedPackageId, out var request, out error))
            {
                return error;
            }

            if (!TryCompleteRequest(request, "remove_package", out error))
            {
                return error;
            }

            return ToolResult.Ok(new
            {
                action = "remove_package",
                package = normalizedPackageId,
                result = CreateMutationResultSummary(GetMemberValue(request, "Result"), normalizedPackageId)
            });
        }

        static string NormalizeRequiredValue(string rawValue, string parameterName, out ToolResult error)
        {
            error = null;
            var normalizedValue = rawValue?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedValue))
            {
                return normalizedValue;
            }

            error = ToolResult.Error("invalid_parameter", $"参数 '{parameterName}' 不能为空。", new
            {
                parameter = parameterName
            });
            return string.Empty;
        }

        static bool TryListInstalledPackages(out PackageInfo[] packages, out ToolResult error)
        {
            packages = Array.Empty<PackageInfo>();
            error = null;

            if (!TryCreateListRequest(out var request, out error))
            {
                return false;
            }

            if (!TryCompleteRequest(request, "list", out error))
            {
                return false;
            }

            packages = ToPackageArray(request.Result);
            return true;
        }

        static bool TrySearchPackages(string query, out PackageInfo[] packages, out ToolResult error)
        {
            packages = Array.Empty<PackageInfo>();
            error = null;

            if (!TryCreateSearchRequest(query, out var request, out error))
            {
                return false;
            }

            if (!TryCompleteRequest(request, "search", out error))
            {
                return false;
            }

            packages = ToPackageArray(request.Result);
            return true;
        }

        static bool TryCreateListRequest(out ListRequest request, out ToolResult error)
        {
            request = null;
            error = null;

            var clientType = typeof(Client);
            var listMethod = clientType.GetMethod("List", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(bool) }, null)
                ?? clientType.GetMethod("List", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null)
                ?? clientType.GetMethod("List", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

            if (listMethod == null)
            {
                error = ToolResult.Error("tool_execution_failed", "当前 Unity 版本未提供 PackageManager.Client.List API。", new
                {
                    action = "list"
                });
                return false;
            }

            try
            {
                object[] arguments;
                switch (listMethod.GetParameters().Length)
                {
                    case 2:
                        arguments = new object[] { true, true };
                        break;
                    case 1:
                        arguments = new object[] { true };
                        break;
                    default:
                        arguments = Array.Empty<object>();
                        break;
                }

                request = listMethod.Invoke(null, arguments) as ListRequest;
                if (request != null)
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = ToolResult.Error("tool_execution_failed", "创建已安装包查询失败。", new
                {
                    action = "list",
                    exception = UnwrapException(exception).ToString()
                });
                return false;
            }

            error = ToolResult.Error("tool_execution_failed", "创建已安装包查询失败。", new
            {
                action = "list",
                method = listMethod.ToString()
            });
            return false;
        }

        static bool TryCreateSearchRequest(string query, out SearchRequest request, out ToolResult error)
        {
            request = null;
            error = null;

            var clientType = typeof(Client);
            var searchMethod = clientType.GetMethod("Search", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(bool) }, null)
                ?? clientType.GetMethod("Search", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

            if (searchMethod == null)
            {
                error = ToolResult.Error("tool_execution_failed", "当前 Unity 版本未提供 PackageManager.Client.Search API。", new
                {
                    action = "search"
                });
                return false;
            }

            try
            {
                var arguments = searchMethod.GetParameters().Length == 2
                    ? new object[] { query, false }
                    : new object[] { query };
                request = searchMethod.Invoke(null, arguments) as SearchRequest;
                if (request != null)
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = ToolResult.Error("tool_execution_failed", "创建包搜索请求失败。", new
                {
                    action = "search",
                    query,
                    exception = UnwrapException(exception).ToString()
                });
                return false;
            }

            error = ToolResult.Error("tool_execution_failed", "创建包搜索请求失败。", new
            {
                action = "search",
                query,
                method = searchMethod.ToString()
            });
            return false;
        }

        static bool TryCreateAddRequest(string packageId, out AddRequest request, out ToolResult error)
        {
            request = null;
            error = null;

            try
            {
                request = Client.Add(packageId);
                if (request != null)
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = ToolResult.Error("tool_execution_failed", "创建包安装请求失败。", new
                {
                    action = "add_package",
                    package = packageId,
                    exception = UnwrapException(exception).ToString()
                });
                return false;
            }

            error = ToolResult.Error("tool_execution_failed", "创建包安装请求失败。", new
            {
                action = "add_package",
                package = packageId
            });
            return false;
        }

        static bool TryCreateRemoveRequest(string packageId, out RemoveRequest request, out ToolResult error)
        {
            request = null;
            error = null;

            try
            {
                request = Client.Remove(packageId);
                if (request != null)
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = ToolResult.Error("tool_execution_failed", "创建包卸载请求失败。", new
                {
                    action = "remove_package",
                    package = packageId,
                    exception = UnwrapException(exception).ToString()
                });
                return false;
            }

            error = ToolResult.Error("tool_execution_failed", "创建包卸载请求失败。", new
            {
                action = "remove_package",
                package = packageId
            });
            return false;
        }

        static bool TryCompleteRequest(Request request, string action, out ToolResult error)
        {
            error = null;
            if (request == null)
            {
                error = ToolResult.Error("tool_execution_failed", "Package Manager 请求对象为空。", new
                {
                    action
                });
                return false;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMs);
            while (request.Status == StatusCode.InProgress)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    error = ToolResult.Error("tool_execution_failed", "等待 Package Manager 请求超时。", new
                    {
                        action,
                        timeoutMs = RequestTimeoutMs
                    });
                    return false;
                }

                Thread.Sleep(RequestPollIntervalMs);
            }

            if (request.Status == StatusCode.Success)
            {
                return true;
            }

            error = ToolResult.Error("tool_execution_failed", "Package Manager 请求失败。", CreateRequestFailureDetails(request, action));
            return false;
        }

        static object CreateRequestFailureDetails(Request request, string action)
        {
            return new
            {
                action,
                status = request.Status.ToString(),
                errorCode = GetMemberValue(request.Error, "errorCode")?.ToString() ?? string.Empty,
                message = GetStringMember(request.Error, "message")
            };
        }

        static object CreateMutationResultSummary(object result, string packageId)
        {
            if (result is PackageInfo package)
            {
                return CreatePackageSummary(package, true);
            }

            return new
            {
                package = packageId,
                value = result?.ToString() ?? string.Empty
            };
        }

        static PackageInfo[] ToPackageArray(IEnumerable source)
        {
            if (source == null)
            {
                return Array.Empty<PackageInfo>();
            }

            var packages = new List<PackageInfo>();
            foreach (var item in source)
            {
                if (item is PackageInfo package)
                {
                    packages.Add(package);
                }
            }

            return packages.ToArray();
        }

        static PackageInfo FindExactPackage(IEnumerable<PackageInfo> packages, string packageIdentifier)
        {
            if (packages == null)
            {
                return null;
            }

            foreach (var package in packages)
            {
                if (package == null)
                {
                    continue;
                }

                if (MatchesPackageIdentifier(package, packageIdentifier))
                {
                    return package;
                }
            }

            return null;
        }

        static bool MatchesPackageIdentifier(PackageInfo package, string packageIdentifier)
        {
            return string.Equals(GetStringMember(package, "name"), packageIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetStringMember(package, "packageId"), packageIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetStringMember(package, "displayName"), packageIdentifier, StringComparison.OrdinalIgnoreCase);
        }

        static string GetSortKey(PackageInfo package)
        {
            var name = GetStringMember(package, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            var displayName = GetStringMember(package, "displayName");
            return !string.IsNullOrWhiteSpace(displayName) ? displayName : string.Empty;
        }

        static object CreatePackageSummary(PackageInfo package, bool isInstalled)
        {
            return new
            {
                name = GetStringMember(package, "name"),
                displayName = GetStringMember(package, "displayName"),
                version = GetStringMember(package, "version"),
                description = GetStringMember(package, "description"),
                source = GetMemberValue(package, "source")?.ToString() ?? string.Empty,
                packageId = GetStringMember(package, "packageId"),
                resolvedPath = GetStringMember(package, "resolvedPath"),
                assetPath = GetStringMember(package, "assetPath"),
                isDirectDependency = GetBoolMember(package, "isDirectDependency"),
                isInstalled
            };
        }

        static object CreateSearchPackageSummary(PackageInfo package)
        {
            return new
            {
                name = GetStringMember(package, "name"),
                displayName = GetStringMember(package, "displayName"),
                version = GetStringMember(package, "version"),
                description = GetStringMember(package, "description"),
                source = GetMemberValue(package, "source")?.ToString() ?? string.Empty,
                packageId = GetStringMember(package, "packageId"),
                versions = CreateVersionsSummary(GetMemberValue(package, "versions"))
            };
        }

        static object CreatePackageDetail(PackageInfo registryPackage, PackageInfo installedPackage)
        {
            var detailPackage = installedPackage ?? registryPackage;
            var metadataPackage = registryPackage ?? detailPackage;

            return new
            {
                name = GetFirstNonEmptyString(installedPackage, registryPackage, "name"),
                displayName = GetFirstNonEmptyString(installedPackage, registryPackage, "displayName"),
                version = GetFirstNonEmptyString(installedPackage, registryPackage, "version"),
                description = GetFirstNonEmptyString(installedPackage, registryPackage, "description"),
                category = GetFirstNonEmptyString(installedPackage, registryPackage, "category"),
                type = GetFirstNonEmptyString(installedPackage, registryPackage, "type"),
                source = GetMemberValue(detailPackage, "source")?.ToString() ?? string.Empty,
                packageId = GetFirstNonEmptyString(installedPackage, registryPackage, "packageId"),
                resolvedPath = GetStringMember(installedPackage, "resolvedPath"),
                assetPath = GetStringMember(installedPackage, "assetPath"),
                isDirectDependency = GetBoolMember(installedPackage, "isDirectDependency"),
                isInstalled = installedPackage != null,
                versions = CreateVersionsSummary(GetMemberValue(metadataPackage, "versions")),
                dependencies = CreateDependenciesSummary(GetMemberValue(detailPackage, "dependencies")),
                resolvedDependencies = CreateDependenciesSummary(GetMemberValue(detailPackage, "resolvedDependencies")),
                keywords = CreateStringArray(GetMemberValue(metadataPackage, "keywords")),
                author = CreateAuthorSummary(GetMemberValue(metadataPackage, "author"))
            };
        }

        static object CreateVersionsSummary(object versions)
        {
            return new
            {
                installed = GetStringMember(versions, "installed"),
                latest = GetStringMember(versions, "latest"),
                latestCompatible = GetFirstNonEmptyString(versions, versions, "latestCompatible", "recommended"),
                recommended = GetFirstNonEmptyString(versions, versions, "recommended", "verified"),
                verified = GetStringMember(versions, "verified"),
                all = CreateStringArray(GetMemberValue(versions, "all")),
                compatible = CreateStringArray(GetMemberValue(versions, "compatible")),
                deprecated = CreateStringArray(GetMemberValue(versions, "deprecated"))
            };
        }

        static object[] CreateDependenciesSummary(object dependencies)
        {
            if (!(dependencies is IEnumerable enumerable))
            {
                return Array.Empty<object>();
            }

            var results = new List<object>();
            foreach (var dependency in enumerable)
            {
                if (dependency == null)
                {
                    continue;
                }

                results.Add(new
                {
                    name = GetStringMember(dependency, "name"),
                    version = GetStringMember(dependency, "version")
                });
            }

            return results.ToArray();
        }

        static object CreateAuthorSummary(object author)
        {
            return new
            {
                name = GetStringMember(author, "name"),
                email = GetStringMember(author, "email"),
                url = GetStringMember(author, "url")
            };
        }

        static string[] CreateStringArray(object value)
        {
            if (!(value is IEnumerable enumerable) || value is string)
            {
                return Array.Empty<string>();
            }

            var results = new List<string>();
            foreach (var item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                results.Add(item.ToString() ?? string.Empty);
            }

            return results.ToArray();
        }

        static string GetFirstNonEmptyString(object primaryTarget, object fallbackTarget, params string[] memberNames)
        {
            foreach (var memberName in memberNames)
            {
                var primaryValue = GetStringMember(primaryTarget, memberName);
                if (!string.IsNullOrWhiteSpace(primaryValue))
                {
                    return primaryValue;
                }

                var fallbackValue = GetStringMember(fallbackTarget, memberName);
                if (!string.IsNullOrWhiteSpace(fallbackValue))
                {
                    return fallbackValue;
                }
            }

            return string.Empty;
        }

        static string GetStringMember(object target, string memberName)
        {
            return GetMemberValue(target, memberName)?.ToString() ?? string.Empty;
        }

        static bool GetBoolMember(object target, string memberName)
        {
            var value = GetMemberValue(target, memberName);
            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value == null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }

        static object GetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            var targetType = target.GetType();
            var property = targetType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return property.GetValue(target, null);
            }

            var field = targetType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(target);
        }

        static Exception UnwrapException(Exception exception)
        {
            return exception is TargetInvocationException invocationException && invocationException.InnerException != null
                ? invocationException.InnerException
                : exception;
        }
    }
}
