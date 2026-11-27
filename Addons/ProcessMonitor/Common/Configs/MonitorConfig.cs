using IOTLinkAddon.Common.Helpers;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace IOTLinkAddon.Common.Configs
{
    public class MonitorConfig
    {
        public string Key { get; set; }
        public List<string> ProcessNames { get; set; }
        public List<string> ProcessWindows { get; set; }
        public List<string> ProcessClassNames { get; set; }
        public GeneralConfig General { get; set; }

        internal static List<MonitorConfig> GetEnabledMonitors(Configuration config)
        {
            var enabled = new List<MonitorConfig>();
            if (config == null || !config.GetValue("enabled", false))
                return enabled;

            foreach (var entry in config.GetConfigurationList("monitors"))
            {
                try
                {
                    if (!entry.GetValue("configs:enabled", false))
                        continue;

                    var monitor = FromConfiguration(entry);
                    if (enabled.Any(existing => existing.Key == monitor.Key))
                        LoggerHelper.Warn("Duplicate process monitor: {0}", monitor.Key);
                    else
                        enabled.Add(monitor);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("Error loading process monitor {0}: {1}", entry.Key, ex);
                }
            }

            return enabled;
        }

        public static MonitorConfig FromConfiguration(Configuration configuration)
        {
            return new MonitorConfig
            {
                Key = configuration.Key.ToLowerInvariant(),
                ProcessNames = configuration.GetList<string>("processes").Select(x => ProcessHelper.CleanProcessName(x)).ToList(),
                ProcessWindows = configuration.GetList<string>("windows"),
                ProcessClassNames = configuration.GetList<string>("classnames"),
                General = GeneralConfig.FromConfiguration(configuration.GetValue("configs"))
            };
        }
    }
}
