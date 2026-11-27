using IOTLinkAddon.Common.Configs;
using IOTLinkAddon.Common.Processes;
using System;

namespace IOTLinkAddon.Service
{
    public class ProcessMonitor
    {
        internal (int Id, DateTime StartTime)? SelectedProcess { get; set; }
        internal bool HasAgentDetails { get; set; }
        internal long LastAcceptedRequest { get; set; }
        internal ProcessInformation PendingServiceSample { get; set; }
        internal string LastSnapshot { get; set; }

        public string Name { get; set; }
        public MonitorConfig Config { get; set; }
    }
}
