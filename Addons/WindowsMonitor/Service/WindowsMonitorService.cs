using IOTLinkAddon.Common;
using IOTLinkAddon.Service.Monitors;
using IOTLinkAPI.Addons;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.Events;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Timers;

namespace IOTLinkAddon.Service
{
    public class WindowsMonitorService : ServiceAddon
    {
        private readonly object _monitorLock = new object();
        private bool _enabled;
        private bool _mqttDisconnected;
        private Timer _monitorTimer;
        private uint _monitorCounter = 0;

        private string _configPath;
        private Configuration _config;
        private Configuration _globalFormats;

        private readonly Dictionary<string, MonitorSettings> _monitorSettings = new Dictionary<string, MonitorSettings>();
        private Dictionary<string, string> _cache = new Dictionary<string, string>();

        private readonly HashSet<string> _discoveredTopics = new HashSet<string>(StringComparer.Ordinal);

        private readonly IMonitor[] monitors;
        private IMonitor[] _polledMonitors;

        public WindowsMonitorService() : this(
            new CPUMonitor(), new MemoryMonitor(), new PowerMonitor(), new NetworkMonitor(),
            new StorageMonitor(), new SystemMonitor(), new UptimeMonitor(), new DisplayMonitor())
        {
        }

        internal WindowsMonitorService(params IMonitor[] monitors)
        {
            this.monitors = monitors;
        }

        public override void Init(IAddonManager addonManager)
        {
            base.Init(addonManager);

            lock (_monitorLock)
            {
                var cfgManager = ConfigurationManager.GetInstance();
                _configPath = Path.Combine(_currentPath, "config.yaml");
                var config = cfgManager.GetConfiguration(_configPath);
                cfgManager.SetReloadHandler(_configPath, OnConfigReload);

                OnSessionChangeHandler += OnSessionChange;
                OnConfigReloadHandler += OnConfigReload;
                OnAgentResponseHandler += OnAgentResponse;
                OnMQTTConnectedHandler += OnMQTTConnected;
                OnMQTTDisconnectedHandler += OnMQTTDisconnected;
                OnRefreshRequestedHandler += OnClearEvent;

                ApplyConfiguration(config, false);
            }
        }

        private void ApplyConfiguration(Configuration config, bool sendValues)
        {
            _config = config;
            _globalFormats = config?.GetValue("formats");
            _monitorSettings.Clear();
            _enabled = config != null && config.GetValue("enabled", false);
            foreach (IMonitor monitor in monitors)
            {
                if (!_enabled || !GetMonitorSettings(monitor.GetConfigKey()).Enabled)
                {
                    (monitor as IDisposable)?.Dispose();
                    continue;
                }

                try
                {
                    monitor.Init(GetMonitorSettings(monitor.GetConfigKey()));
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("Failed to initialize {0} monitor: {1}", monitor.GetConfigKey(), ex.Message);
                }
            }

            _polledMonitors = _enabled ? monitors.Where(NeedsPolling).ToArray() : Array.Empty<IMonitor>();
            if (!_enabled)
                _monitorCounter = 0;

            RefreshMonitoring(sendValues);
        }

        private bool NeedsPolling(IMonitor monitor)
        {
            if (monitor is not DisplayMonitor && GetMonitorSettings(monitor.GetConfigKey()).Enabled)
                return true;
            return monitor.GetAgentRequests()?.Any(entry => GetMonitorSettings(entry.Key).PollAgent(entry.Value)) == true;
        }

        private void StopPolling()
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        private void RefreshMonitoring(bool sendValues)
        {
            StopPolling();
            ClearCaches();
            if (!_enabled || _mqttDisconnected)
                return;

            if (_polledMonitors.Length > 0)
            {
                _monitorTimer = new Timer(1000) { AutoReset = false };
                _monitorTimer.Elapsed += OnMonitorTimerElapsed;
                _monitorTimer.Start();
            }
            SetupDiscovery();
            if (sendValues)
                SendAllInformation();
            // Request the display snapshot even when the service has no periodic sensors.
            if (GetMonitorSettings("Display-Info").Enabled)
                RequestAgent(AddonRequestType.REQUEST_DISPLAY_INFORMATION);
        }

        private void SetupDiscovery()
        {
            foreach (var monitor in monitors)
            {
                string configKey = monitor.GetConfigKey();
                var settings = GetMonitorSettings(configKey);
                if (settings.Enabled)
                {
                    LoggerHelper.Debug("WindowsMonitorService::SetupDiscovery() - {0} Monitor - Sending configuration", configKey);

                    List<MonitorItem> items = monitor.GetMonitorItems(settings.Config, settings.Interval);
                    if (items == null)
                        continue;

                    foreach (var item in items)
                    {
                        PublishDiscoveryItem(item);
                    }
                }
            }
        }

