using System.Runtime.InteropServices;
using System.Windows;
using Velopack;

namespace YFAppCenter;

public static class Program
{
    private const string InstanceName = @"Local\YFAppCenter";
    private const int AnyProcess = -1;

    [STAThread]
    private static void Main()
    {
        // Muss vor allem anderen laufen: Velopack startet die App bei Installation und Update mit eigenen Argumenten.
        VelopackApp.Build().Run();

        // Nur eine Instanz: zwei würden sich beim Speichern gegenseitig die Bibliothek überschreiben.
        using var instance = new Mutex(true, InstanceName, out var isFirst);
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!isFirst)
        {
            // Dieser Start kam vom Nutzer; nur so darf die laufende Instanz ihr Fenster nach vorne holen.
            AllowSetForegroundWindow(AnyProcess);
            showRequest.Set();
            return;
        }

        var app = new App();
        app.InitializeComponent();
        ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => app.Dispatcher.BeginInvoke(ShowMainWindow),
            null, Timeout.Infinite, executeOnlyOnce: false);
        app.Run();
    }

    private static void ShowMainWindow()
    {
        if (Application.Current.MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            // Stellt auch ein vorher maximiertes Fenster wieder maximiert her.
            SystemCommands.RestoreWindow(window);
        }

        window.Activate();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
