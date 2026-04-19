using System.Collections.Generic;
using System.Linq;
using UnityCli.Editor.Core;
using NUnit.Framework;

namespace UnityCli.Tests.Editor
{
    [TestFixture]
    public class SceneToolTests
    {
        [SetUp]
        public void SetUp()
        {
            UnityCliRegistry.Reload();
            UnityCliAllowlist.Reload();
        }

        [Test]
        public void DiscoverTools_ContainsSceneTool()
        {
            var discoveredNames = UnityCliRegistry.DiscoverTools()
                .Select(type => type.FullName)
                .ToArray();

            CollectionAssert.Contains(discoveredNames, "UnityCli.Editor.Tools.SceneTool");
            Assert.IsTrue(UnityCliRegistry.TryGetTool("scene", out var tool));
            Assert.IsNotNull(tool);
        }

        [Test]
        public void Execute_WithoutAction_ReturnsMissingParameter()
        {
            Assert.IsTrue(UnityCliRegistry.TryGetTool("scene", out var tool));

            var result = tool.Execute(new Dictionary<string, object>(), ToolContext.CreateCurrent());

            Assert.IsFalse(result.IsOk);
            Assert.IsNotNull(result.ErrorInfo);
            Assert.AreEqual("missing_parameter", result.ErrorInfo.code);
            StringAssert.Contains("action", result.ErrorInfo.message);
        }
    }
}
