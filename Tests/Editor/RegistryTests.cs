using System.IO;
using System.Linq;
using UnityCli.Editor.Core;
using NUnit.Framework;

namespace UnityCli.Tests.Editor
{
    [TestFixture]
    public class RegistryTests
    {
        [SetUp]
        public void SetUp()
        {
            UnityCliRegistry.Reload();
            UnityCliAllowlist.Reload();
        }

        [Test]
        public void DiscoverTools_ContainsBuiltInTools()
        {
            var discoveredTypes = UnityCliRegistry.DiscoverTools();
            var discoveredNames = discoveredTypes.Select(type => type.FullName).ToArray();

            CollectionAssert.Contains(discoveredNames, "UnityCli.Editor.Tools.BuiltIn.TestEchoTool");
            CollectionAssert.Contains(discoveredNames, "UnityCli.Editor.Tools.BuiltIn.TestSleepTool");
            CollectionAssert.Contains(discoveredNames, "UnityCli.Editor.Tools.BuiltIn.EditorStatusTool");
            CollectionAssert.Contains(discoveredNames, "Game.Editor.Cli.GenerateViewModelTool");
            Assert.IsTrue(UnityCliRegistry.TryGetTool("__test.echo", out _));
            Assert.IsTrue(UnityCliRegistry.TryGetTool("__test.sleep", out _));
            Assert.IsTrue(UnityCliRegistry.TryGetTool("editor.status", out _));
            Assert.IsTrue(UnityCliRegistry.TryGetTool("ui_generate_viewmodel", out _));
        }

        [Test]
        public void Allowlist_UsesDefaultFileAndContainsBuiltIns()
        {
            var activeAllowlistPath = UnityCliAllowlist.ActiveAllowlistPath;
            var enabledTools = UnityCliAllowlist.EnabledTools.ToArray();

            Assert.AreEqual("__default_allowlist.json", Path.GetFileName(activeAllowlistPath));
            CollectionAssert.Contains(enabledTools, "__test.echo");
            CollectionAssert.Contains(enabledTools, "__test.sleep");
            CollectionAssert.Contains(enabledTools, "editor.status");
        }

        [Test]
        public void GetAllowedDescriptors_ReturnsExpectedBuiltInSchemas()
        {
            var descriptors = UnityCliAllowlist.GetAllowedDescriptors();
            var ids = descriptors.Select(descriptor => descriptor.id).ToArray();
            var echoDescriptor = descriptors.Single(descriptor => descriptor.id == "__test.echo");
            var sleepDescriptor = descriptors.Single(descriptor => descriptor.id == "__test.sleep");
            var editorStatusDescriptor = descriptors.Single(descriptor => descriptor.id == "editor.status");

            CollectionAssert.Contains(ids, "__test.echo");
            CollectionAssert.Contains(ids, "__test.sleep");
            CollectionAssert.Contains(ids, "editor.status");

            Assert.AreEqual(1, echoDescriptor.parameters.Count);
            Assert.AreEqual("text", echoDescriptor.parameters[0].name);
            Assert.AreEqual("string", echoDescriptor.parameters[0].type);
            Assert.IsFalse(echoDescriptor.parameters[0].required);

            Assert.AreEqual(1, sleepDescriptor.parameters.Count);
            Assert.AreEqual("ms", sleepDescriptor.parameters[0].name);
            Assert.AreEqual("integer", sleepDescriptor.parameters[0].type);
            Assert.AreEqual(1000, sleepDescriptor.parameters[0].defaultValue);

            Assert.IsTrue(editorStatusDescriptor.parameters == null || editorStatusDescriptor.parameters.Count == 0);
        }
    }
}
