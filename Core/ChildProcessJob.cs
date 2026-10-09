using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeTwitchMiner.Core;

// Job Object с KILL_ON_JOB_CLOSE: всё, что в него попало (и их дочерние процессы),
// Windows закрывает вместе с программой, даже если её убили через диспетчер задач
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private static readonly Lazy<IntPtr> Job = new(Create);

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return job;
        var info = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>());
        // Хэндл держим до конца жизни программы: его закрытие и есть сигнал убить процессы
        return job;
    }

    public static void Attach(Process process)
    {
        try
        {
            if (Job.Value != IntPtr.Zero && !AssignProcessToJobObject(Job.Value, process.Handle))
                Log.Debug("Job attach failed: " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Debug("Job attach failed: " + ex.Message);
        }
    }
}
