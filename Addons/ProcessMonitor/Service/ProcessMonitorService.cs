using IOTLinkAddon.Common;
using IOTLinkAddon.Common.Configs;
using IOTLinkAddon.Common.Helpers;
using IOTLinkAddon.Common.Processes;
using IOTLinkAPI.Addons;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.Events;
using IOTLinkAPI.Platform.Events.Process;
using IOTLinkAPI.Platform.HomeAssistant;
using IOTLinkAPI.Platform.Windows;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Timers;

namespace IOTLinkAddon.Service
{
    public class ProcessMonitorService : ServiceAddon
    {
        private readonly object _configurationLock = new object();
        private string _configPath;
        private Configuration _config;

        private volatile List<ProcessMonitor> _monitors = new List<ProcessMonitor>();

        private Timer _monitorTimer;
        private uint _monitorCounter = 0;

        private readonly object _snapshotLock = new object();
        private long _requestId;
        private readonly Func<string, List<ProcessInformation>> _getProcessesByName;

        public ProcessMonitorService() : this(ProcessHelper.GetProcessesByName) { }

        internal ProcessMonitorService(Func<string, List<ProcessInformation>> getProcessesByName)
        {
            _getProcessesByName = getProcessesByName;
        }

        public override void Init(IAddonManager addonManager)
        {
            base.Init(addonManager);

            LoggerHelper.Verbose("ProcessMonitorService::Init() - Started");

            var cfgManager = ConfigurationManager.GetInstance();
            _configPath = Path.Combine(_currentPath, "config.yaml");
            cfgManager.GetConfiguration(_configPath);
            cfgManager.SetReloadHandler(_configPath, OnConfigReload);

            OnConfigReloadHandler += OnConfigReload;
            OnMQTTConnectedHandler += OnMQTTConnected;
            OnMQTTDisconnectedHandler += OnMQTTDisconnected;
            OnRefreshRequestedHandler += OnClearEvent;
            OnAgentResponseHandler += OnAgentResponse;

            SetupEventMonitor();
            Restart();

            LoggerHelper.Verbose("ProcessMonitorService::Init() - Completed");
        }

        private void Restart()
        {
            lock (_configurationLock)
                ApplyConfiguration(ConfigurationManager.GetInstance().GetConfiguration(_configPath));
        }

        internal void ApplyConfiguration(Configuration configuration)
        {
            lock (_snapshotLock)
            {
                _config = configuration;
                _monitors = MonitorConfig.GetEnabledMonitors(_config)
                    .Select(config => new ProcessMonitor { Name = config.Key, Config = config }).ToList();
                _monitorCounter = 0;
            }
            ProcessEventManager.GetInstance().SetMonitoringEnabled(_monitors.Count > 0);
            CleanTimers();
            if (_monitors.Count > 0)
                StartTimers();

            foreach (var monitor in _monitors)
            {
                try
                {
                    SetupDiscovery(monitor);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("ProcessMonitorService::SetupDiscovery({0}): {1}", monitor.Name, ex);
                }
            }
        }

        private void SetupEventMonitor()
        {
            var eventMonitor = ProcessEventManager.GetInstance();

            eventMonitor.OnProcessStarted += OnProcessStarted;
            eventMonitor.OnProcessStopped += OnProcessStopped;
        }

        private void SetupDiscovery(ProcessMonitor monitor)
        {
            if (!monitor.Config.General.Discoverable)
            {
                LoggerHelper.Debug("ProcessMonitorService::SetupDiscovery({0}) - Discovery is disabled.", monitor.Name);
                return;
            }

            SetupProcessDiscoveryState(monitor);
            if (monitor.Config.General.AdvancedMode)
            {
                SetupProcessDiscoveryMemory(monitor);
                SetupProcessDiscoveryProcessor(monitor);
                SetupProcessDiscoveryUptime(monitor);
                SetupProcessDiscoveryFullscreen(monitor);
            }
        }

