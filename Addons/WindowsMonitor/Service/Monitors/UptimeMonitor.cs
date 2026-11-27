using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.HomeAssistant;
using IOTLinkAPI.Platform.Windows.Native;
using System;
using System.Collections.Generic;

namespace IOTLinkAddon.Service.Monitors
{
    class UptimeMonitor : BaseMonitor
    {
        private static readonly string CONFIG_KEY = "Uptime";

        public override string GetConfigKey()
        {
            return CONFIG_KEY;
        }

        public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
        {
            List<MonitorItem> result = new List<MonitorItem>();

            // Boot Time
            result.Add(new MonitorItem
            {
                ConfigKey = CONFIG_KEY,
                Type = MonitorItemType.TYPE_DATETIME,
                Topic = "Stats/System/BootTime",
                Value = PlatformHelper.LastBootUpTime(),
                CreateDiscoveryOptions = _ => new HassDiscoveryOptions()
                {
                    Component = HomeAssistantComponent.Sensor,
                    Id = "BootTime",
                    Name = "System Boot Time",
                    Icon = "mdi:timer-outline"
                }
            });

            // Uptime
            bool inSeconds = config.GetValue("inSeconds", false);
            result.Add(new MonitorItem
            {
                ConfigKey = CONFIG_KEY,
                Type = inSeconds ? MonitorItemType.TYPE_RAW : MonitorItemType.TYPE_UPTIME,
                Topic = "Stats/System/Uptime",
                Value = Math.Round(Kernel32.GetTickCount64() / 1000d, 0),
                CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                {
                    Component = HomeAssistantComponent.Sensor,
                    Id = "Uptime",
                    Name = "System Uptime",
                    Unit = item.Type == MonitorItemType.TYPE_RAW ? "s" : null,
                    Icon = "mdi:timer-outline"
                }
            });

            return result;
        }
    }
}