        private void OnMQTTConnected(object sender, EventArgs e)
        {
            lock (_monitorLock)
            {
                _mqttDisconnected = false;
                RefreshMonitoring(true);
            }
        }

        private void OnMQTTDisconnected(object sender, EventArgs e)
        {
            lock (_monitorLock)
            {
                _mqttDisconnected = true;
                StopPolling();
            }
        }

        private void OnClearEvent(object sender, EventArgs e)
        {
            lock (_monitorLock)
                RefreshMonitoring(true);
        }

        private void OnConfigReload(object sender, ConfigReloadEventArgs e)
        {
            if (e.ConfigType != ConfigType.CONFIGURATION_ADDON)
                return;

            lock (_monitorLock)
                ApplyConfiguration(ConfigurationManager.GetInstance().GetConfiguration(_configPath), true);
        }

        private void OnSessionChange(object sender, SessionChangeEventArgs e)
        {
            lock (_monitorLock)
            {
                if (!_enabled)
                    return;

                GetManager().PublishMessage(this, e.Reason.ToString(), e.Username);
                GetManager().PublishMessage(this, "status", e.Reason.ToString());
            }
        }

        private void OnMonitorTimerElapsed(object source, ElapsedEventArgs e)
        {
            lock (_monitorLock)
            {
                // An elapsed callback can already be queued when its timer is disposed.
                if (source != _monitorTimer)
                    return;

                try
                {
                    SendAllInformation();
                    _monitorCounter++;
                }
                finally
                {
                    if (source == _monitorTimer)
                        _monitorTimer.Start();
                }
            }
        }

        private void ClearCaches()
        {
            _cache.Clear();
            _discoveredTopics.Clear();
        }

        private void SendAllInformation()
        {
            foreach (IMonitor monitor in _polledMonitors)
                ExecuteMonitor(monitor);
        }

        private void RequestAgent(AddonRequestType type)
        {
            dynamic data = new ExpandoObject();
            data.requestType = type;
            GetManager().SendAgentRequest(this, data);
        }

