using System;
using System.Linq;
using System.Text;
using IOTLinkAddon.Service;
using IOTLinkAddon.Service.Monitors;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using MQTTnet.Server;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkService.Service.MQTT;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class MqttTopicTests
    {
        [TestMethod]
        public void OnlyDiscoveredTopicsReuseSuffixesAndEachAddonHasItsOwnNamespace()
        {
            var topics = new AddonTopicCache();
            const string addon = "WindowsMonitor";
            const string sensor = "Stats/HardDrive/State";
            const string expected = "windows-monitor/stats/hard-drive/state";
            Assert.AreEqual(expected, topics.GetSuffix(addon, sensor));
            string known = topics.GetSuffix(addon, sensor, register: true);
            Assert.AreEqual(expected, known);
            Assert.AreSame(known, topics.GetSuffix(addon, sensor));
            Assert.AreSame(known, topics.GetSuffix(addon, sensor, register: true));
            Assert.AreEqual("other/stats/hard-drive/state", topics.GetSuffix("Other", sensor));

            for (int i = 0; i < AddonTopicCache.Capacity + 1; i++)
                Assert.AreEqual($"windows-monitor/stats/etage_2//{i}/", topics.GetSuffix(addon, $"Stats/Étage 2//{i}/"));
            Assert.AreSame(known, topics.GetSuffix(addon, sensor));
            Assert.AreEqual("_raw/topic", topics.GetSuffix(null, " Raw/Topic "));
        }

        [TestMethod]
        public void NewDiscoveriesCanReplaceOldSuffixesWithoutChangingTheirTopics()
        {
            var topics = new AddonTopicCache();
            string first = topics.GetSuffix("Addon", "Stats/FirstSensor", register: true);
            for (int i = 0; i < AddonTopicCache.Capacity; i++)
                topics.GetSuffix("Addon", $"Stats/Sensor/{i}", register: true);

            string evicted = topics.GetSuffix("Addon", "Stats/FirstSensor");
            Assert.AreEqual(first, evicted);
            Assert.AreNotSame(first, evicted);
        }

        [TestMethod]
        public void AppendingToNormalizedPrefixPreservesFullTopic()
        {
            const string prefix = "  Télémétrie /Étage//";
            string machinePrefix = MQTTHelper.GetFullTopicName(prefix);
            foreach (string suffix in new[] { null, "", "   ", "  Écran /Mémoire  ", "/a//b/", @"A\\B/C", "+/#" })
            {
                Assert.AreEqual(MQTTHelper.GetFullTopicName(prefix, suffix),
                    MQTTHelper.AppendTopic(machinePrefix, suffix), suffix);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task ReloadRebuildsMachineAndGlobalPrefixes()
        {
            using (var port = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                port.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                var config = new MqttConfig
                {
                    TCP = new MqttConfig.TCPConfig { Enabled = true, Hostname = "127.0.0.1", Port = ((IPEndPoint)port.LocalEndPoint).Port },
                    Prefix = "Étage A",
                    GlobalPrefix = "Grøupe/A",
                    LWT = new MqttConfig.LWTConfig { Enabled = true, DisconnectMessage = "OFF" },
                    MaxPendingMessages = 10
                };
                IManagedMqttClient client = null;
                var received = new List<string>();
                using (var manager = new MQTTClientManager(() => config, () => client = new MqttFactory().CreateManagedMqttClient()))
                {
                    manager.OnMQTTMessageReceived += (sender, args) => received.Add(args.Message.Topic);
                    manager.Start();
                    string firstMachine = MQTTHelper.GetFullTopicName(config.Prefix);
                    string firstGlobal = MQTTHelper.GetGlobalTopicName(config.GlobalPrefix);
                    config.Prefix = "Étage B";
                    config.GlobalPrefix = "Grøupe/B";
                    Assert.AreEqual(firstMachine + "lwt", client.Options.ClientOptions.WillMessage.Topic);
                    await Receive(client, firstMachine + "Capteur/A");
                    await Receive(client, firstGlobal + "Capteur/B");

                    manager.ReloadConfiguration();
                    string secondMachine = MQTTHelper.GetFullTopicName(config.Prefix);
                    string secondGlobal = MQTTHelper.GetGlobalTopicName(config.GlobalPrefix);
                    Assert.AreEqual(secondMachine + "lwt", client.Options.ClientOptions.WillMessage.Topic);
                    await Receive(client, secondMachine + "Capteur/C");
                    await Receive(client, secondGlobal + "Capteur/D");
                    CollectionAssert.AreEqual(new[] { "capteur/a", "capteur/b", "capteur/c", "capteur/d" }, received);
                }
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task LateSubscriberReceivesDisplaySnapshotWhenNormalMessagesAreNotRetained()
        {
            var monitor = new DisplayMonitor();
            var items = monitor.OnAgentResponse(null, monitor.GetAgentRequests()[monitor.GetConfigKey()],
                new JObject { ["requestData"] = new JArray(new JObject { ["HdrEnabled"] = true }) }, "test");
            await AssertRetainedSnapshot(items, "windows-monitor/stats/display/state", 10);
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task MemoryDiscoveryExtractsFourSensorsFromOneRetainedState()
        {
            var memory = new MemoryMonitor();
            memory.Init(new MonitorSettings(null, null));
            var items = memory.GetMonitorItems(null, 10);
            var entities = items.Where(i => i.CreateDiscoveryOptions != null).Select(i => i.CreateDiscoveryOptions(i)).ToDictionary(o => o.Id);
            CollectionAssert.AreEquivalent(new[] { "Usage", "Available", "Used", "Total" }, entities.Keys.ToArray());
            foreach (var entity in entities.Values)
            {
                Assert.AreEqual("Memory " + entity.Id, entity.Name);
                Assert.AreEqual(entity.Id == "Usage" ? "%" : "MB", entity.Unit);
                Assert.AreEqual("{{ value_json." + entity.Id.ToLowerInvariant() + " }}", entity.ValueTemplate);
            }
            await AssertRetainedSnapshot(items, "windows-monitor/stats/memory/state", 4);
        }

        internal static async Task AssertRetainedSnapshot(List<MonitorItem> items, string suffix, int entityCount)
        {
            int port;
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)socket.LocalEndPoint).Port;
            }
            var factory = new MqttFactory();
            using var broker = factory.CreateMqttServer();
            await broker.StartAsync(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port)
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build());
            var config = new MqttConfig
            {
                TCP = new MqttConfig.TCPConfig { Enabled = true, Hostname = "127.0.0.1", Port = port },
                Prefix = "test", MaxPendingMessages = 64,
                Messages = new MqttConfig.MsgConfig { Retain = false },
                Discovery = new MqttConfig.DiscoveryConfig { Enabled = true, TopicPrefix = "homeassistant" }
            };
            using var manager = new MQTTClientManager(() => config, () => factory.CreateManagedMqttClient());
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.OnMQTTConnected += (_, _) => connected.TrySetResult();
            manager.Start();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var item in items.Where(i => i.CreateDiscoveryOptions != null))
                manager.PublishDiscoveryMessage(item.Topic, item.ConfigKey, item.CreateDiscoveryOptions(item), "WindowsMonitor");
            var snapshot = items.Single(i => i.Value != null);
            manager.PublishMessage(snapshot.Topic, (string)snapshot.Value, "WindowsMonitor", forceRetain: true);
            string topic = MQTTHelper.GetFullTopicName(config.Prefix) + suffix;
            using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await broker.GetRetainedApplicationMessagesAsync()).Count < entityCount + 1)
                await Task.Delay(20, deadline.Token);
            var retained = await broker.GetRetainedApplicationMessagesAsync();
            Assert.AreEqual(entityCount + 1, retained.Count);
            foreach (var message in retained.Where(m => m.Topic.StartsWith("homeassistant/")))
                Assert.AreEqual(topic, (string)JObject.Parse(Encoding.UTF8.GetString(message.Payload))["state_topic"]);

            using var subscriber = factory.CreateMqttClient();
            var received = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            subscriber.UseApplicationMessageReceivedHandler(e => { received.TrySetResult(e.ApplicationMessage); });
            await subscriber.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
            await subscriber.SubscribeAsync(topic);
            var replay = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(replay.Retain);
            Assert.AreEqual(snapshot.Value, Encoding.UTF8.GetString(replay.Payload));
        }

        private static Task Receive(IManagedMqttClient client, string topic)
        {
            var message = new MqttApplicationMessageBuilder().WithTopic(topic).Build();
            return client.ApplicationMessageReceivedHandler.HandleApplicationMessageReceivedAsync(
                new MqttApplicationMessageReceivedEventArgs("test", message));
        }
    }
}
