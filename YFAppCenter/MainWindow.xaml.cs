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

public partial class MainWindow : Window
{
    private const int RecentCount = 20;

    private readonly AppSettings settings = AppSettings.Load();
    private readonly System.Windows.Shell.WindowChrome windowFrame;
    private WindowState stateBeforeFullscreen = WindowState.Normal;
    private bool fullscreen;

    // Kacheln pro Zeile; „Zuletzt gestartet“ und „Meistgespielt“ bleiben auf der Startseite eine einzige Reihe.
    private int homeColumns = 4;

    private List<LibraryEntry> library = Library.Load();
    private List<LibraryEntry> visible = [];
    private string filter = "all";
    private LibraryEntry? featured;
    private HashSet<string> runningIds = [];
    private bool checkingRunning;
    private bool rescanOnActivate;
    private readonly System.Windows.Threading.DispatcherTimer runningTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public MainWindow()
    {
        InitializeComponent();
        windowFrame = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        // Kachelbilder in der tatsächlich benötigten Auflösung laden (auf großen Bildschirmen bis gut 300 px breit).
        ImageCache.TileWidth = (int)(340 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        AutostartBox.IsChecked = Autostart.IsEnabled;
        StartFullscreenBox.IsChecked = settings.StartFullscreen;
        FullscreenAtLogonBox.IsChecked = settings.FullscreenAtLogon;
        FullscreenAtLogonBox.IsEnabled = Autostart.IsEnabled;
        SetUpTray();
        IsVisibleChanged += OnVisibilityChanged;
        var atLogon = Environment.GetCommandLineArgs().Contains(Autostart.MinimizedArgument);
        if (settings.OpensFullscreen(atLogon))
        {
            SetFullscreen(true);
        }

        if (settings.StaysInTray(atLogon))
        {
            WindowState = WindowState.Minimized;
            // Beim ersten Anzeigen meldet WPF keinen Zustandswechsel, daher hier selbst in den Infobereich.
            // Bei der Windows-Anmeldung nichts durchsuchen; erst wenn das Fenster zum ersten Mal geöffnet wird.
            Loaded += (_, _) =>
            {
                Hide();
                IsVisibleChanged += RescanOnFirstShow;
            };
        }
        else
        {
            Loaded += async (_, _) => await RescanAsync();
        }

        Refresh();
        Loaded += (_, _) => ShowPage();
        // Laufende Programme und Controller nur prüfen, solange das Fenster im Vordergrund ist.
        runningTimer.Tick += async (_, _) => await UpdateRunningAsync();
        padTimer.Tick += (_, _) => PollGamepad();
        Activated += async (_, _) =>
        {
            runningTimer.Start();
            padTimer.Start();
            if (rescanOnActivate)
            {
                rescanOnActivate = false;
                await RescanAsync();
            }
            else
            {
                // Zurück nach dem Spielen: die gerade gemessene Zeit zeigen.
                await UpdatePlaytimeAsync();
            }
        };
        Deactivated += (_, _) =>
        {
            runningTimer.Stop();
            padTimer.Stop();
        };
        LogoPanel.ToolTip = $"Version {AppUpdater.CurrentVersion}";
        AppUpdater.SyncInstalledVersion();
        Loaded += async (_, _) => await CheckForUpdateAsync();
        updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        updateTimer.Start();
    }

    private async void RescanOnFirstShow(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            IsVisibleChanged -= RescanOnFirstShow;
            await RescanAsync();
        }
    }

