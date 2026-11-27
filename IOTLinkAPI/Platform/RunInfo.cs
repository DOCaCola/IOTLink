namespace IOTLinkAPI.Platform
{
    public class RunInfo
    {
        public string Application { get; set; }

        public string CommandLine { get; set; }

        public string WorkingDir { get; set; }

        /// <summary>When specified, launch in this session instead of looking up a session by username.</summary>
        public int? SessionId { get; set; }

        public string Username { get; set; }

        public bool Visible { get; set; } = true;

        public bool Fallback { get; set; } = false;
    }
}
