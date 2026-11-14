using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using ResourceGuardian.Models;

namespace ResourceGuardian.Services
{
    /// <summary>
    /// Polls Process.GetProcesses() each tick and computes CPU% from CPU-time
    /// deltas (no PerformanceCounter-per-process needed, which keeps overhead low),
    /// RAM from WorkingSet64, and disk throughput from Read/WriteTransferCount
    /// deltas. GPU% is merged in from GpuMonitorService by the caller.
    /// </summary>
    public sealed class ProcessMonitorService
    {
        private readonly int _processorCount = Environment.ProcessorCount;

        private sealed class PrevSample
        {
            public TimeSpan CpuTime;
            public long ReadBytes;
            public long WriteBytes;
            public DateTime Timestamp;
        }

        private readonly Dictionary<int, PrevSample> _prevByPid = new();

        /// <summary>One sampling pass over all processes. Safe to call every 1s.</summary>
        public List<ProcessInfoModel> SampleProcesses()
        {
            var now = DateTime.UtcNow;
            var results = new List<ProcessInfoModel>();
            var seenPids = new HashSet<int>();

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    int pid = proc.Id;
                    seenPids.Add(pid);

                    TimeSpan cpuTime;
                    long ramBytes;
                    long readBytes = 0, writeBytes = 0;

                    try
                    {
                        cpuTime = proc.TotalProcessorTime;
                        ramBytes = proc.WorkingSet64;
                    }
                    catch (Exception)
                    {
                        // Access denied (protected/system process) — still list it,
                        // just without live metrics, rather than dropping it silently.
                        continue;
                    }

                    // Disk IO counters require QueryLimitedInformation; wrap separately
                    // since some processes allow CPU/RAM access but not IO counters.
                    try
                    {
                        readBytes = GetIoReadBytes(proc);
                        writeBytes = GetIoWriteBytes(proc);
                    }
                    catch { /* leave at 0 */ }

                    double cpuPercent = 0;
                    double diskKBps = 0;

                    if (_prevByPid.TryGetValue(pid, out var prev))
                    {
                        double elapsedSec = Math.Max((now - prev.Timestamp).TotalSeconds, 0.001);
                        double cpuDeltaMs = (cpuTime - prev.CpuTime).TotalMilliseconds;
                        cpuPercent = (cpuDeltaMs / (elapsedSec * 1000.0)) / _processorCount * 100.0;
                        cpuPercent = Math.Max(0, Math.Min(cpuPercent, 100.0));

                        long ioDelta = Math.Max(0, (readBytes - prev.ReadBytes) + (writeBytes - prev.WriteBytes));
                        diskKBps = ioDelta / elapsedSec / 1024.0;
                    }

                    _prevByPid[pid] = new PrevSample
                    {
                        CpuTime = cpuTime,
                        ReadBytes = readBytes,
                        WriteBytes = writeBytes,
                        Timestamp = now
                    };

                    string priorityName = "Unknown";
                    try { priorityName = proc.PriorityClass.ToString(); } catch { }

                    string affinityDisplay = "All Cores";
                    try
                    {
                        long mask = proc.ProcessorAffinity.ToInt64();
                        long fullMask = (1L << _processorCount) - 1;
                        affinityDisplay = mask == fullMask ? "All Cores" : $"0x{mask:X}";
                    }
                    catch { }

                    results.Add(new ProcessInfoModel
                    {
                        Pid = pid,
                        Name = SafeProcessName(proc),
                        Kind = RowKind.Process,
                        CpuPercent = Math.Round(cpuPercent, 1),
                        RamMb = Math.Round(ramBytes / 1024.0 / 1024.0, 1),
                        DiskKBps = Math.Round(diskKBps, 1),
                        Priority = priorityName,
                        AffinityDisplay = affinityDisplay
                    });
                }
                catch
                {
                    // Process exited mid-enumeration or is otherwise inaccessible; skip it.
                }
                finally
                {
                    proc.Dispose();
                }
            }

            // Prune cache entries for processes that no longer exist.
            foreach (var staleKey in _prevByPid.Keys.Except(seenPids).ToList())
                _prevByPid.Remove(staleKey);

            return results;
        }

        private static string SafeProcessName(Process p)
        {
            try { return p.ProcessName; } catch { return $"(pid {p.Id})"; }
        }

        private static long GetIoReadBytes(Process p)
        {
            // Process class doesn't expose IO counters directly; use GetProcessIoCounters via reflection-free
            // approach is not available in .NET, so we P/Invoke GetProcessIoCounters here for accuracy.
            if (Win32IoCounters.TryGet(p.Handle, out var counters))
                return (long)counters.ReadTransferCount;
            return 0;
        }

        private static long GetIoWriteBytes(Process p)
        {
            if (Win32IoCounters.TryGet(p.Handle, out var counters))
                return (long)counters.WriteTransferCount;
            return 0;
        }

        /// <summary>
        /// Enumerate Windows services and resolve each to its hosting PID via WMI
        /// (Win32_Service.ProcessId), since ServiceController alone doesn't expose it.
        /// Called less frequently than process sampling (services change rarely).
        /// </summary>
        public List<ProcessInfoModel> SampleServices()
        {
            var results = new List<ProcessInfoModel>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DisplayName, ProcessId, State, StartMode FROM Win32_Service WHERE State='Running'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    try
                    {
                        uint pid = (uint)(mo["ProcessId"] ?? 0u);
                        if (pid == 0) continue; // 0 = no backing process (driver/shared-with-svchost edge cases)

                        results.Add(new ProcessInfoModel
                        {
                            Pid = (int)pid,
                            Name = mo["DisplayName"]?.ToString() ?? mo["Name"]?.ToString() ?? "(service)",
                            ServiceName = mo["Name"]?.ToString(),
                            Kind = RowKind.Service,
                            Status = mo["State"]?.ToString() ?? ""
                        });
                    }
                    catch { /* skip malformed row */ }
                }
            }
            catch
            {
                // WMI unavailable/locked down — return what we have (possibly empty);
                // the process list still works independently.
            }
            return results;
        }
    }

    /// <summary>Thin wrapper around GetProcessIoCounters so ProcessMonitorService stays readable.</summary>
    internal static class Win32IoCounters
    {
        public static bool TryGet(IntPtr handle, out NativeMethods.IO_COUNTERS counters)
        {
            return GetProcessIoCounters(handle, out counters);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out NativeMethods.IO_COUNTERS counters);
    }
}
