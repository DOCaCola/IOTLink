using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.HomeAssistant;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;

namespace IOTLinkAddon.Service.Monitors
{
    class NetworkMonitor : BaseMonitor, IDisposable
    {
        private static readonly string CONFIG_KEY = "NetworkInfo";

        private readonly Func<IEnumerable<NetworkAdapter>> _discover;
        internal const string StateTopic = "Stats/Network/State";
        private readonly Func<long> _getTimestamp;
        private readonly Func<string> _indexPath;
        private static readonly object SlotFileLock = new object();
        private List<string> _slots;
        private List<MonitorItem> _discovery = new List<MonitorItem>();
        private List<NetworkAdapter> _adapters = new List<NetworkAdapter>();
        private Dictionary<string, NetworkInfo> _previous = new Dictionary<string, NetworkInfo>();
        private int _refreshRequested = 1;
        private long _lastRefresh;
        private bool _subscribed;

        public NetworkMonitor(Func<string> indexPath) : this(NetworkAdapter.Discover, Stopwatch.GetTimestamp, indexPath) { }

        internal NetworkMonitor(Func<IEnumerable<NetworkAdapter>> discover, Func<long> getTimestamp, Func<string> indexPath)
        {
            _discover = discover;
            _getTimestamp = getTimestamp;
            _indexPath = indexPath;
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
            _discovery.Clear();
            _previous.Clear();
            RequestRefresh();
        }

        private void OnNetworkChanged(object sender, EventArgs args) => RequestRefresh();

        internal void RequestRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);

        private bool RefreshAdapters()
        {
            long now = _getTimestamp();
            // Consume the notification before discovery so a change during discovery survives.
            if (Interlocked.Exchange(ref _refreshRequested, 0) == 0 && now - _lastRefresh < 60L * Stopwatch.Frequency)
                return true;

            try
            {
                var adapters = _discover().ToList();
                var retained = new Dictionary<string, NetworkInfo>(adapters.Count);
                foreach (var adapter in adapters)
                    if (_previous.TryGetValue(adapter.Id, out var sample))
                        retained.Add(adapter.Id, sample);

                try
                {
                    var slots = GetDiscoverySlots(adapters);
                    _discovery = adapters.SelectMany(a => CreateDiscoveryItems(a.Id, slots.IndexOf(a.Id))).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    LoggerHelper.Error("Could not load or save network entity IDs: {0}", ex.Message);
                }
                // Keep the serialized snapshot identical when Windows only reorders interfaces.
                adapters.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
                _adapters = adapters;
                _previous = retained;
                _lastRefresh = now;
                return true;
            }
            catch (NetworkInformationException ex)
            {
                LoggerHelper.Error("Error refreshing network adapters: {0}", ex.Message);
                RequestRefresh();
                return false;
            }
        }

        public override string GetConfigKey()
        {
            return CONFIG_KEY;
        }

        // The original Home Assistant unique IDs contain the enumeration index. Assign those slots
        // in Windows order on migration, then keep each slot bound to the same adapter across restarts.
        private List<string> GetDiscoverySlots(List<NetworkAdapter> adapters)
        {
            if (_slots != null && adapters.All(a => _slots.Contains(a.Id)))
                return _slots;

            lock (SlotFileLock)
            {
                var path = _indexPath();
                var slots = File.Exists(path) ? JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path)) : new List<string>();
                if (slots == null || slots.Any(string.IsNullOrWhiteSpace) || slots.Distinct(StringComparer.Ordinal).Count() != slots.Count)
                    throw new InvalidDataException($"Invalid adapter ID list in {path}");

                int previousCount = slots.Count;
                foreach (var adapter in adapters)
                    if (!slots.Contains(adapter.Id))
                        slots.Add(adapter.Id);
                if (slots.Count != previousCount)
                {
                    var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(tempPath, JsonConvert.SerializeObject(slots));
                        File.Move(tempPath, path, true);
                    }
                    finally { File.Delete(tempPath); }
                }
                _slots = slots;
                return slots;
            }
        }

        private static IEnumerable<MonitorItem> CreateDiscoveryItems(string adapterId, int slot)
        {
            string adapter = "value_json.adapters.get(" + JsonConvert.ToString(adapterId) + ", {})";
            MonitorItem Sensor(string field, string suffix, string label, string unit = null, string icon = null, bool binary = false)
            {
                string id = $"{slot}_{suffix}";
                return new MonitorItem
                {
                    ConfigKey = CONFIG_KEY,
                    Topic = StateTopic,
                    DiscoveryKey = StateTopic + "/" + id,
                    CreateDiscoveryOptions = _ => new HassDiscoveryOptions
                    {
                        Component = binary ? HomeAssistantComponent.BinarySensor : HomeAssistantComponent.Sensor,
                        Id = id,
                        Name = $"Network #{slot} - {label}",
                        Unit = unit,
                        Icon = icon,
                        PayloadOn = binary ? "True" : null,
                        PayloadOff = binary ? "False" : null,
                        ValueTemplate = "{{ " + adapter + ".get('" + field + "') }}"
                    }
                };
            }

            yield return Sensor("ipv4", "IPv4Adress", "IPv4", icon: "mdi:lan");
            yield return Sensor("ipv6", "IPv6Adress", "IPv6", icon: "mdi:lan");
            yield return Sensor("speedMbps", "Speed", "Speed", "Mbps", "mdi:speedometer");
            yield return Sensor("wired", "Wired", "Wired", binary: true);
            yield return Sensor("bytesSent", "BytesSent", "Bytes Sent", "B");
            yield return Sensor("bytesReceived", "BytesReceived", "Bytes Received", "B");
            yield return Sensor("bytesSentPerSecond", "BytesSentPerSecond", "BPS Sent", "Bps");
            yield return Sensor("bytesReceivedPerSecond", "BytesReceivedPerSecond", "BPS Received", "Bps");
        }

        public override List<MonitorItem> GetDiscoveryItems(Configuration config, int interval)
        {
            return RefreshAdapters() ? _discovery : null;
        }

        public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
        {
            if (!RefreshAdapters())
                return null;
            var snapshot = new Dictionary<string, object>(_adapters.Count);
            foreach (var adapter in _adapters)
            {
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
                snapshot.Add(adapter.Id, new
                {
                    ipv4 = networkInfo.IPv4Address,
                    ipv6 = networkInfo.IPv6Address,
                    speedMbps = networkInfo.Speed,
                    wired = networkInfo.Wired,
                    bytesSent = networkInfo.BytesSent,
                    bytesReceived = networkInfo.BytesReceived,
                    bytesSentPerSecond = Rate(networkInfo.BytesSent, previous?.BytesSent, interval),
                    bytesReceivedPerSecond = Rate(networkInfo.BytesReceived, previous?.BytesReceived, interval)
                });
            }
            var items = new List<MonitorItem>(_discovery.Count + 1);
            items.AddRange(_discovery);
            items.Add(new MonitorItem
            {
                ConfigKey = CONFIG_KEY,
                Topic = StateTopic,
                Type = MonitorItemType.TYPE_RETAINED_STATE,
                Value = JsonConvert.SerializeObject(new { adapters = snapshot })
            });
            return items;
        }

        private static long? Rate(long current, long? previous, int interval) =>
            current >= previous ? (current - previous) / interval : null;
    }
}
