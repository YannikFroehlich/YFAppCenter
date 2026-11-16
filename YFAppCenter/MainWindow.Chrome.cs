using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace YFAppCenter;

public partial class MainWindow
{
    // Minimiert verschwindet das Fenster aus der Taskleiste und bleibt über das Tray-Symbol erreichbar.
    private readonly System.Windows.Forms.NotifyIcon tray = new() { Text = "YF AppCenter" };
    private readonly System.Windows.Forms.ToolStripMenuItem trayUpdateItem = new() { Available = false };

    private void SetUpTray()
    {
        using (var icon = Application.GetResourceStream(new Uri("pack://application:,,,/AppCenter.ico")).Stream)
        {
            tray.Icon = new System.Drawing.Icon(icon, System.Windows.Forms.SystemInformation.SmallIconSize);
        }

        trayUpdateItem.Click += (_, _) => AppUpdater.ApplyAndRestart();
        tray.BalloonTipClicked += (_, _) => AppUpdater.ApplyAndRestart();
        tray.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip
        {
            Items =
            {
                trayUpdateItem,
                { "Öffnen", null, (_, _) => BringToFront() },
                { "Beenden", null, (_, _) => Close() }
            }
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                BringToFront();
            }
        };
        tray.Visible = true;
        // Sonst bleibt das Symbol nach dem Beenden stehen, bis die Maus darüberfährt.
        Closed += (_, _) => tray.Dispose();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Verkleinern" : "Maximieren";
        FitMaximized();
    }

    /// <summary>
    /// Maximiert legt Windows den Fensterrahmen außerhalb des Bildschirms an. Ohne eigene Titelleiste gehört er oben
    /// ganz und an den übrigen Seiten teilweise zum Inhalt; so viel Abstand halten, wie tatsächlich übersteht.
    /// </summary>
    private void FitMaximized()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Im Vollbild gibt es keinen Rahmen; die Taskleiste würde hier sonst als Überstand zählen.
        if (fullscreen || WindowState != WindowState.Maximized || !GetClientRect(hwnd, out var client))
        {
            Root.Margin = default;
            return;
        }

        var origin = PointToScreen(new Point(0, 0));
        var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        Root.Margin = new Thickness(
            Math.Max(0, work.Left - origin.X) / dpi.DpiScaleX,
            Math.Max(0, work.Top - origin.Y) / dpi.DpiScaleY,
            Math.Max(0, origin.X + client.Right - work.Right) / dpi.DpiScaleX,
            Math.Max(0, origin.Y + client.Bottom - work.Bottom) / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect32 rect);

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => SetFullscreen(!fullscreen);

    /// <summary>
    /// Echtes Vollbild über der Taskleiste: nur ein maximiertes Fenster ohne Rahmen deckt sie ab. Dafür muss die
    /// eigene Titelleiste (WindowChrome) weichen; die Knöpfe im Fenster bleiben.
    /// </summary>
    private void SetFullscreen(bool on)
    {
        if (on == fullscreen)
        {
            return;
        }

        if (on)
        {
            stateBeforeFullscreen = WindowState;
        }

        fullscreen = on;
        // Windows berechnet die Größe nur beim Wechsel neu; ein schon maximiertes Fenster bliebe sonst über der Taskleiste stehen.
        WindowState = WindowState.Normal;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, on ? null : windowFrame);
        WindowStyle = on ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = on ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowState = on ? WindowState.Maximized : stateBeforeFullscreen;
        FullscreenGlyph.Text = on ? "" : "";
        FullscreenButton.ToolTip = on ? "Vollbild beenden (F11)" : "Vollbild (F11)";
        MaximizeButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F11)
        {
            SetFullscreen(!fullscreen);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && dialogFrame is not null)
        {
            CloseDialog(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && dialogFrame is not null && DialogInput.IsKeyboardFocused)
        {
            CloseDialog(true);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Im Infobereich auf Software-Rendering umschalten: dann gibt der Grafiktreiber seinen Speicher im
    /// App-Prozess frei (gemessen gut 200 MB). Sichtbar wieder per Grafikkarte, damit Scrollen flüssig bleibt.
    /// </summary>
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource { CompositionTarget: { } target })
        {
            target.RenderMode = IsVisible ? System.Windows.Interop.RenderMode.Default : System.Windows.Interop.RenderMode.SoftwareOnly;
        }    }

    /// <summary>Holt das Fenster aus dem Infobereich nach vorne, etwa bei einem zweiten Start der App.</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            // Stellt auch ein vorher maximiertes Fenster wieder maximiert her.
            SystemCommands.RestoreWindow(this);
            if (fullscreen)
            {
                // Nach dem Autostart war das Fenster noch nie sichtbar, Windows kennt also nur die normale Größe.
                WindowState = WindowState.Maximized;
            }
        }

        Activate();
    }
}
