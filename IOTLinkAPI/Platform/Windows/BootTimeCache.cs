using System;

namespace IOTLinkAPI.Platform.Windows
{
    internal sealed class BootTimeCache
    {
        private readonly Func<DateTimeOffset> _readBootTime;
        private readonly object _lock = new object();
        private DateTimeOffset? _bootTime;

        internal BootTimeCache(Func<DateTimeOffset> readBootTime)
        {
            _readBootTime = readBootTime;
        }

        internal DateTimeOffset Get()
        {
            lock (_lock)
            {
                if (!_bootTime.HasValue)
                    _bootTime = _readBootTime();

                return _bootTime.Value;
            }
        }
    }
}
