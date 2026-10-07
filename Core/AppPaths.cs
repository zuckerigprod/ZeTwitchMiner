namespace ZeTwitchMiner.Core;

public static class AppPaths
{
    public static string DataDir { get; private set; } = "";
    public static bool IsPortable { get; private set; }

    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string Session => Path.Combine(DataDir, "session.bin");
    public static string ImageCache => Path.Combine(DataDir, "cache");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ZeTwitchMiner.exe");

    // Если рядом с exe лежит файл portable (или папка data), всё хранится рядом с программой
    public static void Init()
    {
        var baseDir = AppContext.BaseDirectory;
        var localData = Path.Combine(baseDir, "data");
        IsPortable = File.Exists(Path.Combine(baseDir, "portable")) || Directory.Exists(localData);

        DataDir = IsPortable
            ? localData
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZeTwitchMiner");

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ImageCache);
    }
}
