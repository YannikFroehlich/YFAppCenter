using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace YFAppCenter.Setup;

/// <summary>
/// Startet den Assistenten oder, mit --silent, eine Installation ohne Oberfläche:
///   YFAppCenter-Setup.exe --silent [--dir "Pfad"] [--desktop] [--no-startmenu] [--autostart] [--launch]
///   Uninstall.exe --uninstall [--silent] [--delete-data]
/// </summary>
public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        Installer.DeleteLeftoversAfterExit();
        base.OnExit(e);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var uninstall = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(typeof(App).Assembly.Location), Installer.UninstallerName, StringComparison.OrdinalIgnoreCase);

        if (!args.Contains("--silent", StringComparer.OrdinalIgnoreCase))
        {
            MainWindow = new MainWindow(uninstall);
            MainWindow.Show();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var progress = new Progress<(double, string)>();
        try
        {
            if (uninstall)
            {
                await Installer.UninstallAsync(args.Contains("--delete-data", StringComparer.OrdinalIgnoreCase), progress);
            }
            else
            {
                var options = Installer.FindInstallation()?.Options ?? new InstallOptions { Autostart = Installer.IsAutostartEnabled() };
                var dirIndex = Array.FindIndex(args, arg => string.Equals(arg, "--dir", StringComparison.OrdinalIgnoreCase));
                if (dirIndex >= 0 && dirIndex + 1 < args.Length)
                {
                    options.Directory = args[dirIndex + 1];
                }

                options.DesktopShortcut |= args.Contains("--desktop", StringComparer.OrdinalIgnoreCase);
                options.StartMenuShortcut &= !args.Contains("--no-startmenu", StringComparer.OrdinalIgnoreCase);
                options.Autostart |= args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
                if (Installer.ValidateDirectory(options.Directory) is { } problem)
                {
                    throw new InvalidOperationException(problem);
                }

                await Installer.InstallAsync(options, progress);
                if (args.Contains("--launch", StringComparer.OrdinalIgnoreCase))
                {
                    Installer.LaunchApp(options.Directory);
                }
            }

            Shutdown(0);
        }
        catch (Exception exception)
        {
            // Ohne Oberfläche landet der Fehler in einer Logdatei.
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "YFAppCenter-Setup.log"), exception.ToString());
            Shutdown(1);
        }
    }
}
