using System.Globalization;

namespace BiStudent;

internal static class Program
{
    private static readonly string[] PageKeys = ["today", "camera", "schedule", "journal", "groups", "report"];

    [STAThread]
    private static int Main(string[] args)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--self-test")) return SelfTest();
        var screenshots = Array.IndexOf(args, "--screenshots");
        if (screenshots >= 0 && args.Length >= screenshots + 4)
            return Screenshots(args[screenshots + 1], args[screenshots + 2], args[screenshots + 3]);

        while (true)
        {
            using var login = new LoginForm();
            if (login.ShowDialog() != DialogResult.OK || login.Api is null) return 0;
            using var api = login.Api;
            using var main = new MainForm(api);
            Application.Run(main);
            if (!main.SignedOut) return 0;
        }
    }

    private static string Diagnostics()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static int SelfTest()
    {
        try
        {
            var directory = Diagnostics();
            using (var engine = new FaceEngine())
            using (var frame = new OpenCvSharp.Mat(new OpenCvSharp.Size(640, 480), OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black))
            {
                if (engine.Scan(frame).Count != 0) throw new InvalidOperationException("На пустом кадре найдено лицо.");
            }
            _ = CameraDevices.List();
            using (var form = new LoginForm())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-2000, -2000);
                form.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(directory, "login.png"));
                form.Close();
            }
            File.WriteAllText(Path.Combine(directory, "result.txt"), "OK");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"), ex.ToString());
            return 1;
        }
    }

    private static int Screenshots(string server, string login, string password)
    {
        var directory = Diagnostics();
        var report = Path.Combine(directory, "screenshots.txt");
        var settings = ClientSettings.Load();
        settings.ServerUrl = server;
        using var api = new ApiClient(settings);
        var code = 0;
        var saved = new List<string>();
        using var form = new MainForm(api) { AutoOpenFirstPage = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1440, 900) };
        form.Shown += async (_, _) =>
        {
            try
            {
                await api.LoginAsync(login, password);
                await api.LoadMeAsync();
                foreach (var key in PageKeys)
                {
                    await form.ShowPageAsync(key);
                    if (form.LastPageError is { } error) throw new InvalidOperationException($"Страница {key}: {error}");
                    await Task.Delay(700);
                    Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    var file = Path.Combine(directory, $"page-{key}.png");
                    bitmap.Save(file);
                    saved.Add(file);
                }
                File.WriteAllLines(report, saved);
            }
            catch (Exception ex)
            {
                code = 1;
                File.WriteAllText(report, ex.ToString());
            }
            finally { form.Close(); }
        };
        Application.Run(form);
        return code;
    }
}
