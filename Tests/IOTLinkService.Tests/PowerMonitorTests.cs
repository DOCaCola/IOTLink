using System.Linq;
using System.Threading.Tasks;
using IOTLinkAddon.Service;
using IOTLinkAddon.Service.Monitors;
using IOTLinkAPI.Platform.HomeAssistant;
using IOTLinkAPI.Platform.Windows.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class PowerMonitorTests
    {
        [TestMethod]
        public void UnknownEstimatesAreNullAndReportedZeroesArePreserved()
        {
            var sample = new Kernel32.SystemPowerStatus
            {
                ACLineStatus = 0, BatteryFlag = 2, BatteryLifePercent = 0,
                BatteryLifeTime = 0, BatteryFullLifeTime = 3725
            };
            int reads = 0;
            var monitor = new PowerMonitor((out Kernel32.SystemPowerStatus status) =>
            {
                reads++;
                status = sample;
                return true;
            });
            JObject Poll()
            {
                var items = monitor.GetMonitorItems(null, 10);
                Assert.IsTrue(items.All(i => i.Topic == "Stats/Power/State"));
                var state = items.Single(i => i.Value != null);
                Assert.AreEqual(MonitorItemType.TYPE_RETAINED_STATE, state.Type);
                return JObject.Parse((string)state.Value);
            }

            monitor.GetDiscoveryItems(null, 10);
            Assert.AreEqual(0, reads);
            var state = Poll();
            Assert.AreEqual(1, reads);
            Assert.IsFalse((bool)state["acOnline"]);
            Assert.IsTrue((bool)state["batteryPresent"]);
            Assert.AreEqual("Low", (string)state["chargeStatus"]);
            Assert.AreEqual(0, (int)state["remainingPercent"]);
            Assert.AreEqual(0, (int)state["remainingSeconds"]);
            Assert.AreEqual(3725, (int)state["fullLifetimeSeconds"]);

            sample.ACLineStatus = 1;
            sample.BatteryFlag = 8;
            sample.BatteryLifeTime = uint.MaxValue;
            sample.BatteryFullLifeTime = uint.MaxValue;
            sample.BatteryLifePercent = 100;
            state = Poll();
            Assert.IsTrue((bool)state["acOnline"]);
            Assert.AreEqual("Charging", (string)state["chargeStatus"]);
            Assert.AreEqual(100, (int)state["remainingPercent"]);
            Assert.AreEqual(JTokenType.Null, state["remainingSeconds"].Type);
            Assert.AreEqual(JTokenType.Null, state["fullLifetimeSeconds"].Type);

            sample.BatteryFlag = 128; // No battery, even if the driver supplies zeroes.
            sample.BatteryLifePercent = 0;
            sample.BatteryLifeTime = sample.BatteryFullLifeTime = 0;
            state = Poll();
            Assert.IsFalse((bool)state["batteryPresent"]);
            foreach (var field in new[] { "remainingPercent", "remainingSeconds", "fullLifetimeSeconds" })
                Assert.AreEqual(JTokenType.Null, state[field].Type);

            sample.ACLineStatus = sample.BatteryFlag = sample.BatteryLifePercent = 255;
            sample.BatteryLifeTime = 123;
            state = Poll();
            foreach (var field in new[] { "acOnline", "batteryPresent", "chargeStatus", "remainingPercent" })
                Assert.AreEqual(JTokenType.Null, state[field].Type);
            Assert.AreEqual(123, (int)state["remainingSeconds"]);
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task SixPowerEntitiesReadOneRetainedNativeSnapshot()
        {
            var items = new PowerMonitor().GetMonitorItems(null, 10);
            var entities = items.Where(i => i.CreateDiscoveryOptions != null)
                .Select(i => i.CreateDiscoveryOptions(i)).ToDictionary(o => o.Id);
            CollectionAssert.AreEquivalent(new[] { "PowerStatus", "BatteryStatus", "BatteryLevel", "FullLifetime", "RemainingTime", "Remaining" }, entities.Keys.ToArray());
            Assert.AreEqual(HomeAssistantComponent.BinarySensor, entities["PowerStatus"].Component);
            Assert.AreEqual("True", entities["BatteryStatus"].PayloadOn);
            Assert.AreEqual("False", entities["BatteryStatus"].PayloadOff);
            Assert.AreEqual("{{ value_json.acOnline }}", entities["PowerStatus"].ValueTemplate);
            Assert.AreEqual("{{ value_json.batteryPresent }}", entities["BatteryStatus"].ValueTemplate);
            Assert.AreEqual("{{ value_json.chargeStatus }}", entities["BatteryLevel"].ValueTemplate);
            Assert.AreEqual("{{ value_json.remainingPercent }}", entities["Remaining"].ValueTemplate);
            foreach (var (id, field) in new[] { ("RemainingTime", "remainingSeconds"), ("FullLifetime", "fullLifetimeSeconds") })
            {
                Assert.AreEqual("minutes", entities[id].Unit);
                Assert.AreEqual("{{ value_json." + field + " // 60 if value_json." + field + " is not none else none }}", entities[id].ValueTemplate);
            }
            await MqttTopicTests.AssertRetainedSnapshot(items, "windows-monitor/stats/power/state", 6);
        }
    }
}
