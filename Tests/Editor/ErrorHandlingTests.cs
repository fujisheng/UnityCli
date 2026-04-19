using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine;

namespace UnityCli.Tests.Editor
{
    [UnityCliTool("tests.throw", Description = "Throw from Execute", Mode = ToolMode.Both, Capabilities = ToolCapabilities.ReadOnly, Category = "test")]
    public sealed class ThrowingTestTool : IUnityCliTool
    {
        public string Id => "tests.throw";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Throw from Execute",
                mode = ToolMode.Both,
                capabilities = ToolCapabilities.ReadOnly,
                schemaVersion = "1.0"
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            throw new InvalidOperationException("throwing test tool");
        }
    }

    [UnityCliTool("tests.playonly", Description = "Play-only test tool", Mode = ToolMode.PlayOnly, Capabilities = ToolCapabilities.ReadOnly, Category = "test")]
    public sealed class PlayOnlyTestTool : IUnityCliTool
    {
        public string Id => "tests.playonly";

        public ToolDescriptor GetDescriptor()
        {
            return new ToolDescriptor
            {
                id = Id,
                description = "Play-only test tool",
                mode = ToolMode.PlayOnly,
                capabilities = ToolCapabilities.ReadOnly,
                schemaVersion = "1.0"
            };
        }

        public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
        {
            return ToolResult.Ok(new { reached = true });
        }
    }

    [TestFixture]
    public class ErrorHandlingTests
    {
        sealed class BridgeResponseSnapshot
        {
            public int StatusCode { get; set; }

            public string Body { get; set; }
        }

        [SetUp]
        public void SetUp()
        {
            UnityCliRegistry.Reload();
            UnityCliAllowlist.Reload();
            UnityCliServer.EnsureRunning();
        }

        [TearDown]
        public void TearDown()
        {
            UnityCliAllowlist.Reload();
        }

        [Test]
        public void BridgeRequest_WithoutToken_ReturnsUnauthorized()
        {
            var response = SendRequest("GET", "/ping", includeToken: false);

            Assert.AreEqual(401, response.StatusCode);
            StringAssert.Contains("\"code\":\"unauthorized\"", response.Body);
        }

        [Test]
        public void BridgeRequest_WithUnknownRoute_ReturnsRouteNotFound()
        {
            var response = SendRequest("GET", "/missing-route");

            Assert.AreEqual(404, response.StatusCode);
            StringAssert.Contains("\"code\":\"route_not_found\"", response.Body);
        }

        [Test]
        public void BridgeRequest_WithWrongMethod_ReturnsMethodNotAllowed()
        {
            var response = SendRequest("POST", "/ping");

            Assert.AreEqual(405, response.StatusCode);
            StringAssert.Contains("\"code\":\"method_not_allowed\"", response.Body);
        }

        [Test]
        public void Invoke_WithEmptyBody_ReturnsInvalidRequest()
        {
            var response = SendRequest("POST", "/invoke");

            Assert.AreEqual(400, response.StatusCode);
            StringAssert.Contains("\"code\":\"invalid_request\"", response.Body);
        }

        [Test]
        public void DescribeUnknownTool_ReturnsToolNotFound()
        {
            var response = SendRequest("GET", "/tools/nonexistent");

            Assert.AreEqual(404, response.StatusCode);
            StringAssert.Contains("\"code\":\"tool_not_found\"", response.Body);
        }

        [Test]
        public void Dispatch_WithPlayOnlyToolInEditMode_ReturnsWrongMode()
        {
            RegisterTestTool(new PlayOnlyTestTool(), ToolMode.PlayOnly);

            var response = UnityCliDispatcher.Dispatch(new InvokeRequest
            {
                requestId = "wrong-mode",
                tool = "tests.playonly",
                args = new Dictionary<string, object>(StringComparer.Ordinal)
            });

            Assert.IsFalse(response.ok);
            Assert.AreEqual("error", response.status);
            Assert.AreEqual("wrong_mode", response.error.code);
        }

        [Test]
        public void Dispatch_WithThrowingTool_ReturnsToolExecutionFailed()
        {
            RegisterTestTool(new ThrowingTestTool(), ToolMode.Both);
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: throwing test tool"));

            var response = UnityCliDispatcher.Dispatch(new InvokeRequest
            {
                requestId = "throwing-tool",
                tool = "tests.throw",
                args = new Dictionary<string, object>(StringComparer.Ordinal)
            });

            Assert.IsFalse(response.ok);
            Assert.AreEqual("error", response.status);
            Assert.AreEqual("tool_execution_failed", response.error.code);
            StringAssert.Contains("InvalidOperationException", Convert.ToString(response.error.details));
        }

        [UnityTest]
        public IEnumerator Enqueue_WhenQueueIsFull_ReturnsEditorBusy()
        {
            var acceptedTasks = new List<System.Threading.Tasks.Task<InvokeResponse>>();
            for (var index = 0; index < 100; index++)
            {
                acceptedTasks.Add(UnityCliDispatcher.Enqueue(new InvokeRequest
                {
                    requestId = $"queue-request-{index}",
                    tool = "__test.echo",
                    args = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["text"] = index.ToString()
                    }
                }));
            }

            var overflowTask = UnityCliDispatcher.Enqueue(new InvokeRequest
            {
                requestId = "queue-request-overflow",
                tool = "__test.echo",
                args = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["text"] = "overflow"
                }
            });

            Assert.IsTrue(overflowTask.IsCompleted);
            Assert.IsFalse(overflowTask.Result.ok);
            Assert.AreEqual("error", overflowTask.Result.status);
            Assert.AreEqual("editor_busy", overflowTask.Result.error.code);

            var deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline
                && (UnityCliDispatcherQueue.PendingCount > 0 || acceptedTasks.Any(task => !task.IsCompleted)))
            {
                PumpQueuedRequests();
                yield return null;
            }

            Assert.AreEqual(0, UnityCliDispatcherQueue.PendingCount);
            Assert.IsTrue(acceptedTasks.All(task => task.IsCompleted));
            Assert.IsTrue(acceptedTasks.All(task => task.Result.ok));
        }

        [Test]
        public void GetJobStatus_WithForeignSessionId_ReturnsBridgeReloaded()
        {
            var status = UnityCliJobManager.GetJobStatus("foreign-session:1");

            Assert.AreEqual("foreign-session:1", status.jobId);
            Assert.AreEqual("failed", status.status);
            Assert.IsNotNull(status.result);
            Assert.IsFalse(status.result.ok);
            Assert.AreEqual("bridge_reloaded", status.result.error.code);
        }

        [Test]
        public void GetJobStatus_WithUnknownCurrentSessionId_ReturnsToolExecutionFailed()
        {
            var response = UnityCliDispatcher.Dispatch(new InvokeRequest
            {
                requestId = "create-job-for-prefix",
                tool = "__test.sleep",
                args = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ms"] = 1
                }
            });
            Assert.AreEqual("pending", response.status);
            Assert.IsTrue(response.jobId.Contains(":"));

            var separatorIndex = response.jobId.IndexOf(':', StringComparison.Ordinal);
            var unknownJobId = response.jobId.Substring(0, separatorIndex) + ":999999";
            var status = UnityCliJobManager.GetJobStatus(unknownJobId);

            Assert.AreEqual(unknownJobId, status.jobId);
            Assert.AreEqual("failed", status.status);
            Assert.IsNotNull(status.result);
            Assert.IsFalse(status.result.ok);
            Assert.AreEqual("tool_execution_failed", status.result.error.code);
        }

        [Test]
        public void GetJobStatus_WithEmptyJobId_ReturnsToolExecutionFailed()
        {
            var status = UnityCliJobManager.GetJobStatus(string.Empty);

            Assert.AreEqual(string.Empty, status.jobId);
            Assert.AreEqual("failed", status.status);
            Assert.IsNotNull(status.result);
            Assert.IsFalse(status.result.ok);
            Assert.AreEqual("tool_execution_failed", status.result.error.code);
        }

        static void AllowTool(string toolId)
        {
            var field = typeof(UnityCliAllowlist).GetField("enabledTools", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "未找到 UnityCliAllowlist.enabledTools 字段。 ");

            var enabledTools = field.GetValue(null) as HashSet<string>;
            Assert.IsNotNull(enabledTools, "无法读取 allowlist 集合。 ");

            enabledTools.Add(toolId);
        }

        static void RegisterTestTool(IUnityCliTool tool, ToolMode mode)
        {
            AllowTool(tool.Id);

            var toolsField = typeof(UnityCliRegistry).GetField("registeredTools", BindingFlags.Static | BindingFlags.NonPublic);
            var descriptorsField = typeof(UnityCliRegistry).GetField("registeredDescriptors", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(toolsField, "未找到 UnityCliRegistry.registeredTools 字段。 ");
            Assert.IsNotNull(descriptorsField, "未找到 UnityCliRegistry.registeredDescriptors 字段。 ");

            var registeredTools = toolsField.GetValue(null) as Dictionary<string, IUnityCliTool>;
            var registeredDescriptors = descriptorsField.GetValue(null) as Dictionary<string, ToolDescriptor>;
            Assert.IsNotNull(registeredTools, "无法读取工具注册表。 ");
            Assert.IsNotNull(registeredDescriptors, "无法读取描述注册表。 ");

            var descriptor = tool.GetDescriptor() ?? new ToolDescriptor();
            descriptor.id = tool.Id;
            descriptor.mode = mode;
            descriptor.schemaVersion = string.IsNullOrWhiteSpace(descriptor.schemaVersion) ? "1.0" : descriptor.schemaVersion;

            registeredTools[tool.Id] = tool;
            registeredDescriptors[tool.Id] = descriptor;
        }

        static void PumpQueuedRequests()
        {
            var updateMethod = typeof(UnityCliDispatcher).GetMethod("HandleEditorUpdate", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(updateMethod, "未找到 UnityCliDispatcher.HandleEditorUpdate。 ");

            while (UnityCliDispatcherQueue.PendingCount > 0)
            {
                updateMethod.Invoke(null, null);
            }
        }

        static BridgeResponseSnapshot SendRequest(string method, string path, string body = null, bool includeToken = true)
        {
            Assert.IsTrue(UnityCliEndpointFile.TryRead(out var endpoint));

            using (var client = new NamedPipeClientStream(".", endpoint.pipeName, PipeDirection.InOut))
            {
                client.Connect(2000);

                var request = new BridgePipeRequest
                {
                    token = includeToken ? endpoint.token : string.Empty,
                    method = method,
                    path = NormalizePath(path),
                    body = body ?? string.Empty
                };

                BridgePipeProtocol.WriteFrame(client, SerializeJson(request));
                client.Flush();

                var rawResponse = BridgePipeProtocol.ReadFrame(client);
                Assert.IsFalse(string.IsNullOrWhiteSpace(rawResponse), "pipe 响应不应为空。 ");

                Assert.IsTrue(TryDeserializeJsonObject(rawResponse, out var root, out var error), $"无法解析 pipe 响应：{error}");
                return new BridgeResponseSnapshot
                {
                    StatusCode = ReadInt(root, "statusCode"),
                    Body = ReadString(root, "body")
                };
            }
        }

        static string SerializeJson(object value)
        {
            var jsonType = typeof(UnityCliServer).Assembly.GetType("UnityCli.Editor.Core.UnityCliJson");
            Assert.IsNotNull(jsonType, "未找到 UnityCliJson 类型。 ");

            var serializeMethod = jsonType.GetMethod("Serialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(serializeMethod, "未找到 UnityCliJson.Serialize。 ");

            return serializeMethod.Invoke(null, new[] { value }) as string;
        }

        static bool TryDeserializeJsonObject(string json, out Dictionary<string, object> root, out string error)
        {
            var jsonType = typeof(UnityCliServer).Assembly.GetType("UnityCli.Editor.Core.UnityCliJson");
            Assert.IsNotNull(jsonType, "未找到 UnityCliJson 类型。 ");

            var deserializeMethod = jsonType.GetMethod("TryDeserializeObject", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(deserializeMethod, "未找到 UnityCliJson.TryDeserializeObject。 ");

            var arguments = new object[] { json, null, null };
            var success = (bool)deserializeMethod.Invoke(null, arguments);
            root = arguments[1] as Dictionary<string, object>;
            error = arguments[2] as string;
            return success;
        }

        static string NormalizePath(string path)
        {
            return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        }

        static string ReadString(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        static int ReadInt(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                return 0;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
    }
}
