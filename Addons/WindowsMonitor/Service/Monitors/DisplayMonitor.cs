using IOTLinkAddon.Common;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.HomeAssistant;
using System.Collections.Generic;

namespace IOTLinkAddon.Service.Monitors
{
    class DisplayMonitor : BaseMonitor
    {
        private static readonly string CONFIG_KEY_INFO = "Display-Info";
        private static readonly string CONFIG_KEY_SCREEN = "Display-Screenshot";

        private static readonly Dictionary<string, AddonRequestType> AGENT_REQUESTS = new Dictionary<string, AddonRequestType>
        {
            { CONFIG_KEY_INFO, AddonRequestType.REQUEST_DISPLAY_INFORMATION },
            { CONFIG_KEY_SCREEN, AddonRequestType.REQUEST_DISPLAY_SCREENSHOT }
        };

        public override string GetConfigKey()
        {
            return CONFIG_KEY_INFO;
        }

        public override Dictionary<string, AddonRequestType> GetAgentRequests()
        {
            return AGENT_REQUESTS;
        }

        public override List<MonitorItem> OnAgentResponse(Configuration config, AddonRequestType type, dynamic data, string username)
        {
            switch (type)
            {
                case AddonRequestType.REQUEST_DISPLAY_INFORMATION:
                    return ParseDisplayInformation(data, username);

                case AddonRequestType.REQUEST_DISPLAY_SCREENSHOT:
                    return ParseDisplayScreenshot(data, username);

                default: break;
            }

            return null;
        }

        private List<MonitorItem> ParseDisplayInformation(dynamic data, string username)
        {
            List<MonitorItem> result = new List<MonitorItem>();

            LoggerHelper.Verbose("DisplayMonitor - Received Display Informations");
            List<DisplayInfo> displayInfos = data.requestData.ToObject<List<DisplayInfo>>();
            for (var i = 0; i < displayInfos.Count; i++)
            {
                DisplayInfo displayInfo = displayInfos[i];

                string index = i.ToString();
                string topic = $"Stats/Display/{index}";

                result.Add(new MonitorItem(index)
                {
                    ConfigKey = CONFIG_KEY_INFO,
                    Type = MonitorItemType.TYPE_RAW,
                    Topic = topic + "/ScreenWidth",
                    Value = displayInfo.ScreenWidth,
                    CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                    {
                        Id = $"Screen_{item.InstanceId}_Width",
                        Name = $"Screen #{item.InstanceId} - Width",
                        Component = HomeAssistantComponent.Sensor,
                        Icon = "mdi:monitor"
                    }
                });

                result.Add(new MonitorItem(index)
                {
                    ConfigKey = CONFIG_KEY_INFO,
                    Type = MonitorItemType.TYPE_RAW,
                    Topic = topic + "/ScreenHeight",
                    Value = displayInfo.ScreenHeight,
                    CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                    {
                        Id = $"Screen_{item.InstanceId}_Height",
                        Name = $"Screen #{item.InstanceId} - Height",
                        Component = HomeAssistantComponent.Sensor,
                        Icon = "mdi:monitor"
                    }
                });
            }

            return result;
        }

        private List<MonitorItem> ParseDisplayScreenshot(dynamic data, string username)
        {
            List<MonitorItem> result = new List<MonitorItem>();

            LoggerHelper.Verbose("DisplayMonitor - Received Display Screenshot");
            string index = $"{data.requestData.displayIndex}";
            result.Add(new MonitorItem(index)
            {
                ConfigKey = CONFIG_KEY_SCREEN,
                Type = MonitorItemType.TYPE_RAW_BYTES,
                Topic = $"Stats/Display/{index}/Screen",
                Value = (byte[])data.requestData.displayScreen,
                CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                {
                    Id = $"Screen_{item.InstanceId}",
                    Name = $"Screen #{item.InstanceId}",
                    Component = HomeAssistantComponent.Camera
                }
            });

            return result;
        }
    }
}
