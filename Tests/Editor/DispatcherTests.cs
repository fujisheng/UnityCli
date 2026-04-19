using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityCli.Tests.Editor
{
    [TestFixture]
    public class DispatcherTests
    {
        [SetUp]
        public void SetUp()
        {
            UnityCliRegistry.Reload();
            UnityCliAllowlist.Reload();
        }

        [UnityTest]
        public IEnumerator Enqueue_WithEchoTool_ReturnsCompletedResponse()
        {
            var request = new InvokeRequest
            {
                requestId = "echo-request",
                tool = "__test.echo",
                args = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["text"] = "hello dispatcher"
                }
            };
            var task = UnityCliDispatcher.Enqueue(request);

            yield return WaitForTask(task);

            var response = task.Result;
            Assert.AreEqual("echo-request", response.requestId);
            Assert.IsTrue(response.ok);
            Assert.AreEqual("completed", response.status);
            Assert.AreEqual("hello dispatcher", ReadMemberAsString(response.data, "echo"));
        }

        [UnityTest]
        public IEnumerator Enqueue_WithMissingTool_ReturnsToolNotFound()
        {
            var request = new InvokeRequest
            {
                requestId = "missing-tool",
                tool = "nonexistent.tool",
                args = new Dictionary<string, object>(StringComparer.Ordinal)
            };
            var task = UnityCliDispatcher.Enqueue(request);

            yield return WaitForTask(task);

            var response = task.Result;
            Assert.AreEqual("missing-tool", response.requestId);
            Assert.IsFalse(response.ok);
            Assert.AreEqual("error", response.status);
            Assert.IsNotNull(response.error);
            Assert.AreEqual("tool_not_found", response.error.code);
        }

        [UnityTest]
        public IEnumerator Enqueue_WithAsyncTool_CompletesThroughJobManager()
        {
            var request = new InvokeRequest
            {
                requestId = "sleep-request",
                tool = "__test.sleep",
                args = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ms"] = 100
                },
                timeoutMs = 5000
            };
            var task = UnityCliDispatcher.Enqueue(request);

            yield return WaitForTask(task);

            var initialResponse = task.Result;
            Assert.IsTrue(initialResponse.ok);
            Assert.AreEqual("pending", initialResponse.status);
            Assert.IsFalse(string.IsNullOrEmpty(initialResponse.jobId));

            JobStatus finalStatus = null;
            var deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                finalStatus = UnityCliDispatcher.GetJobStatus(initialResponse.jobId);
                if (finalStatus != null
                    && (string.Equals(finalStatus.status, "completed", StringComparison.Ordinal)
                        || string.Equals(finalStatus.status, "failed", StringComparison.Ordinal)))
                {
                    break;
                }

                yield return null;
            }

            Assert.IsNotNull(finalStatus);
            Assert.AreEqual("completed", finalStatus.status);
            Assert.IsNotNull(finalStatus.result);
            Assert.IsTrue(finalStatus.result.ok);
            Assert.AreEqual("completed", finalStatus.result.status);
            Assert.AreEqual(100, ReadMemberAsInt(finalStatus.result.data, "slept"));
        }

        static IEnumerator WaitForTask(Task<InvokeResponse> task, float timeoutSeconds = 5f)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsTrue(task.IsCompleted, "等待调度任务完成超时。 ");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
        }

        static int ReadMemberAsInt(object source, string name)
        {
            var value = ReadMember(source, name);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        static string ReadMemberAsString(object source, string name)
        {
            var value = ReadMember(source, name);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        static object ReadMember(object source, string name)
        {
            Assert.IsNotNull(source, $"缺少数据对象，无法读取成员 {name}。 ");

            if (source is IDictionary<string, object> dictionary && dictionary.TryGetValue(name, out var dictionaryValue))
            {
                return dictionaryValue;
            }

            var property = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property != null)
            {
                return property.GetValue(source, null);
            }

            var field = source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (field != null)
            {
                return field.GetValue(source);
            }

            Assert.Fail($"未找到成员 {name}。 ");
            return null;
        }
    }
}
