using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IOTLinkAPI.Platform.Windows
{
    // The agent holds one factory for the lifetime of its cached display snapshot.
    public sealed class WindowsDisplayReader : IDisposable
    {
        private IFactory _factory;

        public bool IsCurrent => _factory != null && _factory.IsCurrent();

        public List<DisplayInfo> Read()
        {
            Dispose();
            var iid = typeof(IFactory).GUID;
            CreateDXGIFactory1(ref iid, out _factory);
            return WindowsAPI.GetDisplays(ReadOutputColors);
        }

        public void Dispose()
        {
            if (_factory != null)
                Marshal.ReleaseComObject(_factory);
            _factory = null;
        }

        private Dictionary<string, bool?> ReadOutputColors()
        {
            var colors = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            if (_factory == null)
                return colors;
            for (uint i = 0; _factory.EnumAdapters1(i, out IAdapter adapter) == 0; i++)
            {
                try
                {
                    for (uint j = 0; adapter.EnumOutputs(j, out object output) == 0; j++)
                    {
                        try
                        {
                            if (output is IOutput6 modernOutput && modernOutput.GetDesc1(out OutputDescription desc) == 0
                                && desc.AttachedToDesktop != 0)
                            {
                                // DXGI_OUTPUT_DESC1 reports SDR (G22 P709) or HDR10 (PQ P2020).
                                const uint sdr = 0, hdr10 = 12;
                                colors[desc.DeviceName] = desc.ColorSpace == hdr10 ? true :
                                    desc.ColorSpace == sdr ? false : (bool?)null;
                            }
                        }
                        finally { Marshal.ReleaseComObject(output); }
                    }
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            return colors;
        }

        // The gaps skip inherited methods in the native DXGI COM interfaces.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFactory
        {
            void _VtblGap1_9();
            [PreserveSig] int EnumAdapters1(uint index, out IAdapter adapter);
            [PreserveSig]
            [return: MarshalAs(UnmanagedType.Bool)] bool IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAdapter
        {
            void _VtblGap1_4();
            [PreserveSig] int EnumOutputs(uint index, [MarshalAs(UnmanagedType.IUnknown)] out object output);
        }

        [ComImport, Guid("068346e8-aaec-4b84-add7-137f513f77a1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOutput6
        {
            void _VtblGap1_24();
            [PreserveSig] int GetDesc1(out OutputDescription description);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OutputDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom, AttachedToDesktop, Rotation;
            public IntPtr Monitor;
            public uint BitsPerColor, ColorSpace;
            public float RedX, RedY, GreenX, GreenY, BlueX, BlueY, WhiteX, WhiteY;
            public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid iid, out IFactory factory);
    }
}
