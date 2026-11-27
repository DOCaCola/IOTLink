using IOTLinkAPI.Helpers;
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
        // HDR-only changes can invalidate the DXGI factory without a display notification.
        private const int GraphicsCheckMilliseconds = 1000;
        private readonly Func<List<DisplayInfo>> _read;
        private readonly Func<bool> _isCurrent;
        private readonly Action _release;
        private readonly Action<List<DisplayInfo>> _publish;
        private readonly object _lock = new object();
        private List<DisplayInfo> _snapshot;
        private Timer _timer;
        private bool _invalidated;
        private bool _disposed;

        internal DisplayInfoCache(Action<List<DisplayInfo>> publish) : this(new WindowsDisplayReader(), publish) { }

        private DisplayInfoCache(WindowsDisplayReader reader, Action<List<DisplayInfo>> publish)
            : this(reader.Read, () => reader.IsCurrent, reader.Dispose, publish) { }

        internal DisplayInfoCache(Func<List<DisplayInfo>> read, Func<bool> isCurrent, Action release, Action<List<DisplayInfo>> publish)
        {
            _read = read;
            _isCurrent = isCurrent;
            _release = release;
            _publish = publish;
        }

        internal void SetEnabled(bool enabled)
        {
            lock (_lock)
            {
                if (_disposed || enabled == (_timer != null))
                    return;
                if (enabled)
                {
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                    _timer = new Timer(CheckForChanges, null, 0, GraphicsCheckMilliseconds);
                }
                else
                {
                    SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                    _timer.Dispose();
                    _timer = null;
                    _snapshot = null;
                    _release();
                }
            }
        }

        private void CheckForChanges(object state)
        {
            try { Refresh(false); }
            catch (Exception ex) { LoggerHelper.Error("Display update failed: {0}", ex.Message); }
        }

        internal void Refresh(bool sendCurrent)
        {
            lock (_lock)
            {
                if (_timer == null)
                    return;
                if (_invalidated || _snapshot == null || !_isCurrent())
                {
                    _invalidated = false;
                    _snapshot = null;
                    var displays = _read();
                    _publish(displays);
                    _snapshot = displays;
                }
                else if (sendCurrent)
                    _publish(_snapshot);
            }
        }

        internal void Invalidate()
        {
            lock (_lock)
            {
                _invalidated = true;
                _timer?.Change(0, GraphicsCheckMilliseconds);
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e) => Invalidate();

        public void Dispose()
        {
            lock (_lock)
            {
                SetEnabled(false);
                _disposed = true;
            }
        }
    }
}
