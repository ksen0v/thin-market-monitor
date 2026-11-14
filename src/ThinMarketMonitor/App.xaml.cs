using System.IO;
using System.Windows;

namespace ThinMarketMonitor;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? ready;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--apply-update", ..])
        {
            Updater.Apply(e.Args);
            Shutdown();
            return;
        }
        if (e.Args is ["--apply-setup-update", ..])
        {
            Shutdown(Updater.ApplySetup(e.Args));
            return;
        }
        if (e.Args is ["--smoke-test", var marker])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                using var test = new MainWindow(startMonitoring: false, testSettings: new());
                test.Show();
                test.SmokeTestLayout();
                test.Close();
                File.WriteAllText(marker, $"Thin Market Monitor {Updater.Current}\n{(Updater.Installed ? "Installed" : "Portable")}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(marker, ex.ToString());
                Shutdown(1);
            }
            return;
        }

        string name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Environment.ProcessPath!.ToUpperInvariant())));
        instance = new Mutex(true, @"Local\ThinMarketMonitor-" + name, out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }
        if (e.Args is [var updateArgument, var id] && Guid.TryParseExact(id, "N", out _) &&
            updateArgument is "--update-ready" or "--update-cleanup" or "--setup-update-ready" or "--setup-update-cleanup")
        {
            if (updateArgument.EndsWith("-ready", StringComparison.Ordinal) &&
                EventWaitHandle.TryOpenExisting(Updater.ReadyName(id), out var handle))
                ready = handle;
            _ = Updater.CleanupHelper(id, installed: updateArgument.StartsWith("--setup-", StringComparison.Ordinal));
        }

        var window = new MainWindow();
        window.ContentRendered += (_, _) =>
        {
            ready?.Set();
            ready?.Dispose();
            ready = null;
        };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ready?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
