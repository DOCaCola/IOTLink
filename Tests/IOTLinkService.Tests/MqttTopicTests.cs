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

        private static Task Receive(IManagedMqttClient client, string topic)
        {
            var message = new MqttApplicationMessageBuilder().WithTopic(topic).Build();
            return client.ApplicationMessageReceivedHandler.HandleApplicationMessageReceivedAsync(
                new MqttApplicationMessageReceivedEventArgs("test", message));
        }
    }
}