        private void SetupProcessDiscoveryState(ProcessMonitor monitor)
        {
            if (monitor == null)
                return;

            var id = monitor.Name;
            var name = monitor.Config.General.DisplayName;

            HassDiscoveryOptions discoveryOptions = new HassDiscoveryOptions
            {
                Id = id,
                Name = name,
                DeviceClass = "moving",
                ValueTemplate = "{{ value_json.state }}",
                Component = HomeAssistantComponent.BinarySensor
            };

            GetManager().PublishDiscoveryMessage(this, GetStateTopic(monitor), "Processes", discoveryOptions);
        }

        private void SetupProcessDiscoveryMemory(ProcessMonitor monitor)
        {
            if (monitor == null)
                return;

            var id = string.Format("{0}_{1}", monitor.Name, "MemoryUsed");
            var name = string.Format("{0} {1}", monitor.Config.General.DisplayName, "Memory Used");

            HassDiscoveryOptions discoveryOptions = new HassDiscoveryOptions
            {
                Id = id,
                Name = name,
                Icon = "mdi:memory",
                Unit = "MB",
                ValueTemplate = "{{ (value_json.details or {}).get('MemoryUsed') }}",
                Component = HomeAssistantComponent.Sensor
            };

            GetManager().PublishDiscoveryMessage(this, GetStateTopic(monitor), "Processes", discoveryOptions);
        }

        private void SetupProcessDiscoveryProcessor(ProcessMonitor monitor)
        {
            if (monitor == null)
                return;

            var id = string.Format("{0}_{1}", monitor.Name, "ProcessorUsage");
            var name = string.Format("{0} {1}", monitor.Config.General.DisplayName, "Processor Usage");

            HassDiscoveryOptions discoveryOptions = new HassDiscoveryOptions
            {
                Id = id,
                Name = name,
                Icon = "mdi:gauge",
                Unit = "%",
                ValueTemplate = "{{ (value_json.details or {}).get('ProcessorUsage') }}",
                Component = HomeAssistantComponent.Sensor
            };

            GetManager().PublishDiscoveryMessage(this, GetStateTopic(monitor), "Processes", discoveryOptions);
        }

        private void SetupProcessDiscoveryUptime(ProcessMonitor monitor)
        {
            if (monitor == null)
                return;

            var id = string.Format("{0}_{1}", monitor.Name, "Uptime");
            var name = string.Format("{0} {1}", monitor.Config.General.DisplayName, "Uptime");
            var inSeconds = _config.GetValue("formats:uptimeInSeconds", false);

            HassDiscoveryOptions discoveryOptions = new HassDiscoveryOptions
            {
                Id = id,
                Name = name,
                Icon = "mdi:timer-outline",
                ValueTemplate = "{{ (value_json.details or {}).get('Uptime') }}",
                Unit = inSeconds ? "s" : "",
                Component = HomeAssistantComponent.Sensor
            };

            GetManager().PublishDiscoveryMessage(this, GetStateTopic(monitor), "Processes", discoveryOptions);
        }

        private void SetupProcessDiscoveryFullscreen(ProcessMonitor monitor)
        {
            if (monitor == null)
                return;

            var id = string.Format("{0}_{1}", monitor.Name, "FullScreen");
            var name = string.Format("{0} {1}", monitor.Config.General.DisplayName, "FullScreen");

            HassDiscoveryOptions discoveryOptions = new HassDiscoveryOptions
            {
                Id = id,
                Name = name,
                ValueTemplate = "{{ 'False' if value_json.state == 'OFF' else value_json.details.get('FullScreen') }}",
                PayloadOn = "True",
                PayloadOff = "False",
                Component = HomeAssistantComponent.BinarySensor
            };

            GetManager().PublishDiscoveryMessage(this, GetStateTopic(monitor), "Processes", discoveryOptions);
        }

        private void CleanTimers()
        {
            if (_monitorTimer != null)
            {
                _monitorTimer.Dispose();
                _monitorTimer = null;
            }
        }

        private void StartTimers()
        {
            _monitorTimer = new Timer();
            _monitorTimer.Elapsed += new ElapsedEventHandler(OnMonitorTimerElapsed);
            _monitorTimer.Interval = 1000;
            _monitorTimer.Start();
        }

        private void RestartTimers()
        {
            if (_monitorTimer == null)
                return;

            _monitorTimer.Stop();
            _monitorTimer.Start();
        }

        private void StopTimers()
        {
            if (_monitorTimer == null)
                return;

            _monitorTimer.Stop();
        }

