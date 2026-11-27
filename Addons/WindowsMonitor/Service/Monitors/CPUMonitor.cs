using IOTLinkAPI.Configs;
using IOTLinkAPI.Platform.HomeAssistant;
using IOTLinkAPI.Platform.Windows;
using System;
using System.Collections.Generic;

namespace IOTLinkAddon.Service.Monitors
{
    class CPUMonitor : BaseMonitor, IDisposable
    {
        private static readonly string CONFIG_KEY = "CPU";

        private CpuUsageSampler _cpuUsage;

        public override void Init(MonitorSettings settings)
        {
            if (_cpuUsage != null)
                return;

            _cpuUsage = new CpuUsageSampler();
            _cpuUsage.NextValue();
        }

        public void Dispose()
        {
            _cpuUsage = null;
        }

        public override string GetConfigKey()
        {
            return CONFIG_KEY;
        }

        public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
        {
            List<MonitorItem> result = new List<MonitorItem>();

            // CPU Usage
            result.Add(new MonitorItem
            {
                ConfigKey = CONFIG_KEY,
                Type = MonitorItemType.TYPE_RAW,
                Topic = "Stats/CPU/Usage",
                Value = Math.Round(_cpuUsage.NextValue(), 0),
                CreateDiscoveryOptions = _ => new HassDiscoveryOptions()
                {
                    Id = "Usage",
                    Unit = "%",
                    Name = "CPU Usage",
                    Component = HomeAssistantComponent.Sensor,
                    Icon = "mdi:speedometer"
                }
            });

            return result;
        }
    }
}
