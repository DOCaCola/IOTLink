using IOTLinkAPI.Common;
using IOTLinkAPI.Platform.Events.Process;
using System;
using System.Management;

using static IOTLinkAPI.Platform.Events.Process.ProcessHandlers;

namespace IOTLinkAPI.Platform.Windows
{
    public class ProcessEventManager
    {
        private static readonly string WMI_SCOPE = "root\\CIMV2";

        private static readonly int WMI_INTERVAL_PROCESS_PERF = 60;
        private static readonly string WMI_QUERY_PROCESS_PERF = "SELECT * FROM __InstanceModificationEvent WITHIN {0} WHERE TargetInstance Isa 'Win32_PerfFormattedData_PerfProc_Process' AND PreviousInstance.PercentProcessorTime != TargetInstance.PercentProcessorTime";

        private static readonly int WMI_INTERVAL_PROCESS_STATE = 1;
        private static readonly string WMI_QUERY_PROCESS_STATE = "SELECT * FROM __InstanceOperationEvent WITHIN {0} WHERE TargetInstance ISA 'Win32_Process'";

        private static readonly ProcessEventManager Instance = new ProcessEventManager();
        private readonly object _watcherLock = new object();

        private ManagementEventWatcher _processPerformanceWatcher;
        private ManagementEventWatcher _processStateWatcher;

        private volatile CacheContainer<int, ProcessInfoEntry> _cache;

        public event ProcessStartedEventHandler OnProcessStarted;
        public event ProcessStoppedEventHandler OnProcessStopped;
        public event ProcessUpdatedEventHandler OnProcessUpdated;

        public static ProcessEventManager GetInstance()
        {
            return Instance;
        }

        private ProcessEventManager() { }

        public ProcessInfo GetProcessInfo(int processId)
        {
            return _cache?.GetItem(processId)?.ProcessInfo;
        }

        internal bool IsMonitoring => _cache != null;

        public void SetMonitoringEnabled(bool enabled)
        {
            lock (_watcherLock)
            {
                if (enabled == IsMonitoring)
                    return;

                if (!enabled)
                {
                    StopWatchers();
                    return;
                }

                // Each subscription writes only to its own cache, including callbacks already in flight at stop.
                var cache = new CacheContainer<int, ProcessInfoEntry>();
                _cache = cache;
                try
                {
                    _processStateWatcher = new ManagementEventWatcher(WMI_SCOPE, string.Format(WMI_QUERY_PROCESS_STATE, WMI_INTERVAL_PROCESS_STATE));
                    _processStateWatcher.EventArrived += (sender, e) => OnProcessStateWatcherEvent(cache, e);
                    _processStateWatcher.Start();

                    _processPerformanceWatcher = new ManagementEventWatcher(WMI_SCOPE, string.Format(WMI_QUERY_PROCESS_PERF, WMI_INTERVAL_PROCESS_PERF));
                    _processPerformanceWatcher.EventArrived += (sender, e) => OnProcessPerformanceWatcherEvent(cache, e);
                    _processPerformanceWatcher.Start();
                }
                catch
                {
                    StopWatchers();
                    throw;
                }
            }
        }

        private void StopWatchers()
        {
            var cache = _cache;
            _cache = null;
            cache.Dispose();
            try
            {
                StopWatcher(ref _processPerformanceWatcher);
            }
            finally
            {
                StopWatcher(ref _processStateWatcher);
            }
        }

        private static void StopWatcher(ref ManagementEventWatcher watcher)
        {
            using (var current = watcher)
            {
                watcher = null;
                current?.Stop();
            }
        }

        private void OnProcessPerformanceWatcherEvent(CacheContainer<int, ProcessInfoEntry> cache, EventArrivedEventArgs e)
        {
            if (cache != _cache || e.NewEvent.Properties.Count == 0)
                return;

            var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var processId = Convert.ToInt32((uint)targetInstance["IDProcess"]);

            var processEntry = cache.GetItem(processId);
            if (processEntry != null)
            {
                ApplyPerformanceSample(processEntry.ProcessInfo, targetInstance);

                var eventArgs = new ProcessEventArgs { ProcessInfo = processEntry.ProcessInfo };
                if (!processEntry.Initialized)
                    OnProcessStarted?.Invoke(this, eventArgs);
                else
                    OnProcessUpdated?.Invoke(this, eventArgs);

                processEntry.Initialized = true;
                cache.UpdateItem(processId, processEntry);
            }
        }

        private void OnProcessStateWatcherEvent(CacheContainer<int, ProcessInfoEntry> cache, EventArrivedEventArgs e)
        {
            if (cache != _cache || e.NewEvent.Properties.Count == 0)
                return;

            var className = e.NewEvent.ClassPath.ClassName;
            var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];

            if (className.Contains("InstanceCreationEvent"))
                OnProcessStartEvent(cache, targetInstance);
            else if (className.Contains("InstanceDeletionEvent"))
                OnProcessStopEvent(cache, targetInstance);
            else
                OnProcessUpdateEvent(cache, targetInstance);
        }

        private void OnProcessStartEvent(CacheContainer<int, ProcessInfoEntry> cache, ManagementBaseObject targetInstance)
        {
            var processId = Convert.ToInt32((uint)targetInstance["ProcessId"]);

            cache.UpdateItem(processId, new ProcessInfoEntry { ProcessInfo = ParseProcessInformation(targetInstance) });
        }

        private void OnProcessStopEvent(CacheContainer<int, ProcessInfoEntry> cache, ManagementBaseObject targetInstance)
        {
            var processInfo = ParseProcessInformation(targetInstance);

            cache.RemoveKey(processInfo.Id);

            var eventArgs = new ProcessEventArgs { ProcessInfo = processInfo };
            OnProcessStopped?.Invoke(this, eventArgs);
        }

        private void OnProcessUpdateEvent(CacheContainer<int, ProcessInfoEntry> cache, ManagementBaseObject targetInstance)
        {
            var processId = Convert.ToInt32((uint)targetInstance["ProcessId"]);

            cache.GetItem(processId, () => new ProcessInfoEntry { ProcessInfo = ParseProcessInformation(targetInstance) });
        }

        private ProcessInfo ParseProcessInformation(ManagementBaseObject targetInstance)
        {
            return new ProcessInfo
            {
                Id = Convert.ToInt32((uint)targetInstance["ProcessId"]),
                ParentId = Convert.ToInt32((uint)targetInstance["ParentProcessId"]),
                SessionId = Convert.ToInt32((uint)targetInstance["SessionId"]),
                StartDateTime = ManagementDateTimeConverter.ToDateTime((string)targetInstance["CreationDate"]),
                ProcessName = ParseProcessName((string)targetInstance["Name"]),
            };
        }

        internal static void ApplyPerformanceSample(ProcessInfo process, ManagementBaseObject sample)
        {
            var processorTime = Convert.ToDouble(sample["PercentProcessorTime"]);
            process.ProcessorUsage = Math.Round(processorTime / Environment.ProcessorCount, 2);
            process.MemoryUsed = Convert.ToInt64(sample["WorkingSetPrivate"]);
        }

        private string ParseProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return null;

            return processName.ToLowerInvariant().Replace(".exe", "").Split(new char[] { '#' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        }
    }
}
