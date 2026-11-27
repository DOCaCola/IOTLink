using IOTLinkAddon.Common;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.HomeAssistant;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

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
                    return ParseDisplayInformation(data);

                case AddonRequestType.REQUEST_DISPLAY_SCREENSHOT:
                    return ParseDisplayScreenshot(data, username);

                default: break;
            }

            return null;
        }

        internal const string StateTopic = "Stats/Display/State";

        // Keep the discovery ID suffixes stable across topic changes.
        private static readonly (string Field, string Id, string Label, string Icon, string Unit, bool Binary)[] Sensors =
        {
            ("screenWidth", "Width", "Width", "mdi:monitor", null, false),
            ("screenHeight", "Height", "Height", "mdi:monitor", null, false),
            ("signalWidth", "SignalWidth", "Signal Width", "mdi:monitor", "px", false),
            ("signalHeight", "SignalHeight", "Signal Height", "mdi:monitor", "px", false),
            ("monitorName", "MonitorName", "Monitor Name", "mdi:monitor", null, false),
            ("refreshRate", "RefreshRate", "Refresh Rate", "mdi:monitor", "Hz", false),
            ("hdrEnabled", "HdrEnabled", "HDR Enabled", "mdi:hdr", null, true),
            ("primaryDisplay", "PrimaryDisplay", "Primary Display", "mdi:monitor-star", null, true),
            ("connectorType", "ConnectorType", "Connector Type", "mdi:connection", null, false),
            ("rotation", "Rotation", "Rotation", "mdi:screen-rotation", "°", false)
        };

        private static List<MonitorItem> ParseDisplayInformation(dynamic data)
        {
            List<DisplayInfo> displays = data.requestData.ToObject<List<DisplayInfo>>();
            var items = new List<MonitorItem>();
            for (int i = 0; i < displays.Count; i++)
            {
                int index = i;
                foreach (var sensor in Sensors)
                {
                    string id = $"Screen_{index}_{sensor.Id}";
                    items.Add(new MonitorItem
                    {
                        ConfigKey = CONFIG_KEY_INFO,
                        Topic = StateTopic,
                        DiscoveryKey = id,
                        CreateDiscoveryOptions = _ => new HassDiscoveryOptions
                        {
                            Id = id,
                            Name = $"Screen #{index} - {sensor.Label}",
                            Component = sensor.Binary ? HomeAssistantComponent.BinarySensor : HomeAssistantComponent.Sensor,
                            Icon = sensor.Icon,
                            Unit = sensor.Unit,
                            PayloadOn = sensor.Binary ? "True" : null,
                            PayloadOff = sensor.Binary ? "False" : null,
                            ValueTemplate = "{{ value_json.displays[" + index + "]." + sensor.Field +
                                " if value_json.displays | length > " + index + " else none }}"
                        }
                    });
                }
            }

            items.Add(new MonitorItem
            {
                ConfigKey = CONFIG_KEY_INFO,
                Topic = StateTopic,
                Type = MonitorItemType.TYPE_RETAINED_STATE,
                Value = JsonConvert.SerializeObject(new { displays = displays.Select(d => new
                {
                    screenWidth = d.ScreenWidth, screenHeight = d.ScreenHeight,
                    signalWidth = d.SignalWidth, signalHeight = d.SignalHeight,
                    monitorName = d.MonitorName, refreshRate = d.RefreshRate,
                    hdrEnabled = d.HdrEnabled, primaryDisplay = d.IsPrimaryDisplay(),
                    connectorType = d.ConnectorType, rotation = d.Rotation
                }) })
            });
            return items;
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
