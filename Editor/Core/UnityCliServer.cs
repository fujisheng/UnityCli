using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEngine;

namespace UnityCli.Editor.Core
{
    public static class UnityCliServer
    {
        const string PipeNamePrefix = "unitycli-";

        static readonly object syncRoot = new object();

        static CancellationTokenSource listenCancellation;
        static Task listenTask;
        static BridgeEndpoint currentEndpoint;
        static string currentToken = string.Empty;

        public static bool IsRunning
        {
            get
            {
                lock (syncRoot)
                {
                    return listenCancellation != null
                        && !listenCancellation.IsCancellationRequested
                        && listenTask != null
                        && !listenTask.IsCanceled
                        && !listenTask.IsCompleted;
                }
            }
        }

        public static BridgeEndpoint CurrentEndpoint
        {
            get
            {
                lock (syncRoot)
                {
                    return CloneEndpoint(currentEndpoint, includeToken: false);
                }
            }
        }

        public static void EnsureRunning()
        {
            lock (syncRoot)
            {
                if (listenCancellation != null
                    && !listenCancellation.IsCancellationRequested
                    && listenTask != null
                    && !listenTask.IsCanceled
                    && !listenTask.IsCompleted)
                {
                    return;
                }

                StartLocked();
            }
        }

        public static void Stop(bool clearSessionState, bool disposeCancellation = true)
        {
            CancellationTokenSource cancellationToStop;
            Task listenTaskToWait;
            BridgeEndpoint endpointToDelete;
            string pipeNameToSignal;

            lock (syncRoot)
            {
                cancellationToStop = listenCancellation;
                listenTaskToWait = listenTask;
                endpointToDelete = currentEndpoint;
                pipeNameToSignal = currentEndpoint?.pipeName ?? string.Empty;
                listenCancellation = null;
                listenTask = null;
                currentEndpoint = null;
                currentToken = string.Empty;
            }

            if (cancellationToStop != null)
            {
                // 1. 发送取消信号
                try
                {
                    cancellationToStop.Cancel();
                }
                catch
                {
                }

                // 2. 先解除 pipe 阻塞，让 WaitForConnectionAsync 尽快返回
                SignalPipeShutdown(pipeNameToSignal);

                // 3. 等待 ListenLoop 退出
                if (listenTaskToWait != null)
                {
                    try
                    {
                        listenTaskToWait.Wait(TimeSpan.FromMilliseconds(1000));
                    }
                    catch
                    {
                    }
                }

                // 4. 仅在编辑器退出时 Dispose（domain reload 时跳过，
                //    因为线程池上可能还有 pending 的 IO 回调引用该 CTS，
                //    Dispose 后回调触发会导致 ObjectDisposedException 崩溃。
                //    domain reload 会销毁整个 AppDomain，GC 会自动清理。）
                if (disposeCancellation)
                {
                    try
                    {
                        cancellationToStop.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            else if (listenTaskToWait != null)
            {
                try
                {
                    listenTaskToWait.Wait(TimeSpan.FromMilliseconds(250));
                }
                catch
                {
                }
            }

            UnityCliEndpointFile.Delete(endpointToDelete);
            if (clearSessionState)
            {
                UnityCliEndpointFile.ResetSessionState();
            }
        }

        static void StartLocked()
        {
            var token = Guid.NewGuid().ToString("N");
            var pipeName = BuildPipeName(token);
            Debug.Log($"[UnityCli] StartLocked: pipeName={pipeName}");
            var endpoint = UnityCliEndpointFile.CreateEndpoint(pipeName, token);
            Debug.Log($"[UnityCli] StartLocked: endpoint 已创建 (pid={endpoint.pid}, gen={endpoint.generation})");
            try
            {
                UnityCliEndpointFile.Write(endpoint);
                Debug.Log($"[UnityCli] StartLocked: endpoint.json 已写入 {UnityCliEndpointFile.FilePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCli] StartLocked: 写入 endpoint.json 失败 ({UnityCliEndpointFile.FilePath}): {ex}");
                throw;
            }

            var cancellation = new CancellationTokenSource();
            currentToken = token;
            currentEndpoint = endpoint;
            listenCancellation = cancellation;
            listenTask = Task.Run(() => ListenLoop(pipeName, token, cancellation.Token), cancellation.Token);
            Debug.Log("[UnityCli] StartLocked: 服务器启动完成");
        }

        internal static void EnsureEndpointFileWritten()
        {
            BridgeEndpoint endpoint;
            lock (syncRoot)
            {
                if (listenTask == null || currentEndpoint == null)
                {
                    return;
                }

                endpoint = currentEndpoint;
            }

            if (File.Exists(UnityCliEndpointFile.FilePath))
            {
                return;
            }

            Debug.LogWarning("[UnityCli] 服务器运行中但 endpoint.json 缺失，正在补写...");
            try
            {
                UnityCliEndpointFile.Write(endpoint);
                Debug.Log("[UnityCli] endpoint.json 补写成功");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCli] 补写 endpoint.json 失败: {ex}");
            }
        }

        static async Task ListenLoop(string pipeName, string token, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken);
                    _ = ProcessAcceptedConnectionAsync(server, token, cancellationToken);
                    server = null;
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (ObjectDisposedException)
                {
                    server?.Dispose();
                    break;
                }
                catch (IOException exception)
                {
                    server?.Dispose();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    Debug.LogWarning($"[UnityCli] NamedPipe 等待连接失败：{exception.Message}");
                    continue;
                }
                catch
                {
                    server?.Dispose();
                    throw;
                }
            }
        }

