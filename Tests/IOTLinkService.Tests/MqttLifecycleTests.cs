using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using IOTLinkAPI.Configs;
using IOTLinkService.Service.MQTT;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class MqttLifecycleTests
    {
        [TestMethod]
        [Timeout(30000)]
        public void RepeatedStartsLeaveNoManagedClientsRunningAfterStop()
        {
            // Reserve a loopback port without listening. Each started client will be retrying.
            using (var port = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                port.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                var config = new MqttConfig
                {
                    TCP = new MqttConfig.TCPConfig
                    {
                        Enabled = true,
                        Hostname = "127.0.0.1",
                        Port = ((IPEndPoint)port.LocalEndPoint).Port
                    },
                    Prefix = "test",
                    AutoReconnectDelay = 1,
                    MaxPendingMessages = 10
                };
                var clients = new List<IManagedMqttClient>();
                using (var manager = new MQTTClientManager(() => config, () =>
                {
                    var client = new MqttFactory().CreateManagedMqttClient();
                    clients.Add(client);
                    return client;
                }))
                {
                    for (int cycle = 1; cycle <= 2; cycle++)
                    {
                        Parallel.For(0, 4, _ => manager.Start());
                        manager.Start();
                        Assert.IsTrue(clients.Last().IsStarted);

                        manager.Stop();
                        Assert.IsFalse(clients.Any(client => client.IsStarted), "Stop left a reconnect worker running.");
                        Assert.AreEqual(cycle, clients.Count, "Repeated starts created extra clients.");
                        manager.Stop();
                        manager.PublishMessage("status", "offline");
                        manager.ReloadConfiguration();
                        Assert.AreEqual(cycle, clients.Count, "A stopped manager started another client.");
                    }
                }
            }
        }
    }
}
