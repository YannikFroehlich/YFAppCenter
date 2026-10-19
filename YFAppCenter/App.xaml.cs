using System.Windows;
using System.Windows.Threading;

namespace YFAppCenter;

public partial class App : Application
{
    public App() => DispatcherUnhandledException += OnUnhandledException;

    // Ein fehlgeschlagener Dateizugriff (gesperrte Datei, fehlende Rechte) soll die App nicht wortlos beenden.
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (MainWindow is { IsLoaded: true } window)
        {
            MessageBox.Show(window, $"Das hat leider nicht geklappt.\n\n{e.Exception.Message}",
                window.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Fehler beim Start: ohne Fenster gibt es nichts weiterzumachen.
        MessageBox.Show($"YF AppCenter konnte nicht gestartet werden.\n\n{e.Exception.Message}",
            "YF AppCenter", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }
}
