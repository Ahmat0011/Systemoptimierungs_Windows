using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemOptimierer.Core
{
    /// <summary>
    /// Windows-Job-Objekt, das einen Prozess samt aller später gestarteten Kindprozesse erfasst.
    /// Nötig, weil viele Deinstaller (z. B. NSIS) sich in %TEMP% kopieren, dort neu starten und
    /// der ursprüngliche Prozess sofort beendet ist, obwohl die Deinstallation noch läuft.
    /// </summary>
    internal sealed class ProcessJob : IDisposable
    {
        private const int JobObjectBasicAccountingInformation = 1;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        private readonly SafeJobHandle _handle;
        private bool _disposed;

        private ProcessJob(SafeJobHandle handle) => _handle = handle;

        /// <summary>Erstellt ein Job-Objekt und nimmt den Prozess auf. Liefert null, wenn das nicht möglich ist.</summary>
        public static ProcessJob? TryAttach(Process process)
        {
            if (process == null) return null;

            try
            {
                if (process.HasExited) return null;

                var handle = CreateJobObjectW(IntPtr.Zero, null);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }

                // Stellt sicher, dass keine verwaisten Kindprozesse übrig bleiben, falls das Job-Handle geschlossen wird.
                var extendedInfo = new JobObjectExtendedLimitInfo
                {
                    BasicLimitInformation = new JobObjectBasicLimitInfo
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };
                SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformation,
                    ref extendedInfo,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInfo>());

                if (!AssignProcessToJobObject(handle, process.Handle))
                {
                    handle.Dispose();
                    return null;
                }

                return new ProcessJob(handle);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Anzahl der noch laufenden Prozesse im Job (0 bei Abfragefehler, damit niemand endlos wartet).</summary>
        public int ActiveProcessCount
        {
            get
            {
                if (_disposed || _handle.IsClosed || _handle.IsInvalid) return 0;
                try
                {
                    return QueryInformationJobObject(
                            _handle,
                            JobObjectBasicAccountingInformation,
                            out JobObjectBasicAccountingInfo info,
                            (uint)Marshal.SizeOf<JobObjectBasicAccountingInfo>(),
                            IntPtr.Zero)
                        ? (int)info.ActiveProcesses
                        : 0;
                }
                catch (ObjectDisposedException)
                {
                    return 0;
                }
            }
        }

        public void Terminate()
        {
            if (_disposed || _handle.IsClosed || _handle.IsInvalid) return;
            try
            {
                TerminateJobObject(_handle, 1);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInfo
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInfo
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInfo
        {
            public JobObjectBasicLimitInfo BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeJobHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseHandle(handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(
            SafeJobHandle hJob,
            int jobObjectInfoClass,
            out JobObjectBasicAccountingInfo lpJobObjectInfo,
            uint cbJobObjectInfoLength,
            IntPtr lpReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeJobHandle hJob,
            int jobObjectInfoClass,
            ref JobObjectExtendedLimitInfo lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