        private void ExecuteMonitor(IMonitor monitor)
        {
            try
            {
                // Execute agent requests first
                Dictionary<string, AddonRequestType> agentRequests = monitor.GetAgentRequests();
                if (agentRequests != null)
                {
                    foreach (KeyValuePair<string, AddonRequestType> entry in agentRequests)
                    {
                        var settings = GetMonitorSettings(entry.Key);
                        if (!settings.PollAgent(entry.Value) || !settings.IsDue(_monitorCounter))
                            continue;

                        LoggerHelper.Debug("WindowsMonitorService::ExecuteMonitor({0}) - Agent {1} - Request information ({2})", monitor.GetConfigKey(), entry.Key, entry.Value.ToString());

                        RequestAgent(entry.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("WindowsMonitorService::ExecuteMonitor({0}) - Agent Request Error: {1}", monitor.GetConfigKey(), ex.ToString());
            }


            try
            {
                // Get monitor sensors
                string configKey = monitor.GetConfigKey();
                var settings = GetMonitorSettings(configKey);
                if (settings.IsDue(_monitorCounter))
                {
                    LoggerHelper.Debug("WindowsMonitorService::ExecuteMonitor({0}) - Sending information", configKey);

                    List<MonitorItem> items = monitor.GetMonitorItems(settings.Config, settings.Interval);
                    if (items == null)
                        return;

                    foreach (MonitorItem item in items)
                    {
                        PublishDiscoveryItem(item);
                        PublishItem(item, settings);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("WindowsMonitorService::ExecuteMonitor({0}) - Error: {1}", monitor.GetConfigKey(), ex.ToString());
            }
        }

        private void PublishItem(MonitorItem item, MonitorSettings settings)
        {
            if (item == null || item.Value == null || string.IsNullOrWhiteSpace(item.Topic))
                return;

            string value = string.Empty;
            switch (item.Type)
            {
                case MonitorItemType.TYPE_DISK_SIZE:
                    value = settings.DiskFormat.Convert(MathHelper.ToLong(item.Value, 0L)).ToString(CultureInfo.InvariantCulture);
                    break;

                case MonitorItemType.TYPE_NETWORK_SPEED:
                case MonitorItemType.TYPE_NETWORK_SIZE:
                    value = item.Value.ToString();
                    break;

                case MonitorItemType.TYPE_DATE:
                    value = FormatDateObject(item.Value, settings.DateFormat);
                    break;
                case MonitorItemType.TYPE_TIME:
                    value = FormatDateObject(item.Value, settings.TimeFormat);
                    break;
                case MonitorItemType.TYPE_DATETIME:
                    value = FormatDateObject(item.Value, settings.DateTimeFormat);
                    break;

                case MonitorItemType.TYPE_UPTIME:
                    TimeSpan uptime = TimeSpan.FromSeconds(MathHelper.ToDouble(item.Value));
                    value = uptime.ToString(@"dd\:hh\:mm\:ss", CultureInfo.InvariantCulture);
                    break;

                case MonitorItemType.TYPE_RAW:
                default:
                    value = item.Value.ToString();
                    break;

                case MonitorItemType.TYPE_RAW_BYTES:
                    GetManager().PublishMessage(this, item.Topic, (byte[])item.Value);
                    return;
            }

            bool retained = item.Type == MonitorItemType.TYPE_RETAINED_STATE;
            SendMonitorValue(item.Topic, value, settings.Cacheable || item.PublishOnChangeOnly, retained);
        }

        private void PublishDiscoveryItem(MonitorItem item)
        {
            if (item?.CreateDiscoveryOptions == null || string.IsNullOrWhiteSpace(item.Topic))
                return;

            string key = item.DiscoveryKey ?? item.Topic;
            if (_discoveredTopics.Contains(key))
                return;

            var options = item.CreateDiscoveryOptions(item);
            options.Icon = GetDiscoveryOption(item.ConfigKey, options.Id, "icon", options.Icon);
            options.ValueTemplate = GetDiscoveryOption(item.ConfigKey, options.Id, "valueTemplate", options.ValueTemplate);
            options.Unit = GetDiscoveryOption(item.ConfigKey, options.Id, "unitOfMeasurement", options.Unit);
            options.Name = GetDiscoveryOption(item.ConfigKey, options.Id, "name", options.Name);

            GetManager().PublishDiscoveryMessage(this, item.Topic, item.ConfigKey, options);
            _discoveredTopics.Add(key);
        }

        private string FormatDateObject(object value, string format)
        {
            if (value is DateTime)
                return ((DateTime)value).ToString(format, CultureInfo.InvariantCulture);

            if (value is DateTimeOffset)
                return ((DateTimeOffset)value).ToString(format, CultureInfo.InvariantCulture);

            return value.ToString();
        }

        private void OnAgentResponse(object sender, AgentAddonResponseEventArgs e)
        {
            lock (_monitorLock)
            {
                if (_enabled && !_mqttDisconnected)
                    HandleAgentResponse(e);
            }
        }

        private void HandleAgentResponse(AgentAddonResponseEventArgs e)
        {
            foreach (IMonitor monitor in monitors)
            {
                Dictionary<string, AddonRequestType> agentRequests = monitor.GetAgentRequests();
                if (agentRequests == null || agentRequests.Count == 0)
                    continue;

                AddonRequestType requestType = (AddonRequestType)e.Data.requestType;
                string configKey = agentRequests.FirstOrDefault(x => x.Value == requestType).Key;

                if (configKey == null)
                    continue;

                var settings = GetMonitorSettings(configKey);
                if (!settings.Enabled)
                    continue;

                List<MonitorItem> items = monitor.OnAgentResponse(settings.Config, requestType, e.Data, e.Username);
                if (items == null || items.Count == 0)
                    continue;

                foreach (MonitorItem item in items)
                {
                    PublishDiscoveryItem(item);
                    PublishItem(item, settings);
                }
            }
        }

        private MonitorSettings GetMonitorSettings(string configKey)
        {
            if (!_monitorSettings.TryGetValue(configKey, out var settings))
            {
                settings = new MonitorSettings(_config.GetValue($"monitors:{configKey}"), _globalFormats);
                _monitorSettings.Add(configKey, settings);
            }

            return settings;
        }

        private string GetDiscoveryOption(string configKey, string subKey, string option, string defaultValue)
        {
            if (string.IsNullOrWhiteSpace(configKey) || string.IsNullOrWhiteSpace(option))
                return defaultValue;
            var value = _config.GetValue($"monitors:{configKey}:discoveryOptions:{subKey}:{option}", null);
            if (string.IsNullOrWhiteSpace(value))
                value = _config.GetValue($"discoveryOptions:{option}", defaultValue);

            return value;
        }

        private void SendMonitorValue(string topic, string value, bool cacheable, bool retained)
        {
            if (string.IsNullOrWhiteSpace(topic))
                return;

            if (cacheable)
            {
                if (_cache.ContainsKey(topic) && _cache[topic].CompareTo(value) == 0)
                    return;

                _cache[topic] = value;
            }

            if (retained)
                GetManager().PublishRetainedMessage(this, topic, value);
            else
                GetManager().PublishMessage(this, topic, value);
        }
    }
}
