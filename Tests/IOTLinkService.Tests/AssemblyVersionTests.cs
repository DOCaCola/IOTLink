using System;
using IOTLinkAPI.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class AssemblyVersionTests
    {
        [TestMethod]
        public void AddonVersionBoundsUseTheFirstDifferentComponent()
        {
            var current = Version.Parse(AssemblyHelper.GetCurrentVersion());
            string older = $"{current.Major - 1}.{current.Minor + 1}.0";
            string newer = $"{current.Major + 1}.0.0";
            Assert.IsTrue(AssemblyHelper.CheckAssemblyVersion(older, ""));
            Assert.IsTrue(AssemblyHelper.CheckAssemblyVersion(older, newer));
            Assert.IsTrue(AssemblyHelper.CheckAssemblyVersion(current.ToString(), current.ToString()));
            Assert.IsFalse(AssemblyHelper.CheckAssemblyVersion("", older));
            Assert.IsFalse(AssemblyHelper.CheckAssemblyVersion(newer, ""));
        }
    }
}
