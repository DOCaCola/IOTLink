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
        private static readonly int DEFAULT_INTERVAL = 60;

        private static readonly string DEFAULT_FORMAT_DISK_SIZE = "GB";
        private static readonly string DEFAULT_FORMAT_MEMORY_SIZE = "MB";

        private static readonly string DEFAULT_FORMAT_DATE = "yyyy-MM-dd";
        private static readonly string DEFAULT_FORMAT_TIME = "HH:mm:ss";
        private static readonly string DEFAULT_FORMAT_DATETIME = "yyyy-MM-dd HH:mm:ss";

        private readonly object _monitorLock = new object();
        private bool _enabled;
        private bool _mqttDisconnected;
        private Timer _monitorTimer;
        private uint _monitorCounter = 0;

        private string _configPath;
        private Configuration _config;

        private Dictionary<string, string> _cache = new Dictionary<string, string>();

        private List<string> _discoveryItems = new List<string>();

        private readonly List<IMonitor> monitors = new List<IMonitor>()
        {
            new CPUMonitor(),
            new MemoryMonitor(),
            new PowerMonitor(),
            new NetworkMonitor(),
            new StorageMonitor(),
            new SystemMonitor(),
            new UptimeMonitor(),
            new DisplayMonitor()
        };

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
            _enabled = config != null && config.GetValue("enabled", false);
            foreach (IMonitor monitor in monitors)
            {
                if (!_enabled || !CheckMonitorEnabled(monitor.GetConfigKey()))
                {
                    (monitor as IDisposable)?.Dispose();
                    continue;
                }

                try
                {
                    monitor.Init();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("Failed to initialize {0} monitor: {1}", monitor.GetConfigKey(), ex.Message);
                }
            }

            if (!_enabled)
                _monitorCounter = 0;

            RefreshMonitoring(sendValues);
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

            _monitorTimer = new Timer(1000) { AutoReset = false };
            _monitorTimer.Elapsed += OnMonitorTimerElapsed;
            _monitorTimer.Start();
            SetupDiscovery();
            if (sendValues)
                SendAllInformation();
        }

        private void SetupDiscovery()
        {
            foreach (var monitor in monitors)
            {
                string configKey = monitor.GetConfigKey();
                if (CheckMonitorEnabled(configKey))
                {
                    LoggerHelper.Debug("WindowsMonitorService::SetupDiscovery() - {0} Monitor - Sending configuration", configKey);

                    List<MonitorItem> items = monitor.GetMonitorItems(GetMonitorConfiguration(configKey), GetMonitorInterval(configKey));
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
            _discoveryItems.Clear();
        }

        private void SendAllInformation()
        {
            foreach (IMonitor monitor in monitors)
                ExecuteMonitor(monitor);
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
                        if (!CheckMonitorEnabled(entry.Key) || !CheckMonitorInterval(entry.Key))
                            continue;

                        LoggerHelper.Debug("WindowsMonitorService::ExecuteMonitor({0}) - Agent {1} - Request information ({2})", monitor.GetConfigKey(), entry.Key, entry.Value.ToString());

                        dynamic addonData = new ExpandoObject();
                        addonData.requestType = entry.Value;

                        GetManager().SendAgentRequest(this, addonData);
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
                if (CheckMonitorEnabled(configKey) && CheckMonitorInterval(configKey))
                {
                    LoggerHelper.Debug("WindowsMonitorService::ExecuteMonitor({0}) - Sending information", configKey);

                    List<MonitorItem> items = monitor.GetMonitorItems(GetMonitorConfiguration(configKey), GetMonitorInterval(configKey));
                    if (items == null)
                        return;

                    foreach (MonitorItem item in items)
                    {
                        PublishDiscoveryItem(item);
                        PublishItem(item);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("WindowsMonitorService::ExecuteMonitor({0}) - Error: {1}", monitor.GetConfigKey(), ex.ToString());
            }
        }

        private void PublishItem(MonitorItem item)
        {
            if (item == null || item.Value == null || string.IsNullOrWhiteSpace(item.Topic))
                return;

            string value = string.Empty;
            switch (item.Type)
            {
                case MonitorItemType.TYPE_DISK_SIZE:
                    value = FormatSizeObject(item.ConfigKey, "diskFormat", DEFAULT_FORMAT_DISK_SIZE, item.Value);
                    break;

                case MonitorItemType.TYPE_MEMORY_SIZE:
                    value = FormatSizeObject(item.ConfigKey, "memoryFormat", DEFAULT_FORMAT_MEMORY_SIZE, item.Value);
                    break;

                case MonitorItemType.TYPE_NETWORK_SPEED:
                case MonitorItemType.TYPE_NETWORK_SIZE:
                    value = item.Value.ToString();
                    break;

                case MonitorItemType.TYPE_DATE:
                    value = FormatDateObject(item.Value, GetMonitorFormat(item.ConfigKey, "dateFormat", DEFAULT_FORMAT_DATE));
                    break;
                case MonitorItemType.TYPE_TIME:
                    value = FormatDateObject(item.Value, GetMonitorFormat(item.ConfigKey, "timeFormat", DEFAULT_FORMAT_TIME));
                    break;
                case MonitorItemType.TYPE_DATETIME:
                    value = FormatDateObject(item.Value, GetMonitorFormat(item.ConfigKey, "dateTimeFormat", DEFAULT_FORMAT_DATETIME));
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

            SendMonitorValue(item.Topic, value, item.ConfigKey);
        }

        private void PublishDiscoveryItem(MonitorItem item)
        {
            if (item == null || item.DiscoveryOptions == null || string.IsNullOrWhiteSpace(item.Topic))
                return;

            if (_discoveryItems.Contains(item.Topic))
                return;

            item.DiscoveryOptions.Icon = GetDiscoveryOption(item.ConfigKey, item.DiscoveryOptions.Id, "icon", item.DiscoveryOptions.Icon);
            item.DiscoveryOptions.ValueTemplate = GetDiscoveryOption(item.ConfigKey, item.DiscoveryOptions.Id, "valueTemplate", item.DiscoveryOptions.ValueTemplate);
            item.DiscoveryOptions.Unit = GetDiscoveryOption(item.ConfigKey, item.DiscoveryOptions.Id, "unitOfMeasurement", item.DiscoveryOptions.Unit);
            item.DiscoveryOptions.Name = GetDiscoveryOption(item.ConfigKey, item.DiscoveryOptions.Id, "name", item.DiscoveryOptions.Name);

            GetManager().PublishDiscoveryMessage(this, item.Topic, item.ConfigKey, item.DiscoveryOptions); //TODO: name should be combination of configkey and discoveryOptions.name
            _discoveryItems.Add(item.Topic);
        }

        private string FormatSizeObject(string configKey, string formatKey, string defaultFormat, object value)
        {
            long sizeInBytes = MathHelper.ToLong(value, 0L);
            string formatStr = GetMonitorFormat(configKey, formatKey, defaultFormat);

            string format = formatStr.Contains(":") ? formatStr.Split(':')[0] : formatStr;
            int roundDigits = formatStr.Contains(":") ? MathHelper.ToInteger(formatStr.Split(':')[1]) : 0;

            double size = UnitsHelper.ConvertSize(sizeInBytes, format);
            return Math.Round(size, roundDigits).ToString(CultureInfo.InvariantCulture);
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
                if (_enabled)
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

                if (string.IsNullOrWhiteSpace(configKey) || !CheckMonitorEnabled(configKey))
                    continue;

                List<MonitorItem> items = monitor.OnAgentResponse(GetMonitorConfiguration(configKey), requestType, e.Data, e.Username);
                if (items == null || items.Count == 0)
                    continue;

                foreach (MonitorItem item in items)
                {
                    PublishDiscoveryItem(item);
                    PublishItem(item);
                }
            }
        }

        private bool CheckMonitorEnabled(string configKey)
        {
            if (string.IsNullOrWhiteSpace(configKey))
                return false;

            return GetMonitorEnabled(configKey);
        }

        private bool CheckMonitorInterval(string configKey)
        {
            if (string.IsNullOrWhiteSpace(configKey))
                return false;

            if ((_monitorCounter % GetMonitorInterval(configKey)) != 0)
                return false;

            return true;
        }

        private string GetMonitorFormat(string configKey, string formatKey, string defaultValue)
        {
            if (string.IsNullOrWhiteSpace(configKey) || string.IsNullOrWhiteSpace(formatKey))
                return defaultValue;

            string value = _config.GetValue($"monitors:{configKey}:formats:{formatKey}", null);
            if (string.IsNullOrWhiteSpace(value))
                value = _config.GetValue($"formats:{formatKey}", defaultValue);

            return value;
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

        private bool GetMonitorEnabled(string configKey)
        {
            if (string.IsNullOrWhiteSpace(configKey))
                return false;

            return _config.GetValue($"monitors:{configKey}:enabled", false);
        }

        private bool GetMonitorCacheable(string configKey)
        {
            if (string.IsNullOrWhiteSpace(configKey))
                return false;

            return _config.GetValue($"monitors:{configKey}:cacheable", false);
        }

        private int GetMonitorInterval(string configKey)
        {
            if (string.IsNullOrWhiteSpace(configKey))
                return DEFAULT_INTERVAL;

            return _config.GetValue($"monitors:{configKey}:interval", DEFAULT_INTERVAL);
        }

        private Configuration GetMonitorConfiguration(string configKey)
        {
            if (string.IsNullOrWhiteSpace(configKey))
                return null;

            return _config.GetValue($"monitors:{configKey}");
        }

        private void SendMonitorValue(string topic, string value, string configKey = null)
        {
            if (string.IsNullOrWhiteSpace(topic))
                return;

            if (GetMonitorCacheable(configKey))
            {
                if (_cache.ContainsKey(topic) && _cache[topic].CompareTo(value) == 0)
                    return;

                _cache[topic] = value;
            }

            GetManager().PublishMessage(this, topic, value);
        }
    }
}