using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using IOTLinkAddon.Service.Monitors;
using IOTLinkAPI.Platform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class NetworkMonitorTests
    {
        [TestMethod]
        public void DiscoveryIsCachedUntilNotificationOrExpiryAndCountersAreReadOncePerPoll()
        {
            var ethernet = new FakeInterface("ethernet");
            var wifi = new FakeInterface("wifi");
            var adapters = new List<NetworkAdapter> { ethernet.Adapter, wifi.Adapter };
            int discoveries = 0;
            long now = 0;
            using (var monitor = new NetworkMonitor(() => { discoveries++; return adapters; }, () => now))
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
            }, () => 0);
            using (monitor)
            {
                monitor.Init(null);
                monitor.Init(null);
                monitor.GetMonitorItems(null, 10);
                var second = monitor.GetMonitorItems(null, 10);
                Assert.AreEqual(2, discoveries);
                Assert.IsTrue(second.Any(item => item.Topic.EndsWith("PerSecond")));
                monitor.Dispose();
                monitor.Init(null);
                var restarted = monitor.GetMonitorItems(null, 10);
                Assert.AreEqual(3, discoveries);
                Assert.IsFalse(restarted.Any(item => item.Topic.EndsWith("PerSecond")));
            }
        }

        [TestMethod]
        public void RatesFollowIdsOnReorderAndResetAfterRemovalOrCounterDecrease()
        {
            var a = new FakeInterface("a", 0, 1000);
            var b = new FakeInterface("b", 6000, 7000);
            var adapters = new List<NetworkAdapter> { a.Adapter, b.Adapter };
            using (var monitor = new NetworkMonitor(() => adapters, () => 0))
            {
                Assert.IsFalse(monitor.GetMonitorItems(null, 10).Any(item => item.Topic.EndsWith("PerSecond")));
                // Rediscovery returns new interface objects, even for surviving IDs.
                a = new FakeInterface("a", 100, 1);
                b = new FakeInterface("b", 6500, 7250);
                adapters = new List<NetworkAdapter> { b.Adapter, a.Adapter };
                monitor.RequestRefresh();
                var values = monitor.GetMonitorItems(null, 10).ToDictionary(item => item.Topic, item => item.Value);
                Assert.AreEqual(50L, values["Stats/Network/0/BytesSentPerSecond"]);
                Assert.AreEqual(25L, values["Stats/Network/0/BytesReceivedPerSecond"]);
                Assert.AreEqual(10L, values["Stats/Network/1/BytesSentPerSecond"]);
                Assert.IsFalse(values.ContainsKey("Stats/Network/1/BytesReceivedPerSecond"));

                var c = new FakeInterface("c", 10000, 20000);
                adapters = new List<NetworkAdapter> { c.Adapter, a.Adapter };
                monitor.RequestRefresh();
                a.Sent = 0;
                a.Received = 101;
                values = monitor.GetMonitorItems(null, 10).ToDictionary(item => item.Topic, item => item.Value);
                Assert.IsFalse(values.ContainsKey("Stats/Network/0/BytesSentPerSecond"));
                Assert.IsFalse(values.ContainsKey("Stats/Network/1/BytesSentPerSecond"));
                Assert.AreEqual(10L, values["Stats/Network/1/BytesReceivedPerSecond"]);

                adapters = new List<NetworkAdapter> { b.Adapter };
                monitor.RequestRefresh();
                Assert.IsFalse(monitor.GetMonitorItems(null, 10).Any(item => item.Topic.EndsWith("PerSecond")));
            }
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
