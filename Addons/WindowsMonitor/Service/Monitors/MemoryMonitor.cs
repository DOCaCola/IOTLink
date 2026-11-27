using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.HomeAssistant;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace IOTLinkAddon.Service.Monitors
{
    class MemoryMonitor : BaseMonitor
    {
        private const string ConfigKey = "Memory";
        internal const string StateTopic = "Stats/Memory/State";
        private readonly Func<MemoryInfo> _read;
        private SizeFormat _format;
        private MonitorItem[] _discovery;

        public MemoryMonitor() : this(PlatformHelper.GetMemoryInformation) { }
        internal MemoryMonitor(Func<MemoryInfo> read) => _read = read;

        public override string GetConfigKey() => ConfigKey;

        public override void Init(MonitorSettings settings)
        {
            _format = settings.MemoryFormat;
            _discovery = new[]
            {
                Sensor("Usage", "%"), Sensor("Available", _format.Unit),
                Sensor("Used", _format.Unit), Sensor("Total", _format.Unit)
            };
        }

        private static MonitorItem Sensor(string id, string unit) => new MonitorItem
        {
            ConfigKey = ConfigKey,
            Topic = StateTopic,
            DiscoveryKey = StateTopic + "/" + id,
            CreateDiscoveryOptions = _ => new HassDiscoveryOptions
            {
                Component = HomeAssistantComponent.Sensor,
                Id = id,
                Name = "Memory " + id,
                Unit = unit,
                Icon = "mdi:memory",
                ValueTemplate = "{{ value_json." + id.ToLowerInvariant() + " }}"
            }
        };

        public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
        {
            var items = new List<MonitorItem>(_discovery.Length + 1);
            items.AddRange(_discovery);
            var memory = _read();
            if (memory != null)
            {
                items.Add(new MonitorItem
                {
                    ConfigKey = ConfigKey,
                    Topic = StateTopic,
                    Type = MonitorItemType.TYPE_RETAINED_STATE,
                    Value = JsonConvert.SerializeObject(new
                    {
                        usage = memory.MemoryLoad,
                        available = _format.Convert((long)memory.AvailPhysical),
                        used = _format.Convert((long)(memory.TotalPhysical - memory.AvailPhysical)),
                        total = _format.Convert((long)memory.TotalPhysical),
                        unit = _format.Unit
                    })
                });
            }
            return items;
        }
    }
}
