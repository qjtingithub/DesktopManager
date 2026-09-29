using System.Windows;
using DesktopManager.Demo;

namespace DesktopManager;

public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (FeasibilityProbeRunner.TryRun(e.Args, out int exitCode))
        {
            Shutdown(exitCode);
            return;
        }

        MainWindow window = new();
        MainWindow = window;
        window.Show();
    }
}
