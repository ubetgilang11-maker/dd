using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ResourceGuardian.Models;
using ResourceGuardian.Services;
using static ResourceGuardian.Services.NativeMethods;

namespace ResourceGuardian.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly ProcessMonitorService _monitor = new();
        private readonly GpuMonitorService _gpuMonitor = new();
        private readonly JobObjectManager _jobManager = new();
        private readonly ProcessThrottleService _throttle = new();

        private readonly DispatcherTimer _processTimer;
        private readonly DispatcherTimer _serviceTimer;

        public ObservableCollection<ProcessInfoModel> Rows { get; } = new();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnChanged(); ApplyFilter(); }
        }

        private bool _showServices;
        public bool ShowServices
        {
            get => _showServices;
            set { _showServices = value; OnChanged(); RefreshNow(); }
        }

        private ProcessInfoModel? _selectedRow;
        public ProcessInfoModel? SelectedRow
        {
            get => _selectedRow;
            set { _selectedRow = value; OnChanged(); }
        }

        private string _statusBarText = "Ready.";
        public string StatusBarText
        {
            get => _statusBarText;
            set { _statusBarText = value; OnChanged(); }
        }

        public bool GpuMonitoringAvailable => _gpuMonitor.IsAvailable;

        public RelayCommand ApplyLimitsCommand { get; }
        public RelayCommand ResetLimitsCommand { get; }
        public RelayCommand RefreshCommand { get; }

        // Backing full (unfiltered) snapshot of process rows, kept so filtering
        // doesn't require re-sampling.
        private System.Collections.Generic.List<ProcessInfoModel> _allProcessRows = new();
        private System.Collections.Generic.List<ProcessInfoModel> _allServiceRows = new();

        public MainViewModel()
        {
            ApplyLimitsCommand = new RelayCommand(_ => ApplySelectedLimits(), _ => SelectedRow != null);
            ResetLimitsCommand = new RelayCommand(_ => ResetSelectedLimits(), _ => SelectedRow != null);
            RefreshCommand = new RelayCommand(_ => RefreshNow());

            // 1s cadence keeps background overhead low while staying responsive;
            // CPU math is O(number of processes) with no per-process PerformanceCounter
            // objects, which is the main cost driver to avoid.
            _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _processTimer.Tick += (_, _) => TickProcesses();
            _processTimer.Start();

            // Services/WMI query is comparatively expensive and services churn rarely;
            // poll them far less often.
            _serviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _serviceTimer.Tick += (_, _) => TickServices();
            _serviceTimer.Start();

            TickProcesses();
            if (ShowServices) TickServices();
        }

        private void TickProcesses()
        {
            try
            {
                var rows = _monitor.SampleProcesses();

                if (_gpuMonitor.IsAvailable)
                {
                    var gpuByPid = _gpuMonitor.SampleGpuUsageByPid();
                    foreach (var row in rows)
                        if (gpuByPid.TryGetValue(row.Pid, out var gpu))
                            row.GpuPercent = Math.Round(Math.Min(gpu, 100.0), 1);
                }

                _allProcessRows = rows;
                if (!ShowServices) ApplyFilter();

                StatusBarText = $"Monitoring {rows.Count} processes" +
                                 (_gpuMonitor.IsAvailable ? "" : " (GPU counters unavailable on this system)") +
                                 $" — {DateTime.Now:T}";
            }
            catch (Exception ex)
            {
                StatusBarText = $"Sampling error: {ex.Message}";
            }
        }

        private void TickServices()
        {
            try
            {
                _allServiceRows = _monitor.SampleServices();
                if (ShowServices) ApplyFilter();
            }
            catch (Exception ex)
            {
                StatusBarText = $"Service sampling error: {ex.Message}";
            }
        }

        public void RefreshNow()
        {
            TickProcesses();
            if (ShowServices) TickServices();
        }

        private void ApplyFilter()
        {
            var source = ShowServices ? _allServiceRows : _allProcessRows;
            var filtered = string.IsNullOrWhiteSpace(SearchText)
                ? source
                : source.Where(r =>
                        r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        r.Pid.ToString().Contains(SearchText))
                    .ToList();

            // Diff-update rather than clear+add so the DataGrid doesn't lose
            // scroll position / selection every tick.
            SyncCollection(filtered);
        }

        private void SyncCollection(System.Collections.Generic.List<ProcessInfoModel> latest)
        {
            var latestByPid = latest.ToDictionary(r => r.Pid);

            for (int i = Rows.Count - 1; i >= 0; i--)
            {
                if (!latestByPid.ContainsKey(Rows[i].Pid))
                    Rows.RemoveAt(i);
            }

            var existingPids = Rows.Select(r => r.Pid).ToHashSet();

            foreach (var fresh in latest)
            {
                if (existingPids.Contains(fresh.Pid))
                {
                    var existing = Rows.First(r => r.Pid == fresh.Pid);
                    existing.CpuPercent = fresh.CpuPercent;
                    existing.RamMb = fresh.RamMb;
                    existing.DiskKBps = fresh.DiskKBps;
                    existing.GpuPercent = fresh.GpuPercent;
                    existing.Priority = fresh.Priority;
                    existing.AffinityDisplay = fresh.AffinityDisplay;
                    existing.Status = fresh.Status;
                }
                else
                {
                    Rows.Add(fresh);
                }
            }
        }

        private void ApplySelectedLimits()
        {
            var row = SelectedRow;
            if (row == null) return;

            var messages = new System.Text.StringBuilder();

            // CPU affinity
            var affResult = _throttle.SetAffinity(row.Pid, row.AffinityMask);
            messages.AppendLine($"Affinity: {(affResult.success ? "OK" : "FAILED")} — {affResult.message}");

            // RAM hard cap via Job Object
            var ramResult = _jobManager.ApplyRamLimitMb(row.Pid, row.RamLimitMb);
            messages.AppendLine($"RAM: {(ramResult.success ? "OK" : "FAILED")} — {ramResult.message}");

            // Disk I/O: try Job Object bandwidth cap first; fall back to IO priority.
            var diskResult = _jobManager.ApplyDiskLimitMBps(row.Pid, row.DiskLimitMBps);
            if (!diskResult.success)
            {
                var ioPrioResult = _throttle.SetIoPriority(row.Pid, IoPriorityLevel.VeryLow);
                messages.AppendLine($"Disk (Job cap failed, used IO priority instead): {(ioPrioResult.success ? "OK" : "FAILED")} — {ioPrioResult.message}");
            }
            else
            {
                messages.AppendLine($"Disk: OK — {diskResult.message}");
            }

            // GPU: no true per-process cap exists via WDDM for user-mode apps, so we
            // duty-cycle suspend/resume as a best-effort approximation.
            var gpuResult = _throttle.StartGpuDutyCycleThrottle(row.Pid, row.GpuDutyCycle);
            messages.AppendLine($"GPU (approximate, suspend/resume duty-cycle): {(gpuResult.success ? "OK" : "FAILED")} — {gpuResult.message}");

            row.IsThrottled = true;
            StatusBarText = $"Applied limits to {row.Name} (PID {row.Pid}).";
            System.Diagnostics.Debug.WriteLine(messages.ToString());
        }

        private void ResetSelectedLimits()
        {
            var row = SelectedRow;
            if (row == null) return;

            _throttle.StopGpuThrottle(row.Pid);
            _throttle.SetPriority(row.Pid, PriorityClass.Normal);

            long fullMask = (1L << Environment.ProcessorCount) - 1;
            _throttle.SetAffinity(row.Pid, (ulong)fullMask);
            _throttle.SetIoPriority(row.Pid, IoPriorityLevel.Normal);
            _jobManager.ApplyDiskLimitMBps(row.Pid, 0); // 0 clears the bandwidth cap
            _jobManager.ReleaseJob(row.Pid);

            row.IsThrottled = false;
            StatusBarText = $"Reset limits for {row.Name} (PID {row.Pid}). Note: a RAM Job Object ceiling cannot be removed without releasing the process from its job; it has been released, so a freshly reassigned job would be required to cap it again.";
        }

        public void Shutdown()
        {
            _processTimer.Stop();
            _serviceTimer.Stop();
            _throttle.StopAllGpuThrottles();
            _jobManager.Dispose();
            _gpuMonitor.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
