using System.Runtime;
using System.Runtime.InteropServices;

namespace ZeTwitchMiner.Core;

public static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    // После закрытия окна отдаём системе освободившуюся память
    public static void Run()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }
}
