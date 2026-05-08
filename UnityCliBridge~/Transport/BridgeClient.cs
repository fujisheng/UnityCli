using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityCli.Output;

namespace UnityCli.Transport
{
    class BridgeClient
    {
        const int MaxRequestAttempts = 5;
        const int RetryDelayMs = 250;
<<<<<<< Updated upstream
        // 与 Editor/Core/UnityCliDispatcherQueue.cs 默认值保持一致，避免 transport 先断开而 editor 仍在等待。
        const int DefaultRequestTimeoutMs = 30000;
=======
        const int DefaultRequestTimeoutMs = 5000;
        const int HeavyInvokeRequestTimeoutMs = 15000;
>>>>>>> Stashed changes

        public sealed class BridgeEndpointInfo
        {
            public string ProtocolVersion { get; set; } = string.Empty;

            public string Transport { get; set; } = string.Empty;

            public string PipeName { get; set; } = string.Empty;

            public int Pid { get; set; }

            public string InstanceId { get; set; } = string.Empty;

            public long Generation { get; set; }

            public string Token { get; set; } = string.Empty;

            public DateTime StartedAt { get; set; }
        }

        public sealed class BridgeDiscoveryResult
        {
            BridgeDiscoveryResult(bool isAvailable, BridgeEndpointInfo? endpoint, object errorPayload)
            {
                IsAvailable = isAvailable;
                Endpoint = endpoint;
                ErrorPayload = errorPayload;
            }

            public bool IsAvailable { get; }

            public BridgeEndpointInfo? Endpoint { get; }

            public object ErrorPayload { get; }

            public static BridgeDiscoveryResult Available(BridgeEndpointInfo endpoint)
            {
                return new BridgeDiscoveryResult(true, endpoint, ResultFormatter.CreateSuccessPayload(endpoint));
            }

            public static BridgeDiscoveryResult Unavailable(object errorPayload)
            {
                return new BridgeDiscoveryResult(false, null, errorPayload);
            }
        }

        public sealed class BridgeCallResult
        {
            BridgeCallResult(object payload, int? statusCode)
            {
                Payload = payload;
                StatusCode = statusCode;
            }

            public object Payload { get; }

            public int? StatusCode { get; }

            public static BridgeCallResult FromPayload(object payload, int? statusCode = null)
            {
                return new BridgeCallResult(payload, statusCode);
            }
        }

        public string ProjectPath { get; }

        public BridgeClient(string? projectPath)
        {
            ProjectPath = ResolveProjectPath(projectPath);
        }

        public string GetEndpointFilePath()
        {
            return Path.Combine(ProjectPath, "Library", "UnityCliBridge", "endpoint.json");
        }

        public static BridgeDiscoveryResult FindBridge(string? projectPath)
        {
            return new BridgeClient(projectPath).FindBridge();
        }

        static string ResolveProjectPath(string? projectPath)
        {
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                return TryResolveUnityProjectPath(projectPath, out var resolvedProjectPath)
                    ? resolvedProjectPath
                    : Path.GetFullPath(projectPath);
            }

            if (TryResolveUnityProjectPath(Directory.GetCurrentDirectory(), out var currentDirectoryProjectPath))
            {
                return currentDirectoryProjectPath;
            }

            if (TryResolveUnityProjectPath(AppContext.BaseDirectory, out var appBaseDirectoryProjectPath))
            {
                return appBaseDirectoryProjectPath;
            }

            return Directory.GetCurrentDirectory();
        }

        static bool TryResolveUnityProjectPath(string path, out string resolvedProjectPath)
        {
            resolvedProjectPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            DirectoryInfo? directory = null;
            if (Directory.Exists(fullPath))
            {
                directory = new DirectoryInfo(fullPath);
            }
            else if (File.Exists(fullPath))
            {
                directory = new FileInfo(fullPath).Directory;
            }

            while (directory != null)
            {
                if (LooksLikeUnityProjectRoot(directory.FullName))
                {
                    resolvedProjectPath = directory.FullName;
                    return true;
                }

                directory = directory.Parent;
            }

            return false;
        }

        static bool LooksLikeUnityProjectRoot(string path)
        {
            return Directory.Exists(Path.Combine(path, "Assets"))
                && Directory.Exists(Path.Combine(path, "Packages"))
                && Directory.Exists(Path.Combine(path, "ProjectSettings"));
        }

