using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ResourceGuardian.Models
{
    public enum RowKind { Process, Service }

    public sealed class ProcessInfoModel : INotifyPropertyChanged
    {
        public int Pid { get; set; }
        public string Name { get; set; } = string.Empty;
        public RowKind Kind { get; set; } = RowKind.Process;
        public string? ServiceName { get; set; } // only for Kind == Service

        private double _cpuPercent;
        public double CpuPercent
        {
            get => _cpuPercent;
            set { _cpuPercent = value; OnChanged(); }
        }

        private double _ramMb;
        public double RamMb
        {
            get => _ramMb;
            set { _ramMb = value; OnChanged(); }
        }

        private double _diskKBps;
        public double DiskKBps
        {
            get => _diskKBps;
            set { _diskKBps = value; OnChanged(); }
        }

        private double _gpuPercent;
        public double GpuPercent
        {
            get => _gpuPercent;
            set { _gpuPercent = value; OnChanged(); }
        }

        private string _priority = "Normal";
        public string Priority
        {
            get => _priority;
            set { _priority = value; OnChanged(); }
        }

        private string _affinityDisplay = "All Cores";
        public string AffinityDisplay
        {
            get => _affinityDisplay;
            set { _affinityDisplay = value; OnChanged(); }
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnChanged(); }
        }

        // ---- Editable limit inputs bound to the UI (per-row apply) ----
        public int RamLimitMb { get; set; } = 512;
        public double DiskLimitMBps { get; set; } = 10;
        public double GpuDutyCycle { get; set; } = 0.5;
        public ulong AffinityMask { get; set; } = ulong.MaxValue;
        public bool IsThrottled { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
