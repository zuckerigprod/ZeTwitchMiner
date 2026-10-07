using System.Runtime.InteropServices;

namespace ZeTwitchMiner.Core;

// Командная строка чужого процесса без WMI (WMI плохо дружит с NativeAOT)
public static class ProcessCommandLine
{
    private const int QueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returned);

    public static string? Get(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
            if (size <= 0) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out _) != 0) return null;
                // UNICODE_STRING: ushort Length, ushort MaximumLength, затем указатель на текст
                var length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