        public BridgeDiscoveryResult FindBridge()
        {
            var endpointFile = GetEndpointFilePath();
            if (!File.Exists(endpointFile))
            {
                return BridgeDiscoveryResult.Unavailable(ResultFormatter.CreateErrorPayload(
                    "bridge_unavailable",
                    "未找到 UnityCli bridge endpoint.json",
                    new
                    {
                        endpointFile
                    }));
            }

            string endpointJson;
            try
            {
                endpointJson = File.ReadAllText(endpointFile);
            }
            catch (Exception exception)
            {
                return BridgeDiscoveryResult.Unavailable(ResultFormatter.CreateErrorPayload(
                    "bridge_unavailable",
                    "读取 endpoint.json 失败。",
                    new
                    {
                        endpointFile,
                        reason = exception.Message
                    }));
            }

            if (!CliJson.TryDeserializeObject(endpointJson, out var root, out var parseError))
            {
                return BridgeDiscoveryResult.Unavailable(ResultFormatter.CreateErrorPayload(
                    "bridge_unavailable",
                    "endpoint.json 不是有效的 JSON 对象。",
                    new
                    {
                        endpointFile,
                        reason = parseError
                    }));
            }

            if (!CliObjectAccessor.TryGetString(root, "token", out var token) || string.IsNullOrWhiteSpace(token))
            {
                return BridgeDiscoveryResult.Unavailable(ResultFormatter.CreateErrorPayload(
                    "bridge_unavailable",
                    "endpoint.json 缺少有效 token。",
                    new
                    {
                        endpointFile
                    }));
            }

            var pipeName = ReadString(root, "pipeName");
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                return BridgeDiscoveryResult.Unavailable(ResultFormatter.CreateErrorPayload(
                    "bridge_unavailable",
                    "endpoint.json 缺少有效 pipeName。",
                    new
                    {
                        endpointFile
                    }));
            }

            var endpoint = new BridgeEndpointInfo
            {
                ProtocolVersion = ReadString(root, "protocolVersion"),
                Transport = ReadString(root, "transport"),
                PipeName = pipeName,
                Pid = ReadInt(root, "pid"),
                InstanceId = ReadString(root, "instanceId"),
                Generation = ReadLong(root, "generation"),
                Token = token,
                StartedAt = ReadDateTime(root, "startedAt")
            };

