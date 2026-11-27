using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using IOTLinkAgent.Agent.Loaders;
using IOTLinkAPI.Addons;
using Microsoft.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class AgentAssemblyReloadTests
    {
        [TestMethod]
        public void ReloadsLoadEachDistinctAddonBinaryOnlyOnce()
        {
            string name = "ReloadTest" + Guid.NewGuid().ToString("N");
            string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name)).FullName;
            string dll = Path.Combine(directory, name + ".dll");
            try
            {
                BuildAddon(dll, "OriginalAddon");
                byte[] original = File.ReadAllBytes(dll);
                var first = LoadAddon(dll);
                for (int reload = 0; reload < 20; reload++)
                {
                    var next = LoadAddon(dll);
                    Assert.AreNotSame(first, next);
                }
                Assert.AreEqual(1, LoadedCopies(name));

                BuildAddon(dll, "UpdatedAddon");
                var updated = LoadAddon(dll);
                Assert.AreEqual("UpdatedAddon", updated.GetType().Name);
                Assert.AreEqual(first.GetType().Assembly.FullName, updated.GetType().Assembly.FullName);
                Assert.AreSame(updated.GetType(), LoadAddon(dll).GetType());

                File.WriteAllBytes(dll, original);
                Assert.AreSame(first.GetType(), LoadAddon(dll).GetType());
                Assert.AreEqual(2, LoadedCopies(name));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static int LoadedCopies(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == name);
        }

        private static AgentAddon LoadAddon(string dll)
        {
            var info = new AddonInfo { AddonPath = Path.GetDirectoryName(dll), AddonFile = Path.GetFileName(dll) };
            Assert.IsTrue(AssemblyLoader.LoadAssemblyDLL(ref info));
            return info.AgentAddon;
        }

        private static void BuildAddon(string dll, string typeName)
        {
            using (var compiler = new CSharpCodeProvider())
            {
                var options = new CompilerParameters { OutputAssembly = dll };
                options.ReferencedAssemblies.Add(typeof(AgentAddon).Assembly.Location);
                var result = compiler.CompileAssemblyFromSource(options,
                    "public class " + typeName + " : IOTLinkAPI.Addons.AgentAddon { }");
                Assert.AreEqual(0, result.NativeCompilerReturnValue, string.Join(Environment.NewLine, result.Output.Cast<string>()));
            }
        }
    }
}
