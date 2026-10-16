namespace BiStudent;

/// Последняя линия защиты: необработанные исключения пишутся в журнал и показываются сообщением,
/// а не закрывают приложение посреди занятия.
internal static class CrashGuard
{
    private static int showing;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiStudent", "crash.log");

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception, "unobserved task"); e.SetObserved(); };
    }

    public static void Log(Exception? exception, string kind)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    private static void Report(Exception? exception, bool fatal)
    {
        Log(exception, fatal ? "fatal" : "ui");
        if (fatal || Interlocked.Exchange(ref showing, 1) == 1) return;
        try
        {
            var message = exception is ApiException api ? api.Message : exception?.Message ?? "Неизвестная ошибка";
            MessageBox.Show($"Произошла ошибка, но приложение продолжает работать:{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в {LogPath}",
                "BiStudent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { }
        finally { Interlocked.Exchange(ref showing, 0); }
    }
}
