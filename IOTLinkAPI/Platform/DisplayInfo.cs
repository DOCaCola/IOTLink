namespace IOTLinkAPI.Platform
{
    public class DisplayInfo
    {
        public string Availability { get; set; }
        public int ScreenHeight { get; set; }
        public int ScreenWidth { get; set; }
        public uint? SignalWidth { get; set; }
        public uint? SignalHeight { get; set; }
        public string MonitorName { get; set; }
        public string ConnectorType { get; set; }
        public int? Rotation { get; set; }
        public double? RefreshRate { get; set; }
        public bool? HdrEnabled { get; set; }

        public bool? IsPrimaryDisplay()
        {
            // Availability carries the MONITORINFO flags in the agent response.
            const uint monitorInfoPrimary = 1;
            return uint.TryParse(Availability, out uint flags) ? (flags & monitorInfoPrimary) != 0 : (bool?)null;
        }
    }
}
