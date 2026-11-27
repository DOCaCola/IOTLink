using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using IOTLinkAddon.Agent;
using IOTLinkAddon.Service.Monitors;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.HomeAssistant;
using IOTLinkAPI.Platform.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class DisplayMonitorTests
    {
        [TestMethod]
        public void PollsReuseSnapshotUntilDisplayNotificationOrGraphicsConfigurationChange()
        {
            int reads = 0, releases = 0;
            bool current = true;
            DisplayInfoCache cache = null;
            cache = new DisplayInfoCache(() =>
            {
                if (++reads == 1)
                    cache.Invalidate(); // A change delivered during enumeration must survive.
                current = true;
                return new List<DisplayInfo>();
            }, () => current, () => releases++);

            using (cache)
            {
                var first = cache.Get();
                var second = cache.Get();
                Assert.AreNotSame(first, second);
                for (int i = 0; i < 100; i++)
                    Assert.AreSame(second, cache.Get());
                current = false; // HDR-only change without a resolution notification.
                Assert.AreNotSame(second, cache.Get());
                cache.Invalidate();
                cache.Invalidate();
                cache.Get();
                Assert.AreEqual(4, reads);
            }
            cache.Dispose();
            Assert.AreEqual(1, releases);
            Assert.IsNull(cache.Get());
            Assert.AreEqual(4, reads);
        }

        [TestMethod]
        public void DesktopAndSignalModesUseTheirOwnIndexesOnLegacyAndVirtualAwarePaths()
        {
            var modes = new[] {
                new DisplayConfiguration.ModeInfo { DesktopWidth = 1920, DesktopHeight = 1080 },
                new DisplayConfiguration.ModeInfo { ActiveWidth = 3840, ActiveHeight = 2160 }
            };
            Assert.AreEqual(1920, DisplayConfiguration.GetMode(modes, 2, 0, 0).DesktopWidth);
            Assert.AreEqual(3840u, DisplayConfiguration.GetMode(modes, 2, 0, 1).ActiveWidth);
            Assert.AreEqual(1080, DisplayConfiguration.GetMode(modes, 2, 0x8, 0xffff).DesktopHeight);
            Assert.AreEqual(2160u, DisplayConfiguration.GetMode(modes, 2, 0x8, 0x10000).ActiveHeight);
            Assert.AreEqual(0u, DisplayConfiguration.GetMode(modes, 2, 0x8, 0xffff0000).ActiveHeight);
        }

        [TestMethod]
        public void MirroredTargetsPublishOnlyAgreedSignalDimensionsAndColorState()
        {
            var sources = new Dictionary<string, DisplayInfo>();
            var first = new DisplayInfo { MonitorName = "TV", SignalWidth = 3840, SignalHeight = 2160, RefreshRate = 60, HdrEnabled = true };
            var second = new DisplayInfo { MonitorName = "Projector", SignalWidth = 3840, SignalHeight = 1600, RefreshRate = 60, HdrEnabled = false };
            DisplayConfiguration.MergeTarget(sources, "source1", first);
            DisplayConfiguration.MergeTarget(sources, "source1", second);
            DisplayConfiguration.MergeTarget(sources, "source1", second);
            var merged = sources["source1"];
            Assert.AreEqual(60d, merged.RefreshRate);
            Assert.IsNull(merged.MonitorName);
            Assert.IsNull(merged.HdrEnabled);
            Assert.IsNull(merged.SignalWidth);
            Assert.IsNull(merged.SignalHeight);
        }

        [TestMethod]
        public void AgentResponsePreservesDesktopAndSignalSizesAndDiscoversPerDisplayTopics()
        {
            var rows = new JArray(
                new JObject { ["ScreenWidth"] = 1920, ["ScreenHeight"] = 1080, ["SignalWidth"] = 3840,
                    ["SignalHeight"] = 2160, ["RefreshRate"] = 60000d / 1001, ["MonitorName"] = "Living Room TV", ["HdrEnabled"] = true },
                new JObject { ["ScreenWidth"] = 1280, ["ScreenHeight"] = 720 },
                new JObject { ["HdrEnabled"] = false });
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var monitor = new DisplayMonitor();
                var items = monitor.OnAgentResponse(null, monitor.GetAgentRequests()[monitor.GetConfigKey()],
                    new JObject { ["requestData"] = rows }, "sample").ToDictionary(item => item.Topic);
                const string tv = "Stats/Display/0/", legacy = "Stats/Display/1/";
                Assert.AreEqual(1920, items[tv + "ScreenWidth"].Value);
                Assert.AreEqual(1080, items[tv + "ScreenHeight"].Value);
                Assert.AreEqual("3840", items[tv + "SignalWidth"].Value);
                Assert.AreEqual("2160", items[tv + "SignalHeight"].Value);
                Assert.AreEqual("Living Room TV", items[tv + "MonitorName"].Value);
                var rate = items[tv + "RefreshRate"];
                Assert.AreEqual("59.94", rate.Value);
                Assert.AreEqual("Hz", rate.CreateDiscoveryOptions(rate).Unit);
                var hdr = items[tv + "HdrEnabled"];
                var discovery = hdr.CreateDiscoveryOptions(hdr);
                Assert.AreEqual(HomeAssistantComponent.BinarySensor, discovery.Component);
                Assert.AreEqual(discovery.PayloadOn, hdr.Value);
                Assert.AreEqual(discovery.PayloadOff, items["Stats/Display/2/HdrEnabled"].Value);
                foreach (string suffix in new[] { "SignalWidth", "SignalHeight", "MonitorName", "RefreshRate", "HdrEnabled" })
                    Assert.AreEqual("None", items[legacy + suffix].Value);
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }
    }
}
