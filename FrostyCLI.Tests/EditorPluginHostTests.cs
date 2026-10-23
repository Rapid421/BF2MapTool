using System;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    // The shared host that BundleManagerHost and PathfindingHost both sit on. Its job is finding a
    // type across whatever the resolver has loaded, and failing usefully when the plugin is absent
    // -- because "plugin not installed" and "command is broken" must not look the same to a caller.
    public class EditorPluginHostTests
    {
        [Test]
        public void FindsATypeThatIsAlreadyLoaded()
        {
            Assert.IsNotNull(EditorPluginHost.FindType("FrostyCLI.Core.NavmeshObjPin"));
        }

        [Test]
        public void ReturnsNullForATypeNoLoadedAssemblyHas()
        {
            Assert.IsNull(EditorPluginHost.FindType("PathfindingPluginNamespace.NotARealType"));
        }

        [Test]
        public void RequireTypeNamesTheMissingTypeAndHowToSupplyIt()
        {
            // A bare TypeLoadException here reads as a CLI bug. It is almost always a missing
            // editor Plugins directory, and the message has to say so.
            string message = null;
            try { EditorPluginHost.RequireType("PathfindingPluginNamespace.NavMeshFormat.NavMesh"); }
            catch (InvalidOperationException ex) { message = ex.Message; }

            Assert.IsNotNull(message);
            Assert.IsTrue(message.Contains("PathfindingPluginNamespace.NavMeshFormat.NavMesh"));
            Assert.IsTrue(message.Contains("--frosty-plugins"));
        }

        [Test]
        public void RequireTypeReturnsTheTypeWhenItIsLoaded()
        {
            Assert.IsNotNull(EditorPluginHost.RequireType("FrostyCLI.Core.NavmeshObjPin"));
        }
    }
}
