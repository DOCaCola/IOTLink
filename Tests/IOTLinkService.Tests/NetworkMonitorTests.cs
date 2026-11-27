using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using IOTLinkAddon.Service;
using Newtonsoft.Json.Linq;
using System.Net.NetworkInformation;
using IOTLinkAddon.Service.Monitors;
using IOTLinkAPI.Platform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class NetworkMonitorTests
    {
        private string _directory;
        private string IndexPath() => Path.Combine(_directory, "network-adapter-ids.json");

        [TestInitialize]
        public void CreateDirectory() => _directory = Directory.CreateTempSubdirectory().FullName;

        [TestCleanup]
        public void RemoveDirectory() => Directory.Delete(_directory, true);

        private static JObject Adapters(List<MonitorItem> items)
        {
            Assert.IsTrue(items.All(i => i.Topic == "Stats/Network/State"));
            var state = items.Single(i => i.Value != null);
            Assert.AreEqual(MonitorItemType.TYPE_RETAINED_STATE, state.Type);
            return (JObject)JObject.Parse((string)state.Value)["adapters"];
        }

        [TestMethod]
        public void DiscoveryIsCachedUntilNotificationOrExpiryAndCountersAreReadOncePerPoll()
        {
            var ethernet = new FakeInterface("ethernet");
            var wifi = new FakeInterface("wifi");
            var adapters = new List<NetworkAdapter> { ethernet.Adapter, wifi.Adapter };
            int discoveries = 0;
            long now = 0;
            using (var monitor = new NetworkMonitor(() => { discoveries++; return adapters; }, () => now, IndexPath))
            {
                monitor.GetMonitorItems(null, 10);
                now = 59 * Stopwatch.Frequency;
                monitor.GetMonitorItems(null, 10);
                Assert.AreEqual(1, discoveries);
                now += Stopwatch.Frequency;
                monitor.GetMonitorItems(null, 10);
                Assert.AreEqual(2, discoveries);
                monitor.RequestRefresh();
                monitor.RequestRefresh();
                monitor.GetMonitorItems(null, 10);
                Assert.AreEqual(3, discoveries);
                Assert.AreEqual(4, ethernet.Reads);
                Assert.AreEqual(4, wifi.Reads);
            }
        }

        [TestMethod]
        public void ChangeDuringDiscoverySurvivesAndReenableStartsWithFreshCounters()
        {
            var adapter = new FakeInterface("wifi").Adapter;
            int discoveries = 0;
            NetworkMonitor monitor = null;
            monitor = new NetworkMonitor(() =>
            {
                if (++discoveries == 1)
                    monitor.RequestRefresh();
                return new[] { adapter };
            }, () => 0, IndexPath);
            using (monitor)
            {
                monitor.Init(null);
                monitor.Init(null);
                monitor.GetMonitorItems(null, 10);
                var second = monitor.GetMonitorItems(null, 10);
                Assert.AreEqual(2, discoveries);
                Assert.AreEqual(0L, (long)Adapters(second)["wifi"]["bytesSentPerSecond"]);
                monitor.Dispose();
                monitor.Init(null);
                var restarted = monitor.GetMonitorItems(null, 10);
                Assert.AreEqual(3, discoveries);
                Assert.AreEqual(JTokenType.Null, Adapters(restarted)["wifi"]["bytesSentPerSecond"].Type);
            }
        }

        [TestMethod]
        public void RatesFollowIdsOnReorderAndResetAfterRemovalOrCounterDecrease()
        {
            var a = new FakeInterface("a", 0, 1000);
            var b = new FakeInterface("b", 6000, 7000);
            var adapters = new List<NetworkAdapter> { a.Adapter, b.Adapter };
            using (var monitor = new NetworkMonitor(() => adapters, () => 0, IndexPath))
            {
                Assert.AreEqual(JTokenType.Null, Adapters(monitor.GetMonitorItems(null, 10))["a"]["bytesSentPerSecond"].Type);
                // Rediscovery returns new interface objects, even for surviving IDs.
                a = new FakeInterface("a", 100, 1);
                b = new FakeInterface("b", 6500, 7250);
                adapters = new List<NetworkAdapter> { b.Adapter, a.Adapter };
                monitor.RequestRefresh();
                var values = Adapters(monitor.GetMonitorItems(null, 10));
                Assert.AreEqual(50L, (long)values["b"]["bytesSentPerSecond"]);
                Assert.AreEqual(25L, (long)values["b"]["bytesReceivedPerSecond"]);
                Assert.AreEqual(10L, (long)values["a"]["bytesSentPerSecond"]);
                Assert.AreEqual(JTokenType.Null, values["a"]["bytesReceivedPerSecond"].Type);

                var c = new FakeInterface("c", 10000, 20000);
                adapters = new List<NetworkAdapter> { c.Adapter, a.Adapter };
                monitor.RequestRefresh();
                a.Sent = 0;
                a.Received = 101;
                values = Adapters(monitor.GetMonitorItems(null, 10));
                Assert.AreEqual(JTokenType.Null, values["c"]["bytesSentPerSecond"].Type);
                Assert.AreEqual(JTokenType.Null, values["a"]["bytesSentPerSecond"].Type);
                Assert.AreEqual(10L, (long)values["a"]["bytesReceivedPerSecond"]);

                adapters = new List<NetworkAdapter> { b.Adapter };
                monitor.RequestRefresh();
                Assert.AreEqual(JTokenType.Null, Adapters(monitor.GetMonitorItems(null, 10))["b"]["bytesSentPerSecond"].Type);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task DiscoveryKeepsAdapterBindingsAcrossRestartsAndTheBrokerReplaysOneSnapshot()
        {
            var wifi = new FakeInterface("wifi");
            var ethernet = new FakeInterface("ethernet", 123, 456);
            var adapters = new List<NetworkAdapter> { wifi.Adapter, ethernet.Adapter };
            using (var monitor = new NetworkMonitor(() => adapters, () => 0, IndexPath))
            {
                monitor.GetDiscoveryItems(null, 10);
                Assert.AreEqual(0, ethernet.Reads);
            }
            adapters.Reverse();
            using var restarted = new NetworkMonitor(() => adapters, () => 0, IndexPath);
            var items = restarted.GetMonitorItems(null, 10);
            var values = Adapters(items);
            CollectionAssert.AreEquivalent(new[] { "wifi", "ethernet" }, values.Properties().Select(p => p.Name).ToArray());
            var row = values["ethernet"];
            Assert.AreEqual("192.0.2.1", (string)row["ipv4"]);
            Assert.AreEqual(JTokenType.Null, row["ipv6"].Type);
            Assert.AreEqual(1000, (int)row["speedMbps"]);
            Assert.IsTrue((bool)row["wired"]);
            Assert.AreEqual(123L, (long)row["bytesSent"]);
            Assert.AreEqual(456L, (long)row["bytesReceived"]);
            var entities = items.Where(i => i.CreateDiscoveryOptions != null).Select(i => i.CreateDiscoveryOptions(i)).ToDictionary(d => d.Id);
            Assert.AreEqual("{{ value_json.adapters.get(\"wifi\", {}).get('ipv4') }}", entities["0_IPv4Adress"].ValueTemplate);
            Assert.AreEqual("{{ value_json.adapters.get(\"ethernet\", {}).get('bytesSent') }}", entities["1_BytesSent"].ValueTemplate);
            await MqttTopicTests.AssertRetainedSnapshot(items, "windows-monitor/stats/network/state", 16);
            adapters.Clear();
            restarted.RequestRefresh();
            Assert.AreEqual(0, Adapters(restarted.GetMonitorItems(null, 10)).Count);
        }

        private sealed class FakeInterface : NetworkInterface
        {
            public override string Id { get; }
            public override long Speed => 1000000000;
            public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Ethernet;
            public NetworkAdapter Adapter { get; }
            public int Reads { get; private set; }
            public long Sent { get; set; }
            public long Received { get; set; }

            public FakeInterface(string id, long sent = 0, long received = 0)
            {
                Id = id;
                Sent = sent;
                Received = received;
                Adapter = new NetworkAdapter(this, "192.0.2.1", null);
            }

            public override IPInterfaceStatistics GetIPStatistics()
            {
                Reads++;
                return new Counters(Sent, Received);
            }
        }

        private sealed class Counters : IPInterfaceStatistics
        {
            public Counters(long sent, long received)
            {
                BytesSent = sent;
                BytesReceived = received;
            }

            public override long BytesSent { get; }
            public override long BytesReceived { get; }
            public override long IncomingPacketsDiscarded => 0;
            public override long IncomingPacketsWithErrors => 0;
            public override long IncomingUnknownProtocolPackets => 0;
            public override long NonUnicastPacketsReceived => 0;
            public override long NonUnicastPacketsSent => 0;
            public override long OutgoingPacketsDiscarded => 0;
            public override long OutgoingPacketsWithErrors => 0;
            public override long OutputQueueLength => 0;
            public override long UnicastPacketsReceived => 0;
            public override long UnicastPacketsSent => 0;
        }
    }
}
