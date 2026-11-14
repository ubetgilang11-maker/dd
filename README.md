# Resource Guardian

Per-process CPU / RAM / Disk I/O / GPU monitor and throttler for Windows 10/11 (x64).
WPF, .NET 8, dark theme, admin-elevated.

## Build & Run

1. Open `ResourceGuardian.sln` in Visual Studio 2022 (17.8+) with the **.NET desktop
   development** workload installed.
2. Set the solution platform to **x64** (required — Job Object / NT structures are
   pointer-size sensitive).
3. Build (`Ctrl+Shift+B`). NuGet restores `System.ServiceProcess.ServiceController`
   and `System.Management` automatically.
4. Run (`F5`). Because of `app.manifest`'s `requireAdministrator`, Windows will show
   a UAC prompt every launch — this is required, not optional, for most of the
   throttling features to work on processes you don't own.

Or from the CLI:
```
dotnet build -c Release -p:Platform=x64
```
The output `.exe` carries the embedded manifest, so double-clicking it (outside VS)
also triggers UAC elevation.

## Project layout

```
ResourceGuardian/
  app.manifest                     requireAdministrator + DPI awareness
  App.xaml / App.xaml.cs           dark theme resource dictionary
  MainWindow.xaml / .xaml.cs       table UI + per-row limit controls
  Models/
    ProcessInfoModel.cs            bindable row (CPU/RAM/Disk/GPU + limit inputs)
  Services/
    NativeMethods.cs               all P/Invoke declarations in one place
    JobObjectManager.cs            Job Objects: hard RAM cap + disk bandwidth cap
    ProcessThrottleService.cs      affinity, priority class, IO priority, GPU duty-cycle
    ProcessMonitorService.cs       per-tick CPU%/RAM/Disk sampling + service enumeration
    GpuMonitorService.cs           PDH "GPU Engine" counters, cached by instance
  ViewModels/
    MainViewModel.cs               polling timers, filtering, apply/reset orchestration
    RelayCommand.cs                basic ICommand
```

## How each limit is actually enforced

| Resource | Mechanism | Notes |
|---|---|---|
| CPU (core pinning) | `SetProcessAffinityMask` | Reversible any time; doesn't cap total CPU%, only which cores are eligible. |
| CPU (overall share) | `SetPriorityClass` (Idle/BelowNormal/.../High) | Soft — the scheduler still gives it CPU when nothing else wants it. |
| RAM (hard cap) | Job Objects: `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` with `JOB_OBJECT_LIMIT_PROCESS_MEMORY` / `_JOB_MEMORY` | This is a genuine OS-enforced ceiling — allocations beyond it fail inside the target process. A process can only belong to one job at a time; some sandboxed/UWP processes already have one and will refuse assignment (surfaced as an error in the status bar, not swallowed). |
| Disk I/O | Job Objects: `JOBOBJECT_IO_RATE_CONTROL_INFORMATION` (Win8+/Server2012+) | Caps bytes/sec for everything the process does on disk. Falls back automatically to `NtSetInformationProcess(ProcessIoPriority)` (set to Very Low) if rate control isn't supported on the volume/OS edition. |
| GPU | **No true per-process cap exists** for user-mode apps under WDDM. Monitoring uses the public "GPU Engine" PDH counter category; throttling uses a suspend/resume duty-cycle on the whole process as a best-effort approximation. | This is disclosed in the UI status text, not hidden. It throttles CPU scheduling too while suspended, since there's no GPU-only lever. |

## Performance

- No per-process `PerformanceCounter` objects for CPU — CPU% is computed from
  `Process.TotalProcessorTime` deltas, which is O(1) per process per tick with no
  WMI/PDH round-trip.
- GPU PDH counters are cached by instance name and only rebuilt when the instance
  set changes (processes/engines starting or stopping), not every tick.
- Service enumeration (WMI `Win32_Service`) is the heaviest single query, so it
  polls on a separate 10s timer instead of the 1s process timer.
- The DataGrid does diff-based updates (not clear+rebuild) to avoid WPF visual
  churn and preserve scroll/selection.

Expect low single-digit percent CPU on a typical 50–150 process system; exact
overhead depends on process count and how often the GPU counter instance set
churns.

## Known limitations (by design, not bugs)

- **IO priority uses `NtSetInformationProcess`**, an internal NT API (stable since
  Vista but not part of the public Win32 contract). If Microsoft changes its
  behavior in a future build, this could stop working; the call result is always
  checked and surfaced rather than assumed.
- **RAM limit removal**: once a process is assigned to a Job Object, Windows does
  not provide a way to *un-assign* it from that job short of the process exiting.
  "Reset Limits" closes the job handle (so the ceiling is dropped) and releases our
  tracking of it, but the process técnically keeps running inside the job's kernel
  object until it exits; this is a documented OS constraint, not an app bug.
- **GPU duty-cycling** reduces overall process throughput, not GPU-only throughput
  — flagged above and in the Apply Limits status message.
- Requires Windows 10 1803+ for "GPU Engine" counters and Windows 8 / Server 2012+
  for Job Object I/O rate control; on older systems those two features degrade
  gracefully (GPU column stays at 0 / disk falls back to IO priority) rather than
  crashing.
