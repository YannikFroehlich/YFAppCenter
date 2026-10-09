using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace YFAppCenter.Setup;

public partial class MainWindow : Window
{
    private enum Page { Welcome, Options, Progress, Done, Uninstall }

    private readonly bool uninstallMode;
    private readonly InstalledInfo? existing = Installer.FindInstallation();
    private readonly string[] steps;
    private Page page;
    private bool busy;
    private bool succeeded;
    private long packageSize;

    public MainWindow(bool uninstallMode)
    {
        InitializeComponent();
        this.uninstallMode = uninstallMode;
        VersionText.Text = "Version " + Installer.Version;
        steps = uninstallMode
            ? ["Bestätigen", "Entfernen", "Fertig"]
            : ["Willkommen", "Einstellungen", "Installation", "Fertig"];

        Features.ItemsSource = new[]
        {
            new Feature("\uE7FC", "Alle Spiele an einem Ort", "Steam, Epic, Xbox, EA, Ubisoft, GOG und Battle.net werden automatisch erkannt."),
            new Feature("\uE71D", "Deine Programme dazu", "Verknüpfungen einfach ins Fenster ziehen – der Desktop bleibt aufgeräumt."),
            new Feature("\uE734", "Favoriten, Kategorien und Controller", "Ordne deine Bibliothek, wie du willst, und steuere sie vom Sofa aus."),
            new Feature("\uE895", "Immer aktuell", "Neue Versionen werden im Hintergrund geladen und installiert.")
        };
        WelcomeText.Text = $"Dieser Assistent installiert YF AppCenter {Installer.Version} auf deinem PC. Das dauert nur einen Moment.";
        if (existing is not null)
        {
            ExistingCard.Visibility = Visibility.Visible;
            ExistingText.Text = $"Version {existing.Version} ist bereits installiert. Setup aktualisiert sie – Bibliothek, Favoriten und Kategorien bleiben erhalten.";
        }

        // Ohne bisherige Installation den aktuellen Autostart übernehmen (etwa aus Version 0.2.0), statt ihn abzuschalten.
        var options = existing?.Options ?? new InstallOptions { Autostart = Installer.IsAutostartEnabled() };
        DirectoryBox.Text = options.Directory;
        StartMenuBox.IsChecked = options.StartMenuShortcut;
        DesktopBox.IsChecked = options.DesktopShortcut;
        AutostartBox.IsChecked = options.Autostart;

        UninstallText.Text = existing is null
            ? "YF AppCenter ist auf diesem PC nicht installiert."
            : $"YF AppCenter {existing.Version} wird aus „{existing.Directory}“ entfernt – samt Verknüpfungen und Autostart.";
        DeleteDataBox.IsEnabled = existing is not null;

        ShowPage(uninstallMode ? Page.Uninstall : Page.Welcome);
    }

    private sealed class Feature(string glyph, string title, string text)
    {
        public string Glyph { get; } = glyph;
        public string Title { get; } = title;
        public string Text { get; } = text;
    }