        private void OnMQTTConnected(object sender, EventArgs e)
        {
            Restart();
        }

        private void OnMQTTDisconnected(object sender, EventArgs e)
        {
            lock (_configurationLock)
                StopTimers();
        }

        private void OnClearEvent(object sender, EventArgs e)
        {
            LoggerHelper.Verbose("ProcessMonitorService::OnClearEvent() - Clearing cache and resending information.");

            lock (_configurationLock)
            {
                lock (_snapshotLock)
                    foreach (var monitor in _monitors)
                        monitor.LastSnapshot = null;
                ExecuteMonitors(refresh: true);
            }
        }

        private void OnConfigReload(object sender, ConfigReloadEventArgs e)
        {
            if (e.ConfigType != ConfigType.CONFIGURATION_ADDON)
                return;

            LoggerHelper.Verbose("ProcessMonitorService::OnConfigReload() - Reloading configuration");

            Restart();
        }

        private void OnMonitorTimerElapsed(object source, ElapsedEventArgs e)
        {
            LoggerHelper.Verbose("ProcessMonitorService::OnMonitorTimerElapsed() - Started");

            lock (_configurationLock)
            {
                if (source != _monitorTimer)
                    return;

                ExecuteMonitors();
                if (_monitorCounter++ == uint.MaxValue)
                    _monitorCounter = 0;
            }

            LoggerHelper.Verbose("ProcessMonitorService::OnMonitorTimerElapsed() - Completed");
        }

