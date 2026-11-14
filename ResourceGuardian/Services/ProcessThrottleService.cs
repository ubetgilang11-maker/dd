using System;
using System.Runtime.InteropServices;
using System.Threading;
using static ResourceGuardian.Services.NativeMethods;

namespace ResourceGuardian.Services
{
    public enum IoPriorityLevel
    {
        VeryLow = 0,
        Low = 1,
        Normal = 2
        // High (3) is reserved for the system and ignored for user-mode sets.
    }

    /// <summary>
    /// Applies CPU affinity, process priority class, and IO priority directly via
    /// Win32/NT APIs. Also implements the GPU "soft throttle" fallback: since WDDM
    /// does not expose a per-process GPU bandwidth cap to user-mode applications,
    /// the only generically reliable lever is duty-cycling the process (brief
    /// suspend/resume) to reduce how much wall-clock time it gets to submit work
    /// to the GPU queue. This is explicitly an approximation, not a true cap.
    /// </summary>
    public sealed class ProcessThrottleService
    {
        public (bool success, string message) SetAffinity(int pid, ulong coreMask)
        {
            IntPtr h = OpenProcess(ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryInformation, false, pid);
            if (h == IntPtr.Zero)
                return (false, $"OpenProcess failed (error {Marshal.GetLastWin32Error()}).");
            try
            {
                if (!SetProcessAffinityMask(h, (UIntPtr)coreMask))
                    return (false, $"SetProcessAffinityMask failed (error {Marshal.GetLastWin32Error()}).");
                return (true, $"Affinity set to mask 0x{coreMask:X}.");
            }
            finally { CloseHandle(h); }
        }

        public (bool success, string message) SetPriority(int pid, PriorityClass priority)
        {
            IntPtr h = OpenProcess(ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryInformation, false, pid);
            if (h == IntPtr.Zero)
                return (false, $"OpenProcess failed (error {Marshal.GetLastWin32Error()}).");
            try
            {
                if (!SetPriorityClass(h, (uint)priority))
                    return (false, $"SetPriorityClass failed (error {Marshal.GetLastWin32Error()}).");
                return (true, $"Priority class set to {priority}.");
            }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// Sets IO priority via the semi-documented NtSetInformationProcess /
        /// ProcessIoPriority (0x21) class. Stable across Vista->24H2 but is an
        /// internal NT API, not in the public Win32 surface, so we guard every
        /// call and never assume success.
        /// </summary>
        public (bool success, string message) SetIoPriority(int pid, IoPriorityLevel level)
        {
            IntPtr h = OpenProcess(ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryInformation, false, pid);
            if (h == IntPtr.Zero)
                return (false, $"OpenProcess failed (error {Marshal.GetLastWin32Error()}).");
            try
            {
                int value = (int)level;
                int status = NtSetInformationProcess(h, ProcessIoPriority, ref value, sizeof(int));
                if (status != 0) // NTSTATUS 0 == STATUS_SUCCESS
                    return (false, $"NtSetInformationProcess(IoPriority) returned NTSTATUS 0x{status:X8}.");
                return (true, $"IO priority set to {level}.");
            }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// GPU duty-cycle throttle fallback. Suspends the process for
        /// (1-dutyCycle) of each window, resumes it for dutyCycle. Runs on a
        /// dedicated background thread per PID; call StopGpuThrottle to cancel.
        /// dutyCycle: 0.1 = process runs ~10% of real time, 1.0 = effectively off.
        /// NOTE: this throttles the *whole process* (all engines: CPU scheduling
        /// too), because WDDM does not expose a GPU-only user-mode quota API.
        /// Use sparingly and prefer it only when CPU/affinity/priority alone
        /// isn't reducing GPU engine usage enough.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _gpuThrottles = new();

        public (bool success, string message) StartGpuDutyCycleThrottle(int pid, double dutyCycle, int windowMs = 200)
        {
            dutyCycle = Math.Clamp(dutyCycle, 0.05, 1.0);
            StopGpuThrottle(pid); // replace any existing throttle

            var cts = new CancellationTokenSource();
            _gpuThrottles[pid] = cts;
            var token = cts.Token;

            Thread worker = new Thread(() =>
            {
                IntPtr h = OpenProcess(ProcessAccessFlags.SuspendResume | ProcessAccessFlags.QueryInformation, false, pid);
                if (h == IntPtr.Zero) return;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        int runMs = (int)(windowMs * dutyCycle);
                        int suspendMs = windowMs - runMs;

                        if (suspendMs > 0)
                        {
                            NtSuspendProcess(h);
                            if (token.WaitHandle.WaitOne(suspendMs)) break;
                            NtResumeProcess(h);
                        }
                        if (token.WaitHandle.WaitOne(runMs)) break;
                    }
                }
                finally
                {
                    // Always ensure the process is left in a running state on exit.
                    NtResumeProcess(h);
                    CloseHandle(h);
                }
            })
            { IsBackground = true, Name = $"GpuThrottle-{pid}" };

            worker.Start();
            return (true, $"GPU duty-cycle throttle started at {dutyCycle:P0}.");
        }

        public void StopGpuThrottle(int pid)
        {
            if (_gpuThrottles.TryRemove(pid, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        public void StopAllGpuThrottles()
        {
            foreach (var pid in _gpuThrottles.Keys)
                StopGpuThrottle(pid);
        }
    }
}
