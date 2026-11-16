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

    // Dauerhafte Effekte nur, wenn Windows-Animationen an sind und eine Grafikkarte rendert (nicht etwa über Remotedesktop).
    private readonly bool motion = SystemParameters.ClientAreaAnimation && RenderCapability.Tier >> 16 == 2;
    private readonly Storyboard? ambient;

    public MainWindow()
    {
        InitializeComponent();
        if (motion)
        {
            // Ressourcen aus XAML sind eingefroren; animieren lässt sich nur eine Kopie.
            var runningPulse = ((Brush)FindResource("RunningBrush")).Clone();
            Resources["RunningBrush"] = runningPulse;
            RegisterName("RunningPulse", runningPulse);
            ambient = (Storyboard)FindResource("AmbientLoop");
            ambient.Begin(this, isControllable: true);
            ambient.Pause(this);
        }

        windowFrame = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        // Kachelbilder in der tatsächlich benötigten Auflösung laden (auf großen Bildschirmen bis gut 300 px breit).
        ImageCache.TileWidth = (int)(340 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        AutostartBox.IsChecked = Autostart.IsEnabled;
        StartFullscreenBox.IsChecked = settings.StartFullscreen;
        FullscreenAtLogonBox.IsChecked = settings.FullscreenAtLogon;
        FullscreenAtLogonBox.IsEnabled = Autostart.IsEnabled;
        SortBox.SelectedItem = SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == settings.Sort) ?? SortBox.Items[0];
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
        // Laufende Programme, Controller und Effekte nur, solange das Fenster im Vordergrund ist: beim Spielen kosten sie nichts.
        runningTimer.Tick += async (_, _) => await UpdateRunningAsync();
        padTimer.Tick += (_, _) => PollGamepad();
        Activated += async (_, _) =>
        {
            runningTimer.Start();
            padTimer.Start();
            ambient?.Resume(this);
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
            ambient?.Pause(this);
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
            var now = DateTimeOffset.Now;
            // Woche ab Montag, Monat ab dem 1., jeweils 0 Uhr Ortszeit.
            var weekStart = new DateTimeOffset(now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)), now.Offset);
            var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);
            (playtime, weekPlaytime, monthPlaytime) = await Task.Run(() => (
                PlaytimeReader.Read(PlaytimeReader.DatabasePath, now),
                PlaytimeReader.Read(PlaytimeReader.DatabasePath, now, weekStart),
                PlaytimeReader.Read(PlaytimeReader.DatabasePath, now, monthStart)));
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
        if (changed || filter == "playtime")
        {
            Refresh();
        }
    }

    /// <param name="Times">Spielzeit im Zeitraum des Bereichs (Seite „Spielzeit“); dann zeigen die Kacheln diese statt der Gesamtzeit.</param>
    public sealed record Section(string Title, IReadOnlyList<LibraryEntry> Items, IReadOnlyDictionary<string, TimeSpan>? Times = null)
    {
        // Name des Bereichs für Bedienungshilfen wie die Sprachausgabe.
        public override string ToString() => Title;
    }

    public sealed record CategoryItem(string Name, int Count, bool IsSelected);

    private const string CategoryPrefix = "cat:";

    private List<CategoryItem> shownCategories = [];

    // Spielzeit dieser Woche und dieses Monats aus YFTimeTracker, für die Seite „Spielzeit“.
    private Dictionary<string, TimeSpan> weekPlaytime = [];
    private Dictionary<string, TimeSpan> monthPlaytime = [];

    private const string AllSources = "Alle Launcher";
    private string sourceFilter = AllSources;
    private bool updatingFilters;

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

        var isHome = filter == "all" && query.Length == 0;
        var sources = pool.Select(entry => entry.SourceName).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).Prepend(AllSources).ToList();
        if (SourceBox.ItemsSource is not List<string> shownSources || !shownSources.SequenceEqual(sources))
        {
            updatingFilters = true;
            SourceBox.ItemsSource = sources;
            SourceBox.SelectedItem = sources.Contains(sourceFilter) ? sourceFilter : AllSources;
            sourceFilter = (string)SourceBox.SelectedItem;
            updatingFilters = false;
        }

        var matches = pool
            .Where(entry => filter switch
            {
                "games" => entry.IsGame,
                "apps" => !entry.IsGame,
                "favorites" => entry.Favorite,
                "recent" => entry.LastLaunched is not null,
                "playtime" => entry.Playtime is not null,
                _ when SelectedCategory is { } category => entry.Category == category,
                _ => true
            })
            .Where(entry => entry.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            // Der Launcher-Filter steht in der Seitenüberschrift; die Startseite hat keine und zeigt immer alles.
            .Where(entry => isHome || sourceFilter == AllSources || entry.SourceName == sourceFilter)
            .ToList();
        var byName = matches.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var recent = matches.Where(entry => entry.LastLaunched is not null).OrderByDescending(entry => entry.LastLaunched);
        // OrderBy ist stabil: bei Gleichstand bleibt die alphabetische Reihenfolge.
        IEnumerable<LibraryEntry> sorted = settings.Sort switch
        {
            "recent" => byName.OrderByDescending(entry => entry.LastLaunched ?? DateTime.MinValue),
            "playtime" => byName.OrderByDescending(entry => entry.Playtime ?? TimeSpan.Zero),
            _ => byName.OrderByDescending(entry => entry.Favorite)
        };
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
            "playtime" =>
            [
                PlaytimeSection("Diese Woche", weekPlaytime),
                PlaytimeSection("Dieser Monat", monthPlaytime),
                PlaytimeSection("Insgesamt", matches.ToDictionary(entry => entry.Id, entry => entry.Playtime!.Value))
            ],
            _ => [new("", sorted.ToList())]
        };

        // Titel mit der Summe des Zeitraums; Kacheln darunter nach ihrem Anteil sortiert.
        Section PlaytimeSection(string title, Dictionary<string, TimeSpan> times)
        {
            var played = matches.Where(entry => times.ContainsKey(entry.Id)).OrderByDescending(entry => times[entry.Id]).ToList();
            var total = played.Aggregate(TimeSpan.Zero, (sum, entry) => sum + times[entry.Id]);
            return new($"{title}  ·  {LibraryEntry.FormatPlaytime(total)}", played, times);
        }
        sections.RemoveAll(section => section.Items.Count == 0);
        visible = sections.SelectMany(section => section.Items).ToList();
        Sections.ItemsSource = sections;

        // Das Banner ersetzt auf der Startseite die Überschrift.
        PageHeader.Visibility = Hero.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = query.Length > 0 ? "Suchergebnisse" : filter switch
        {
            "favorites" => "Favoriten",
            "recent" => "Zuletzt gestartet",
            "playtime" => "Spielzeit",
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
        // Auf „Zuletzt gestartet“ und „Spielzeit“ ergibt sich die Reihenfolge aus der Seite selbst.
        SortBox.Visibility = filter is "recent" or "playtime" ? Visibility.Collapsed : Visibility.Visible;

        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyActions.Visibility = pool.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        (EmptyGlyph.Text, EmptyTitle.Text, EmptyText.Text) = (query.Length > 0, filter) switch
        {
            (true, _) => ("", "Keine Treffer", $"Nichts passt zu „{query}“."),
            (_, "favorites") => ("", "Noch keine Favoriten", "Rechtsklick auf eine Kachel und „Zu Favoriten hinzufügen“."),
            (_, "recent") => ("", "Noch nichts gestartet", "Alles, was du hier startest, taucht in dieser Liste auf."),
            (_, "playtime") => ("", "Noch keine Spielzeit", "Die Spielzeit kommt aus YFTimeTracker. Ist er installiert, erscheint sie nach dem ersten Spielen hier."),
            _ when sourceFilter != AllSources => ("", "Keine Treffer", $"Hier gibt es nichts von {sourceFilter}."),
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

    /// <summary>Bild und Schrift gleiten gegenläufig zur Maus: wirkt wie zwei Ebenen hintereinander.</summary>
    private void OnHeroMouseMove(object sender, MouseEventArgs e)
    {
        if (!motion)
        {
            return;
        }

        var offset = e.GetPosition(Hero).X / Hero.ActualWidth - 0.5;
        var glide = TimeSpan.FromMilliseconds(400);
        HeroParallax.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset * -28, glide));
        HeroContentShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset * 10, glide));
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

    private async void Launch(LibraryEntry entry)
    {
        // Doppelklick oder A-Taste während der Startsequenz: nicht ein zweites Mal starten.
        if (LaunchOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        try
        {
            Library.Launch(entry.LaunchTarget);
            if (padConnected)
            {
                _ = XInput.RumbleAsync(TimeSpan.FromMilliseconds(250));
            }

            entry.LastLaunched = DateTime.Now;
            SaveAndRefresh();
        }
        catch (Exception exception)
        {
            ShowDialogCard($"„{entry.DisplayName}“ konnte nicht gestartet werden", exception.Message);
            return;
        }

        await PlayLaunchSplashAsync(entry);
        WindowState = WindowState.Minimized;
        LaunchOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Das Spiel startet schon im Hintergrund; so lange überbrückt die Sequenz die Wartezeit.</summary>
    private async Task PlayLaunchSplashAsync(LibraryEntry entry)
    {
        if (!motion)
        {
            return;
        }

        LaunchOverlay.DataContext = entry;
        LaunchArt.Source = entry.Cover ?? ImageCache.Load(entry.HeroPath, 0);
        LaunchLogo.Source = ImageCache.Load(entry.LogoPath);
        LaunchFallback.Visibility = LaunchLogo.Source is null ? Visibility.Visible : Visibility.Collapsed;
        LaunchOverlay.Visibility = Visibility.Visible;
        ((Storyboard)FindResource("LaunchSplash")).Begin(this);
        await Task.Delay(1200);
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

    /// <summary>Neue Seite oben beginnen und einblenden; nur Banner und die ersten Kachelreihen schweben einzeln ein, das bleibt auch bei großen Bibliotheken flüssig.</summary>
    private void ShowPage()
    {
        ContentScroll.ScrollToTop();
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = (IEasingFunction)FindResource("EaseOut");
        ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        ContentShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });

        // Die Kacheln entstehen erst beim Layout der neuen Liste.
        UpdateLayout();
        var tiles = FindDescendants<Button>(Sections).Where(IsTile).Take(homeColumns * 2);
        var index = 0;
        foreach (var element in HeroContent.Children.Cast<UIElement>().Where(row => row.IsVisible).Concat(tiles))
        {
            Reveal(element, TimeSpan.FromMilliseconds(30 * index++));
        }
    }

    /// <summary>Lässt ein Element nach <paramref name="delay"/> von unten einschweben.</summary>
    private void Reveal(UIElement element, TimeSpan delay)
    {
        if (!motion)
        {
            return;
        }

        var ease = (IEasingFunction)FindResource("EaseOut");
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        // Basiswert statt 1: ausgeblendete Kacheln bleiben halbtransparent.
        element.BeginAnimation(OpacityProperty, Track(0, (double)element.GetAnimationBaseValue(OpacityProperty)));
        shift.BeginAnimation(TranslateTransform.YProperty, Track(26, 0));

        // Keyframes statt BeginTime: so ist das Element auch während der Wartezeit schon unsichtbar.
        DoubleAnimationUsingKeyFrames Track(double from, double to) => new()
        {
            FillBehavior = FillBehavior.Stop,
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)),
                new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(380)), ease)
            }
        };
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
        // Sonst spielt der gewählte Eintrag bei jedem Refresh (etwa jedem Tastendruck in der Suche) seine Animation neu ab.
        if (categories.SequenceEqual(shownCategories))
        {
            return;
        }

        shownCategories = categories;
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

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem { Tag: string sort } && sort != settings.Sort)
        {
            settings.Sort = sort;
            settings.Save();
            Refresh();
        }
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingFilters && SourceBox.SelectedItem is string source)
        {
            sourceFilter = source;
            Refresh();
        }
    }

    /// <summary>„Was spiel ich?“: schlägt ein zufälliges sichtbares Spiel vor.</summary>
    private void OnRandomClick(object sender, RoutedEventArgs e)
    {
        var games = library.Where(entry => entry.IsGame && !entry.Hidden).ToList();
        if (games.Count == 0)
        {
            ShowDialogCard("Keine Spiele gefunden", "Sobald installierte Spiele erkannt sind, schlägt dieser Knopf eins vor.");
            return;
        }

        var pick = games[Random.Shared.Next(games.Count)];
        if (ShowDialogCard($"Wie wär's mit „{pick.DisplayName}“?", pick.Subtitle, "Spielen", "Nein danke"))
        {
            Launch(pick);
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Bibliothek sichern",
            FileName = $"YFAppCenter-Sicherung-{DateTime.Now:yyyy-MM-dd}.zip",
            Filter = "Sicherung|*.zip"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            Library.Save(library);
            Library.Export(dialog.FileName);
            ShowDialogCard("Bibliothek gesichert", $"Favoriten, Kategorien, eigene Einträge und Cover stehen in {dialog.FileName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowDialogCard("Sichern fehlgeschlagen", exception.Message);
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Bibliothek wiederherstellen", Filter = "Sicherung|*.zip" };
        if (dialog.ShowDialog(this) != true
            || !ShowDialogCard("Bibliothek wiederherstellen?",
                "Favoriten, Kategorien, eigene Einträge und Cover werden durch die Sicherung ersetzt. Danach sucht die App die installierten Spiele neu.",
                "Wiederherstellen", "Abbrechen"))
        {
            return;
        }

        try
        {
            Library.Import(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowDialogCard("Wiederherstellen fehlgeschlagen", exception.Message);
            return;
        }

        library = Library.Load();
        await RescanAsync();
    }

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
}

/// <summary>
/// Untertitel einer Kachel: mit der Spielzeit des Bereichs, falls er eine hat (Seite „Spielzeit“), sonst der normale.
/// Werte: Eintrag, sein Untertitel (nur damit die Anzeige Änderungen folgt), Zeiten des Bereichs.
/// </summary>
public sealed class PeriodSubtitleConverter : System.Windows.Data.IMultiValueConverter
{
    public object? Convert(object?[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        values is [LibraryEntry entry, _, IReadOnlyDictionary<string, TimeSpan> times, ..] && times.TryGetValue(entry.Id, out var time)
            ? $"{entry.SourceName} · {LibraryEntry.FormatPlaytime(time)}"
            : values.ElementAtOrDefault(1) as string;

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
