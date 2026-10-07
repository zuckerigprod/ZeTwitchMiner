using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace ZeTwitchMiner.Core;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public bool IsWarn => Level == LogLevel.Warn;
    public bool IsError => Level == LogLevel.Error;
}

public static class Log
{
    private const int MaxEntries = 500;
    private static readonly Lock FileLock = new();
    private static StreamWriter? _file;

    public static ObservableCollection<LogEntry> Entries { get; } = [];
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static void Init()
    {
        try
        {
            // Держим только последний запуск, чтобы лог не разрастался
            _file = new StreamWriter(AppPaths.LogFile, append: false) { AutoFlush = true };
        }
        catch
        {
            _file = null;
        }
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    private static void Write(LogLevel level, string msg)
    {
        var entry = new LogEntry(DateTime.Now, level, msg);
        lock (FileLock)
            _file?.WriteLine($"{entry.Time:yyyy-MM-dd HH:mm:ss} [{level}] {msg}");

        if (level < MinLevel || Avalonia.Application.Current is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        });
    }
}
