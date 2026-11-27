using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IOTLinkAPI.Platform.Windows
{
    internal static class DisplayConfiguration
    {
        private const uint ActiveVirtualAwarePaths = 0x2 | 0x10; // QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE
        private const int InsufficientBuffer = 122;

        internal static Dictionary<string, DisplayInfo> ReadActiveModes(Func<Dictionary<string, bool?>> readOutputColors)
        {
            var result = new Dictionary<string, DisplayInfo>(StringComparer.OrdinalIgnoreCase);
            var clones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(ActiveVirtualAwarePaths, out uint pathCount, out uint modeCount) != 0)
                    break;

                var paths = new PathInfo[pathCount];
                var modes = new ModeInfo[modeCount];
                int status = QueryDisplayConfig(ActiveVirtualAwarePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == InsufficientBuffer)
                    continue; // The topology changed between sizing and reading.
                if (status != 0)
                    break;
                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];
                    var source = new SourceName { Header = new DeviceHeader(DeviceQuery.SourceName,
                        Marshal.SizeOf<SourceName>(), path.Source.Adapter, path.Source.Id) };
                    if (DisplayConfigGetDeviceInfo(ref source) != 0)
                        continue;

                    var rate = path.Target.RefreshRate;
                    var signal = GetMode(modes, modeCount, path.Flags, path.Target.ModeIndex);
                    if (!rate.Hertz.HasValue)
                        rate = signal.VerticalSync;
                    var color = new AdvancedColorInfo { Header = new DeviceHeader(DeviceQuery.AdvancedColorInfo2,
                        Marshal.SizeOf<AdvancedColorInfo>(), path.Target.Adapter, path.Target.Id) };
                    bool? hdr = DisplayConfigGetDeviceInfo(ref color) == 0 ? color.IsHdrActive : null;
                    var target = new TargetName { Header = new DeviceHeader(DeviceQuery.TargetName,
                        Marshal.SizeOf<TargetName>(), path.Target.Adapter, path.Target.Id) };
                    string name = DisplayConfigGetDeviceInfo(ref target) == 0 ? target.FriendlyName : null;
                    if (result.ContainsKey(source.DeviceName))
                        clones.Add(source.DeviceName);
                    var desktop = GetMode(modes, modeCount, path.Flags, path.Source.ModeIndex);
                    MergeTarget(result, source.DeviceName, new DisplayInfo
                    {
                        ScreenWidth = desktop.DesktopWidth,
                        ScreenHeight = desktop.DesktopHeight,
                        MonitorName = string.IsNullOrWhiteSpace(name) ? null : name,
                        RefreshRate = rate.Hertz,
                        HdrEnabled = hdr,
                        SignalWidth = signal.ActiveWidth == 0 ? (uint?)null : signal.ActiveWidth,
                        SignalHeight = signal.ActiveHeight == 0 ? (uint?)null : signal.ActiveHeight
                    });
                }
                break;
            }

            // Older drivers/Windows builds expose HDR through DXGI per logical output.
            // Such an output cannot distinguish the physical targets of a cloned desktop.
            Dictionary<string, bool?> colors = null;
            foreach (var pair in result)
            {
                if (pair.Value.HdrEnabled.HasValue || clones.Contains(pair.Key))
                    continue;
                colors = colors ?? readOutputColors();
                if (colors.TryGetValue(pair.Key, out bool? hdr))
                    pair.Value.HdrEnabled = hdr;
            }
            return result;
        }

        internal static ModeInfo GetMode(ModeInfo[] modes, uint count, uint pathFlags, uint index)
        {
            // On a virtual-aware path the low word is a clone group or desktop image
            // index. The high word indexes the actual source/target mode.
            const uint supportsVirtualMode = 0x8;
            if ((pathFlags & supportsVirtualMode) != 0)
            {
                index >>= 16;
                if (index == ushort.MaxValue)
                    return default;
            }
            return index < count ? modes[index] : default;
        }

        internal static void MergeTarget(Dictionary<string, DisplayInfo> modes, string source, DisplayInfo target)
        {
            // A mirrored desktop has one GDI monitor but can drive targets with different modes.
            if (modes.TryGetValue(source, out var previous))
            {
                if (previous.MonitorName != target.MonitorName) previous.MonitorName = null;
                if (previous.RefreshRate != target.RefreshRate) previous.RefreshRate = null;
                if (previous.HdrEnabled != target.HdrEnabled) previous.HdrEnabled = null;
                if (previous.SignalWidth != target.SignalWidth || previous.SignalHeight != target.SignalHeight)
                    previous.SignalWidth = previous.SignalHeight = null;
                return;
            }
            modes[source] = target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AdapterId { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rational
        {
            public uint Numerator, Denominator;
            public double? Hertz => Numerator == 0 || Denominator == 0 ? (double?)null : (double)Numerator / Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathSource
        {
            public AdapterId Adapter;
            public uint Id, ModeIndex, StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathTarget
        {
            public AdapterId Adapter;
            public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
            public Rational RefreshRate;
            public uint ScanLineOrdering, Available, StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }

        // DISPLAYCONFIG_MODE_INFO's union starts at byte 16. Source modes contain desktop
        // dimensions, while target modes contain DISPLAYCONFIG_VIDEO_SIGNAL_INFO.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct ModeInfo
        {
            [FieldOffset(16)] public int DesktopWidth;
            [FieldOffset(20)] public int DesktopHeight;
            [FieldOffset(32)] public Rational VerticalSync;
            [FieldOffset(40)] public uint ActiveWidth;
            [FieldOffset(44)] public uint ActiveHeight;
        }

        private enum DeviceQuery : uint { SourceName = 1, TargetName = 2, AdvancedColorInfo2 = 15 }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceHeader
        {
            public DeviceQuery Type;
            public int Size;
            public AdapterId Adapter;
            public uint Id;

            public DeviceHeader(DeviceQuery type, int size, AdapterId adapter, uint id)
            {
                Type = type;
                Size = size;
                Adapter = adapter;
                Id = id;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SourceName
        {
            public DeviceHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TargetName
        {
            public DeviceHeader Header;
            public uint Flags, OutputTechnology;
            public ushort EdidManufacturerId, EdidProductCodeId;
            public uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }

        private enum ColorMode : uint { Sdr, WideColorGamut, Hdr }

        [StructLayout(LayoutKind.Sequential)]
        private struct AdvancedColorInfo
        {
            public DeviceHeader Header;
            public uint Flags, Encoding, BitsPerChannel;
            public ColorMode ActiveMode;
            public bool? IsHdrActive => ActiveMode == ColorMode.Hdr ? true :
                ActiveMode == ColorMode.Sdr || ActiveMode == ColorMode.WideColorGamut ? false : (bool?)null;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
            ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int DisplayConfigGetDeviceInfo(ref TargetName request);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);
    }
}
