using System;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using IOTLinkAPI.Platform.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class BootTimeCacheTests
    {
        [TestMethod]
        public void FailedReadIsRetriedAndSuccessfulReadIsSharedAcrossPolls()
        {
            var expected = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.FromHours(2));
            int reads = 0;
            var cache = new BootTimeCache(() =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                    throw new ManagementException("WMI unavailable");

                return expected;
            });

            Assert.ThrowsException<ManagementException>(() => cache.Get());
            Parallel.For(0, 32, _ => Assert.IsTrue(expected.EqualsExact(cache.Get())));
            Assert.AreEqual(2, reads);
        }
    }
}
