using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Management;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IOTLinkAddon.Common.Processes;
using IOTLinkAPI.Addons;
using IOTLinkAPI.Platform.Events;
using IOTLinkAPI.Platform.Events.MQTT;
using IOTLinkAPI.Platform.HomeAssistant;
using Newtonsoft.Json.Linq;
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

        [TestMethod]
        [Timeout(30000)]
        public async Task SnapshotsAndDiscoveryFollowEachConfiguredMonitor()
        {
            var directory = Directory.CreateTempSubdirectory();
            var process = new ProcessInformation
            {
                Id = 42, ProcessName = "testapp", StartDateTime = DateTime.Now.AddMinutes(-2),
                MemoryUsed = 3 << 20, ProcessorUsage = 12.5, FullScreen = true,
                Windows = new List<string> { "Meeting" }
            };
            var processes = new List<ProcessInformation> { process };
            var service = new ProcessMonitorService(_ => processes);
            var manager = new ProcessManager();
            const string app = "Processes/app/State";
            const string meeting = "Processes/meeting/State";
            void Poll()
            {
                service.Raise_OnRefreshRequested(this, EventArgs.Empty);
                service.Raise_OnMQTTDisconnected(this, null);
            }
            void Reply(JObject request)
            {
                request["requestData"] = JObject.FromObject(process);
                service.Raise_OnAgentResponse(this, new AgentAddonResponseEventArgs { Data = request });
            }
            try
            {
                File.WriteAllText(Path.Combine(directory.FullName, "config.yaml"), """
                    enabled: true
                    monitors:
                      app:
                        processes: [testapp]
                        configs: { enabled: true, discoverable: true, advancedMode: true, cacheable: true, interval: 60 }
                      meeting:
                        processes: [testapp]
                        windows: [Meeting]
                        configs: { enabled: true, discoverable: true, interval: 60 }
                    """);
                service.SetCurrentPath(directory.FullName);
                service.SetAddonInfo(new AddonInfo { AddonId = "ProcessMonitor" });
                service.Init(manager);
                service.Raise_OnMQTTDisconnected(this, null);
                var entities = manager.Discovery;
                CollectionAssert.AreEquivalent(new[] { "app", "app_MemoryUsed", "app_ProcessorUsage", "app_Uptime", "app_FullScreen", "meeting" }, entities.Keys.ToArray());
                foreach (var (id, entity) in entities)
                {
                    Assert.AreEqual(id == "meeting" ? meeting : app, entity.Topic);
                    if (id.Contains('_'))
                        StringAssert.Contains(entity.Options.ValueTemplate, "get('" + id.Split('_')[1] + "')");
                    else
                        Assert.AreEqual("{{ value_json.state }}", entity.Options.ValueTemplate);
                }

                Poll();
                Assert.AreEqual(0, manager.Snapshots.Count);
                Reply(manager.Requests["app"]);
                Reply(manager.Requests["meeting"]);
                Assert.AreEqual(2, manager.Snapshots.Count);
                foreach (var (topic, snapshot) in manager.Snapshots)
                {
                    Assert.IsTrue(topic == app || topic == meeting);
                    Assert.AreEqual("ON", (string)snapshot["state"]);
                    Assert.AreEqual("3", (string)snapshot["details"]["MemoryUsed"]);
                    Assert.AreEqual("12.5", (string)snapshot["details"]["ProcessorUsage"]);
                    Assert.AreEqual("True", (string)snapshot["details"]["FullScreen"]);
                    Assert.AreEqual("Meeting", (string)snapshot["details"]["Windows"][0]);
                }

                process.Windows.Clear();
                Poll();
                Reply(manager.Requests["meeting"]);
                Assert.AreEqual(meeting, manager.Snapshots.Last().Topic);
                Assert.IsTrue(JToken.DeepEquals(JObject.Parse("{state:'OFF',details:null}"), manager.Snapshots.Last().Payload));
                var lateRequest = manager.Requests["app"];
                processes.Clear();
                Poll();
                int afterStop = manager.Snapshots.Count;
                foreach (var (topic, snapshot) in manager.Snapshots.TakeLast(2))
                    Assert.IsTrue(JToken.DeepEquals(JObject.Parse("{state:'OFF',details:null}"), snapshot), topic);
                Reply(lateRequest);
                Assert.AreEqual(afterStop, manager.Snapshots.Count);

                processes.Add(process);
                service.Raise_OnRefreshRequested(this, EventArgs.Empty);
                Assert.IsTrue(SpinWait.SpinUntil(() => manager.Snapshots.Count > afterStop, 5000));
                service.Raise_OnMQTTDisconnected(this, null);
                var basic = manager.Snapshots.Last();
                Assert.AreEqual(app, basic.Topic);
                Assert.AreEqual("ON", (string)basic.Payload["state"]);
                Assert.AreEqual("42", (string)basic.Payload["details"]["Id"]);
                Assert.IsNull(basic.Payload["details"]["FullScreen"]);
            }
            finally
            {
                service.ApplyConfiguration(null);
                directory.Delete(true);
            }

            var payload = manager.Snapshots.Last().Payload.ToString(Newtonsoft.Json.Formatting.None);
            await MqttTopicTests.AssertRetainedSnapshot("process-monitor/processes/app/state", payload, 5, mqtt =>
            {
                foreach (var entity in manager.Discovery.Values.Where(entity => entity.Topic == app))
                    mqtt.PublishDiscoveryMessage(entity.Topic, "Processes", entity.Options, "ProcessMonitor");
                mqtt.PublishMessage(app, payload, "ProcessMonitor", forceRetain: true);
            });
        }

        private sealed class ProcessManager : IAddonServiceManager
        {
            public readonly Dictionary<string, (string Topic, HassDiscoveryOptions Options)> Discovery = new();
            public readonly Dictionary<string, JObject> Requests = new();
            public readonly ConcurrentQueue<(string Topic, JObject Payload)> Snapshots = new();
            public void PublishDiscoveryMessage(ServiceAddon sender, string topic, string prefix, HassDiscoveryOptions options) => Discovery.Add(options.Id, (topic, options));
            public void PublishRetainedMessage(ServiceAddon sender, string topic, string message) => Snapshots.Enqueue((topic, JObject.Parse(message)));
            public void SendAgentRequest(ServiceAddon sender, dynamic addonData, string username = null) => Requests[(string)addonData.monitorName] = JObject.FromObject((object)addonData);
            public void PublishMessage(ServiceAddon sender, string topic, string message) => Assert.Fail("Unexpected unretained state");
            public void PublishMessage(ServiceAddon sender, string topic, byte[] message) => throw new NotSupportedException();
            public void SubscribeTopic(ServiceAddon sender, string topic, MQTTHandlers.MQTTMessageEventHandler handler, bool acceptGlobals = true) => throw new NotSupportedException();
            public bool HasSubscription(ServiceAddon sender, string topic) => throw new NotSupportedException();
            public void RemoveSubscription(ServiceAddon sender, string topic) => throw new NotSupportedException();
            public void ShowNotification(ServiceAddon sender, string title, string message, string iconUrl = null, string launchParams = null) => throw new NotSupportedException();
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
