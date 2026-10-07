using Avalonia;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Второй экземпляр просто будит первый и выходит
        using var instance = new SingleInstance("ZeTwitchMiner.Instance");
        if (!instance.IsFirst)
        {
            instance.SignalFirst();
            return 0;
        }

        AppPaths.Init();
        Log.Init();

        try
        {
            return BuildAvaloniaApp(instance, args.Contains("--tray"), args.Contains("--gpu"))
                .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal: " + ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null, false, false);

    // Интерфейс спокойный, программная отрисовка экономит память на драйверах GPU
    private static AppBuilder BuildAvaloniaApp(SingleInstance? instance, bool startHidden, bool gpu) =>
        AppBuilder.Configure(() => new App(instance, startHidden))
            .UsePlatformDetect()
            .WithInterFont()
            .With(new Win32PlatformOptions { RenderingMode = gpu ? [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software] : [Win32RenderingMode.Software] })
            .LogToTrace();
}