        private void ExecuteMonitors(bool refresh = false)
        {
            StopTimers();

            LoggerHelper.Verbose("ProcessMonitorService::ExecuteMonitors() - Started");
            foreach (ProcessMonitor monitor in _monitors)
            {
                try
                {
                    ProcessInformation pending;
                    lock (_snapshotLock)
                        pending = monitor.PendingServiceSample;

                    if (refresh || _monitorCounter % monitor.Config.General.Interval == 0)
                        ExecuteMonitor(monitor);

                    // After one timer tick, publish the service observation if the agent hasn't
                    // answered and this process is still selected. Window filters require the agent.
                    if (!refresh && pending != null)
                    {
                        lock (_snapshotLock)
                        {
                            if (monitor.PendingServiceSample == pending)
                            {
                                PublishSnapshot(monitor, CreateProcessInfoMQTT(pending, false));
                                monitor.PendingServiceSample = null;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("ProcessMonitorService::ExecuteMonitors({0}) - Exception: {1}", monitor.Name, ex);
                }
            }
            LoggerHelper.Verbose("ProcessMonitorService::ExecuteMonitors() - Completed");

            RestartTimers();
        }

        private void ExecuteMonitor(ProcessMonitor monitor)
        {
            LoggerHelper.Verbose("ProcessMonitorService::ExecuteMonitor({0}) - Started", monitor.Name);

            var config = monitor.Config;
            var processes = new List<ProcessInformation>();
            foreach (string processName in config.ProcessNames)
            {
                processes.AddRange(_getProcessesByName(processName));
            }

            LoggerHelper.Verbose("ProcessMonitorService::ExecuteMonitor({0}) - {1} Processes Found", monitor.Name, processes.Count);
            if (processes.Count == 0)
            {
                LoggerHelper.Debug("ProcessMonitorService::ExecuteMonitor({0}) - Not Running", monitor.Name);
                ProcessSingleProcess(monitor);
                return;
            }

            ProcessGroup(monitor, processes);

            LoggerHelper.Verbose("ProcessMonitorService::ExecuteMonitor({0}) - Completed", monitor.Name);
        }

        private void ProcessGroup(ProcessMonitor monitor, List<ProcessInformation> processes)
        {
            LoggerHelper.Verbose("ProcessMonitorService::ProcessGroup({0}) - Started", monitor.Name);

            List<ProcessInformation> mainProcesses = new List<ProcessInformation>();
            foreach (ProcessInformation item in processes)
            {
                ProcessInformation pi = item;
                while (pi.Parent != null && IsChildProcess(pi))
                {
                    LoggerHelper.Debug("ProcessMonitorService::ProcessMonitor({0}) - Iterating over child process to find parent: {1}", monitor.Name, JsonConvert.SerializeObject(pi));
                    pi = pi.Parent;
                }

                if (!mainProcesses.Any(x => x.Id == pi.Id))
                    mainProcesses.Add(pi);
            }

            if (mainProcesses.Count == 0)
                return;

            ProcessSingleProcess(monitor, mainProcesses[0]);

            LoggerHelper.Verbose("ProcessMonitorService::ProcessGroup({0}) - Completed", monitor.Name);
        }

        private void ProcessSingleProcess(ProcessMonitor monitor, ProcessInformation processInfo = null)
        {
            lock (_snapshotLock)
            {
                if (!_monitors.Contains(monitor))
                    return;

                if (processInfo == null || processInfo.Status == ProcessState.NotRunning)
                {
                    monitor.SelectedProcess = null;
                    monitor.PendingServiceSample = null;
                    PublishSnapshot(monitor, null);
                    return;
                }

                var identity = (processInfo.Id, processInfo.StartDateTime);
                if (monitor.SelectedProcess != identity)
                {
                    monitor.SelectedProcess = identity;
                    monitor.LastAcceptedRequest = _requestId;
                    monitor.HasAgentDetails = false;
                    monitor.PendingServiceSample = null;
                }
                if (!monitor.HasAgentDetails && !MonitorHelper.HasConditions(monitor))
                    monitor.PendingServiceSample ??= processInfo;

                dynamic request = new ExpandoObject();
                request.requestType = AddonRequestType.REQUEST_PROCESS_INFORMATION;
                request.processId = processInfo.Id;
                request.monitorName = monitor.Name;
                request.requestId = ++_requestId;
                GetManager().SendAgentRequest(this, request);
            }
        }

        private void OnAgentResponse(object sender, AgentAddonResponseEventArgs e)
        {
            AddonRequestType requestType = (AddonRequestType)e.Data.requestType;
            if (requestType != AddonRequestType.REQUEST_PROCESS_INFORMATION)
                return;

            ProcessInformation processInfo = e.Data.requestData.ToObject<ProcessInformation>();
            string monitorName = (string)e.Data.monitorName;
            long requestId = (long)e.Data.requestId;
            lock (_snapshotLock)
            {
                var monitor = MonitorHelper.GetProcessMonitorByName(_monitors, monitorName);
                if (monitor == null || monitor.SelectedProcess != (processInfo.Id, processInfo.StartDateTime) ||
                    requestId <= monitor.LastAcceptedRequest)
                    return;

                monitor.LastAcceptedRequest = requestId;
                monitor.HasAgentDetails = true;
                monitor.PendingServiceSample = null;
                var matched = processInfo.Status == ProcessState.Running && MonitorHelper.CheckMonitorFilters(monitor, processInfo);
                PublishSnapshot(monitor, matched ? CreateProcessInfoMQTT(processInfo, true) : null);
            }
        }

        private void OnProcessStarted(object sender, ProcessEventArgs e)
        {
            string processName = ProcessHelper.CleanProcessName(e.ProcessInfo.ProcessName);

            List<ProcessMonitor> monitors = MonitorHelper.GetProcessMonitorsByProcessName(_monitors, processName);

            LoggerHelper.Debug("ProcessMonitorService::OnProcessStarted({0}) - {1} Monitors found.", processName, monitors.Count);
            if (monitors.Count == 0)
            {
                LoggerHelper.Debug("ProcessMonitorService::OnProcessStarted({0}) - Monitoring NOT FOUND (PID: {1} - Parent: {2})", processName, e.ProcessInfo.Id, e.ProcessInfo.ParentId);
                return;
            }

            ProcessInformation pi = ProcessHelper.GetProcessInformation(e.ProcessInfo.Id);
            if (pi == null)
            {
                LoggerHelper.Debug("ProcessMonitorService::OnProcessStarted({0}) - Unable to get information from PID {1}", processName, e.ProcessInfo.Id);
                return;
            }

            if (pi.Parent != null && IsChildProcess(pi))
            {
                LoggerHelper.Debug("ProcessMonitorService::OnProcessStarted({0}) - Spawning child {1} from {0}", processName, pi.Id, pi.Parent.Id);
                return;
            }

            LoggerHelper.Debug("ProcessMonitorService::OnProcessStarted({0}) - Process Detected (PID: {1} - Parent: {2})", processName, e.ProcessInfo.Id, e.ProcessInfo.ParentId);

            foreach (ProcessMonitor monitor in monitors)
                ProcessSingleProcess(monitor, pi);
        }

        private void OnProcessStopped(object sender, ProcessEventArgs e)
        {
            string processName = ProcessHelper.CleanProcessName(e.ProcessInfo.ProcessName);

            List<ProcessMonitor> monitors = MonitorHelper.GetProcessMonitorsByProcessName(_monitors, processName);

            LoggerHelper.Debug("ProcessMonitorService::OnProcessStarted({0}) - {1} Monitors found.", processName, monitors.Count);
            foreach (ProcessMonitor monitor in monitors)
                ExecuteMonitor(monitor);
        }

        private bool IsChildProcess(ProcessInformation processInfo)
        {
            return (processInfo.Parent != null) && (string.Compare(processInfo.ProcessName, processInfo.Parent.ProcessName, StringComparison.OrdinalIgnoreCase) == 0);
        }

        private ProcessInfoMQTT CreateProcessInfoMQTT(ProcessInformation processInfo, bool hasAgentDetails)
        {
            var uptime = (int)(DateTime.Now - processInfo.StartDateTime).TotalSeconds;

            ProcessInfoMQTT pi = new ProcessInfoMQTT
            {
                Id = processInfo.Id.ToString(),
                SessionId = processInfo.SessionId.ToString(),
                ProcessName = processInfo.ProcessName,
                MainWindowTitle = processInfo.MainWindowTitle,
                FullScreen = hasAgentDetails ? processInfo.FullScreen.ToString() : null,
                Windows = processInfo.Windows,
                ClassNames = processInfo.ClassNames,
                ProcessorUsage = hasAgentDetails ? processInfo.ProcessorUsage.ToString(CultureInfo.InvariantCulture) : null,
                MemoryUsed = hasAgentDetails ? FormatSizeObject(processInfo.MemoryUsed) : null,
            };

            pi.Status = processInfo.Status.ToString();
            pi.StartDateTime = FormatDateObject(processInfo.StartDateTime);
            pi.Uptime = FormatUptime(uptime);

            return pi;
        }

        private string FormatSizeObject(object value)
        {
            long sizeInBytes = MathHelper.ToLong(value, 0L);
            string formatStr = _config.GetValue("formats:memoryFormat", "MB");

            string format = formatStr.Contains(":") ? formatStr.Split(':')[0] : formatStr;
            int roundDigits = formatStr.Contains(":") ? MathHelper.ToInteger(formatStr.Split(':')[1]) : 0;

            double size = UnitsHelper.ConvertSize(sizeInBytes, format);
            return Math.Round(size, roundDigits).ToString(CultureInfo.InvariantCulture);
        }

        private string FormatDateObject(object value)
        {
            var format = _config.GetValue("formats:dateTimeFormat", "yyyy-MM-dd HH:mm:ss");
            if (value is DateTime)
                return ((DateTime)value).ToString(format, CultureInfo.InvariantCulture);

            return value.ToString();
        }

        private string FormatUptime(int value)
        {
            var inSeconds = _config.GetValue("formats:uptimeInSeconds", false);
            if (inSeconds)
                return value.ToString();

            TimeSpan uptime = TimeSpan.FromSeconds(MathHelper.ToDouble(value));
            return uptime.ToString(@"dd\:hh\:mm\:ss", CultureInfo.InvariantCulture);
        }

        private string GetStateTopic(ProcessMonitor monitor)
        {
            return $"Processes/{monitor.Name}/State";
        }

        private void PublishSnapshot(ProcessMonitor monitor, ProcessInfoMQTT details)
        {
            var payload = JsonConvert.SerializeObject(new { state = details == null ? "OFF" : "ON", details });
            if (monitor.Config.General.Cacheable && payload == monitor.LastSnapshot)
                return;

            GetManager().PublishRetainedMessage(this, GetStateTopic(monitor), payload);
            monitor.LastSnapshot = monitor.Config.General.Cacheable ? payload : null;
        }
    }
}
