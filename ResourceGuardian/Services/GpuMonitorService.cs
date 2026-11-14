using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace ResourceGuardian.Services
{
    /// <summary>
    /// Reads the "GPU Engine" performance counter category, which Windows (10 1803+)
    /// exposes with one counter instance per (process, adapter, engine) tuple, e.g.:
    ///   pid_1234_luid_0x... _phys_0_eng_0_engtype_3D
    /// We parse the pid out of the instance name and sum "Utilization Percentage"
    /// across every engine instance for that PID. This is read-only monitoring;
    /// there is no public API to cap a specific GPU engine for one process, which
    /// is why throttling falls back to ProcessThrottleService's duty-cycle suspend.
    /// </summary>
    public sealed class GpuMonitorService : IDisposable
    {
        private PerformanceCounterCategory? _category;
        private static readonly Regex PidRegex = new(@"pid_(\d+)_", RegexOptions.Compiled);
        // Cache live PerformanceCounter objects by instance name so we aren't
        // re-querying WMI/PDH metadata every tick (keeps background CPU low).
        private readonly Dictionary<string, PerformanceCounter> _liveCounters = new();
        private string[] _lastInstanceNames = Array.Empty<string>();
        private bool _available = true;

        public GpuMonitorService()
        {
            try
            {
                _category = new PerformanceCounterCategory("GPU Engine");
            }
            catch
            {
                _available = false; // No GPU counters on this system/driver
            }
        }

        public bool IsAvailable => _available;

        /// <summary>
        /// Returns a dictionary of PID -> summed GPU utilization % across all engines.
        /// Expensive-ish (creates/reads counters each call); call on the same cadence
        /// as the rest of the sampler, not faster than ~1s.
        /// </summary>
        public Dictionary<int, float> SampleGpuUsageByPid()
        {
            var result = new Dictionary<int, float>();
            if (!_available || _category == null) return result;

            string[] instanceNames;
            try
            {
                instanceNames = _category.GetInstanceNames();
            }
            catch
            {
                _available = false;
                return result;
            }

            // Reconcile the cache only when the instance set actually changed
            // (processes/engines starting or stopping) — the common case is "no
            // change", so most ticks skip straight to NextValue() calls below.
            if (!instanceNames.OrderBy(x => x).SequenceEqual(_lastInstanceNames.OrderBy(x => x)))
            {
                ReconcileCounters(instanceNames);
                _lastInstanceNames = instanceNames;
            }

            foreach (var kvp in _liveCounters)
            {
                var match = PidRegex.Match(kvp.Key);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int pid)) continue;

                float value;
                try { value = kvp.Value.NextValue(); }
                catch { continue; } // instance vanished between reconcile and read

                result[pid] = result.TryGetValue(pid, out var existing) ? existing + value : value;
            }

            return result;
        }

        private void ReconcileCounters(string[] currentInstances)
        {
            // Drop counters for instances that no longer exist.
            foreach (var staleKey in _liveCounters.Keys.Except(currentInstances).ToList())
            {
                _liveCounters[staleKey].Dispose();
                _liveCounters.Remove(staleKey);
            }

            // Add counters for new instances (only the ones we actually use).
            foreach (var instance in currentInstances.Except(_liveCounters.Keys))
            {
                if (!PidRegex.IsMatch(instance)) continue;
                try
                {
                    _liveCounters[instance] = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                }
                catch
                {
                    // Some engine instances don't expose this counter name on certain
                    // driver versions; skip rather than fail the whole sample.
                }
            }
        }

        public void Dispose()
        {
            foreach (var c in _liveCounters.Values) c.Dispose();
            _liveCounters.Clear();
        }
    }
}
