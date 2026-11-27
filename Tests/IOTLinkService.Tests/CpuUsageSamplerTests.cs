using IOTLinkAPI.Platform.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static IOTLinkAPI.Platform.Windows.CpuUsageSampler;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class CpuUsageSamplerTests
    {
        [TestMethod]
        public void UnequalGroupsAreWeightedByElapsedProcessorTime()
        {
            var sampler = new CpuUsageSampler();
            var baseline = new[] { new CpuTimes(400, 1000), new CpuTimes(50, 100) };
            Assert.AreEqual(0d, sampler.Update(SumGroups(2, group => baseline[group]), 80));

            // The 64-CPU group was busy and the 16-CPU group was idle.
            var next = new[] { new CpuTimes(400, 1640), new CpuTimes(210, 260) };
            var sample = SumGroups(2, group => next[group]);
            Assert.AreEqual(80d, sampler.Update(sample, 80));
            Assert.AreEqual(80d, sampler.Update(sample, 80));
        }

        [TestMethod]
        public void NewProcessorCountStartsANewSamplingInterval()
        {
            var sampler = new CpuUsageSampler();
            sampler.Update(new CpuTimes(100, 200), 64);
            Assert.AreEqual(0d, sampler.Update(new CpuTimes(1000, 2000), 96));
            Assert.AreEqual(25d, sampler.Update(new CpuTimes(1075, 2100), 96));
        }

        [TestMethod]
        public void NativeGroupReaderReturnsProcessorTimes()
        {
            var times = CpuUsageSampler.ReadAllGroups();
            Assert.IsTrue(times.Total > 0 && times.Idle >= 0 && times.Idle <= times.Total);
        }
    }
}
