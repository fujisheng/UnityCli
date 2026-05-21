using System.Collections.Generic;
using System.Linq;
using UnityCli.Editor.Core;
using NUnit.Framework;

namespace UnityCli.Tests.Editor
{
    [TestFixture]
    public class ScreenshotCaptureToolTests
    {
        [SetUp]
        public void SetUp()
        {
            UnityCliRegistry.Reload();
            UnityCliAllowlist.Reload();
        }

        [Test]
        public void DiscoverTools_ContainsScreenshotCaptureTool()
        {
            var discoveredNames = UnityCliRegistry.DiscoverTools()
                .Select(type => type.FullName)
                .ToArray();

            CollectionAssert.Contains(discoveredNames, "UnityCli.Editor.Tools.ScreenshotCaptureTool");
            Assert.IsTrue(UnityCliRegistry.TryGetTool("screenshot.capture", out var tool));
            Assert.IsNotNull(tool);
        }

        [Test]
        public void Execute_WithoutPath_ReturnsMissingParameter()
        {
            Assert.IsTrue(UnityCliRegistry.TryGetTool("screenshot.capture", out var tool));

            var result = tool.Execute(new Dictionary<string, object>
            {
                ["mode"] = "camera"
            }, ToolContext.CreateCurrent());

            Assert.IsFalse(result.IsOk);
            Assert.IsNotNull(result.ErrorInfo);
            Assert.AreEqual("missing_parameter", result.ErrorInfo.code);
            StringAssert.Contains("path", result.ErrorInfo.message);
        }

        [Test]
        public void Execute_UnsupportedMode_ReturnsInvalidParameter()
        {
            Assert.IsTrue(UnityCliRegistry.TryGetTool("screenshot.capture", out var tool));

            var result = tool.Execute(new Dictionary<string, object>
            {
                ["mode"] = "minimap",
                ["path"] = "Temp/UnityCliTests/minimap.png"
            }, ToolContext.CreateCurrent());

            Assert.IsFalse(result.IsOk);
            Assert.IsNotNull(result.ErrorInfo);
            Assert.AreEqual("invalid_parameter", result.ErrorInfo.code);
            StringAssert.Contains("mode", result.ErrorInfo.message);
        }

        [Test]
        public void Execute_UnsupportedFormat_ReturnsInvalidParameter()
        {
            Assert.IsTrue(UnityCliRegistry.TryGetTool("screenshot.capture", out var tool));

            var result = tool.Execute(new Dictionary<string, object>
            {
                ["mode"] = "camera",
                ["path"] = "Temp/UnityCliTests/camera.bmp",
                ["format"] = "bmp"
            }, ToolContext.CreateCurrent());

            Assert.IsFalse(result.IsOk);
            Assert.IsNotNull(result.ErrorInfo);
            Assert.AreEqual("invalid_parameter", result.ErrorInfo.code);
            StringAssert.Contains("format", result.ErrorInfo.message);
        }
    }
}