    private void ShowPage(Page next)
    {
        page = next;
        WelcomePage.Visibility = Show(Page.Welcome);
        OptionsPage.Visibility = Show(Page.Options);
        ProgressPage.Visibility = Show(Page.Progress);
        DonePage.Visibility = Show(Page.Done);
        UninstallPage.Visibility = Show(Page.Uninstall);

        BackButton.Visibility = page == Page.Options ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = page is Page.Welcome or Page.Options or Page.Uninstall ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = page == Page.Progress ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Style = (Style)FindResource(page == Page.Uninstall && existing is not null ? "DangerButton" : "PrimaryButton");
        NextButton.Content = page switch
        {
            Page.Welcome => "Weiter",
            Page.Options => existing is null ? "Installieren" : "Aktualisieren",
            Page.Uninstall => existing is null ? "Schließen" : "Deinstallieren",
            _ => succeeded && !uninstallMode ? "Fertig" : "Schließen"
        };

        var step = page switch
        {
            Page.Welcome or Page.Uninstall => 0,
            Page.Options => 1,
            Page.Progress => uninstallMode ? 1 : 2,
            _ => steps.Length - 1
        };
        RenderSteps(step, page == Page.Done);

        if (page == Page.Options)
        {
            ValidateDirectory();
            DirectoryBox.Focus();
            DirectoryBox.CaretIndex = DirectoryBox.Text.Length;
        }
        else
        {
            NextButton.Focus();
        }

        Visibility Show(Page candidate) => page == candidate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderSteps(int current, bool finished)
    {
        StepsPanel.Children.Clear();
        for (var index = 0; index < steps.Length; index++)
        {
            var done = index < current || finished;
            var active = index == current && !finished;
            var number = new TextBlock
            {
                Text = done ? "\uE73E" : (index + 1).ToString(),
                FontFamily = done ? (FontFamily)FindResource("Icons") : FontFamily,
                FontSize = done ? 11 : 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = active ? new SolidColorBrush(Color.FromRgb(0x6D, 0x28, 0xD9)) : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var circle = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(active || done ? (byte)255 : (byte)120, 255, 255, 255)),
                Background = active ? Brushes.White : done ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : Brushes.Transparent,
                Child = number
            };
            var label = new TextBlock
            {
                Text = steps[index],
                Foreground = Brushes.White,
                Opacity = active || done ? 1 : 0.65,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            StepsPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 18),
                Children = { circle, label }
            });
        }
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        switch (page)
        {
            case Page.Welcome:
                ShowPage(Page.Options);
                break;
            case Page.Options when ValidateDirectory():
                await RunAsync(install: true);
                break;
            case Page.Uninstall when existing is not null:
                await RunAsync(install: false);
                break;
            case Page.Uninstall:
                Close();
                break;
            case Page.Done:
                if (succeeded && !uninstallMode && LaunchBox.IsChecked == true)
                {
                    Installer.LaunchApp(DirectoryBox.Text.Trim());
                }

                Close();
                break;
        }
    }

    private async System.Threading.Tasks.Task RunAsync(bool install)
    {
        busy = true;
        ProgressTitle.Text = install ? (existing is null ? "YF AppCenter wird installiert" : "YF AppCenter wird aktualisiert") : "YF AppCenter wird entfernt";
        ShowPage(Page.Progress);
        var progress = new Progress<(double Percent, string Status)>(report =>
        {
            ProgressBar.Value = report.Percent;
            ProgressStatus.Text = report.Status;
            ProgressPercent.Text = $"{report.Percent:0} %";
        });

        try
        {
            if (install)
            {
                await Installer.InstallAsync(new InstallOptions
                {
                    Directory = DirectoryBox.Text.Trim(),
                    StartMenuShortcut = StartMenuBox.IsChecked == true,
                    DesktopShortcut = DesktopBox.IsChecked == true,
                    Autostart = AutostartBox.IsChecked == true
                }, progress);
                ShowResult(true, "Alles bereit!", "YF AppCenter ist installiert. Deine Spiele werden beim ersten Start automatisch gefunden.");
            }
            else
            {
                await Installer.UninstallAsync(DeleteDataBox.IsChecked == true, progress);
                ShowResult(true, "Deinstalliert", DeleteDataBox.IsChecked == true
                    ? "YF AppCenter und deine Bibliothek wurden entfernt."
                    : "YF AppCenter wurde entfernt. Deine Bibliothek bleibt für eine spätere Installation erhalten.");
            }
        }
        catch (Exception exception)
        {
            ShowResult(false, install ? "Installation fehlgeschlagen" : "Deinstallation fehlgeschlagen", exception.Message);
        }
        finally
        {
            busy = false;
        }
    }

    private void ShowResult(bool success, string title, string text)
    {
        succeeded = success;
        DoneTitle.Text = title;
        DoneText.Text = text;
        DoneGlyph.Text = success ? "\uE73E" : "\uE711";
        DoneBadge.Background = success ? (Brush)FindResource("Accent") : (Brush)FindResource("Danger");
        var showInstallExtras = success && !uninstallMode ? Visibility.Visible : Visibility.Collapsed;
        LaunchBox.Visibility = showInstallExtras;
        PinTip.Visibility = showInstallExtras;
        ShowPage(Page.Done);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowPage(Page.Welcome);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = DirectoryBox.Text.Trim();
            if (FolderPicker.Pick(new WindowInteropHelper(this).Handle, "Installationsordner wählen", current) is { } folder)
            {
                // Wer einen Sammelordner wie „D:\Programme“ wählt, bekommt darin einen eigenen Unterordner.
                DirectoryBox.Text = string.Equals(Path.GetFileName(folder), Installer.AppName, StringComparison.OrdinalIgnoreCase)
                    ? folder
                    : Path.Combine(folder, Installer.AppName);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            MessageBox.Show(this, "Der Ordnerdialog konnte nicht geöffnet werden. Bitte den Pfad direkt eintippen.", Title);
        }
    }

    private void OnDirectoryChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ValidateDirectory();
        }
    }

    private bool ValidateDirectory()
    {
        var directory = DirectoryBox.Text.Trim();
        var problem = Installer.ValidateDirectory(directory);
        DirectoryError.Text = problem ?? "";
        DirectoryError.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        NextButton.IsEnabled = problem is null;

        if (packageSize == 0 && Installer.HasPackage)
        {
            packageSize = Installer.PackageSize();
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory)));
            SpaceText.Text = $"Benötigt etwa {Math.Max(1, packageSize / 1048576)} MB · frei auf {drive.Name.TrimEnd('\\')} {drive.AvailableFreeSpace / 1073741824.0:0.#} GB";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            SpaceText.Text = "";
        }

        return problem is null;
    }

    private void OnClosing(object sender, CancelEventArgs e) => e.Cancel = busy;

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        // Windows 11: abgerundete Ecken auch ohne Systemrahmen. Unter Windows 10 wirkungslos.
        var handle = new WindowInteropHelper(this).Handle;
        var round = 2;
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
