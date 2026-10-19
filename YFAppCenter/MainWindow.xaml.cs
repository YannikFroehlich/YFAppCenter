using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace YFAppCenter;

public partial class MainWindow : Window
{
    private const int RecentCount = 20;
    private const int HomeRecentCount = 6;

    private List<LibraryEntry> library = Library.Load();
    private List<LibraryEntry> visible = [];
    private string filter = "all";
    private LibraryEntry? featured;
    private HashSet<string> runningIds = [];
    private bool checkingRunning;
    private readonly System.Windows.Threading.DispatcherTimer runningTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        AutostartBox.IsChecked = Autostart.IsEnabled;
        SetUpTray();
        if (Environment.GetCommandLineArgs().Contains(Autostart.MinimizedArgument))
        {
            WindowState = WindowState.Minimized;
            // Beim ersten Anzeigen meldet WPF keinen Zustandswechsel, daher hier selbst in den Infobereich.
            Loaded += (_, _) => Hide();
        }

        Refresh();
        Loaded += async (_, _) => await RescanAsync();
        runningTimer.Tick += async (_, _) => await UpdateRunningAsync();
        runningTimer.Start();
        padTimer.Tick += (_, _) => PollGamepad();
        Activated += (_, _) => padTimer.Start();
        Deactivated += (_, _) => padTimer.Stop();
        LogoPanel.ToolTip = $"Version {AppUpdater.CurrentVersion}";
        AppUpdater.SyncInstalledVersion();
        Loaded += async (_, _) => await CheckForUpdateAsync();
        updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        updateTimer.Start();
    }

    // Läuft die App per Autostart tagelang, soll sie trotzdem neue Versionen finden.
    private readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private async Task CheckForUpdateAsync()
    {
        try
        {
            if (await AppUpdater.DownloadUpdateAsync() is { } version)
            {
                UpdateText.Text = $"Version {version} ist bereit";
                UpdatePanel.Visibility = Visibility.Visible;
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

    private void SetUpTray()
    {
        using (var icon = Application.GetResourceStream(new Uri("pack://application:,,,/AppCenter.ico")).Stream)
        {
            tray.Icon = new System.Drawing.Icon(icon, System.Windows.Forms.SystemInformation.SmallIconSize);
        }

        tray.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip
        {
            Items =
            {
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
        }
    }

    /// <summary>Holt das Fenster aus dem Infobereich nach vorne, etwa bei einem zweiten Start der App.</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            // Stellt auch ein vorher maximiertes Fenster wieder maximiert her.
            SystemCommands.RestoreWindow(this);
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
                if (openMenu is not null)
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
    {
        RescanButton.IsEnabled = false;
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
            StatusText.Text = $"{detected.Count} Spiele erkannt · {library.Count(entry => entry.IsManual)} eigene Einträge";
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

        List<Section> sections = filter switch
        {
            // Startseite ohne Suche: in Bereiche gegliedert, wie ein Startmenü.
            "all" when query.Length == 0 =>
            [
                new("Läuft gerade", byName.Where(entry => entry.IsRunning).ToList()),
                new("Zuletzt gestartet", recent.Take(HomeRecentCount).ToList()),
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

        var isHome = filter == "all" && query.Length == 0;
        UpdateHero(pool, isHome);
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

        HeroImage.Source = featured.Cover ?? ImageCache.Load(featured.HeroPath);
        HeroLogo.Source = ImageCache.Load(featured.LogoPath);
        HeroLogo.Visibility = HeroLogo.Source is null ? Visibility.Collapsed : Visibility.Visible;
        HeroTitle.Visibility = HeroLogo.Source is null ? Visibility.Visible : Visibility.Collapsed;
        HeroLabel.Text = featured.IsRunning ? "●  LÄUFT GERADE"
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

    private void OnHeroSizeChanged(object sender, SizeChangedEventArgs e) => HeroClip.Rect = new Rect(e.NewSize);

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
            MessageBox.Show(this, $"„{entry.DisplayName}“ konnte nicht gestartet werden.\n\n{exception.Message}",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        SearchBox.Focus();
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
        }
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
        // Feuert auch beim Setzen des Startzustands; ein Entwickler-Build würde sonst seinen Pfad eintragen.
        if (IsLoaded)
        {
            Autostart.Set(AutostartBox.IsChecked == true);
        }
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await RescanAsync();

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
        }
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
            MessageBox.Show(this, "Dieses Bild kann nicht geöffnet werden.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private string? Prompt(string title, string value)
    {
        var box = new TextBox { Text = value, MinWidth = 340, Margin = new Thickness(0, 0, 0, 16) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
                }
            }
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
