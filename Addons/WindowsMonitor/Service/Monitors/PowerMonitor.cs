using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.HomeAssistant;
using IOTLinkAPI.Platform.Windows.Native;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IOTLinkAddon.Service.Monitors
{
    class PowerMonitor : BaseMonitor
    {
        private const string ConfigKey = "Power";
        internal const string StateTopic = "Stats/Power/State";
        internal delegate bool ReadPowerStatus(out Kernel32.SystemPowerStatus status);
        private readonly ReadPowerStatus _read;
        private static readonly MonitorItem[] Discovery =
        {
            Sensor("PowerStatus", "Power Status", "acOnline", binary: true, deviceClass: "plug"),
            Sensor("BatteryStatus", "Battery Status", "batteryPresent", binary: true, deviceClass: "plug"),
            Sensor("BatteryLevel", "Battery Level", "chargeStatus", icon: "mdi:battery"),
            Sensor("FullLifetime", "Battery Full Lifetime", "fullLifetimeSeconds", unit: "minutes", icon: "mdi:timer-outline"),
            Sensor("RemainingTime", "Battery Remaining Time", "remainingSeconds", unit: "minutes", icon: "mdi:timer-outline"),
            Sensor("Remaining", "Battery Remaining", "remainingPercent", unit: "%", deviceClass: "battery")
        };

        public PowerMonitor() : this(Kernel32.GetSystemPowerStatus) { }
        internal PowerMonitor(ReadPowerStatus read) => _read = read;

        public override string GetConfigKey() => ConfigKey;

        private static MonitorItem Sensor(string id, string name, string field, bool binary = false,
            string unit = null, string deviceClass = null, string icon = null)
        {
            string value = "value_json." + field;
            return new MonitorItem
            {
                ConfigKey = ConfigKey,
                Topic = StateTopic,
                DiscoveryKey = StateTopic + "/" + id,
                CreateDiscoveryOptions = _ => new HassDiscoveryOptions
                {
                    Id = id,
                    Name = name,
                    Component = binary ? HomeAssistantComponent.BinarySensor : HomeAssistantComponent.Sensor,
                    PayloadOn = binary ? "True" : null,
                    PayloadOff = binary ? "False" : null,
                    Unit = unit,
                    DeviceClass = deviceClass,
                    Icon = icon,
                    ValueTemplate = unit == "minutes"
                        ? "{{ " + value + " // 60 if " + value + " is not none else none }}"
                        : "{{ " + value + " }}"
                }
            };
        }

        public override List<MonitorItem> GetDiscoveryItems(Configuration config, int interval) => new(Discovery);

        public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
        {
            var items = GetDiscoveryItems(config, interval);
            if (!_read(out var status))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                LoggerHelper.Error("Could not read system power status: {0}", error.Message);
                return items;
            }

            var flags = (BatteryChargeStatus)status.BatteryFlag;
            // Unknown (255) also has the NoSystemBattery bit set.
            bool? present = flags == BatteryChargeStatus.Unknown ? null : !flags.HasFlag(BatteryChargeStatus.NoSystemBattery);
            items.Add(new MonitorItem
            {
                ConfigKey = ConfigKey,
                Topic = StateTopic,
                Type = MonitorItemType.TYPE_RETAINED_STATE,
                Value = JsonConvert.SerializeObject(new
                {
                    acOnline = status.ACLineStatus switch { 0 => false, 1 => true, _ => (bool?)null },
                    batteryPresent = present,
                    chargeStatus = flags == BatteryChargeStatus.Unknown ? null : flags.ToString(),
                    fullLifetimeSeconds = Estimate(status.BatteryFullLifeTime),
                    remainingSeconds = Estimate(status.BatteryLifeTime),
                    remainingPercent = present != false && status.BatteryLifePercent <= 100 ? (int?)status.BatteryLifePercent : null
                })
            });
            return items;

            uint? Estimate(uint seconds) => present != false && seconds != uint.MaxValue ? seconds : null;
        }
    }
}
