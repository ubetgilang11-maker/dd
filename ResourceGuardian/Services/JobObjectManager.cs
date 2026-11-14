using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static ResourceGuardian.Services.NativeMethods;

namespace ResourceGuardian.Services
{
    /// <summary>
    /// One Job Object per throttled PID. Job Objects give us a *hard* enforced
    /// working-set (RAM) ceiling the OS itself trims to (the process gets paged
    /// out / page-faults rather than being allowed to grow), and, on Windows 8+,
    /// a disk bandwidth/IOPS cap that applies to every handle the process opens.
    ///
    /// Caveats (surfaced in the UI, not hidden):
    ///  - A process can only belong to ONE job unless it was created with
    ///    JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK / nested jobs are allowed (Win8+,
    ///    which this targets). Some processes (notably ones already in a job,
    ///    e.g. UWP/MSIX apps, or processes under another sandbox) will refuse
    ///    AssignProcessToJobObject and we report that back instead of silently
    ///    failing.
    ///  - The RAM limit is a *working set* clamp, not a hard allocation failure
    ///    for every workload; some processes will instead thrash/page more
    ///    under memory pressure rather than crash. This is the correct and
    ///    documented behavior of JOB_OBJECT_LIMIT_WORKINGSET /
    ///    JOB_OBJECT_LIMIT_PROCESS_MEMORY.
    /// </summary>
    public sealed class JobObjectManager : IDisposable
    {
        private readonly Dictionary<int, IntPtr> _jobsByPid = new();

        /// <summary>
        /// Create (or reuse) a job for this PID, assign the process into it, and apply a
        /// hard working-set / committed-memory ceiling in megabytes.
        /// </summary>
        public (bool success, string message) ApplyRamLimitMb(int pid, int maxMegabytes)
        {
            try
            {
                IntPtr hJob = GetOrCreateJob(pid);
                if (hJob == IntPtr.Zero)
                    return (false, "Failed to create Job Object.");

                IntPtr hProcess = OpenProcess(ProcessAccessFlags.SetQuota | ProcessAccessFlags.Terminate |
                                               ProcessAccessFlags.QueryInformation, false, pid);
                if (hProcess == IntPtr.Zero)
                    return (false, $"OpenProcess failed (Win32 error {Marshal.GetLastWin32Error()}). Run as Administrator.");

                try
                {
                    if (!AssignProcessToJobObject(hJob, hProcess))
                    {
                        int err = Marshal.GetLastWin32Error();
                        return (false, $"AssignProcessToJobObject failed (error {err}). " +
                                       "The process may already belong to another job (common for UWP/sandboxed apps).");
                    }

                    ulong bytes = (ulong)maxMegabytes * 1024UL * 1024UL;

                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                    {
                        BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                        {
                            LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_JOB_MEMORY
                        },
                        ProcessMemoryLimit = (UIntPtr)bytes,
                        JobMemoryLimit = (UIntPtr)bytes
                    };

                    int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    IntPtr p = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(info, p, false);
                        if (!SetInformationJobObject(hJob, JobObjectInfoClass.ExtendedLimitInformation, p, (uint)size))
                        {
                            int err = Marshal.GetLastWin32Error();
                            return (false, $"SetInformationJobObject (memory) failed (error {err}).");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(p);
                    }

                    return (true, $"RAM capped at {maxMegabytes} MB via Job Object.");
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Exception applying RAM limit: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply a disk bandwidth cap (in MB/sec) to the process's job. Requires
        /// Windows 8 / Server 2012 or later. 0 means "no cap" (removes the limit).
        /// </summary>
        public (bool success, string message) ApplyDiskLimitMBps(int pid, double maxMegabytesPerSecond)
        {
            try
            {
                IntPtr hJob = GetOrCreateJob(pid);
                if (hJob == IntPtr.Zero)
                    return (false, "Failed to create Job Object.");

                var rateInfo = new JOBOBJECT_IO_RATE_CONTROL_INFORMATION
                {
                    MaxBandwidth = maxMegabytesPerSecond > 0 ? (long)(maxMegabytesPerSecond * 1024 * 1024) : 0,
                    MaxIops = 0,
                    ReservationIops = 0,
                    VolumeName = IntPtr.Zero,
                    VolumeNameLength = 0,
                    BaseIoSize = 4096,
                    ControlFlags = maxMegabytesPerSecond > 0 ? JOBOBJECT_IO_RATE_CONTROL_ENABLE : 0
                };

                int size = Marshal.SizeOf<JOBOBJECT_IO_RATE_CONTROL_INFORMATION>();
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(rateInfo, p, false);
                    if (!SetInformationJobObject(hJob, JobObjectInfoClass.IoRateControlInformation, p, (uint)size))
                    {
                        int err = Marshal.GetLastWin32Error();
                        // ERROR_NOT_SUPPORTED (50) happens on Server editions without the storage
                        // QoS feature, or on non-NTFS/non-local volumes. We surface it rather than
                        // pretending the limit applied.
                        return (false, $"IO rate control not supported on this volume/OS (error {err}). " +
                                       "Falling back to IO priority throttle is recommended.");
                    }
                    return (true, $"Disk I/O capped at {maxMegabytesPerSecond:F1} MB/s via Job Object.");
                }
                finally
                {
                    Marshal.FreeHGlobal(p);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Exception applying disk limit: {ex.Message}");
            }
        }

        private IntPtr GetOrCreateJob(int pid)
        {
            if (_jobsByPid.TryGetValue(pid, out var existing))
                return existing;

            IntPtr hJob = CreateJobObject(IntPtr.Zero, $"ResourceGuardian_Job_{pid}_{Guid.NewGuid():N}");
            if (hJob != IntPtr.Zero)
                _jobsByPid[pid] = hJob;
            return hJob;
        }

        /// <summary>Release the job for a PID (does NOT kill the process; it just stops future enforcement).</summary>
        public void ReleaseJob(int pid)
        {
            if (_jobsByPid.TryGetValue(pid, out var hJob))
            {
                CloseHandle(hJob);
                _jobsByPid.Remove(pid);
            }
        }

        public void Dispose()
        {
            foreach (var kv in _jobsByPid)
                CloseHandle(kv.Value);
            _jobsByPid.Clear();
        }
    }
}
