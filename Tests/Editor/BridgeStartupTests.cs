using System;
using System.IO;
using UnityCli.Editor.Core;
using UnityCli.Protocol;
using NUnit.Framework;

namespace UnityCli.Tests.Editor
{
    [TestFixture]
    public class BridgeStartupTests
    {
        [SetUp]
        public void SetUp()
        {
            UnityCliServer.EnsureRunning();
        }

        [Test]
        public void EnsureRunning_WritesReadableEndpointFile()
        {
            Assert.IsTrue(UnityCliServer.IsRunning);
            Assert.IsTrue(File.Exists(UnityCliEndpointFile.FilePath), $"未找到 endpoint 文件：{UnityCliEndpointFile.FilePath}");
            Assert.IsTrue(UnityCliEndpointFile.TryRead(out var endpoint), "endpoint.json 应可被读取。 ");
            Assert.IsNotNull(endpoint);
            Assert.AreEqual(UnityCliEndpointFile.ProtocolVersion, endpoint.protocolVersion);
            Assert.AreEqual(BridgeEndpoint.TransportNamedPipe, endpoint.transport);
            Assert.IsFalse(string.IsNullOrEmpty(endpoint.pipeName));
            Assert.Greater(endpoint.pid, 0);
            Assert.IsFalse(string.IsNullOrEmpty(endpoint.instanceId));
            Assert.Greater(endpoint.generation, 0);
            Assert.IsFalse(string.IsNullOrEmpty(endpoint.token));
            Assert.Greater(endpoint.startedAt, DateTime.MinValue);
        }

        [Test]
        public void CurrentEndpoint_MatchesEndpointFileAndOmitsToken()
        {
            Assert.IsTrue(UnityCliEndpointFile.TryRead(out var fileEndpoint));
            var currentEndpoint = UnityCliServer.CurrentEndpoint;

            Assert.IsNotNull(currentEndpoint);
            Assert.AreEqual(fileEndpoint.protocolVersion, currentEndpoint.protocolVersion);
            Assert.AreEqual(fileEndpoint.transport, currentEndpoint.transport);
            Assert.AreEqual(fileEndpoint.pipeName, currentEndpoint.pipeName);
            Assert.AreEqual(fileEndpoint.pid, currentEndpoint.pid);
            Assert.AreEqual(fileEndpoint.instanceId, currentEndpoint.instanceId);
            Assert.AreEqual(fileEndpoint.generation, currentEndpoint.generation);
            Assert.AreEqual(fileEndpoint.startedAt, currentEndpoint.startedAt);
            Assert.IsTrue(string.IsNullOrEmpty(currentEndpoint.token), "CurrentEndpoint 不应暴露 token。 ");
            Assert.IsFalse(string.IsNullOrEmpty(fileEndpoint.token), "endpoint.json 应保留 token 供 CLI 发现。 ");
        }

        [Test]
        public void EnsureRunning_WhenAlreadyRunning_DoesNotChangeEndpointGeneration()
        {
            Assert.IsTrue(UnityCliEndpointFile.TryRead(out var beforeEndpoint));

            UnityCliServer.EnsureRunning();

            Assert.IsTrue(UnityCliEndpointFile.TryRead(out var afterEndpoint));
            Assert.AreEqual(beforeEndpoint.instanceId, afterEndpoint.instanceId);
            Assert.AreEqual(beforeEndpoint.generation, afterEndpoint.generation);
            Assert.AreEqual(beforeEndpoint.transport, afterEndpoint.transport);
            Assert.AreEqual(beforeEndpoint.pipeName, afterEndpoint.pipeName);
            Assert.AreEqual(beforeEndpoint.token, afterEndpoint.token);
        }
    }
}
