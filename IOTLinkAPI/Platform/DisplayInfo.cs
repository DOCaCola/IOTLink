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
        public double? RefreshRate { get; set; }
        public bool? HdrEnabled { get; set; }
    }
}
