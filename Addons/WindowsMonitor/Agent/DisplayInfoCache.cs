using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.Windows;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Threading;

namespace IOTLinkAddon.Agent
{
    internal sealed class DisplayInfoCache : IDisposable
    {
        private readonly Func<List<DisplayInfo>> _read;
        private readonly Func<bool> _isCurrent;
        private readonly Action _release;
        private readonly object _lock = new object();
        private List<DisplayInfo> _snapshot;
        private int _invalidated;
        private bool _subscribed;
        private bool _disposed;

        internal DisplayInfoCache() : this(new WindowsDisplayReader()) { }

        private DisplayInfoCache(WindowsDisplayReader reader) : this(reader.Read, () => reader.IsCurrent, reader.Dispose) { }

        internal DisplayInfoCache(Func<List<DisplayInfo>> read, Func<bool> isCurrent, Action release)
        {
            _read = read;
            _isCurrent = isCurrent;
            _release = release;
        }

        internal List<DisplayInfo> Get()
        {
            lock (_lock)
            {
                if (_disposed)
                    return null;
                if (!_subscribed)
                {
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                    _subscribed = true;
                }

                // Consume before reading so a notification during enumeration survives.
                bool changed = Interlocked.Exchange(ref _invalidated, 0) != 0;
                if (changed || _snapshot == null || !_isCurrent())
                {
                    _snapshot = null;
                    _snapshot = _read();
                }
                return _snapshot;
            }
        }

        internal void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

        private void OnDisplaySettingsChanged(object sender, EventArgs e) => Invalidate();

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                if (_subscribed)
                    SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _subscribed = false;
                _disposed = true;
                _snapshot = null;
                _release();
            }
        }
    }
}