        static async Task ProcessAcceptedConnectionAsync(NamedPipeServerStream stream, string token, CancellationToken cancellationToken)
        {
            using (stream)
            {
                try
                {
                    await ProcessPipeConnection(stream, token, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        static async Task ProcessPipeConnection(NamedPipeServerStream stream, string token, CancellationToken cancellationToken)
        {
            var rawRequest = await BridgePipeProtocol.ReadFrameAsync(stream, cancellationToken);
            if (rawRequest == null)
            {
                if (cancellationToken.IsCancellationRequested || !stream.IsConnected)
                {
                    return;
                }

                await WritePipeResponse(stream, 400, ToolResult.Error("invalid_request", "请求体不能为空。", null).ToInvokeResponse(string.Empty), cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(rawRequest))
            {
                await WritePipeResponse(stream, 400, ToolResult.Error("invalid_request", "请求体不能为空。", null).ToInvokeResponse(string.Empty), cancellationToken);
                return;
            }

            if (!UnityCliJson.TryDeserializeObject(rawRequest, out var root, out var parseError))
            {
                await WritePipeResponse(stream, 400, ToolResult.Error("invalid_request", parseError, null).ToInvokeResponse(string.Empty), cancellationToken);
                return;
            }

            var request = new BridgePipeRequest
            {
                token = ReadString(root, "token"),
                method = ReadString(root, "method"),
                path = ReadString(root, "path"),
                body = ReadString(root, "body")
            };

            if (!IsAuthorized(request.token, token))
            {
                await WriteError(stream, 401, string.Empty, "unauthorized", "缺少或无效的 token。", null, cancellationToken);
                return;
            }

            var path = NormalizePath(request.path);
            var method = (request.method ?? string.Empty).ToUpperInvariant();

            if (string.Equals(path, "/ping", StringComparison.Ordinal))
            {
                if (!string.Equals(method, "GET", StringComparison.Ordinal))
                {
                    await WriteMethodNotAllowed(stream, "GET", cancellationToken);
                    return;
                }

                await WriteJson(stream, 200, BuildPingResponse(), cancellationToken);
                return;
            }

            if (string.Equals(path, "/tools", StringComparison.Ordinal))
            {
                if (!string.Equals(method, "GET", StringComparison.Ordinal))
                {
                    await WriteMethodNotAllowed(stream, "GET", cancellationToken);
                    return;
                }

                await WriteJson(stream, 200, UnityCliAllowlist.GetAllowedDescriptors(), cancellationToken);
                return;
            }

            if (TryExtractRouteValue(path, "/tools/", out var toolId))
            {
                if (!string.Equals(method, "GET", StringComparison.Ordinal))
                {
                    await WriteMethodNotAllowed(stream, "GET", cancellationToken);
                    return;
                }

                await WriteToolDescriptorResponse(stream, toolId, cancellationToken);
                return;
            }

            if (string.Equals(path, "/invoke", StringComparison.Ordinal))
            {
                if (!string.Equals(method, "POST", StringComparison.Ordinal))
                {
                    await WriteMethodNotAllowed(stream, "POST", cancellationToken);
                    return;
                }

                await WriteInvokeResponse(stream, request.body, cancellationToken);
                return;
            }

            if (TryExtractRouteValue(path, "/job/", out var jobId))
            {
                if (!string.Equals(method, "GET", StringComparison.Ordinal))
                {
                    await WriteMethodNotAllowed(stream, "GET", cancellationToken);
                    return;
                }

                await WriteJobStatusResponse(stream, jobId, cancellationToken);
                return;
            }

            await WriteError(stream, 404, string.Empty, "route_not_found", $"未找到路由 '{path}'。", path, cancellationToken);
        }

        static bool IsAuthorized(string actualToken, string token)
        {
            return !string.IsNullOrWhiteSpace(actualToken)
                && string.Equals(actualToken, token, StringComparison.Ordinal);
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            {
                return path.TrimEnd('/');
            }

            return path;
        }

        static bool TryExtractRouteValue(string path, string prefix, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(path)
                || string.IsNullOrWhiteSpace(prefix)
                || !path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var suffix = path.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(suffix) || suffix.IndexOf('/', StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            value = Uri.UnescapeDataString(suffix);
            return true;
        }

        static object BuildPingResponse()
        {
            return new InvokeResponse
            {
                requestId = "ping",
                ok = true,
                status = "completed",
                data = CloneEndpoint(CurrentEndpoint, includeToken: false)
            };
        }

        static async Task WriteToolDescriptorResponse(Stream stream, string toolId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(toolId)
                || !UnityCliAllowlist.IsAllowed(toolId)
                || !UnityCliRegistry.TryGetDescriptor(toolId, out var descriptor))
            {
                await WriteError(stream, 404, $"tools:{toolId}", "tool_not_found", $"未找到工具 '{toolId}'。", toolId, cancellationToken);
                return;
            }

            await WriteJson(stream, 200, descriptor, cancellationToken);
        }

        static async Task WriteInvokeResponse(Stream stream, string body, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                await WriteError(stream, 400, string.Empty, "invalid_request", "请求体不能为空。", null, cancellationToken);
                return;
            }

            if (!TryDeserializeInvokeRequest(body, out var invokeRequest, out var errorMessage))
            {
                await WriteError(stream, 400, string.Empty, "invalid_request", errorMessage, null, cancellationToken);
                return;
            }

            var invokeResponse = await UnityCliDispatcher.DispatchOnMainThreadAsync(invokeRequest);
            await WriteJson(stream, 200, invokeResponse, cancellationToken);
        }

        static async Task WriteJobStatusResponse(Stream stream, string jobId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                await WriteError(stream, 400, string.Empty, "invalid_request", "JobId 不能为空。", null, cancellationToken);
                return;
            }

            await WriteJson(stream, 200, UnityCliDispatcher.GetJobStatus(jobId), cancellationToken);
        }

        static bool TryDeserializeInvokeRequest(string json, out InvokeRequest request, out string errorMessage)
        {
            request = null;
            errorMessage = null;
            if (!UnityCliJson.TryDeserializeObject(json, out var root, out var parseError))
            {
                errorMessage = parseError;
                return false;
            }

            try
            {
                Dictionary<string, object> args = null;
                if (root.TryGetValue("args", out var argsValue) && argsValue != null)
                {
                    args = argsValue as Dictionary<string, object>;
                    if (args == null)
                    {
                        errorMessage = "args 必须是 JSON 对象。";
                        return false;
                    }
                }

                int? timeoutMs = null;
                if (root.TryGetValue("timeoutMs", out var timeoutValue) && timeoutValue != null)
                {
                    timeoutMs = Convert.ToInt32(timeoutValue, CultureInfo.InvariantCulture);
                }

                request = new InvokeRequest
                {
                    requestId = ReadString(root, "requestId"),
                    tool = ReadString(root, "tool"),
                    args = args != null ? new Dictionary<string, object>(args, StringComparer.Ordinal) : new Dictionary<string, object>(StringComparer.Ordinal),
                    timeoutMs = timeoutMs
                };

                if (string.IsNullOrWhiteSpace(request.tool))
                {
                    errorMessage = "tool 不能为空。";
                    request = null;
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                request = null;
                return false;
            }
        }

        static async Task WriteMethodNotAllowed(Stream stream, string allowedMethod, CancellationToken cancellationToken)
        {
            await WriteError(stream, 405, string.Empty, "method_not_allowed", $"仅允许 {allowedMethod} 请求。", allowedMethod, cancellationToken);
        }

        static async Task WriteError(Stream stream, int statusCode, string requestId, string code, string message, object details, CancellationToken cancellationToken)
        {
            var payload = ToolResult.Error(code, message, details).ToInvokeResponse(requestId ?? string.Empty);
            await WriteJson(stream, statusCode, payload, cancellationToken);
        }

        static async Task WriteJson(Stream stream, int statusCode, object payload, CancellationToken cancellationToken)
        {
            await WritePipeResponse(stream, statusCode, payload, cancellationToken);
        }

        static async Task WritePipeResponse(Stream stream, int statusCode, object payload, CancellationToken cancellationToken)
        {
            var response = new BridgePipeResponse
            {
                statusCode = statusCode,
                body = UnityCliJson.Serialize(payload)
            };

            try
            {
                await BridgePipeProtocol.WriteFrameAsync(stream, UnityCliJson.Serialize(response), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (IOException exception) when (IsDisconnectedPipeException(exception))
            {
            }
        }

        static bool IsDisconnectedPipeException(IOException exception)
        {
            if (exception == null)
            {
                return false;
            }

            var win32ErrorCode = exception.HResult & 0xFFFF;
            if (win32ErrorCode == 109 || win32ErrorCode == 232)
            {
                return true;
            }

            var message = exception.Message ?? string.Empty;
            return message.IndexOf("Pipe is broken", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("pipe has been ended", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("broken pipe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void SignalPipeShutdown(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                return;
            }

            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                client.Connect(100);
            }
            catch
            {
            }
        }

        static string BuildPipeName(string token)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            var hash = projectRoot.GetHashCode().ToString("X8", CultureInfo.InvariantCulture);
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            var uniqueSuffix = string.IsNullOrWhiteSpace(token)
                ? Guid.NewGuid().ToString("N")
                : token;
            return PipeNamePrefix + hash + "-" + pid + "-" + uniqueSuffix;
        }

        static string ReadString(IDictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        static BridgeEndpoint CloneEndpoint(BridgeEndpoint endpoint, bool includeToken)
        {
            if (endpoint == null)
            {
                return null;
            }

            return new BridgeEndpoint
            {
                protocolVersion = endpoint.protocolVersion,
                transport = endpoint.transport,
                pipeName = endpoint.pipeName,
                pid = endpoint.pid,
                instanceId = endpoint.instanceId,
                generation = endpoint.generation,
                token = includeToken ? endpoint.token : null,
                startedAt = endpoint.startedAt
            };
        }
    }
}
