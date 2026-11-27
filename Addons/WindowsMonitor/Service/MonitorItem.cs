using IOTLinkAPI.Platform.HomeAssistant;
using System;

namespace IOTLinkAddon.Service
{
    class MonitorItem
    {
        public MonitorItem(string instanceId = null)
        {
            InstanceId = instanceId;
        }

        public string InstanceId { get; }

        public bool PublishOnChangeOnly { get; set; }

        public string DiscoveryKey { get; set; }

        public string Topic { get; set; }

        public string ConfigKey { get; set; }

        public MonitorItemType Type { get; set; }

        public object Value { get; set; }

        public Func<MonitorItem, HassDiscoveryOptions> CreateDiscoveryOptions { get; set; }
    }
}
