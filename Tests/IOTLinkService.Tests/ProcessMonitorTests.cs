using System;
using System.Collections.Generic;
using System.Management;
using IOTLinkAddon.Service;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class ProcessMonitorTests
    {
        [TestMethod]
        public void PerformanceSampleUsesThePrivateWorkingSetInTheEvent()
        {
            const ulong bytes = (5UL << 30) + 17;
            using (var processClass = new ManagementClass("Win32_PerfFormattedData_PerfProc_Process"))
            using (var sample = processClass.CreateInstance())
            {
                sample["WorkingSetPrivate"] = bytes;
                sample["PercentProcessorTime"] = 100UL;
                var process = new ProcessInfo();
                ProcessEventManager.ApplyPerformanceSample(process, sample);
                Assert.AreEqual((long)bytes, process.MemoryUsed);
                Assert.AreEqual(Math.Round(100.0 / Environment.ProcessorCount, 2), process.ProcessorUsage);
            }
        }

        [TestMethod]
        public void WatchersFollowTheEnabledMonitorsAcrossReloads()
        {
            var events = ProcessEventManager.GetInstance();
            var service = new ProcessMonitorService();
            try
            {
                Assert.IsFalse(events.IsMonitoring);
                service.ApplyConfiguration(Config(true));
                Assert.IsFalse(events.IsMonitoring);
                service.ApplyConfiguration(Config(true, true, false));
                Assert.IsTrue(events.IsMonitoring);
                service.ApplyConfiguration(Config(true, true, true));
                Assert.IsTrue(events.IsMonitoring);
                service.ApplyConfiguration(Config(true, false, false));
                Assert.IsFalse(events.IsMonitoring);
                service.ApplyConfiguration(Config(true, false, true));
                Assert.IsTrue(events.IsMonitoring);
                service.ApplyConfiguration(Config(false, true));
                Assert.IsFalse(events.IsMonitoring);
            }
            finally
            {
                service.ApplyConfiguration(null);
            }
        }

        private static Configuration Config(bool enabled, params bool[] monitorFlags)
        {
            var monitors = new Dictionary<object, object>();
            foreach (bool flag in monitorFlags)
                monitors.Add("app" + monitors.Count, new Dictionary<object, object>
                {
                    ["configs"] = new Dictionary<object, object> { ["enabled"] = flag, ["interval"] = 60 }
                });

            return Configuration.BuildConfiguration(null, new Dictionary<object, object>
            {
                ["enabled"] = enabled,
                ["monitors"] = monitors
            });
        }
    }
}
