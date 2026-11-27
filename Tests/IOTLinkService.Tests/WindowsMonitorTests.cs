using System;
using System.Dynamic;
using System.IO;
using System.Threading;
using IOTLinkAddon.Service;
using IOTLinkAPI.Addons;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Platform.Events;
using IOTLinkAPI.Platform.Events.MQTT;
using IOTLinkAPI.Platform.HomeAssistant;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class WindowsMonitorTests
    {
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
                    int requests = manager.Requests;
                    Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref manager.Requests) > requests, 5000), "Polling did not resume");

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
            public int Discoveries, Values, Requests;
            public int Total => Volatile.Read(ref Discoveries) + Volatile.Read(ref Values) + Volatile.Read(ref Requests);

            public void PublishDiscoveryMessage(ServiceAddon sender, string topic, string prefix, HassDiscoveryOptions options) => Interlocked.Increment(ref Discoveries);
            public void PublishMessage(ServiceAddon sender, string topic, string message) => Interlocked.Increment(ref Values);
            public void PublishMessage(ServiceAddon sender, string topic, byte[] message) => Interlocked.Increment(ref Values);
            public void SendAgentRequest(ServiceAddon sender, dynamic addonData, string username = null) => Interlocked.Increment(ref Requests);
            public void SubscribeTopic(ServiceAddon sender, string topic, MQTTHandlers.MQTTMessageEventHandler handler, bool acceptGlobals = true) => throw new NotSupportedException();
            public bool HasSubscription(ServiceAddon sender, string topic) => throw new NotSupportedException();
            public void RemoveSubscription(ServiceAddon sender, string topic) => throw new NotSupportedException();
            public void ShowNotification(ServiceAddon sender, string title, string message, string iconUrl = null, string launchParams = null) => throw new NotSupportedException();
        }
    }
}
