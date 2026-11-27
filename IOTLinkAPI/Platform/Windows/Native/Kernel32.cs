using IOTLinkAPI.Platform.Windows.Native.Internal;
using System;
using System.Runtime.InteropServices;

namespace IOTLinkAPI.Platform.Windows.Native
{
#pragma warning disable 1591
    public class Kernel32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            private byte _systemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [StructLayout(LayoutKind.Sequential)]
        internal struct GroupAffinity
        {
            internal UIntPtr Mask;
            internal ushort Group;
            private ushort reserved0, reserved1, reserved2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetActiveProcessorCount(ushort group);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern ushort GetActiveProcessorGroupCount();

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, IntPtr previousAffinity);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceExW(string directoryName, out long freeBytesAvailableToCaller,
            out long totalNumberOfBytes, out long totalNumberOfFreeBytes);

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", CharSet = CharSet.Auto)]
        public static extern bool CloseHandle(IntPtr hHandle);

        [DllImport("kernel32.dll", EntryPoint = "WTSGetActiveConsoleSessionId", CharSet = CharSet.Auto)]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);
    }
}
