using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.HomeAssistant;
using IOTLinkAPI.Platform.Windows.Native;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;

namespace IOTLinkAddon.Service.Monitors
{
    class StorageMonitor : BaseMonitor
    {
        private const string ConfigKey = "HardDrive";
        internal const string StateTopic = "Stats/HardDrive/State";

        internal delegate bool ReadDiskSpace(string rootPath, out long availableFreeSpace, out long totalSize, out long totalFreeSpace);
        private readonly ReadDiskSpace _readDiskSpace;
        private readonly Func<DriveInfo[]> _getDrives;
        private readonly Dictionary<string, MonitorItem[]> _discovery = new();
        private SizeFormat _format;

        public StorageMonitor() : this(Kernel32.GetDiskFreeSpaceExW, DriveInfo.GetDrives) { }

        internal StorageMonitor(ReadDiskSpace readDiskSpace, Func<DriveInfo[]> getDrives)
        {
            _readDiskSpace = readDiskSpace;
            _getDrives = getDrives;
        }

        public override string GetConfigKey() => ConfigKey;

        public override void Init(MonitorSettings settings)
        {
            _format = settings.DiskFormat;
            _discovery.Clear();
        }

        private IEnumerable<DriveInfo> FixedDrives() => _getDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady);

        private MonitorItem[] DiscoveryFor(string letter)
        {
            if (_discovery.TryGetValue(letter, out var items))
                return items;

            MonitorItem Sensor(string field, string suffix, string label, string unit = null, string icon = "mdi:harddisk")
            {
                string id = $"{letter}_{suffix}";
                return new MonitorItem(letter)
                {
                    ConfigKey = ConfigKey,
                    Topic = StateTopic,
                    DiscoveryKey = StateTopic + "/" + id,
                    CreateDiscoveryOptions = _ => new HassDiscoveryOptions
                    {
                        Component = HomeAssistantComponent.Sensor,
                        Id = id,
                        Name = $"Storage {letter} - {label}",
                        Icon = icon,
                        Unit = unit,
                        ValueTemplate = "{{ value_json.drives.get('" + letter + "', {}).get('" + field + "') }}"
                    }
                };
            }

            items = new[]
            {
                Sensor("totalSize", "TotalStorage", "Total Storage", _format.Unit),
                Sensor("availableFreeSpace", "AvailableFreeSpace", "Available Free Space", _format.Unit),
                Sensor("totalFreeSpace", "TotalFreeSpace", "Total Free Space", _format.Unit),
                Sensor("usedSpace", "UsedSpace", "Used Space", _format.Unit),
                Sensor("driveFormat", "DriveFormat", "Format"),
                Sensor("driveUsage", "DriveUsage", "Usage", "%", "mdi:chart-donut"),
                Sensor("volumeLabel", "VolumeLabel", "Label")
            };
            _discovery.Add(letter, items);
            return items;
        }

        public override List<MonitorItem> GetDiscoveryItems(Configuration config, int interval) =>
            FixedDrives().SelectMany(d => DiscoveryFor(d.Name[..1])).ToList();

        public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
        {
            var items = new List<MonitorItem>();
            var drives = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var drive in FixedDrives())
            {
                string letter = drive.Name[..1];
                items.AddRange(DiscoveryFor(letter));
                if (!_readDiskSpace(drive.Name, out long availableFreeSpace, out long totalSize, out long totalFreeSpace))
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error());
                    LoggerHelper.Error("Could not read disk capacity for {0}: {1}", drive.Name, error.Message);
                    continue;
                }

                long used = totalSize - totalFreeSpace;
                string driveFormat = null, volumeLabel = null;
                try
                {
                    driveFormat = drive.DriveFormat;
                    volumeLabel = drive.VolumeLabel;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    LoggerHelper.Error("Could not read volume information for {0}: {1}", drive.Name, ex.Message);
                }
                drives.Add(letter, new
                {
                    totalSize = _format.Convert(totalSize),
                    availableFreeSpace = _format.Convert(availableFreeSpace),
                    totalFreeSpace = _format.Convert(totalFreeSpace),
                    usedSpace = _format.Convert(used),
                    driveUsage = totalSize > 0 ? (int?)((100.0 / totalSize) * used) : null,
                    driveFormat,
                    volumeLabel
                });
            }
            items.Add(new MonitorItem
            {
                ConfigKey = ConfigKey,
                Topic = StateTopic,
                Type = MonitorItemType.TYPE_RETAINED_STATE,
                Value = JsonConvert.SerializeObject(new { unit = _format.Unit, drives })
            });
            return items;
        }
    }
}
