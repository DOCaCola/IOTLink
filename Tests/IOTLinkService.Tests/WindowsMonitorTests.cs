using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IOTLinkAddon.Service;
using IOTLinkAddon.Service.Monitors;
using IOTLinkAPI.Addons;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.Events;
using IOTLinkAPI.Platform.Events.MQTT;
using IOTLinkAPI.Platform.HomeAssistant;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class WindowsMonitorTests
    {
        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void MemoryPublishesOneRetainedSnapshotPerEligibleSample(bool cacheable)
        {
            const long mib = 1024 * 1024;
            var sample = new IOTLinkAPI.Platform.MemoryInfo { TotalPhysical = 8 * mib, AvailPhysical = 2 * mib, MemoryLoad = 75 };
            int reads = 0;
            var memory = new MemoryMonitor(() => { Interlocked.Increment(ref reads); return sample; });
            string directory = Directory.CreateTempSubdirectory().FullName;
            var service = new WindowsMonitorService(memory);
            var manager = new RecordingManager();
            try
            {
                File.WriteAllText(Path.Combine(directory, "config.yaml"), $"""
                    enabled: true
                    monitors:
                      Memory:
                        enabled: true
                        interval: 1
                        cacheable: {cacheable.ToString().ToLowerInvariant()}
                    """);
                service.SetCurrentPath(directory);
                service.SetAddonInfo(new AddonInfo { AddonId = "WindowsMonitor" });
                service.Init(manager);
                int discoveryReads = reads;
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref reads) >= discoveryReads + 2, 5000));
                service.Raise_OnMQTTDisconnected(this, null);
                int polls = reads - discoveryReads;
                Assert.AreEqual(cacheable ? 1 : polls, manager.Values);
                Assert.AreEqual(manager.Values, manager.Retained);
                CollectionAssert.AreEqual(new[] { "Stats/Memory/State" }, manager.LastValues.Keys.ToArray());
                var state = JObject.Parse(manager.LastValues["Stats/Memory/State"]);
                Assert.AreEqual(75, (int)state["usage"]);
                Assert.AreEqual(2d, (double)state["available"]);
                Assert.AreEqual(6d, (double)state["used"]);
                Assert.AreEqual(8d, (double)state["total"]);
                Assert.AreEqual("MB", (string)state["unit"]);
                Assert.AreEqual(4, manager.Discoveries);
            }
            finally
            {
                service.Raise_OnMQTTDisconnected(this, null);
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void CachedDisplayPublishesHdrChangesAndOnlyRequestsSnapshotsOnRefresh()
        {
            string directory = Directory.CreateTempSubdirectory().FullName;
            string path = Path.Combine(directory, "config.yaml");
            var service = new WindowsMonitorService();
            var manager = new RecordingManager();
            try
            {
                File.WriteAllText(path, """
                    enabled: true
                    monitors:
                      Display-Info: { enabled: true, interval: 1, cacheable: false }
                    """);
                service.SetCurrentPath(directory);
                service.SetAddonInfo(new AddonInfo { AddonId = "WindowsMonitor" });
                service.Init(manager);
                Assert.AreEqual(1, manager.Requests.Count);
                Assert.IsFalse(SpinWait.SpinUntil(() => manager.Requests.Count > 1, 2200));
                var display = new JObject { ["HdrEnabled"] = false };
                var reply = new AgentAddonResponseEventArgs { Username = "test", Data = new JObject {
                    ["requestType"] = 1, ["requestData"] = new JArray(display) } };
                service.Raise_OnAgentResponse(this, reply);
                const string topic = "Stats/Display/State";
                Assert.AreEqual(1, manager.Values);
                Assert.AreEqual(10, manager.Discoveries);
                Assert.IsFalse((bool)JObject.Parse(manager.LastValues[topic])["displays"][0]["hdrEnabled"]);
                display["HdrEnabled"] = true;
                service.Raise_OnAgentResponse(this, reply);
                service.Raise_OnAgentResponse(this, reply);
                Assert.IsTrue((bool)JObject.Parse(manager.LastValues[topic])["displays"][0]["hdrEnabled"]);
                Assert.AreEqual(2, manager.Values);
                ((JArray)reply.Data.requestData).Clear();
                service.Raise_OnAgentResponse(this, reply);
                Assert.AreEqual(0, ((JArray)JObject.Parse(manager.LastValues[topic])["displays"]).Count);
                Assert.AreEqual(3, manager.Retained);
                CollectionAssert.AreEqual(new[] { topic }, manager.LastValues.Keys.ToArray());
                service.Raise_OnMQTTDisconnected(this, null);
                display["HdrEnabled"] = false;
                service.Raise_OnAgentResponse(this, reply);
                Assert.AreEqual(3, manager.Values);
                service.Raise_OnMQTTConnected(this, null);
                service.Raise_OnRefreshRequested(this, EventArgs.Empty);
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, manager.Requests.ToArray());
            }
            finally
            {
                service.Raise_OnMQTTDisconnected(this, null);
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void DiskPollReadsCapacityOncePerFixedDriveAndPreservesQuotaLimitedFreeSpace()
        {
            const long gib = 1L << 30;
            var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToArray();
            Assert.IsTrue(fixedDrives.Length > 0);
            var reads = new List<string>();
            long free = 33 * gib + gib / 2;
            var monitor = new StorageMonitor((string root, out long available, out long total, out long totalFree) =>
            {
                reads.Add(root);
                available = 7 * gib;
                total = 100 * gib;
                totalFree = free;
                return true;
            }, () => fixedDrives);
            var formats = Configuration.BuildConfiguration("formats", new Dictionary<object, object> { ["diskFormat"] = "GB:1" });
            monitor.Init(new MonitorSettings(null, formats));
            monitor.GetDiscoveryItems(null, 10);
            Assert.AreEqual(0, reads.Count);

            for (int poll = 0; poll < 2; poll++)
            {
                reads.Clear();
                var items = monitor.GetMonitorItems(null, 10);
                CollectionAssert.AreEquivalent(fixedDrives.Select(d => d.Name).ToArray(), reads);
                Assert.IsTrue(items.All(i => i.Topic == "Stats/HardDrive/State"));
                var state = items.Single(i => i.Value != null);
                Assert.AreEqual(MonitorItemType.TYPE_RETAINED_STATE, state.Type);
                var snapshot = JObject.Parse((string)state.Value);
                Assert.AreEqual("GB", (string)snapshot["unit"]);
                var drives = (JObject)snapshot["drives"];
                Assert.AreEqual(fixedDrives.Length, drives.Count);
                foreach (var d in fixedDrives)
                {
                    var row = drives[d.Name[..1]];
                    Assert.AreEqual(100d, (double)row["totalSize"]);
                    Assert.AreEqual(7d, (double)row["availableFreeSpace"]);
                    Assert.AreEqual(33.5 + poll, (double)row["totalFreeSpace"]);
                    Assert.AreEqual(66.5 - poll, (double)row["usedSpace"]);
                    Assert.AreEqual(66 - poll, (int)row["driveUsage"]);
                    Assert.AreEqual(d.DriveFormat, (string)row["driveFormat"]);
                    Assert.AreEqual(d.VolumeLabel, (string)row["volumeLabel"]);
                }
                free += gib;
            }
            fixedDrives = Array.Empty<DriveInfo>();
            var empty = monitor.GetMonitorItems(null, 10).Single();
            Assert.AreEqual(MonitorItemType.TYPE_RETAINED_STATE, empty.Type);
            Assert.AreEqual(0, ((JObject)JObject.Parse((string)empty.Value)["drives"]).Count);
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task DiskDiscoveryReadsEachFieldFromTheRetainedDriveSnapshot()
        {
            var monitor = new StorageMonitor();
            monitor.Init(new MonitorSettings(null, null));
            var items = monitor.GetMonitorItems(null, 10);
            var state = items.Single(i => i.Value != null);
            var drives = (JObject)JObject.Parse((string)state.Value)["drives"];
            Assert.IsTrue(drives.Count > 0);
            var entities = items.Where(i => i.CreateDiscoveryOptions != null).Select(i => i.CreateDiscoveryOptions(i)).ToDictionary(o => o.Id);
            foreach (var letter in drives.Properties().Select(p => p.Name))
            {
                foreach (var (suffix, field, unit) in new[] {
                    ("TotalStorage", "totalSize", "GB"), ("AvailableFreeSpace", "availableFreeSpace", "GB"),
                    ("TotalFreeSpace", "totalFreeSpace", "GB"), ("UsedSpace", "usedSpace", "GB"),
                    ("DriveUsage", "driveUsage", "%"), ("DriveFormat", "driveFormat", null), ("VolumeLabel", "volumeLabel", null) })
                {
                    var entity = entities[letter + "_" + suffix];
                    Assert.AreEqual(unit, entity.Unit);
                    Assert.AreEqual("{{ value_json.drives.get('" + letter + "', {}).get('" + field + "') }}", entity.ValueTemplate);
                }
            }
            await MqttTopicTests.AssertRetainedSnapshot(items, "windows-monitor/stats/hard-drive/state", entities.Count);
        }

        [TestMethod]
        public void CurrentUserFallsBackToSystemWhenTheConsoleOrSessionIsUnavailable()
        {
            // Windows also uses uint.MaxValue to mean the caller's session in WTS queries.
            Assert.AreEqual("SYSTEM", IOTLinkAPI.Platform.Windows.WindowsAPI.GetConsoleUsername(uint.MaxValue));
            Assert.AreEqual("SYSTEM", IOTLinkAPI.Platform.Windows.WindowsAPI.GetConsoleUsername(uint.MaxValue - 1));
        }

        [TestMethod]
        public void DiscoveryBuildsOncePerTopicUntilRefreshOrReload()
        {
            string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
            string path = Path.Combine(directory, "config.yaml");
            const string config = "enabled: true\nmonitors:\n  Probe:\n    enabled: true\n    interval: 1\n";
            var monitor = new DiscoveryProbe();
            var service = new WindowsMonitorService(monitor);
            var manager = new RecordingManager();
            var configs = ConfigurationManager.GetInstance();
            using (var reload = new AutoResetEvent(false))
            {
                ConfigurationManager.ConfigReloadedHandler onReload = (s, e) => reload.Set();
                try
                {
                    File.WriteAllText(path, config + "    discoveryOptions:\n      First: { name: Custom }\n");
                    service.SetCurrentPath(directory);
                    service.SetAddonInfo(new AddonInfo { AddonId = "WindowsMonitor" });
                    service.Init(manager);
                    configs.SetReloadHandler(path, onReload);

                    Assert.AreEqual(1, monitor.Builds);
                    var initial = manager.LastDiscovery["Stats/First"];
                    Assert.AreEqual("Custom", initial.Name);
                    Assert.IsTrue(SpinWait.SpinUntil(() => manager.Values >= 4, 5000), "Expected two polls with both topics");
                    Assert.AreEqual(2, monitor.Builds);
                    Assert.AreEqual(2, manager.Discoveries);
                    Assert.AreEqual("Second", manager.LastDiscovery["Stats/Second"].Id);

                    service.Raise_OnRefreshRequested(this, EventArgs.Empty);
                    Assert.AreEqual(4, monitor.Builds);
                    Assert.AreEqual(4, manager.Discoveries);
                    Assert.AreNotSame(initial, manager.LastDiscovery["Stats/First"]);
                    Assert.AreEqual("Custom", manager.LastDiscovery["Stats/First"].Name);

                    File.WriteAllText(path, config);
                    Assert.IsTrue(reload.WaitOne(TimeSpan.FromSeconds(15)));
                    Assert.AreEqual(6, monitor.Builds);
                    Assert.AreEqual(6, manager.Discoveries);
                    Assert.AreEqual("First", manager.LastDiscovery["Stats/First"].Name);
                }
                finally
                {
                    service.Raise_OnMQTTDisconnected(this, null);
                    configs.RemoveReloadHandler(path, onReload);
                    Directory.Delete(directory, true);
                }
            }
        }

        private sealed class DiscoveryProbe : BaseMonitor
        {
            public int Builds;
            private int _polls;

            public override string GetConfigKey() => "Probe";

            public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
            {
                var items = new List<MonitorItem> { Sensor("First") };
                if (++_polls > 1)
                    items.Add(Sensor("Second"));
                return items;
            }

            private MonitorItem Sensor(string id) => new MonitorItem(id)
            {
                ConfigKey = GetConfigKey(),
                Topic = "Stats/" + id,
                Value = 1,
                Type = MonitorItemType.TYPE_RAW,
                CreateDiscoveryOptions = item =>
                {
                    Interlocked.Increment(ref Builds);
                    return new HassDiscoveryOptions { Id = item.InstanceId, Name = item.InstanceId };
                }
            };
        }

        [TestMethod]
        public void MasterSwitchStopsMonitoringAndCanBeReenabledByConfigReload()
        {
            string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
            string path = Path.Combine(directory, "config.yaml");
            var service = new WindowsMonitorService();
            var manager = new RecordingManager();
            var configs = ConfigurationManager.GetInstance();
            using (var reload = new AutoResetEvent(false))
            {
                ConfigurationManager.ConfigReloadedHandler onReload = (sender, args) => reload.Set();
                try
                {
                    WriteConfig(path, false);
                    service.SetCurrentPath(directory);
                    service.SetAddonInfo(new AddonInfo { AddonId = "WindowsMonitor" });
                    service.Init(manager);
                    configs.SetReloadHandler(path, onReload);
                    AssertSilent(service, manager, 0);

                    WriteConfig(path, true);
                    Assert.IsTrue(reload.WaitOne(TimeSpan.FromSeconds(15)), "Enable was not applied");
                    Assert.IsTrue(manager.Discoveries > 0);
                    Assert.IsTrue(manager.Values > 0);
                    int requests = manager.Requests.Count;
                    Assert.IsTrue(SpinWait.SpinUntil(() => manager.Requests.Count > requests, 5000), "Polling did not resume");

                    WriteConfig(path, false);
                    Assert.IsTrue(reload.WaitOne(TimeSpan.FromSeconds(15)), "Disable was not applied");
                    int afterDisable = manager.Total;
                    Assert.IsFalse(SpinWait.SpinUntil(() => manager.Total != afterDisable, 1500), "Polling continued after disable");
                    AssertSilent(service, manager, afterDisable);
                }
                finally
                {
                    service.Raise_OnMQTTDisconnected(this, null);
                    configs.RemoveReloadHandler(path, onReload);
                    Directory.Delete(directory, true);
                }
            }
        }

        [TestMethod]
        public void ReloadUpdatesSensorIntervalsCacheabilityAndRemovedSensors()
        {
            string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
            string path = Path.Combine(directory, "config.yaml");
            var service = new WindowsMonitorService();
            var manager = new RecordingManager();
            var configs = ConfigurationManager.GetInstance();
            const int idleTimeRequest = 0;
            const int screenshotRequest = 2;
            dynamic data = new ExpandoObject();
            data.requestType = idleTimeRequest;
            data.requestData = 17;
            var reply = new AgentAddonResponseEventArgs { Data = data, Username = "sample" };

            using (var reload = new AutoResetEvent(false))
            {
                ConfigurationManager.ConfigReloadedHandler onReload = (sender, args) => reload.Set();
                try
                {
                    File.WriteAllText(path, "enabled: true\nmonitors:\n" +
                        "  IdleTime: { enabled: true, interval: 3600, cacheable: true }\n" +
                        "  Display-Screenshot: { enabled: true, interval: 1 }\n");
                    service.SetCurrentPath(directory);
                    service.SetAddonInfo(new AddonInfo { AddonId = "WindowsMonitor" });
                    service.Init(manager);
                    configs.SetReloadHandler(path, onReload);

                    Assert.IsTrue(SpinWait.SpinUntil(() => manager.Requests.Count(r => r == screenshotRequest) >= 2, 5000));
                    Assert.AreEqual(1, manager.Requests.Count(r => r == idleTimeRequest));
                    service.Raise_OnAgentResponse(this, reply);
                    service.Raise_OnAgentResponse(this, reply);
                    Assert.AreEqual(1, manager.Values, "Identical cacheable IdleTime values should publish once");

                    File.WriteAllText(path, "enabled: true\nmonitors:\n  IdleTime: { enabled: true, interval: 1 }\n");
                    Assert.IsTrue(reload.WaitOne(TimeSpan.FromSeconds(15)), "Sensor settings were not reloaded");
                    int idleBefore = manager.Requests.Count(r => r == idleTimeRequest);
                    int displayBefore = manager.Requests.Count(r => r == screenshotRequest);
                    Assert.IsTrue(SpinWait.SpinUntil(() => manager.Requests.Count(r => r == idleTimeRequest) >= idleBefore + 2, 5000));
                    Assert.AreEqual(displayBefore, manager.Requests.Count(r => r == screenshotRequest), "Removed sensor kept polling");
                    service.Raise_OnAgentResponse(this, reply);
                    service.Raise_OnAgentResponse(this, reply);
                    Assert.AreEqual(3, manager.Values, "Removing cacheable should publish both replies");
                }
                finally
                {
                    service.Raise_OnMQTTDisconnected(this, null);
                    configs.RemoveReloadHandler(path, onReload);
                    Directory.Delete(directory, true);
                }
            }
        }

        [TestMethod]
        public void ReloadUsesNewGlobalFormatsWhenSensorOverridesAreRemoved()
        {
            string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
            string path = Path.Combine(directory, "config.yaml");
            var service = new WindowsMonitorService();
            var manager = new RecordingManager();
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
            void AssertDiskTotal(double total, string unit)
            {
                var state = JObject.Parse(manager.LastValues["Stats/HardDrive/State"]);
                Assert.AreEqual(total, (double)state["drives"][drive.Name[..1]]["totalSize"]);
                Assert.AreEqual(unit, (string)state["unit"]);
            }
            const string memoryTopic = "Stats/Memory/State";
            void AssertMemoryTotal(double total, string unit)
            {
                var state = JObject.Parse(manager.LastValues[memoryTopic]);
                Assert.AreEqual(total, (double)state["total"]);
                Assert.AreEqual(unit, (string)state["unit"]);
                Assert.AreEqual(unit, manager.LastDiscovery[memoryTopic].Unit);
            }
            const string bootTopic = "Stats/System/BootTime";
            const string sensors = "monitors:\n" +
                "  HardDrive: { enabled: true, interval: 1 }\n" +
                "  Memory: { enabled: true, interval: 1 }\n" +
                "  Uptime: { enabled: true, interval: 1 }\n";
            var culture = CultureInfo.InvariantCulture;
            long memory = (long)PlatformHelper.GetMemoryInformation().TotalPhysical;
            var boot = PlatformHelper.LastBootUpTime();
            var configs = ConfigurationManager.GetInstance();
            using (var reload = new AutoResetEvent(false))
            {
                ConfigurationManager.ConfigReloadedHandler onReload = (sender, args) => reload.Set();
                try
                {
                    File.WriteAllText(path, "enabled: true\nformats: { diskFormat: TB, memoryFormat: TB, dateTimeFormat: yyyy }\n" +
                        "monitors:\n  HardDrive: { enabled: true, formats: { diskFormat: B } }\n" +
                        "  Memory: { enabled: true, formats: { memoryFormat: B } }\n" +
                        "  Uptime: { enabled: true, formats: { dateTimeFormat: MM/dd } }\n");
                    service.SetCurrentPath(directory);
                    service.SetAddonInfo(new AddonInfo { AddonId = "WindowsMonitor" });
                    service.Init(manager);
                    configs.SetReloadHandler(path, onReload);
                    service.Raise_OnRefreshRequested(this, EventArgs.Empty);
                    AssertDiskTotal(drive.TotalSize, "B");
                    AssertMemoryTotal(memory, "B");
                    Assert.AreEqual(boot.ToString("MM/dd", culture), manager.LastValues[bootTopic]);

                    File.WriteAllText(path, "enabled: true\nformats: { diskFormat: 'TB:2', memoryFormat: 'GB:1', dateTimeFormat: yyyyMMdd }\n" + sensors);
                    Assert.IsTrue(reload.WaitOne(TimeSpan.FromSeconds(15)));
                    AssertDiskTotal(Math.Round(UnitsHelper.BytesToTerabytes(drive.TotalSize), 2), "TB");
                    AssertMemoryTotal(Math.Round(UnitsHelper.BytesToGigabytes(memory), 1), "GB");
                    Assert.AreEqual(boot.ToString("yyyyMMdd", culture), manager.LastValues[bootTopic]);

                    File.WriteAllText(path, "enabled: true\n" + sensors);
                    Assert.IsTrue(reload.WaitOne(TimeSpan.FromSeconds(15)));
                    AssertDiskTotal(Math.Round(UnitsHelper.BytesToGigabytes(drive.TotalSize)), "GB");
                    AssertMemoryTotal(Math.Round(UnitsHelper.BytesToMegabytes(memory)), "MB");
                    Assert.AreEqual(boot.ToString("yyyy-MM-dd HH:mm:ss", culture), manager.LastValues[bootTopic]);
                }
                finally
                {
                    service.Raise_OnMQTTDisconnected(this, null);
                    configs.RemoveReloadHandler(path, onReload);
                    Directory.Delete(directory, true);
                }
            }
        }

        private static void WriteConfig(string path, bool enabled)
        {
            File.WriteAllText(path, "enabled: " + (enabled ? "true" : "false") +
                "\nmonitors:\n  Memory:\n    enabled: true\n  IdleTime:\n    enabled: true\n    interval: 1\n");
        }

        private static void AssertSilent(WindowsMonitorService service, RecordingManager manager, int expected)
        {
            service.Raise_OnMQTTDisconnected(service, null);
            service.Raise_OnMQTTConnected(service, null);
            service.Raise_OnRefreshRequested(service, EventArgs.Empty);
            service.Raise_OnSessionChange(service, new SessionChangeEventArgs { Username = "sample" });
            dynamic reply = new ExpandoObject();
            reply.requestType = 0;
            reply.requestData = 17;
            service.Raise_OnAgentResponse(service, new AgentAddonResponseEventArgs { Data = reply, Username = "sample" });
            Assert.IsFalse(SpinWait.SpinUntil(() => manager.Total != expected, 1500), "Disabled monitor produced output");
        }

        private sealed class RecordingManager : IAddonServiceManager
        {
            public int Discoveries, Values, Retained;
            public readonly ConcurrentDictionary<string, string> LastValues = new ConcurrentDictionary<string, string>();
            public readonly ConcurrentDictionary<string, HassDiscoveryOptions> LastDiscovery = new ConcurrentDictionary<string, HassDiscoveryOptions>();
            public readonly ConcurrentQueue<int> Requests = new ConcurrentQueue<int>();
            public int Total => Volatile.Read(ref Discoveries) + Volatile.Read(ref Values) + Requests.Count;

            public void PublishDiscoveryMessage(ServiceAddon sender, string topic, string prefix, HassDiscoveryOptions options)
            {
                LastDiscovery[topic] = options;
                Interlocked.Increment(ref Discoveries);
            }
            public void PublishMessage(ServiceAddon sender, string topic, string message)
            {
                LastValues[topic] = message;
                Interlocked.Increment(ref Values);
            }
            public void PublishRetainedMessage(ServiceAddon sender, string topic, string message)
            {
                Interlocked.Increment(ref Retained);
                PublishMessage(sender, topic, message);
            }
            public void PublishMessage(ServiceAddon sender, string topic, byte[] message) => Interlocked.Increment(ref Values);
            public void SendAgentRequest(ServiceAddon sender, dynamic addonData, string username = null) => Requests.Enqueue((int)addonData.requestType);
            public void SubscribeTopic(ServiceAddon sender, string topic, MQTTHandlers.MQTTMessageEventHandler handler, bool acceptGlobals = true) => throw new NotSupportedException();
            public bool HasSubscription(ServiceAddon sender, string topic) => throw new NotSupportedException();
            public void RemoveSubscription(ServiceAddon sender, string topic) => throw new NotSupportedException();
            public void ShowNotification(ServiceAddon sender, string title, string message, string iconUrl = null, string launchParams = null) => throw new NotSupportedException();
        }
    }
}
