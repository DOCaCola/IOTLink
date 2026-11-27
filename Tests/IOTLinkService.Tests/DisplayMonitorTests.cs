using System.Threading;
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
        public void AgentPushesDisplayAndHdrChangesWithoutAServiceRequest()
        {
            int reads = 0, publishes = 0, releases = 0;
            bool current = true;
            using var sent = new AutoResetEvent(false);
            DisplayInfoCache cache = null;
            cache = new DisplayInfoCache(() =>
            {
                if (++reads == 1)
                    cache.Invalidate(); // A notification during enumeration must survive.
                Volatile.Write(ref current, true);
                return new List<DisplayInfo> { new DisplayInfo { HdrEnabled = reads >= 3 } };
            }, () => Volatile.Read(ref current), () => releases++, displays =>
            {
                Interlocked.Increment(ref publishes);
                if (displays[0].HdrEnabled == true) sent.Set();
            });
            using (cache)
            {
                cache.SetEnabled(true);
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref publishes) == 2, 5000));
                for (int i = 0; i < 100; i++) cache.Refresh(false);
                Assert.AreEqual(2, reads);
                Assert.AreEqual(2, publishes);

                Volatile.Write(ref current, false); // HDR-only change, no display notification.
                Assert.IsTrue(sent.WaitOne(5000));
                Assert.AreEqual(3, reads);
                cache.Refresh(true); // Discovery refresh serves the cached snapshot.
                Assert.AreEqual(3, reads);
                Assert.AreEqual(4, publishes);

                cache.SetEnabled(false);
                cache.Invalidate();
                cache.Refresh(true);
                Assert.AreEqual(4, publishes);
                Assert.AreEqual(1, releases);
                cache.SetEnabled(true);
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref publishes) == 5, 5000));
            }
            cache.SetEnabled(true);
            cache.Refresh(true);
            Assert.AreEqual(5, publishes);
            Assert.AreEqual(2, releases);
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
        public void DisplayPathValuesMapToConnectorNamesAndClockwiseDegrees()
        {
            Assert.AreEqual("VGA", DisplayConfiguration.GetConnectorType(0));
            Assert.AreEqual("HDMI", DisplayConfiguration.GetConnectorType(5));
            Assert.AreEqual("DisplayPort", DisplayConfiguration.GetConnectorType(10));
            Assert.AreEqual("Embedded DisplayPort", DisplayConfiguration.GetConnectorType(11));
            Assert.AreEqual("DisplayPort USB Tunnel", DisplayConfiguration.GetConnectorType(18));
            Assert.AreEqual("Internal", DisplayConfiguration.GetConnectorType(0x80000000));
            Assert.AreEqual("Other", DisplayConfiguration.GetConnectorType(uint.MaxValue));
            Assert.IsNull(DisplayConfiguration.GetConnectorType(7));
            CollectionAssert.AreEqual(new int?[] { null, 0, 90, 180, 270, null },
                Enumerable.Range(0, 6).Select(value => DisplayConfiguration.GetRotationDegrees((uint)value)).ToArray());
        }

        [TestMethod]
        public void MirroredConnectorsAndRotationsAreMergedIndependently()
        {
            var sources = new Dictionary<string, DisplayInfo>();
            DisplayConfiguration.MergeTarget(sources, "desktop", new DisplayInfo { ConnectorType = "HDMI", Rotation = 0 });
            DisplayConfiguration.MergeTarget(sources, "desktop", new DisplayInfo { ConnectorType = "DisplayPort", Rotation = 0 });
            Assert.IsNull(sources["desktop"].ConnectorType);
            Assert.AreEqual(0, sources["desktop"].Rotation);
            DisplayConfiguration.MergeTarget(sources, "desktop", new DisplayInfo { ConnectorType = "HDMI", Rotation = 90 });
            Assert.IsNull(sources["desktop"].ConnectorType);
            Assert.IsNull(sources["desktop"].Rotation);
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
        public void AllDisplaysShareOneTypedSnapshotAndKeepTheirDiscoveryIds()
        {
            var rows = new JArray(
                new JObject { ["ScreenWidth"] = 1920, ["ScreenHeight"] = 1080, ["SignalWidth"] = 3840,
                    ["SignalHeight"] = 2160, ["RefreshRate"] = 60000d / 1001, ["MonitorName"] = "Living Room TV", ["HdrEnabled"] = true,
                    ["Availability"] = "0", ["ConnectorType"] = "HDMI", ["Rotation"] = 90 },
                new JObject { ["ScreenWidth"] = 1280, ["ScreenHeight"] = 720 },
                new JObject { ["HdrEnabled"] = false, ["Availability"] = "1" });
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var monitor = new DisplayMonitor();
                var items = monitor.OnAgentResponse(null, monitor.GetAgentRequests()[monitor.GetConfigKey()],
                    new JObject { ["requestData"] = rows }, "sample");
                Assert.IsTrue(items.All(item => item.Topic == "Stats/Display/State"));
                var state = items.Single(item => item.Value != null);
                var displays = (JArray)JObject.Parse((string)state.Value)["displays"];
                Assert.AreEqual(3, displays.Count);
                var tv = displays[0];
                Assert.AreEqual(1920, (int)tv["screenWidth"]);
                Assert.AreEqual(1080, (int)tv["screenHeight"]);
                Assert.AreEqual(3840, (int)tv["signalWidth"]);
                Assert.AreEqual(2160, (int)tv["signalHeight"]);
                Assert.AreEqual(60000d / 1001, (double)tv["refreshRate"]);
                Assert.AreEqual("Living Room TV", (string)tv["monitorName"]);
                Assert.AreEqual("HDMI", (string)tv["connectorType"]);
                Assert.AreEqual(90, (int)tv["rotation"]);
                Assert.IsTrue((bool)tv["hdrEnabled"]);
                Assert.IsFalse((bool)tv["primaryDisplay"]);
                Assert.AreEqual(JTokenType.Null, displays[1]["hdrEnabled"].Type);
                Assert.IsTrue((bool)displays[2]["primaryDisplay"]);

                var entities = items.Where(item => item.CreateDiscoveryOptions != null)
                    .ToDictionary(item => item.DiscoveryKey, item => item.CreateDiscoveryOptions(item));
                Assert.AreEqual(30, entities.Count);
                for (int i = 0; i < displays.Count; i++)
                {
                    foreach (var field in ((JObject)displays[i]).Properties())
                        Assert.AreEqual(1, entities.Values.Count(entity => entity.ValueTemplate.Contains($"displays[{i}].{field.Name} ")));
                }
                Assert.AreEqual("Screen_0_Width", entities["Screen_0_Width"].Id);
                Assert.AreEqual("Hz", entities["Screen_0_RefreshRate"].Unit);
                Assert.AreEqual(HomeAssistantComponent.BinarySensor, entities["Screen_0_HdrEnabled"].Component);
                Assert.AreEqual("True", entities["Screen_0_HdrEnabled"].PayloadOn);
                Assert.AreEqual("False", entities["Screen_2_PrimaryDisplay"].PayloadOff);
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }
    }
}
