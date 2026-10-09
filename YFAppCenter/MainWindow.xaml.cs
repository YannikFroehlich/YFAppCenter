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
    private static readonly Dictionary<string, ImageSource?> HeroImages = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        AutostartBox.IsChecked = Autostart.IsEnabled;
        if (Environment.GetCommandLineArgs().Contains(Autostart.MinimizedArgument))
        {
            WindowState = WindowState.Minimized;
        }

        Refresh();
        Loaded += async (_, _) => await RescanAsync();
    }

    private async Task RescanAsync()
    {
        RescanButton.IsEnabled = false;
        StatusText.Text = "Suche installierte Spiele …";
        try
        {
            var detected = await GameScanner.ScanAsync();
            library = Library.Merge(library, detected);
            Library.Save(library);
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

    public sealed record Section(string Title, IReadOnlyList<LibraryEntry> Items);

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        var pool = library.Where(entry => ShowHiddenBox.IsChecked == true || !entry.Hidden).ToList();
        CountAll.Text = pool.Count.ToString();
        CountFavorites.Text = pool.Count(entry => entry.Favorite).ToString();
        CountRecent.Text = Math.Min(RecentCount, pool.Count(entry => entry.LastLaunched is not null)).ToString();
        CountGames.Text = pool.Count(entry => entry.IsGame).ToString();
        CountApps.Text = pool.Count(entry => !entry.IsGame).ToString();

        var matches = pool
            .Where(entry => filter switch
            {
                "games" => entry.IsGame,
                "apps" => !entry.IsGame,
                "favorites" => entry.Favorite,
                "recent" => entry.LastLaunched is not null,
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
                new("Zuletzt gestartet", recent.Take(HomeRecentCount).ToList()),
                new("Favoriten", byName.Where(entry => entry.Favorite).ToList()),
                new("Spiele", byName.Where(entry => entry.IsGame).ToList()),
                new("Programme", byName.Where(entry => !entry.IsGame).ToList())
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
        featured = pool.Where(entry => entry.LastLaunched is not null).MaxBy(entry => entry.LastLaunched)
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

        HeroImage.Source = LoadImage(featured.HeroPath);
        HeroLogo.Source = LoadImage(featured.LogoPath);
        HeroLogo.Visibility = HeroLogo.Source is null ? Visibility.Collapsed : Visibility.Visible;
        HeroTitle.Visibility = HeroLogo.Source is null ? Visibility.Visible : Visibility.Collapsed;
        HeroLabel.Text = featured.LastLaunched is { } launched
            ? $"WEITERMACHEN  ·  {Ago(launched)}"
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

    private static ImageSource? LoadImage(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        if (!HeroImages.TryGetValue(path, out var image))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 1600;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
            {
                image = null;
            }

            HeroImages[path] = image;
        }

        return image;
    }

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
            library.Add(Library.CreateManual(path, isGame: filter == "games"));
        }

        SaveAndRefresh();
    }

    private void OnActivated(object? sender, EventArgs e) => SearchBox.Focus();

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

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            filter = tag;
        }

        if (IsLoaded)
        {
            Refresh();
        }
    }

    // Feuert auch beim Setzen des Startzustands; das schreibt nur den aktuellen Programmpfad neu.
    private void OnAutostartChanged(object sender, RoutedEventArgs e) => Autostart.Set(AutostartBox.IsChecked == true);

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
        var location = File.Exists(entry.LaunchTarget) ? entry.LaunchTarget : entry.IconSource;
        Add("Dateispeicherort öffnen", () => Process.Start("explorer.exe", $"/select,\"{location}\"")?.Dispose(),
            File.Exists(location));
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
                Library.DeleteImportedShortcut(entry.Target);
                Library.DeleteImportedShortcut(entry.CustomTarget);
                SaveAndRefresh();
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

        Library.DeleteImportedShortcut(entry.CustomTarget);
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
