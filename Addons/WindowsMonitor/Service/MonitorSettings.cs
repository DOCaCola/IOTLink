using IOTLinkAddon.Common;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using System;

namespace IOTLinkAddon.Service
{
    internal readonly struct SizeFormat
    {
        public string Unit { get; }
        private readonly int _roundDigits;

        public SizeFormat(string format)
        {
            string[] parts = format.Split(':');
            Unit = parts[0] is "KB" or "MB" or "GB" or "TB" ? parts[0] : "B";
            _roundDigits = parts.Length > 1 ? MathHelper.ToInteger(parts[1]) : 0;
        }

        public double Convert(long bytes)
        {
            return Math.Round(UnitsHelper.ConvertSize(bytes, Unit), _roundDigits);
        }
    }

    internal sealed class MonitorSettings
    {
        private static readonly int DEFAULT_INTERVAL = 60;

        private static readonly string DEFAULT_FORMAT_DISK_SIZE = "GB";
        private static readonly string DEFAULT_FORMAT_MEMORY_SIZE = "MB";

        private static readonly string DEFAULT_FORMAT_DATE = "yyyy-MM-dd";
        private static readonly string DEFAULT_FORMAT_TIME = "HH:mm:ss";
        private static readonly string DEFAULT_FORMAT_DATETIME = "yyyy-MM-dd HH:mm:ss";

        public Configuration Config { get; }
        public bool Enabled { get; }
        public int Interval { get; }
        public bool Cacheable { get; }

        public SizeFormat DiskFormat { get; }
        public SizeFormat MemoryFormat { get; }
        public string DateFormat { get; }
        public string TimeFormat { get; }
        public string DateTimeFormat { get; }

        public MonitorSettings(Configuration config, Configuration globalFormats)
        {
            Config = config;
            Enabled = config?.GetValue("enabled", false) ?? false;
            Interval = config?.GetValue("interval", DEFAULT_INTERVAL) ?? DEFAULT_INTERVAL;
            Cacheable = config?.GetValue("cacheable", false) ?? false;

            var formats = config?.GetValue("formats");
            string ResolveFormat(string key, string defaultValue)
            {
                string value = formats?.GetValue(key, null);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;

                return globalFormats?.GetValue(key, defaultValue) ?? defaultValue;
            }

            DiskFormat = new SizeFormat(ResolveFormat("diskFormat", DEFAULT_FORMAT_DISK_SIZE));
            MemoryFormat = new SizeFormat(ResolveFormat("memoryFormat", DEFAULT_FORMAT_MEMORY_SIZE));
            DateFormat = ResolveFormat("dateFormat", DEFAULT_FORMAT_DATE);
            TimeFormat = ResolveFormat("timeFormat", DEFAULT_FORMAT_TIME);
            DateTimeFormat = ResolveFormat("dateTimeFormat", DEFAULT_FORMAT_DATETIME);
        }

        public bool PollAgent(AddonRequestType type) =>
            Enabled && type != AddonRequestType.REQUEST_DISPLAY_INFORMATION;

        public bool IsDue(uint counter) => Enabled && counter % Interval == 0;
    }
}
