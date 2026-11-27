using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.HomeAssistant;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;

namespace IOTLinkAddon.Service.Monitors
{
    class NetworkMonitor : BaseMonitor, IDisposable
    {
        private static readonly string CONFIG_KEY = "NetworkInfo";

        private readonly Func<IEnumerable<NetworkAdapter>> _discover;
        private readonly Func<long> _getTimestamp;
        private List<NetworkAdapter> _adapters = new List<NetworkAdapter>();
        private Dictionary<string, NetworkInfo> _previous = new Dictionary<string, NetworkInfo>();
        private int _refreshRequested = 1;
        private long _lastRefresh;
        private bool _subscribed;

        public NetworkMonitor() : this(NetworkAdapter.Discover, Stopwatch.GetTimestamp) { }

        internal NetworkMonitor(Func<IEnumerable<NetworkAdapter>> discover, Func<long> getTimestamp)
        {
            _discover = discover;
            _getTimestamp = getTimestamp;
        }

        public override void Init(MonitorSettings settings)
        {
            if (_subscribed)
                return;

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            _subscribed = true;
        }

        public void Dispose()
        {
            if (_subscribed)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
                _subscribed = false;
            }
            _adapters.Clear();
            _previous.Clear();
            RequestRefresh();
        }

        private void OnNetworkChanged(object sender, EventArgs args) => RequestRefresh();

        internal void RequestRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);

        private void RefreshAdapters()
        {
            long now = _getTimestamp();
            // Consume the notification before discovery so a change during discovery survives.
            if (Interlocked.Exchange(ref _refreshRequested, 0) == 0 && now - _lastRefresh < 60L * Stopwatch.Frequency)
                return;

            try
            {
                var adapters = _discover().ToList();
                var retained = new Dictionary<string, NetworkInfo>(adapters.Count);
                foreach (var adapter in adapters)
                    if (_previous.TryGetValue(adapter.Id, out var sample))
                        retained.Add(adapter.Id, sample);

                _adapters = adapters;
                _previous = retained;
                _lastRefresh = now;
            }
            catch (NetworkInformationException ex)
            {
                LoggerHelper.Error("Error refreshing network adapters: {0}", ex.Message);
                RequestRefresh();
            }
        }

        public override string GetConfigKey()
        {
            return CONFIG_KEY;
        }

        public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
        {
            List<MonitorItem> result = new List<MonitorItem>();

            RefreshAdapters();
            for (int i = 0; i < _adapters.Count; i++)
            {
                var adapter = _adapters[i];
                NetworkInfo networkInfo;
                try
                {
                    networkInfo = adapter.Read();
                }
                catch (NetworkInformationException ex)
                {
                    LoggerHelper.Error("Error reading network adapter {0}: {1}", adapter.Id, ex.Message);
                    _previous.Remove(adapter.Id);
                    RequestRefresh();
                    continue;
                }

                _previous.TryGetValue(adapter.Id, out var previous);
                _previous[adapter.Id] = networkInfo;
                string index = i.ToString();
                var topic = $"Stats/Network/{index}";

                // IPv4
                if (!string.IsNullOrWhiteSpace(networkInfo.IPv4Address))
                {
                    result.Add(new MonitorItem(index)
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_RAW,
                        Topic = topic + "/IPv4",
                        Value = networkInfo.IPv4Address,
                        CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{item.InstanceId}_IPv4Adress",
                            Name = $"Network #{item.InstanceId} - IPv4",
                            Icon = "mdi:lan"
                        }
                    });
                }

                // IPv6
                if (!string.IsNullOrWhiteSpace(networkInfo.IPv6Address))
                {
                    result.Add(new MonitorItem(index)
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_RAW,
                        Topic = topic + "/IPv6",
                        Value = networkInfo.IPv6Address,
                        CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{item.InstanceId}_IPv6Adress",
                            Name = $"Network #{item.InstanceId} - IPv6",
                            Icon = "mdi:lan"
                        }
                    });
                }

                // Network Speed
                result.Add(new MonitorItem(index)
                {
                    ConfigKey = CONFIG_KEY,
                    Type = MonitorItemType.TYPE_RAW,
                    Topic = topic + "/Speed",
                    Value = networkInfo.Speed,
                    CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                    {
                        Component = HomeAssistantComponent.Sensor,
                        Id = $"{item.InstanceId}_Speed",
                        Name = $"Network #{item.InstanceId} - Speed",
                        Unit = "Mbps",
                        Icon = "mdi:speedometer"
                    }
                });

                // Network Wired
                result.Add(new MonitorItem(index)
                {
                    ConfigKey = CONFIG_KEY,
                    Type = MonitorItemType.TYPE_RAW,
                    Topic = topic + "/Wired",
                    Value = networkInfo.Wired,
                    CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                    {
                        Component = HomeAssistantComponent.BinarySensor,
                        Id = $"{item.InstanceId}_Wired",
                        Name = $"Network #{item.InstanceId} - Wired",
                        PayloadOff = "False",
                        PayloadOn = "True"
                    }
                });

                // Network Bytes Sent
                result.Add(new MonitorItem(index)
                {
                    ConfigKey = CONFIG_KEY,
                    Type = MonitorItemType.TYPE_NETWORK_SIZE,
                    Topic = topic + "/BytesSent",
                    Value = networkInfo.BytesSent,
                    CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                    {
                        Component = HomeAssistantComponent.Sensor,
                        Id = $"{item.InstanceId}_BytesSent",
                        Name = $"Network #{item.InstanceId} - Bytes Sent",
                        Unit = "B"
                    }
                });

                // Network Bytes Received
                result.Add(new MonitorItem(index)
                {
                    ConfigKey = CONFIG_KEY,
                    Type = MonitorItemType.TYPE_NETWORK_SIZE,
                    Topic = topic + "/BytesReceived",
                    Value = networkInfo.BytesReceived,
                    CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                    {
                        Component = HomeAssistantComponent.Sensor,
                        Id = $"{item.InstanceId}_BytesReceived",
                        Name = $"Network #{item.InstanceId} - Bytes Received",
                        Unit = "B"
                    }
                });

                // Bytes Sent per second
                if (previous != null && networkInfo.BytesSent >= previous.BytesSent)
                {
                    result.Add(new MonitorItem(index)
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_NETWORK_SPEED,
                        Topic = topic + "/BytesSentPerSecond",
                        Value = (networkInfo.BytesSent - previous.BytesSent) / interval,
                        CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{item.InstanceId}_BytesSentPerSecond",
                            Name = $"Network #{item.InstanceId} - BPS Sent",
                            Unit = "Bps"
                        }
                    });
                }

                // Bytes Received per second
                if (previous != null && networkInfo.BytesReceived >= previous.BytesReceived)
                {
                    result.Add(new MonitorItem(index)
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_NETWORK_SPEED,
                        Topic = topic + "/BytesReceivedPerSecond",
                        Value = (networkInfo.BytesReceived - previous.BytesReceived) / interval,
                        CreateDiscoveryOptions = item => new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{item.InstanceId}_BytesReceivedPerSecond",
                            Name = $"Network #{item.InstanceId} - BPS Received",
                            Unit = "Bps"
                        }
                    });
                }
            }
            return result;
        }
    }
}
