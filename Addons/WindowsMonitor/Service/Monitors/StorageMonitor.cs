using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.HomeAssistant;
using IOTLinkAPI.Platform.Windows.Native;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace IOTLinkAddon.Service.Monitors
{
    class StorageMonitor : BaseMonitor
    {
        private static readonly string CONFIG_KEY = "HardDrive";

        internal delegate bool ReadDiskSpace(string rootPath, out long availableFreeSpace, out long totalSize, out long totalFreeSpace);
        private readonly ReadDiskSpace _readDiskSpace;

        public StorageMonitor() : this(Kernel32.GetDiskFreeSpaceExW) { }

        internal StorageMonitor(ReadDiskSpace readDiskSpace)
        {
            _readDiskSpace = readDiskSpace;
        }

        public override string GetConfigKey()
        {
            return CONFIG_KEY;
        }

        public override List<MonitorItem> GetMonitorItems(Configuration config, int interval)
        {
            List<MonitorItem> result = new List<MonitorItem>();

            foreach (DriveInfo driveInfo in DriveInfo.GetDrives())
            {
                if (driveInfo == null || driveInfo.DriveType != DriveType.Fixed || !driveInfo.IsReady)
                    continue;

                try
                {
                    if (!_readDiskSpace(driveInfo.Name, out long availableFreeSpace, out long totalSize, out long totalFreeSpace))
                    {
                        var error = new Win32Exception(Marshal.GetLastWin32Error());
                        LoggerHelper.Error("Could not read disk capacity for {0}: {1}", driveInfo.Name, error.Message);
                        continue;
                    }

                    string drive = driveInfo.Name.Remove(1, 2);
                    string topic = string.Format("Stats/HardDrive/{0}", drive);

                    long usedSpace = totalSize - totalFreeSpace;
                    int driveUsage = (int)((100.0 / totalSize) * usedSpace);

                    // Total Size
                    result.Add(new MonitorItem
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_DISK_SIZE,
                        Topic = topic + "/TotalSize",
                        Value = totalSize,
                        DiscoveryOptions = new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{drive}_TotalStorage",
                            Name = $"Storage {drive} - Total Storage",
                            Unit = "GB",
                            Icon = "mdi:harddisk"
                        }
                    });

                    // Available Free Space
                    result.Add(new MonitorItem
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_DISK_SIZE,
                        Topic = topic + "/AvailableFreeSpace",
                        Value = availableFreeSpace,
                        DiscoveryOptions = new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{drive}_AvailableFreeSpace",
                            Name = $"Storage {drive} - Available Free Space",
                            Unit = "GB",
                            Icon = "mdi:harddisk"
                        }
                    });

                    // Total Free Space
                    result.Add(new MonitorItem
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_DISK_SIZE,
                        Topic = topic + "/TotalFreeSpace",
                        Value = totalFreeSpace,
                        DiscoveryOptions = new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{drive}_TotalFreeSpace",
                            Name = $"Storage {drive} - Total Free Space",
                            Unit = "GB",
                            Icon = "mdi:harddisk"
                        }
                    });

                    // Used Space
                    result.Add(new MonitorItem
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_DISK_SIZE,
                        Topic = topic + "/UsedSpace",
                        Value = usedSpace,
                        DiscoveryOptions = new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{drive}_UsedSpace",
                            Name = $"Storage {drive} - Used Space",
                            Unit = "GB",
                            Icon = "mdi:harddisk"
                        }
                    });

                    // Drive Format
                    result.Add(new MonitorItem
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_RAW,
                        Topic = topic + "/DriveFormat",
                        Value = driveInfo.DriveFormat,
                        DiscoveryOptions = new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{drive}_DriveFormat",
                            Name = $"Storage {drive} - Format",
                            Icon = "mdi:harddisk"
                        }
                    });

                    // Drive Usage
                    result.Add(new MonitorItem
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_RAW,
                        Topic = topic + "/DriveUsage",
                        Value = driveUsage,
                        DiscoveryOptions = new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{drive}_DriveUsage",
                            Name = $"Storage {drive} - Usage",
                            Unit = "%",
                            Icon = "mdi:chart-donut"
                        }
                    });

                    // Drive Label
                    result.Add(new MonitorItem
                    {
                        ConfigKey = CONFIG_KEY,
                        Type = MonitorItemType.TYPE_RAW,
                        Topic = topic + "/VolumeLabel",
                        Value = driveInfo.VolumeLabel,
                        DiscoveryOptions = new HassDiscoveryOptions()
                        {
                            Component = HomeAssistantComponent.Sensor,
                            Id = $"{drive}_VolumeLabel",
                            Name = $"Storage {drive} - Label",
                            Icon = "mdi:harddisk"
                        }
                    });
                }
                catch (Exception ex)
                {
                    if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                        LoggerHelper.Error("Access to drives not allowed. Error: {0}", ex.ToString());
                    else if (ex is DriveNotFoundException)
                        LoggerHelper.Error("Drive not found. Error: {0}", ex.ToString());
                    else if (ex is IOException)
                        LoggerHelper.Error("Drive inaccessible. Error: {0}", ex.ToString());
                    else
                        LoggerHelper.Error("Error while getting drive information: {0}", ex.ToString());
                }
            }
            return result;
        }
    }
}
