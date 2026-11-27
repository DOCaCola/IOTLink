using IOTLinkAPI.Platform.Windows.Native;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IOTLinkAPI.Platform.Windows
{
    /// <summary>Measures whole-machine CPU usage between successive samples.</summary>
    public sealed class CpuUsageSampler
    {
        private CpuTimes _previous;
        private uint _processorCount;
        private double _usage;

        internal readonly struct CpuTimes
        {
            internal long Idle { get; }
            internal long Total { get; }

            internal CpuTimes(long idle, long total)
            {
                Idle = idle;
                Total = total;
            }
        }

        /// <summary>Returns a percentage, or zero when establishing a new baseline.</summary>
        public double NextValue()
        {
            const ushort allProcessorGroups = ushort.MaxValue;
            uint processors = Kernel32.GetActiveProcessorCount(allProcessorGroups);
            if (processors == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            CpuTimes times = processors <= 64 ? ReadSystemTimes() : ReadAllGroups();
            return Update(times, processors);
        }

        internal double Update(CpuTimes times, uint processors)
        {
            long elapsed = times.Total - _previous.Total;
            long idle = times.Idle - _previous.Idle;
            if (processors != _processorCount || elapsed < 0 || idle < 0)
                _usage = 0;
            else if (elapsed == 0)
                return _usage;
            else
                _usage = 100d * (elapsed - Math.Min(idle, elapsed)) / elapsed;

            _previous = times;
            _processorCount = processors;
            return _usage;
        }

        internal static CpuTimes ReadAllGroups()
        {
            ushort count = Kernel32.GetActiveProcessorGroupCount();
            if (count == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // A shared timer thread may have default affinity spanning all groups. Binding it
            // to one group would narrow that affinity even after restoring its primary group.
            return Task.Factory.StartNew(() => SumGroups(count, ReadGroup), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
        }

        internal static CpuTimes SumGroups(ushort count, Func<ushort, CpuTimes> readGroup)
        {
            long idle = 0;
            long total = 0;
            for (ushort group = 0; group < count; group++)
            {
                CpuTimes times = readGroup(group);
                idle += times.Idle;
                total += times.Total;
            }
            return new CpuTimes(idle, total);
        }

        private static CpuTimes ReadGroup(ushort group)
        {
            // One logical processor selects the primary group. GetSystemTimes sums the entire group.
            var affinity = new Kernel32.GroupAffinity { Group = group, Mask = new UIntPtr(1) };
            if (!Kernel32.SetThreadGroupAffinity(Kernel32.GetCurrentThread(), ref affinity, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return ReadSystemTimes();
        }

        private static CpuTimes ReadSystemTimes()
        {
            if (!Kernel32.GetSystemTimes(out long idle, out long kernel, out long user))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // Windows includes idle time in kernel time.
            return new CpuTimes(idle, kernel + user);
        }
    }
}
