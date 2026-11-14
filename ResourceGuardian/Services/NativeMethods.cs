using System;
using System.Runtime.InteropServices;

namespace ResourceGuardian.Services
{
    /// <summary>
    /// Centralized P/Invoke surface. Keep all unsafe/native declarations here so the
    /// rest of the app only talks to managed wrapper classes (JobObjectManager, etc).
    /// </summary>
    internal static class NativeMethods
    {
        // ---------- Process access & priority/affinity ----------

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            Terminate = 0x0001,
            CreateThread = 0x0002,
            VirtualMemoryOperation = 0x0008,
            VirtualMemoryRead = 0x0010,
            VirtualMemoryWrite = 0x0020,
            DuplicateHandle = 0x0040,
            CreateProcess = 0x0080,
            SetQuota = 0x0100,
            SetInformation = 0x0200,
            QueryInformation = 0x0400,
            QueryLimitedInformation = 0x1000,
            SuspendResume = 0x0800,
            Synchronize = 0x00100000,
            All = 0x001F0FFF
        }

        public enum PriorityClass : uint
        {
            Idle = 0x00000040,
            BelowNormal = 0x00004000,
            Normal = 0x00000020,
            AboveNormal = 0x00008000,
            High = 0x00000080,
            Realtime = 0x00000100
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetPriorityClass(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessAffinityMask(IntPtr hProcess, UIntPtr dwProcessAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessAffinityMask(IntPtr hProcess, out UIntPtr lpProcessAffinityMask, out UIntPtr lpSystemAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, UIntPtr dwMinimumWorkingSetSize, UIntPtr dwMaximumWorkingSetSize, uint flags);

        public const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
        public const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x2;

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtResumeProcess(IntPtr processHandle);

        // ---------- NtSetInformationProcess for IO priority ----------
        // IoPriority is ProcessInformationClass 0x21 (33) on modern Windows (undocumented but stable since Vista).
        // Values: 0=VeryLow, 1=Low, 2=Normal, 3=High (reserved/unused by scheduler), 
        // In practice user-mode throttling realistically uses 0 (VeryLow) or 1 (Low) or 2 (Normal).
        public const int ProcessIoPriority = 0x21;

        [DllImport("ntdll.dll")]
        public static extern int NtSetInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength, out int returnLength);

        // ---------- Job Objects (hard RAM limits + Disk I/O rate control) ----------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoClass infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool QueryInformationJobObject(IntPtr hJob, JobObjectInfoClass infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

        public enum JobObjectInfoClass : int
        {
            ExtendedLimitInformation = 9,
            BasicUIRestrictions = 4,
            NotificationLimitInformation = 18,
            IoRateControlInformation = 23 // JOBOBJECT_IO_RATE_CONTROL_INFORMATION (Win8+/Server2012+)
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        // Job limit flags
        public const uint JOB_OBJECT_LIMIT_WORKINGSET = 0x00000001;
        public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        public const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
        public const uint JOB_OBJECT_LIMIT_PRIORITY_CLASS = 0x00000020;
        public const uint JOB_OBJECT_LIMIT_AFFINITY = 0x00000010;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        // JOBOBJECT_IO_RATE_CONTROL_INFORMATION (undocumented-but-public struct from winnt.h, Win8+)
        // Used to cap disk bandwidth (bytes/sec) and/or IOPS for everything inside the job.
        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_IO_RATE_CONTROL_INFORMATION
        {
            public long MaxIops;              // Not used if 0
            public long MaxBandwidth;         // Bytes/sec cap. Not used if 0
            public long ReservationIops;
            public IntPtr VolumeName;         // Pointer to UNICODE_STRING-like buffer; NULL = all volumes
            public uint BaseIoSize;
            public uint ControlFlags;
            public ushort VolumeNameLength;
        }

        public const uint JOBOBJECT_IO_RATE_CONTROL_ENABLE = 0x1;

        // ---------- WTS / Session helpers not required for v1, left out intentionally ----------
    }
}