    // Läuft die App per Autostart tagelang, soll sie trotzdem neue Versionen finden.
    private readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private async Task CheckForUpdateAsync()
    {
        try
        {
            if (await AppUpdater.DownloadUpdateAsync() is { } version && !trayUpdateItem.Available)
            {
                UpdateText.Text = $"Version {version} ist bereit";
                UpdatePanel.Visibility = Visibility.Visible;
                trayUpdateItem.Text = $"Version {version} installieren";
                trayUpdateItem.Available = true;
                // Im Infobereich (etwa nach dem Autostart) sieht man den Hinweis im Fenster nicht; einmal pro Version melden.
                if (!IsVisible)
                {
                    tray.ShowBalloonTip(10000, "Update verfügbar", $"YF AppCenter {version} ist bereit. Klicken, um es jetzt zu installieren.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
            }
        }
        catch (Exception)
        {
            // Kein Internet oder GitHub nicht erreichbar: beim nächsten Mal erneut versuchen.
        }
    }

    private void OnUpdateRestartClick(object sender, RoutedEventArgs e) => AppUpdater.ApplyAndRestart();

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

    // Controller: nur abfragen, solange das Fenster im Vordergrund ist. Ohne Controller seltener,
    // weil XInput leere Anschlüsse vergleichsweise langsam abfragt.
    private readonly System.Windows.Threading.DispatcherTimer padTimer = new(System.Windows.Threading.DispatcherPriority.Input);
    private readonly GamepadNavigator pad = new();
    private bool padConnected;

    private void PollGamepad()
    {
        var state = XInput.ReadFirst();
        var connected = state is not null;
        padTimer.Interval = TimeSpan.FromMilliseconds(connected ? 33 : 1000);
        if (connected != padConnected)
        {
            padConnected = connected;
            PadHints.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            if (connected)
            {
                // Tasten, die beim Anschließen schon gedrückt sind, nicht auslösen.
                pad.Reset(state!.Value.Buttons);
                return;
            }
        }

        if (state is not { } current)
        {
            return;
        }

        foreach (var action in pad.Update(current.Buttons, current.ThumbLX, current.ThumbLY, Environment.TickCount64))
        {
            HandlePadAction(action);
        }
    }

    private void HandlePadAction(PadAction action)
    {
        if (dialogFrame is not null && action is PadAction.Favorite or PadAction.Menu or PadAction.PreviousSection or PadAction.NextSection)
        {
            return;
        }

        var focused = Keyboard.FocusedElement as FrameworkElement;
        var openMenu = focused is null ? null : FindAncestor<ContextMenu>(focused);
        switch (action)
        {
            case PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right:
                var direction = action switch
                {
                    PadAction.Up => FocusNavigationDirection.Up,
                    PadAction.Down => FocusNavigationDirection.Down,
                    PadAction.Left => FocusNavigationDirection.Left,
                    _ => FocusNavigationDirection.Right
                };
                if (focused is null || (focused == SearchBox && action == PadAction.Down && !focused.MoveFocus(new TraversalRequest(direction))))
                {
                    FocusTile(visible.FirstOrDefault());
                }
                else
                {
                    focused.MoveFocus(new TraversalRequest(direction));
                }

                (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
                break;

            case PadAction.Activate when focused is not null:
                Invoke(focused);
                break;

            case PadAction.Back:
                if (dialogFrame is not null)
                {
                    CloseDialog(false);
                }
                else if (openMenu is not null)
                {
                    openMenu.IsOpen = false;
                }
                else if (SearchBox.Text.Length > 0)
                {
                    SearchBox.Clear();
                }
                else if (filter != "all")
                {
                    HomeNav.IsChecked = true;
                }
                else
                {
                    WindowState = WindowState.Minimized;
                }

                break;

            case PadAction.Favorite when focused is Button { DataContext: LibraryEntry entry } && IsTile(focused):
                entry.Favorite = !entry.Favorite;
                SaveAndRefresh();
                break;

            case PadAction.Menu when focused is Button tile && IsTile(tile):
                FillTileMenu(tile);
                tile.ContextMenu!.PlacementTarget = tile;
                tile.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                tile.ContextMenu.IsOpen = true;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    () => (tile.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault())?.Focus());
                break;

            case PadAction.PreviousSection or PadAction.NextSection:
                var navItems = NavItems().ToList();
                var current = navItems.FindIndex(item => item.IsChecked == true);
                var next = current + (action == PadAction.NextSection ? 1 : -1);
                if (next >= 0 && next < navItems.Count)
                {
                    navItems[next].IsChecked = true;
                    navItems[next].Focus();
                }

                break;
        }
    }

    /// <summary>Löst ein Bedienelement so aus, wie es die Sprachausgabe tun würde.</summary>
    private static void Invoke(UIElement element)
    {
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element);
        switch (peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
                ?? peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)
                ?? peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)
                ?? peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse))
        {
            case System.Windows.Automation.Provider.IInvokeProvider invoke:
                invoke.Invoke();
                break;
            case System.Windows.Automation.Provider.ISelectionItemProvider selection:
                selection.Select();
                break;
            case System.Windows.Automation.Provider.IToggleProvider toggle:
                toggle.Toggle();
                break;
            case System.Windows.Automation.Provider.IExpandCollapseProvider expand:
                expand.Expand();
                break;
        }
    }

    private IEnumerable<RadioButton> NavItems() => FindDescendants<RadioButton>(HomeNav.Parent);

    private bool IsTile(FrameworkElement element) => element.Style == FindResource("TileButton");

    /// <summary>Setzt den Fokus auf die Kachel eines Eintrags, etwa nachdem die Ansicht neu aufgebaut wurde.</summary>
    private void FocusTile(LibraryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var tile = FindDescendants<Button>(Sections).FirstOrDefault(button => button.DataContext == entry && IsTile(button));
        tile?.Focus();
        tile?.BringIntoView();
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = element; current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Prüft alle paar Sekunden, welche Einträge gerade laufen; nur solange das Fenster sichtbar ist.</summary>
    private async Task UpdateRunningAsync()
    {
        if (checkingRunning || WindowState == WindowState.Minimized)
        {
            return;
        }

        checkingRunning = true;
        try
        {
            var ids = await RunningDetector.GetRunningIdsAsync(library);
            if (!ids.SetEquals(runningIds))
            {
                runningIds = ids;
                Refresh();
            }
        }
        catch (Exception)
        {
            // Nur eine Statusanzeige; beim nächsten Durchlauf wird es erneut versucht.
        }
        finally
        {
            checkingRunning = false;
        }
    }

    private async Task RescanAsync()
    {        RescanButton.IsEnabled = false;
        StatusText.Text = "Suche installierte Spiele …";
        try
        {
            var detected = await GameScanner.ScanAsync();
            var merged = Library.Merge(library, detected);
            var keptIds = merged.Select(entry => entry.Id).ToHashSet();
            var removed = library.Where(entry => !keptIds.Contains(entry.Id)).ToList();
            library = merged;
            Library.Save(library);
            // Erst nach dem Speichern löschen, damit die Bibliothek nie auf fehlende Dateien zeigt.
            removed.ForEach(Library.DeleteImportedFiles);
            StatusText.Text = "";
            RescanButton.ToolTip = $"Installierte Spiele neu suchen\n{detected.Count} Spiele erkannt · {library.Count(entry => entry.IsManual)} eigene Einträge";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Spielsuche fehlgeschlagen: {exception.Message}";
        }
        finally
        {
            RescanButton.IsEnabled = true;
        }

        Refresh();
        await UpdatePlaytimeAsync();
        if (await StoreArt.FetchMissingAsync(library))
        {
            SaveAndRefresh();
        }
    }

    /// <summary>Übernimmt die Spielzeit aus YFTimeTracker; ohne Tracker bleibt sie leer.</summary>
    private async Task UpdatePlaytimeAsync()
    {
        Dictionary<string, TimeSpan> playtime;
        try
        {
            playtime = await Task.Run(() => PlaytimeReader.Read(PlaytimeReader.DatabasePath, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // Datenbank gesperrt oder von einer anderen Tracker-Version: beim nächsten Mal erneut versuchen.
            return;
        }

        var changed = false;
        foreach (var entry in library)
        {
            TimeSpan? time = playtime.TryGetValue(entry.Id, out var value) ? value : null;
            changed |= entry.Playtime != time;
            entry.Playtime = time;
        }

        // Neu aufbauen, damit „Meistgespielt“ die neue Reihenfolge zeigt.
        if (changed)
        {
            Refresh();
        }
    }

    public sealed record Section(string Title, IReadOnlyList<LibraryEntry> Items)
    {
        // Name des Bereichs für Bedienungshilfen wie die Sprachausgabe.
        public override string ToString() => Title;
    }

    public sealed record CategoryItem(string Name, int Count, bool IsSelected);

    private const string CategoryPrefix = "cat:";

    private string? SelectedCategory => filter.StartsWith(CategoryPrefix, StringComparison.Ordinal) ? filter[CategoryPrefix.Length..] : null;

    private void Refresh()
    {
        // Nach dem Neuaufbau soll der Fokus auf derselben Kachel bleiben (wichtig für Controller und Tastatur).
        var focusedEntry = Keyboard.FocusedElement is Button { DataContext: LibraryEntry entryWithFocus } focusedButton && IsTile(focusedButton)
            ? entryWithFocus
            : null;
        if (focusedEntry is not null)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => FocusTile(focusedEntry));
        }

        var query = SearchBox.Text.Trim();
        var pool = library.Where(entry => ShowHiddenBox.IsChecked == true || !entry.Hidden).ToList();
        foreach (var entry in library)
        {
            entry.IsRunning = runningIds.Contains(entry.Id);
        }

        CountAll.Text = pool.Count.ToString();
        CountFavorites.Text = pool.Count(entry => entry.Favorite).ToString();
        CountRecent.Text = Math.Min(RecentCount, pool.Count(entry => entry.LastLaunched is not null)).ToString();
        CountGames.Text = pool.Count(entry => entry.IsGame).ToString();
        CountApps.Text = pool.Count(entry => !entry.IsGame).ToString();

        var categories = pool
            .Where(entry => entry.Category is not null)
            .GroupBy(entry => entry.Category!)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new CategoryItem(group.Key, group.Count(), group.Key == SelectedCategory))
            .ToList();
        if (SelectedCategory is not null && !categories.Any(category => category.IsSelected))
        {
            // Die gewählte Kategorie ist leer geworden.
            filter = "all";
            HomeNav.IsChecked = true;
        }

        BuildCategoryNav(categories);
        CategoryHeader.Visibility = categories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var matches = pool
            .Where(entry => filter switch
            {
                "games" => entry.IsGame,
                "apps" => !entry.IsGame,
                "favorites" => entry.Favorite,
                "recent" => entry.LastLaunched is not null,
                _ when SelectedCategory is { } category => entry.Category == category,
                _ => true
            })
            .Where(entry => entry.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var byName = matches.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var recent = matches.Where(entry => entry.LastLaunched is not null).OrderByDescending(entry => entry.LastLaunched);
        var isHome = filter == "all" && query.Length == 0;
        // Vor den Bereichen, denn das Spiel im Banner soll nicht direkt darunter noch einmal auftauchen.
        UpdateHero(pool, isHome);

        List<Section> sections = filter switch
        {
            // Startseite ohne Suche: in Bereiche gegliedert, wie ein Startmenü.
            "all" when query.Length == 0 =>
            [
                new("Läuft gerade", byName.Where(entry => entry.IsRunning).ToList()),
                new("Zuletzt gestartet", recent.Where(entry => entry != featured).Take(homeColumns).ToList()),
                new("Meistgespielt", matches.Where(entry => entry.Playtime is not null).OrderByDescending(entry => entry.Playtime)
                    .Take(homeColumns).ToList()),
                new("Favoriten", byName.Where(entry => entry.Favorite).ToList()),
                .. categories.Select(category => new Section(category.Name, byName.Where(entry => entry.Category == category.Name).ToList())),
                // Einträge mit Kategorie stehen schon in ihrem eigenen Bereich.
                new("Spiele", byName.Where(entry => entry.IsGame && entry.Category is null).ToList()),
                new("Programme", byName.Where(entry => !entry.IsGame && entry.Category is null).ToList())
            ],
            "recent" => [new("", recent.Take(RecentCount).ToList())],
            _ => [new("", byName.OrderByDescending(entry => entry.Favorite).ToList())]
        };
        sections.RemoveAll(section => section.Items.Count == 0);
        visible = sections.SelectMany(section => section.Items).ToList();
        Sections.ItemsSource = sections;

        // Das Banner ersetzt auf der Startseite die Überschrift.
        PageHeader.Visibility = Hero.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = query.Length > 0 ? "Suchergebnisse" : filter switch
        {
            "favorites" => "Favoriten",
            "recent" => "Zuletzt gestartet",
            "games" => "Spiele",
            "apps" => "Programme",
            _ when SelectedCategory is { } category => category,
            _ => DateTime.Now.Hour switch
            {
                >= 5 and < 11 => "Guten Morgen",
                >= 11 and < 18 => "Guten Tag",
                >= 18 and < 23 => "Guten Abend",
                _ => "Hallo, Nachteule"
            }
        };
        PageSubtitle.Text = isHome
            ? $"{pool.Count(entry => entry.IsGame)} Spiele · {pool.Count(entry => !entry.IsGame)} Programme"
            : matches.Count == 1 ? "1 Eintrag" : $"{matches.Count} Einträge";

        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyActions.Visibility = pool.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        (EmptyGlyph.Text, EmptyTitle.Text, EmptyText.Text) = (query.Length > 0, filter) switch
        {
            (true, _) => ("", "Keine Treffer", $"Nichts passt zu „{query}“."),
            (_, "favorites") => ("", "Noch keine Favoriten", "Rechtsklick auf eine Kachel und „Zu Favoriten hinzufügen“."),
            (_, "recent") => ("", "Noch nichts gestartet", "Alles, was du hier startest, taucht in dieser Liste auf."),
            _ => ("", "Noch nichts hier", "Programme oder Desktop-Verknüpfungen einfach ins Fenster ziehen.")
        };

        foreach (var entry in visible.Where(entry => entry.Icon is null).Distinct())
        {
            _ = LoadIconAsync(entry);
        }
    }

    private void UpdateHero(List<LibraryEntry> pool, bool show)
    {
        var games = pool.Where(entry => entry.IsGame).ToList();
        var withArt = games.Where(entry => entry.HeroPath is not null).ToList();
        var daily = withArt.Count > 0 ? withArt : games;
        featured = pool.Where(entry => entry.IsRunning).MaxBy(entry => entry.LastLaunched)
            ?? pool.Where(entry => entry.LastLaunched is not null).MaxBy(entry => entry.LastLaunched)
            ?? pool.FirstOrDefault(entry => entry.Favorite)
            ?? (daily.Count > 0 ? daily[DateTime.Today.DayOfYear % daily.Count] : null);

        Hero.Visibility = show && featured is not null ? Visibility.Visible : Visibility.Collapsed;
        if (featured is null)
        {
            return;
        }

        Hero.DataContext = featured;
        if (featured.Icon is null)
        {
            _ = LoadIconAsync(featured);
        }

        // Volle Auflösung (0): im Vollbild füllt das Banner die ganze Bildschirmbreite.
        HeroImage.Source = featured.Cover ?? ImageCache.Load(featured.HeroPath, 0);
        Ambient.Source = ImageCache.Load(featured.CustomCoverPath ?? featured.HeroPath, ImageCache.GlowWidth) ?? featured.IconWash;
        HeroLogo.Source = ImageCache.Load(featured.LogoPath);
        HeroLogo.Visibility = HeroLogo.Source is null ? Visibility.Collapsed : Visibility.Visible;
        HeroTitle.Visibility = HeroLogo.Source is null ? Visibility.Visible : Visibility.Collapsed;
        HeroDot.Fill = (Brush)FindResource(featured.IsRunning ? "RunningBrush" : "BrandBrush");
        HeroLabel.Text = featured.IsRunning ? "LÄUFT GERADE"
            : featured.LastLaunched is { } launched ? $"WEITERMACHEN  ·  {Ago(launched)}"
            : featured.Favorite ? "AUS DEINEN FAVORITEN" : "TIPP DES TAGES";
        HeroPlayText.Text = featured.IsGame ? "Spielen" : "Öffnen";
        HeroFavoriteGlyph.Text = featured.Favorite ? "" : "";
        HeroFavoriteButton.ToolTip = featured.Favorite ? "Aus Favoriten entfernen" : "Zu Favoriten hinzufügen";
    }

    private static string Ago(DateTime time) => (DateTime.Now - time) switch
    {
        { TotalMinutes: < 1 } => "gerade eben",
        { TotalHours: < 1 } span => $"vor {(int)span.TotalMinutes} Min.",
        { TotalDays: < 1 } span => $"vor {(int)span.TotalHours} Std.",
        { TotalDays: < 2 } => "gestern",
        var span => $"vor {(int)span.TotalDays} Tagen"
    };

    /// <summary>Das Banner füllt gut zwei Drittel der Höhe, darunter schaut noch die erste Kachelreihe hervor.</summary>
    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) =>
        Hero.Height = Math.Clamp(Math.Min(e.NewSize.Width * 0.6, e.NewSize.Height * 0.68), 340, 960);

    /// <summary>Banner bleibt beim Scrollen leicht zurück (Tiefe); was unter die Titelleiste läuft, wird abgedunkelt.</summary>
    private void OnContentScrolled(object sender, ScrollChangedEventArgs e)
    {
        HeroParallax.Y = e.VerticalOffset * 0.35;
        TitleScrim.Opacity = 0.6 + 0.4 * Math.Min(1, e.VerticalOffset / 160);
    }

    private void OnHeroPlayClick(object sender, RoutedEventArgs e)
    {
        if (featured is not null)
        {
            Launch(featured);
        }
    }

    private void OnHeroFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (featured is not null)
        {
            featured.Favorite = !featured.Favorite;
            SaveAndRefresh();
        }
    }

    private static async Task LoadIconAsync(LibraryEntry entry) => entry.Icon = await IconLoader.LoadAsync(entry.IconSource);

    private void SaveAndRefresh()
    {
        Library.Save(library);
        Refresh();
    }

    private void Launch(LibraryEntry entry)
    {
        try
        {
            Library.Launch(entry.LaunchTarget);
            entry.LastLaunched = DateTime.Now;
            SaveAndRefresh();
            WindowState = WindowState.Minimized;
        }
        catch (Exception exception)
        {
            ShowDialogCard($"„{entry.DisplayName}“ konnte nicht gestartet werden", exception.Message);
        }
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            var entry = Library.CreateManual(path, isGame: filter == "games");
            entry.Category = SelectedCategory;
            library.Add(entry);
        }

        SaveAndRefresh();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (dialogFrame is null)
        {
            SearchBox.Focus();
        }
        _ = UpdateRunningAsync();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && visible.Count > 0)
        {
            Launch(visible[0]);
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
        }
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        // Beim Neuaufbau der Kategorieliste wird der gewählte Eintrag erneut angehakt; dann nichts tun.
        if (sender is not RadioButton { Tag: string tag } || tag == filter)
        {
            return;
        }

        filter = tag;
        if (IsLoaded)
        {
            Refresh();
            ShowPage();
        }
    }

    /// <summary>Neue Seite oben beginnen und kurz einblenden: eine Animation für die ganze Seite statt einer pro Kachel.</summary>
    private void ShowPage()
    {
        ContentScroll.ScrollToTop();
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = (IEasingFunction)FindResource("EaseOut");
        ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        ContentShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    // Muss zum rechten Kachelrand im TileButton-Stil und zum negativen Rand der Kachelliste passen.
    private const double TileGap = 20;
    private const double TileMinWidth = 200;
    private const double WideTileMinWidth = 260;
    private const double WideLayoutWidth = 1700;

    /// <summary>So viele Spalten wie mit Mindestbreite passen, Rest auf die Kacheln verteilt: jede Zeile schließt bündig ab.</summary>
    private void OnSectionsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = e.NewSize.Width + TileGap;
        var minWidth = e.NewSize.Width >= WideLayoutWidth ? WideTileMinWidth : TileMinWidth;
        var columns = Math.Max(1, (int)(available / (minWidth + TileGap)));
        if (columns != homeColumns)
        {
            homeColumns = columns;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Refresh);
        }

        var width = Math.Floor(available / columns - TileGap);
        if (Resources["TileWidth"] is double current && current == width)
        {
            return;
        }

        Resources["TileWidth"] = width;
        Resources["TileArtHeight"] = Math.Round(width * 9 / 16);
    }

    private void OnShowHiddenChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Direkt als Schaltflächen aufgebaut statt über ein ItemsControl, weil dessen Elemente für die
    /// Sprachausgabe sonst leer erscheinen.
    /// </summary>
    private void BuildCategoryNav(List<CategoryItem> categories)
    {
        CategoryNav.Children.Clear();
        foreach (var category in categories)
        {
            var rename = new MenuItem { Header = "Umbenennen …" };
            rename.Click += (_, _) => RenameCategory(category);
            var dissolve = new MenuItem { Header = "Kategorie auflösen" };
            dissolve.Click += (_, _) => DissolveCategory(category);
            var button = new RadioButton
            {
                Style = (Style)FindResource("NavItem"),
                GroupName = "Nav",
                Tag = CategoryPrefix + category.Name,
                Content = category,
                ContentTemplate = (DataTemplate)FindResource("CategoryNavContent"),
                ContextMenu = new ContextMenu { Items = { rename, dissolve } }
            };
            System.Windows.Automation.AutomationProperties.SetName(button, category.Name);
            button.IsChecked = category.IsSelected;
            button.Checked += OnNavChecked;
            CategoryNav.Children.Add(button);
        }
    }

    private void RenameCategory(CategoryItem category)
    {
        if (Prompt("Kategorie umbenennen", category.Name) is not { Length: > 0 } name || name == category.Name)
        {
            return;
        }

        foreach (var entry in library.Where(entry => entry.Category == category.Name))
        {
            entry.Category = name;
        }

        if (SelectedCategory == category.Name)
        {
            filter = CategoryPrefix + name;
        }

        SaveAndRefresh();
    }

    private void DissolveCategory(CategoryItem category)
    {
        foreach (var entry in library.Where(entry => entry.Category == category.Name))
        {
            entry.Category = null;
        }

        SaveAndRefresh();
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        FullscreenAtLogonBox.IsEnabled = AutostartBox.IsChecked;
        // Feuert auch beim Setzen des Startzustands; ein Entwickler-Build würde sonst seinen Pfad eintragen.
        if (IsLoaded)
        {
            Autostart.Set(AutostartBox.IsChecked == true);
        }
    }

    private void OnStartModeClick(object sender, RoutedEventArgs e)
    {
        settings.StartFullscreen = StartFullscreenBox.IsChecked;
        settings.FullscreenAtLogon = FullscreenAtLogonBox.IsChecked;
        settings.Save();
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await RescanAsync();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsButton.ContextMenu.PlacementTarget = SettingsButton;
        SettingsButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        SettingsButton.ContextMenu.IsOpen = true;
    }

    private void OnTileClick(object sender, RoutedEventArgs e) => Launch((LibraryEntry)((Button)sender).DataContext);

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Programm oder Verknüpfung hinzufügen",
            Filter = "Programme und Verknüpfungen|*.exe;*.lnk;*.url;*.bat;*.cmd|Alle Dateien|*.*",
            Multiselect = true,
            // Sonst liefert der Dialog statt der Verknüpfung deren Ziel und Startparameter gehen verloren.
            DereferenceLinks = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };
        if (dialog.ShowDialog(this) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave feuert auch beim Wechsel zwischen Kindelementen; nur beim Verlassen des Fensters ausblenden.
        var position = e.GetPosition(this);
        if (position.X <= 0 || position.Y <= 0 || position.X >= ActualWidth || position.Y >= ActualHeight)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddFiles(paths);
        }
    }

    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var tile = (Button)sender;
        tile.ContextMenu!.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        FillTileMenu(tile);
    }

    private void FillTileMenu(Button tile)
    {
        var entry = (LibraryEntry)tile.DataContext;
        var menu = tile.ContextMenu!;
        menu.Items.Clear();

        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Starten", () => Launch(entry));
        if (entry.IsRunning)
        {
            Add("Beenden …", async () => await StopAsync(entry));
        }

        Add(entry.Favorite ? "Aus Favoriten entfernen" : "Zu Favoriten hinzufügen", () =>
        {
            entry.Favorite = !entry.Favorite;
            SaveAndRefresh();
        });
        if (entry.LastLaunched is not null)
        {
            Add("Aus „Zuletzt“ entfernen", () =>
            {
                entry.LastLaunched = null;
                SaveAndRefresh();
            });
        }

        menu.Items.Add(new Separator());
        Add("Umbenennen …", () =>
        {
            if (Prompt("Umbenennen", entry.DisplayName) is { } name)
            {
                entry.CustomName = name.Length == 0 || name == entry.Name ? null : name;
                SaveAndRefresh();
            }
        });
        Add("Startziel ändern …", () => ChangeTarget(entry));
        Add("Cover-Bild festlegen …", () => ChangeCover(entry));
        if (entry.CustomCoverPath is not null)
        {
            Add("Cover-Bild entfernen", () =>
            {
                Library.DeleteImportedFile(entry.CustomCoverPath);
                entry.CustomCoverPath = null;
                SaveAndRefresh();
            });
        }

        var location = File.Exists(entry.LaunchTarget) ? entry.LaunchTarget : entry.IconSource;
        Add("Dateispeicherort öffnen", () => Process.Start("explorer.exe", $"/select,\"{location}\"")?.Dispose(),
            File.Exists(location));
        var categoryMenu = new MenuItem { Header = "Kategorie" };
        foreach (var name in library.Select(other => other.Category).OfType<string>().Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = entry.Category == name };
            item.Click += (_, _) =>
            {
                entry.Category = entry.Category == name ? null : name;
                SaveAndRefresh();
            };
            categoryMenu.Items.Add(item);
        }

        if (categoryMenu.Items.Count > 0)
        {
            categoryMenu.Items.Add(new Separator());
        }

        var newCategory = new MenuItem { Header = "Neue Kategorie …" };
        newCategory.Click += (_, _) =>
        {
            if (Prompt("Neue Kategorie", "") is { Length: > 0 } name)
            {
                entry.Category = name;
                SaveAndRefresh();
            }
        };
        categoryMenu.Items.Add(newCategory);
        menu.Items.Add(categoryMenu);
        Add(entry.IsGame ? "Zu Programmen verschieben" : "Zu Spielen verschieben", () =>
        {
            entry.IsGame = !entry.IsGame;
            SaveAndRefresh();
        });
        menu.Items.Add(new Separator());
        if (entry.IsManual)
        {
            Add("Entfernen", () =>
            {
                library.Remove(entry);
                SaveAndRefresh();
                Library.DeleteImportedFiles(entry);
            });
        }
        else
        {
            // Erkannte Spiele würden bei der nächsten Suche wieder auftauchen, daher nur ausblenden.
            Add(entry.Hidden ? "Einblenden" : "Ausblenden", () =>
            {
                entry.Hidden = !entry.Hidden;
                SaveAndRefresh();
            });
            Add("Deinstallieren …", async () => await UninstallAsync(entry));
        }
    }

    private async Task StopAsync(LibraryEntry entry)
    {
        if (!ShowDialogCard($"„{entry.DisplayName}“ beenden?", "Nicht gespeicherter Fortschritt geht verloren.", "Beenden", "Abbrechen"))
        {
            return;
        }

        if (!await RunningDetector.StopAsync(entry))
        {
            ShowDialogCard($"„{entry.DisplayName}“ ließ sich nicht ganz beenden", "Vermutlich läuft es mit Administratorrechten.");
        }

        await UpdateRunningAsync();
    }

    private async Task UninstallAsync(LibraryEntry entry)
    {
        if (!ShowDialogCard($"„{entry.DisplayName}“ deinstallieren?", "Öffnet den Launcher oder das Deinstallationsprogramm des Spiels.", "Deinstallieren", "Abbrechen"))
        {
            return;
        }

        try
        {
            if (!await Uninstaller.StartAsync(entry))
            {
                ShowDialogCard("Kein Deinstallationsprogramm gefunden", $"Bitte „{entry.DisplayName}“ über {entry.SourceName} deinstallieren.");
                return;
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // Nachfrage der Benutzerkontensteuerung abgelehnt.
            return;
        }
        catch (Exception exception)
        {
            ShowDialogCard($"„{entry.DisplayName}“ konnte nicht deinstalliert werden", exception.Message);
            return;
        }

        // Launcher und Deinstallationsprogramme arbeiten in eigenen Fenstern weiter; zurück in der App neu suchen.
        rescanOnActivate = true;
        await RescanAsync();
    }

    private void ChangeCover(LibraryEntry entry)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Cover-Bild für „{entry.DisplayName}“",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (ImageCache.Load(dialog.FileName) is null)
        {
            ShowDialogCard("Dieses Bild kann nicht geöffnet werden", "");
            return;
        }

        Library.DeleteImportedFile(entry.CustomCoverPath);
        entry.CustomCoverPath = Library.ImportCover(dialog.FileName);
        SaveAndRefresh();
    }

    private void ChangeTarget(LibraryEntry entry)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Startziel für „{entry.DisplayName}“",
            Filter = "Programme und Verknüpfungen|*.exe;*.lnk;*.url;*.bat;*.cmd|Alle Dateien|*.*",
            DereferenceLinks = false
        };
        if (entry.IconSource is { } current && File.Exists(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        Library.DeleteImportedFile(entry.CustomTarget);
        (entry.CustomTarget, entry.CustomIconPath) = Library.ImportFile(dialog.FileName);
        entry.Icon = null;
        SaveAndRefresh();
    }

    private string? Prompt(string title, string value) =>
        ShowDialogCard(title, "", "OK", "Abbrechen", value) ? DialogInput.Text.Trim() : null;

    private System.Windows.Threading.DispatcherFrame? dialogFrame;
    private bool dialogConfirmed;

    /// <summary>Zeigt einen Dialog im Fenster und wartet wie eine MessageBox auf die Antwort; die Oberfläche läuft währenddessen weiter.</summary>
    private bool ShowDialogCard(string title, string text, string confirm = "OK", string? cancel = null, string? input = null)
    {
        DialogTitle.Text = title;
        DialogText.Text = text;
        DialogText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DialogInput.Text = input ?? "";
        DialogInput.Visibility = input is null ? Visibility.Collapsed : Visibility.Visible;
        DialogConfirm.Content = confirm;
        DialogCancel.Content = cancel;
        DialogCancel.Visibility = cancel is null ? Visibility.Collapsed : Visibility.Visible;
        var previousFocus = Keyboard.FocusedElement;
        DialogHost.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            // Bei Rückfragen startet der Fokus auf „Abbrechen“, damit Enter oder die A-Taste nichts Unwiderrufliches auslöst.
            if (input is not null)
            {
                DialogInput.Focus();
                DialogInput.SelectAll();
            }
            else if (cancel is not null)
            {
                DialogCancel.Focus();
            }
            else
            {
                DialogConfirm.Focus();
            }
        });
        dialogFrame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.PushFrame(dialogFrame);
        dialogFrame = null;
        DialogHost.Visibility = Visibility.Collapsed;
        previousFocus?.Focus();
        return dialogConfirmed;
    }

    private void CloseDialog(bool confirmed)
    {
        dialogConfirmed = confirmed;
        dialogFrame!.Continue = false;
    }

    private void OnDialogConfirmClick(object sender, RoutedEventArgs e) => CloseDialog(true);

    private void OnDialogCancelClick(object sender, RoutedEventArgs e) => CloseDialog(false);
}