            return BridgeDiscoveryResult.Available(endpoint);
        }

        public async Task<BridgeCallResult> GetAsync(string route, int? timeoutMs = null)
        {
            return await SendAsync("GET", route, null, timeoutMs);
        }

        public async Task<BridgeCallResult> PostAsync(string route, object body, int? timeoutMs = null)
        {
            return await SendAsync("POST", route, body, timeoutMs);
        }

        async Task<BridgeCallResult> SendAsync(string method, string route, object? body, int? timeoutMs)
        {
            Exception? lastException = null;
            object? lastErrorPayload = null;

            for (var attempt = 0; attempt < MaxRequestAttempts; attempt++)
            {
                var discovery = FindBridge();
                if (!discovery.IsAvailable || discovery.Endpoint == null)
                {
                    lastErrorPayload = discovery.ErrorPayload;
                    if (attempt < MaxRequestAttempts - 1)
                    {
                        await Task.Delay(RetryDelayMs);
                        continue;
                    }

                    return BridgeCallResult.FromPayload(discovery.ErrorPayload);
                }

                try
                {
                    return await SendViaNamedPipeAsync(discovery.Endpoint, method, route, body, timeoutMs);
                }
                catch (OperationCanceledException exception)
                {
                    return BridgeCallResult.FromPayload(ResultFormatter.CreateErrorPayload(
                        "bridge_timeout",
                        "UnityCli bridge 请求超时。",
                        new
                        {
                            endpointFile = GetEndpointFilePath(),
                            route = NormalizeRoute(route),
                            timeoutMs = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : DefaultRequestTimeoutMs,
                            lastError = exception.Message
                        }));
                }
                catch (Exception exception) when (IsRetryable(exception))
                {
                    lastException = exception;
                    if (attempt < MaxRequestAttempts - 1)
                    {
                        await Task.Delay(RetryDelayMs);
                        continue;
                    }
                }
            }

            return BridgeCallResult.FromPayload(ResultFormatter.CreateErrorPayload(
                "bridge_unavailable",
                "UnityCli bridge 不可用或正在重启。",
                new
                {
                    endpointFile = GetEndpointFilePath(),
                    lastError = lastException?.Message,
                    bridge = lastErrorPayload
                }));
        }

        async Task<BridgeCallResult> SendViaNamedPipeAsync(BridgeEndpointInfo endpoint, string method, string route, object? body, int? timeoutMs)
        {
            using var client = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cancellation = CreateCancellationSource(method, route, body, timeoutMs);
            await client.ConnectAsync(cancellation.Token);

            var request = new BridgePipeRequest
            {
                token = endpoint.Token,
                method = method,
                path = NormalizeRoute(route),
                body = body != null ? CliJson.Serialize(body) : string.Empty
            };

            await BridgePipeProtocol.WriteFrameAsync(client, CliJson.Serialize(request), cancellation.Token);
            await client.FlushAsync(cancellation.Token);

            var rawResponse = await BridgePipeProtocol.ReadFrameAsync(client, cancellation.Token);
            if (string.IsNullOrWhiteSpace(rawResponse))
            {
                return BridgeCallResult.FromPayload(ResultFormatter.CreateErrorPayload(
                    "invalid_response",
                    "bridge 返回了空响应。",
                    new
                    {
                        pipeName = endpoint.PipeName
                    }));
            }

            if (!CliJson.TryDeserializeObject(rawResponse, out var responseObject, out var parseError))
            {
                return BridgeCallResult.FromPayload(ResultFormatter.CreateErrorPayload(
                    "invalid_response",
                    "bridge 返回了无法解析的响应帧。",
                    new
                    {
                        reason = parseError,
                        raw = rawResponse
                    }));
            }

            var statusCode = ReadInt(responseObject, "statusCode");
            var responseBody = ReadString(responseObject, "body");
            return BridgeCallResult.FromPayload(ParseResponsePayload(statusCode, responseBody), statusCode);
        }

        static string NormalizeRoute(string route)
        {
            var normalized = string.IsNullOrWhiteSpace(route) ? "/" : route;
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = "/" + normalized;
            }

            return normalized;
        }

        static object ParseResponsePayload(int statusCode, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return ResultFormatter.CreateErrorPayload(
                    "invalid_response",
                    "bridge 返回了空响应。",
                    new
                    {
                        statusCode
                    });
            }

            if (CliJson.TryDeserialize(content, out var payload, out var error) && payload != null)
            {
                return payload;
            }

            return ResultFormatter.CreateErrorPayload(
                "invalid_response",
                "bridge 返回了无法解析的 JSON。",
                new
                {
                    statusCode,
                    reason = error,
                    raw = content
                });
        }

        static CancellationTokenSource CreateCancellationSource(string method, string route, object? body, int? timeoutMs)
        {
            var effectiveTimeout = timeoutMs.HasValue && timeoutMs.Value > 0
                ? timeoutMs.Value
                : ResolveDefaultTimeoutMs(method, route, body);
            return new CancellationTokenSource(effectiveTimeout);
        }

        static int ResolveDefaultTimeoutMs(string method, string route, object? body)
        {
            if (string.Equals(method, "POST", StringComparison.Ordinal)
                && string.Equals(NormalizeRoute(route), "/invoke", StringComparison.Ordinal)
                && body is InvokeRequest request
                && string.Equals(request.tool, "ui.validate_prefab", StringComparison.Ordinal))
            {
                return HeavyInvokeRequestTimeoutMs;
            }

            return DefaultRequestTimeoutMs;
        }

        static bool IsRetryable(Exception exception)
        {
            return exception is IOException or TimeoutException;
        }

        static string ReadString(System.Collections.Generic.IDictionary<string, object> root, string key)
        {
            return CliObjectAccessor.TryGetString(root, key, out var value) ? value : string.Empty;
        }

        static int ReadInt(System.Collections.Generic.IDictionary<string, object> root, string key)
        {
            return CliObjectAccessor.TryGetInt(root, key, out var value) ? value : 0;
        }

        static long ReadLong(System.Collections.Generic.IDictionary<string, object> root, string key)
        {
            if (!CliObjectAccessor.TryGetMember(root, key, out var raw) || raw == null)
            {
                return 0L;
            }

            return raw switch
            {
                long longValue => longValue,
                int intValue => intValue,
                double doubleValue => Convert.ToInt64(doubleValue, CultureInfo.InvariantCulture),
                _ => long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0L
            };
        }

        static DateTime ReadDateTime(System.Collections.Generic.IDictionary<string, object> root, string key)
        {
            if (!CliObjectAccessor.TryGetMember(root, key, out var raw) || raw == null)
            {
                return DateTime.MinValue;
            }

            if (raw is DateTime dateTime)
            {
                return dateTime;
            }

            if (DateTime.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed;
            }

            return DateTime.MinValue;
        }
    }
}
