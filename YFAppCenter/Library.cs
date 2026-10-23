using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.Storage.FileProperties;
using YFTimeTracker.Core.Models;
using YFTimeTracker.Core.Services;
using YFTimeTracker.Windows.Processes;
using YFTimeTracker.Windows.SystemInfo;

namespace YFAppCenter;

public sealed class LibraryEntry : INotifyPropertyChanged
{
    private ImageSource? icon;
    private TimeSpan? playtime;

    /// <summary>"Steam:123" für erkannte Spiele, "manual:…" für selbst hinzugefügte Einträge.</summary>
    public required string Id { get; init; }
    public required string Name { get; set; }
    /// <summary>Datei, Verknüpfung oder URI (steam://…), die beim Starten geöffnet wird.</summary>
    public required string Target { get; set; }
    public string? IconPath { get; set; }
    /// <summary>Breites Titelbild und Schriftzug aus dem Steam-Cache; für das Banner der Startseite.</summary>
    public string? HeroPath { get; set; }
    public string? LogoPath { get; set; }
    /// <summary>Querformatiges Titelbild aus dem Steam-Cache; für die Kacheln.</summary>
    public string? HeaderPath { get; set; }
    /// <summary>Installationsordner erkannter Spiele; läuft eine .exe darin, gilt das Spiel als gestartet.</summary>
    public string? InstallDirectory { get; set; }
    public bool IsGame { get; set; }
    public bool IsManual { get; init; }
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
    public string? Category { get; set; }
    public DateTime? LastLaunched { get; set; }

    // Änderungen des Nutzers an erkannten Spielen; überleben jede neue Suche.
    public string? CustomName { get; set; }
    public string? CustomTarget { get; set; }
    public string? CustomIconPath { get; set; }
    public string? CustomCoverPath { get; set; }

    [JsonIgnore] public bool IsRunning { get; set; }
    [JsonIgnore] public string DisplayName => CustomName ?? Name;
    [JsonIgnore] public string LaunchTarget => CustomTarget ?? Target;
    [JsonIgnore] public string? IconSource => CustomIconPath ?? IconPath;
    [JsonIgnore] public ImageSource? Cover => ImageCache.Load(CustomCoverPath);
    [JsonIgnore] public ImageSource? TileArt => ImageCache.Load(CustomCoverPath ?? HeaderPath, ImageCache.TileWidth);

    /// <summary>Gesamte Spielzeit laut YFTimeTracker; null ohne Tracker oder wenn das Spiel dort nicht vorkommt.</summary>
    [JsonIgnore]
    public TimeSpan? Playtime
    {
        get => playtime;
        set
        {
            playtime = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
        }
    }

    [JsonIgnore]
    public string SourceName => IsManual ? (IsGame ? "Spiel" : "Programm") : Id.Split(':')[0] switch
    {
        "Epic" => "Epic Games",
        "Gog" => "GOG",
        "BattleNet" => "Battle.net",
        "Ubisoft" => "Ubisoft Connect",
        "EaApp" => "EA app",
        var source => source
    };

    [JsonIgnore]
    public string Subtitle => Playtime switch
    {
        null => SourceName,
        { TotalHours: < 1 } time => $"{SourceName} · {(int)time.TotalMinutes} Min.",
        var time => $"{SourceName} · {time.Value.TotalHours:0.#} Std."
    };

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => icon;
        set
        {
            icon = value;
            IconWash = value is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap ? Halo(bitmap) : null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconWash)));
        }
    }

    /// <summary>
    /// Lichthof in der kräftigsten Farbe des Icons: ein winziges Bild, das groß gezogen weich zum Rand ausläuft.
    /// Wirkt wie ein Weichzeichner, kostet aber keinen Grafikeffekt pro Kachel.
    /// </summary>
    [JsonIgnore] public ImageSource? IconWash { get; private set; }

    internal static BitmapSource Halo(BitmapSource icon)
    {
        const double sample = 16;
        var small = new FormatConvertedBitmap(
            new TransformedBitmap(icon, new ScaleTransform(sample / icon.PixelWidth, sample / icon.PixelHeight)),
            PixelFormats.Bgra32, null, 0);
        var pixels = new byte[small.PixelWidth * small.PixelHeight * 4];
        small.CopyPixels(pixels, small.PixelWidth * 4, 0);
        double red = 0, green = 0, blue = 0, total = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            int b = pixels[index], g = pixels[index + 1], r = pixels[index + 2], a = pixels[index + 3];
            // Kräftige Farben zählen mehr als Grau, Weiß und Schwarz; durchsichtige Pixel gar nicht.
            var weight = a / 255.0 * (0.05 + (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0);
            (red, green, blue, total) = (red + r * weight, green + g * weight, blue + b * weight, total + weight);
        }

        (red, green, blue) = total > 0 ? (red / total, green / total, blue / total) : (128, 128, 128);
        // Dunkle Farben aufhellen, sonst verschwindet der Schein auf dem dunklen Hintergrund.
        var brighten = Math.Max(1, 220 / Math.Max(1, Math.Max(red, Math.Max(green, blue))));

        // Mitte voll deckend, zum Rand hin (weich ausgeglättet) durchsichtig; vormultipliertes Alpha.
        const int size = 7;
        var halo = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var falloff = Math.Clamp(1 - Math.Sqrt((x - 3) * (x - 3) + (y - 3) * (y - 3)) / 3, 0, 1);
                var alpha = falloff * falloff * (3 - 2 * falloff);
                var index = (y * size + x) * 4;
                halo[index] = (byte)Math.Min(255, blue * brighten * alpha);
                halo[index + 1] = (byte)Math.Min(255, green * brighten * alpha);
                halo[index + 2] = (byte)Math.Min(255, red * brighten * alpha);
                halo[index + 3] = (byte)(255 * alpha);
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, halo, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Name der Kachel für Bedienungshilfen wie die Sprachausgabe.
    public override string ToString() => DisplayName;
}

public static class Library
{
    public static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YFAppCenter");

    private static readonly string LibraryFile = Path.Combine(DataFolder, "library.json");
    private static readonly string ShortcutFolder = Path.Combine(DataFolder, "Shortcuts");
    private static readonly string CoverFolder = Path.Combine(DataFolder, "Covers");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<LibraryEntry> Load()
    {
        if (!File.Exists(LibraryFile))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<LibraryEntry>>(File.ReadAllText(LibraryFile)) ?? [];
        }
        catch (JsonException)
        {
            // Kaputte Datei beiseitelegen statt sie beim nächsten Speichern zu überschreiben.
            File.Move(LibraryFile, LibraryFile + $".defekt-{DateTime.Now:yyyyMMdd-HHmmss}");
            return [];
        }
    }

    public static void Save(IEnumerable<LibraryEntry> entries)
    {
        Directory.CreateDirectory(DataFolder);
        var temporary = LibraryFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporary, LibraryFile, overwrite: true);
    }

    /// <summary>
    /// Ersetzt die erkannten Spiele durch das neue Suchergebnis. Eigene Einträge bleiben, Umbenennungen,
    /// Ausblenden usw. werden auf die neu erkannten Einträge übertragen, deinstallierte Spiele fallen weg.
    /// Spiele auf einem gerade fehlenden Laufwerk (USB-Platte, Netzlaufwerk) bleiben samt Einstellungen erhalten.
    /// </summary>
    public static List<LibraryEntry> Merge(IEnumerable<LibraryEntry> existing, IEnumerable<LibraryEntry> detected)
    {
        var previous = existing.ToList();
        var byId = previous.Where(entry => !entry.IsManual).ToDictionary(entry => entry.Id);
        var merged = previous.Where(entry => entry.IsManual).ToList();
        foreach (var entry in detected)
        {
            if (byId.Remove(entry.Id, out var old))
            {
                entry.CustomName = old.CustomName;
                entry.CustomTarget = old.CustomTarget;
                entry.CustomIconPath = old.CustomIconPath;
                entry.CustomCoverPath = old.CustomCoverPath;
                entry.Hidden = old.Hidden;
                entry.IsGame = old.IsGame;
                entry.Favorite = old.Favorite;
                entry.Category = old.Category;
                entry.LastLaunched = old.LastLaunched;
            }

            merged.Add(entry);
        }

        // Übrig sind nicht mehr gefundene Spiele.
        merged.AddRange(byId.Values.Where(entry => IsOnMissingDrive(entry.InstallDirectory)));
        return merged;
    }

    private static bool IsOnMissingDrive(string? directory) =>
        Path.GetPathRoot(directory) is { Length: > 0 } root && !Directory.Exists(root);

    /// <summary>
    /// Bereitet eine Datei als Startziel vor. Verknüpfungen werden kopiert, damit die Originale
    /// (z. B. auf dem Desktop) danach gelöscht werden können.
    /// </summary>
    public static (string Target, string IconPath) ImportFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".lnk" or ".url"))
        {
            return (path, path);
        }

        Directory.CreateDirectory(ShortcutFolder);
        var copy = Path.Combine(ShortcutFolder, $"{Guid.NewGuid():N}{extension}");
        File.Copy(path, copy);
        return (copy, ResolveShortcutTarget(copy) ?? copy);
    }

    public static LibraryEntry CreateManual(string path, bool isGame)
    {
        var (target, iconPath) = ImportFile(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
        {
            name = description.Trim();
        }

        return new LibraryEntry
        {
            Id = $"manual:{Guid.NewGuid():N}",
            Name = name,
            Target = target,
            IconPath = iconPath,
            IsGame = isGame,
            IsManual = true
        };
    }

    /// <summary>Kopiert ein Cover-Bild in den Datenordner, damit das Original verschoben oder gelöscht werden darf.</summary>
    public static string ImportCover(string path)
    {
        Directory.CreateDirectory(CoverFolder);
        var copy = Path.Combine(CoverFolder, $"{Guid.NewGuid():N}{Path.GetExtension(path).ToLowerInvariant()}");
        File.Copy(path, copy);
        return copy;
    }

    /// <summary>Löscht die Verknüpfungs- und Cover-Kopien eines Eintrags, der aus der Bibliothek entfernt wurde.</summary>
    public static void DeleteImportedFiles(LibraryEntry entry)
    {
        DeleteImportedFile(entry.Target);
        DeleteImportedFile(entry.CustomTarget);
        DeleteImportedFile(entry.CustomCoverPath);
    }

    /// <summary>Löscht nur Kopien, die das AppCenter selbst angelegt hat.</summary>
    public static void DeleteImportedFile(string? path)
    {
        if (path is not null
            && (path.StartsWith(ShortcutFolder, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(CoverFolder, StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(path);
        }
    }

    public static void Launch(string target)
    {
        var workingDirectory = File.Exists(target) ? Path.GetDirectoryName(target) ?? "" : "";
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = workingDirectory })?.Dispose();
    }

    private static string? ResolveShortcutTarget(string shortcutPath)
    {
        if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            string target = shell.CreateShortcut(shortcutPath).TargetPath;
            return File.Exists(target) ? target : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class RunningDetector
{
    private static readonly WindowsProcessSnapshotProvider Processes = new(NullLogger<WindowsProcessSnapshotProvider>.Instance);

    public static async Task<HashSet<string>> GetRunningIdsAsync(IReadOnlyList<LibraryEntry> entries)
    {
        var processes = await Task.Run(() => Processes.GetRunningProcessesAsync(CancellationToken.None));
        var processKeys = processes.Select(process => process.ExecutablePathKey).ToList();
        return entries.Where(entry => IsRunning(entry, processKeys)).Select(entry => entry.Id).ToHashSet();
    }

    internal static bool IsRunning(LibraryEntry entry, IReadOnlyList<string> processKeys) => processKeys.Any(BelongsTo(entry));

    /// <summary>
    /// Beendet alle Prozesse, an denen der Eintrag als laufend erkannt wird. Liefert false, wenn einer davon
    /// sich nicht beenden ließ (etwa weil er mit Administratorrechten läuft).
    /// </summary>
    public static async Task<bool> StopAsync(LibraryEntry entry)
    {
        var belongs = BelongsTo(entry);
        var processes = await Task.Run(() => Processes.GetRunningProcessesAsync(CancellationToken.None));
        return await Task.Run(() =>
        {
            var stoppedAll = true;
            foreach (var running in processes.Where(process => belongs(process.ExecutablePathKey)))
            {
                // Die Prozessliste kennt nur Pfade; gleichnamige Prozesse anderer Ordner bleiben verschont.
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(running.ExecutablePath)))
                {
                    using (process)
                    {
                        try
                        {
                            if (Key(process.MainModule?.FileName) == running.ExecutablePathKey)
                            {
                                process.Kill();
                                // Sonst zählt der Prozess beim gleich folgenden Abgleich noch als laufend.
                                process.WaitForExit(TimeSpan.FromSeconds(3));
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // Inzwischen von selbst beendet.
                        }
                        catch (Win32Exception)
                        {
                            // Kein Zugriff, etwa bei Prozessen mit Administratorrechten.
                            stoppedAll = false;
                        }
                    }
                }
            }

            return stoppedAll;
        });
    }

    // ponytail: eigene Einträge zählen nur bei genau derselben .exe. Programme, die über einen Starter
    // laufen (etwa Discords Update.exe), werden nicht erkannt; bei Bedarf Startziel auf die echte .exe setzen.
    private static Func<string, bool> BelongsTo(LibraryEntry entry)
    {
        var prefix = Key(entry.InstallDirectory) is { } directory ? directory + Path.DirectorySeparatorChar : null;
        var executables = new[] { entry.CustomTarget, entry.Target, entry.CustomIconPath, entry.IconPath }
            .Where(path => path?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            .Select(Key)
            .OfType<string>()
            .ToHashSet();
        return key => (prefix is not null && key.StartsWith(prefix, StringComparison.Ordinal)) || executables.Contains(key);
    }

    private static string? Key(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return ExecutablePathNormalizer.CreateKey(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

public static class Autostart
{
    public const string MinimizedArgument = "--minimized";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "YFAppCenter";

    /// <summary>
    /// Velopacks Starter im Installationsordner (eine Ebene über "current"), wie ihn auch der Installer einträgt.
    /// Ohne Installation, etwa beim Entwickeln, die laufende Datei.
    /// </summary>
    private static string Executable
    {
        get
        {
            var installRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!;
            var stub = Path.Combine(installRoot, "YF AppCenter.exe");
            return File.Exists(stub) ? stub : Environment.ProcessPath!;
        }
    }

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Executable}\" {MinimizedArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

public static class GameScanner
{
    private const string SteamworksRedistributablesAppId = "228980";

    private static readonly string[] HelperExecutableNames =
    [
        "unins", "crash", "setup", "redist", "dxsetup", "dotnet", "prereq", "install", "helper",
        "report", "cleanup", "easyanticheat", "beservice", "battleye", "touchup", "cefprocess"
    ];

    public static async Task<List<LibraryEntry>> ScanAsync()
    {
        var provider = new WindowsGameInstallationProvider(NullLogger<WindowsGameInstallationProvider>.Instance);
        var result = await Task.Run(() => provider.DiscoverAsync(CancellationToken.None));
        return await Task.Run(() =>
        {
            var epicAppNames = ReadEpicAppNames();
            var uninstallIcons = ReadUninstallIcons();
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamRoot = steamKey?.GetValue("SteamPath") as string;
            var entries = new List<LibraryEntry>();
            // Steam meldet manche Spiele doppelt (common + content\app_<Id>); der erste Ordner ist der richtige.
            foreach (var game in result.Games.GroupBy(game => (game.Source, game.ExternalGameId)).Select(group => group.First()))
            {
                if (game.Source == GameSource.Steam && game.ExternalGameId == SteamworksRedistributablesAppId)
                {
                    continue;
                }

                // Das Icon aus dem Deinstallationseintrag ist das offizielle (bei Steam eine eigene .ico).
                var registryIcon = uninstallIcons.GetValueOrDefault(Path.TrimEndingDirectorySeparator(game.InstallDirectory));
                var registryExecutable = registryIcon?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? registryIcon : null;
                var executable = game.LaunchExecutablePaths.FirstOrDefault(File.Exists)
                    // Bei EA zeigt das Icon auf das eigentliche Spiel; Battle.net-Spiele brauchen dagegen den Launcher im Hauptordner.
                    ?? (game.Source == GameSource.EaApp ? registryExecutable : null)
                    ?? FindMainExecutable(game.InstallDirectory, game.Name);
                var (xboxTarget, xboxLogo) = game.Source == GameSource.Xbox ? XboxApp(game.ExternalGameId) : (null, null);
                var target = game.Source switch
                {
                    GameSource.Steam => $"steam://rungameid/{game.ExternalGameId}",
                    GameSource.Ubisoft => $"uplay://launch/{game.ExternalGameId}/0",
                    GameSource.Epic when epicAppNames.TryGetValue(game.ExternalGameId, out var appName) =>
                        $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true",
                    GameSource.Xbox => xboxTarget ?? executable,
                    // GOG, Battle.net, EA: Spiel direkt starten; nötige Launcher starten die Spiele selbst.
                    // ponytail: Battle.net/EA haben keine verlässliche Start-URI, Startziel notfalls im Kontextmenü ändern.
                    _ => executable
                };
                if (target is not null)
                {
                    var steamArt = game.Source == GameSource.Steam && steamRoot is not null
                        ? Path.Combine(steamRoot, "appcache", "librarycache")
                        : null;
                    var entry = new LibraryEntry
                    {
                        Id = $"{game.Source}:{game.ExternalGameId}",
                        Name = game.Name,
                        Target = target,
                        IconPath = xboxLogo ?? registryIcon ?? executable,
                        HeroPath = steamArt is null ? null : FindSteamImage(steamArt, game.ExternalGameId, "library_hero.jpg"),
                        LogoPath = steamArt is null ? null : FindSteamImage(steamArt, game.ExternalGameId, "logo.png"),
                        // Neuere Steam-Versionen nennen das Titelbild library_header.jpg.
                        HeaderPath = steamArt is null ? null : FindSteamImage(steamArt, game.ExternalGameId, "header.jpg")
                            ?? FindSteamImage(steamArt, game.ExternalGameId, "library_header.jpg"),
                        InstallDirectory = game.InstallDirectory,
                        IsGame = true
                    };
                    StoreArt.ApplyCached(entry);
                    entries.Add(entry);
                }
            }

            return entries;
        });
    }

    // ponytail: Heuristik – .exe, die wie Spiel/Ordner heißt, sonst flachste, dann größte, die nicht nach
    // Hilfsprogramm aussieht. Trifft bei den meisten Spielen; bei Fehlgriffen Startziel im Kontextmenü ändern.
    internal static string? FindMainExecutable(string directory, string gameName)
    {
        try
        {
            var root = new DirectoryInfo(directory);
            var names = new[] { Letters(gameName), Letters(root.Name) };
            return root
                .EnumerateFiles("*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(file => !HelperExecutableNames.Any(name => file.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(file => names.Contains(Letters(Path.GetFileNameWithoutExtension(file.Name))))
                .ThenBy(file => Path.GetRelativePath(root.FullName, file.FullName).Count(c => c == Path.DirectorySeparatorChar))
                .ThenByDescending(file => file.Length)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static string Letters(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>Neuere Steam-Versionen legen Bilder unter librarycache\&lt;AppId&gt;\&lt;Hash&gt;\ ab, ältere als &lt;AppId&gt;_&lt;Name&gt;.</summary>
    private static string? FindSteamImage(string libraryCache, string appId, string fileName)
    {
        try
        {
            var flat = Path.Combine(libraryCache, $"{appId}_{fileName}");
            if (File.Exists(flat))
            {
                return flat;
            }

            var directory = Path.Combine(libraryCache, appId);
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Installationsordner → Icon-Datei aus den Windows-Deinstallationseinträgen.</summary>
    private static Dictionary<string, string> ReadUninstallIcons()
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (location, displayIcon, _) in ReadUninstallEntries())
        {
            // Format: "C:\Pfad\Spiel.exe",0
            if (displayIcon?.Split(',')[0].Trim().Trim('"') is { Length: > 0 } icon && File.Exists(icon))
            {
                icons.TryAdd(location, icon);
            }
        }

        return icons;
    }

    /// <summary>Windows-Deinstallationseinträge mit Installationsordner, Icon und Deinstallationsbefehl.</summary>
    internal static IEnumerable<(string Location, string? Icon, string? Command)> ReadUninstallEntries()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(name);
                    if (entry?.GetValue("InstallLocation") is string { Length: > 0 } location)
                    {
                        yield return (Path.TrimEndingDirectorySeparator(location.Trim().Trim('"')),
                            entry.GetValue("DisplayIcon") as string, entry.GetValue("UninstallString") as string);
                    }
                }
            }
        }
    }

    /// <summary>YFTimeTracker kennt nur die CatalogItemId, die Epic-Start-URI braucht den AppName.</summary>
    private static Dictionary<string, string> ReadEpicAppNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
        {
            return names;
        }

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                if (json.RootElement.TryGetProperty("AppName", out var appName) && appName.GetString() is { Length: > 0 } name)
                {
                    names[name] = name;
                    if (json.RootElement.TryGetProperty("CatalogItemId", out var catalogId) && catalogId.GetString() is { Length: > 0 } id)
                    {
                        names[id] = name;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
            }
        }

        return names;
    }

    /// <summary>Start-Ziel der Xbox-App und deren Logo (die .exe-Dateien von Xbox-Spielen haben meist kein Icon).</summary>
    private static (string? Target, string? Logo) XboxApp(string packageFamilyName)
    {
        try
        {
            var package = new PackageManager().FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
            var app = package?.GetAppListEntries().FirstOrDefault();
            if (app is null)
            {
                return (null, null);
            }

            var logo = Path.Combine(Library.DataFolder, "Icons", $"{packageFamilyName}.png");
            if (!File.Exists(logo))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logo)!);
                using var source = app.DisplayInfo.GetLogo(new Windows.Foundation.Size(256, 256)).OpenReadAsync().AsTask().GetAwaiter().GetResult();
                using var output = File.Create(logo);
                source.AsStreamForRead().CopyTo(output);
            }

            return ($@"shell:AppsFolder\{app.AppUserModelId}", logo);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }
}

/// <summary>Liest die Spielzeit aus der Datenbank von YFTimeTracker, falls der installiert ist. Schreibt nie hinein.</summary>
public static class PlaytimeReader
{
    public static readonly string DatabasePath = new WindowsAppPathProvider().DatabasePath;

    /// <summary>
    /// "Steam:123" → Spielzeit, gezählt wie in YFTimeTracker: Basis-Spielzeit plus Sitzungen, laufende bis jetzt.
    /// Beide Programme erkennen Spiele mit demselben Code, daher passen die Kennungen zusammen.
    /// </summary>
    public static Dictionary<string, TimeSpan> Read(string databasePath, DateTimeOffset now)
    {
        var playtime = new Dictionary<string, TimeSpan>();
        if (!File.Exists(databasePath))
        {
            return playtime;
        }

        // Ohne Pooling bleibt die Datei nicht offen, während der Tracker hineinschreibt.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // Zeitpunkte stehen dort als UTC-Ticks; Sitzungen ohne gespeicherte Dauer laufen noch oder wurden nicht sauber beendet.
        command.CommandText = """
            SELECT g.Source, g.ExternalGameId, COALESCE(g.BaselinePlaytimeMinutes, 0) * 60 + COALESCE((
                SELECT SUM(COALESCE(s.DurationSeconds, MAX(0, (COALESCE(s.EndedAtUtc, $now) - s.StartedAtUtc) / 10000000)))
                FROM GameSessions s WHERE s.GameId = g.Id), 0)
            FROM Games g
            WHERE g.ExternalGameId IS NOT NULL
            """;
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(2) >= 60)
            {
                playtime[$"{(GameSource)reader.GetInt32(0)}:{reader.GetString(1)}"] = TimeSpan.FromSeconds(reader.GetInt64(2));
            }
        }

        return playtime;
    }
}

public static class Uninstaller
{
    /// <summary>
    /// Startet die Deinstallation eines erkannten Spiels über seinen Launcher oder sein Deinstallationsprogramm.
    /// Liefert false, wenn es keinen Weg dafür gibt (etwa bei Epic Games, das nur selbst deinstalliert).
    /// </summary>
    public static async Task<bool> StartAsync(LibraryEntry entry)
    {
        var id = entry.Id[(entry.Id.IndexOf(':') + 1)..];
        switch (entry.Id.Split(':')[0])
        {
            case "Steam":
                Library.Launch($"steam://uninstall/{id}");
                return true;
            case "Ubisoft":
                Library.Launch($"uplay://uninstall/{id}");
                return true;
            case "Xbox":
                var manager = new PackageManager();
                if (manager.FindPackagesForUser(string.Empty, id).FirstOrDefault() is not { } package)
                {
                    return false;
                }

                await manager.RemovePackageAsync(package.Id.FullName).AsTask();
                return true;
        }

        if (entry.InstallDirectory is null)
        {
            return false;
        }

        var directory = Path.TrimEndingDirectorySeparator(entry.InstallDirectory);
        var command = await Task.Run(() => GameScanner.ReadUninstallEntries()
            .FirstOrDefault(uninstall => uninstall.Command is { Length: > 0 }
                && string.Equals(uninstall.Location, directory, StringComparison.OrdinalIgnoreCase)).Command);
        if (command is null)
        {
            return false;
        }

        var (file, arguments) = SplitCommand(command);
        Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })?.Dispose();
        return true;
    }

    /// <summary>Teilt einen Befehl aus der Registry in Programm und Argumente; dort steht der Pfad nicht immer in Anführungszeichen.</summary>
    internal static (string File, string Arguments) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            return close < 0 ? (command.Trim('"'), "") : (command[1..close], command[(close + 1)..].Trim());
        }

        var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + ".exe".Length;
        return end < ".exe".Length ? (command, "") : (command[..end], command[end..].Trim());
    }
}

/// <summary>
/// Holt Titelbild, Banner und Schriftzug aus dem Steam-Shop, für Spiele anderer Launcher über den Namen.
/// Die Bilder liegen danach im Datenordner; ein leerer Ordner merkt sich, dass es das Spiel dort nicht gibt.
/// </summary>
public static class StoreArt
{
    private const string ImageBase = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps";
    private const string StoreApi = "https://store.steampowered.com/api";
    private static readonly string ArtFolder = Path.Combine(Library.DataFolder, "Art");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string FolderFor(string id) => Path.Combine(ArtFolder, string.Join("_", id.Split(Path.GetInvalidFileNameChars())));

    /// <summary>Übernimmt schon geladene Bilder, ohne etwas herunterzuladen. Bilder aus dem Steam-Cache haben Vorrang.</summary>
    public static void ApplyCached(LibraryEntry entry)
    {
        var folder = FolderFor(entry.Id);
        entry.HeaderPath ??= Existing(Path.Combine(folder, "header.jpg"));
        entry.HeroPath ??= Existing(Path.Combine(folder, "library_hero.jpg"));
        entry.LogoPath ??= Existing(Path.Combine(folder, "logo.png"));

        static string? Existing(string path) => File.Exists(path) ? path : null;
    }

    /// <summary>Lädt Bilder für erkannte Spiele ohne Titelbild. Liefert true, wenn neue dazugekommen sind.</summary>
    public static async Task<bool> FetchMissingAsync(IEnumerable<LibraryEntry> entries)
    {
        var changed = false;
        foreach (var entry in entries.Where(entry => entry is { IsManual: false, IsGame: true, HeaderPath: null }).ToList())
        {
            var folder = FolderFor(entry.Id);
            if (Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                var appId = entry.Id.StartsWith("Steam:", StringComparison.Ordinal) ? entry.Id["Steam:".Length..] : await FindAppIdAsync(entry.Name);
                var images = new Dictionary<string, byte[]>();
                var urls = new Dictionary<string, string?>();
                if (appId is not null)
                {
                    // Neuere Spiele haben das Titelbild unter einem Pfad mit Hash, den nur die Shop-Daten kennen.
                    urls["header.jpg"] = await HeaderUrlAsync(appId);
                    urls["library_hero.jpg"] = $"{ImageBase}/{appId}/library_hero.jpg";
                    urls["logo.png"] = $"{ImageBase}/{appId}/logo.png";
                }

                foreach (var (file, url) in urls)
                {
                    if (url is null)
                    {
                        continue;
                    }

                    using var response = await Http.GetAsync(url);
                    if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        images[file] = await response.EnsureSuccessStatusCode().Content.ReadAsByteArrayAsync();
                    }
                }

                // Erst schreiben, wenn alles geklappt hat: ohne Netz wird es beim nächsten Mal erneut versucht.
                Directory.CreateDirectory(folder);
                foreach (var (file, bytes) in images)
                {
                    await File.WriteAllBytesAsync(Path.Combine(folder, file), bytes);
                }

                ApplyCached(entry);
                changed |= images.Count > 0;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IOException)
            {
            }
        }

        return changed;
    }

    private static async Task<string?> FindAppIdAsync(string name)
    {
        var url = $"{StoreApi}/storesearch/?cc=US&l=english&term={Uri.EscapeDataString(name)}";
        using var json = JsonDocument.Parse(await Http.GetStringAsync(url));
        var results = json.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (Name: item.GetProperty("name").GetString() ?? "", Id: item.GetProperty("id").GetInt32()))
            .ToList();
        if (MatchAppId(name, results) is { } id)
        {
            return id.ToString();
        }

        // Zusätze wie "Cities: Skylines II - PC Edition" findet die Shop-Suche nicht; dann ohne sie suchen.
        var dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? await FindAppIdAsync(name[..dash]) : null;
    }

    private static async Task<string?> HeaderUrlAsync(string appId)
    {
        using var json = JsonDocument.Parse(await Http.GetStringAsync($"{StoreApi}/appdetails?appids={appId}&filters=basic"));
        return json.RootElement.GetProperty(appId).TryGetProperty("data", out var data)
            && data.TryGetProperty("header_image", out var url) ? url.GetString() : null;
    }

    // ponytail: Abgleich nur über den Namen. Erst ein genauer Treffer, sonst der beste Treffer, wenn ein Name wortweise
    // mit dem anderen beginnt ("Overwatch" ↔ "Overwatch® 2"). Erwischt das ein falsches Spiel: eigenes Cover setzen.
    internal static int? MatchAppId(string name, IReadOnlyList<(string Name, int Id)> results)
    {
        var words = Words(name);
        if (words.Length == 0 || results.Count == 0)
        {
            return null;
        }

        if (results.FirstOrDefault(result => Words(result.Name).SequenceEqual(words)) is { Id: > 0 } exact)
        {
            return exact.Id;
        }

        var best = Words(results[0].Name);
        var (shorter, longer) = best.Length < words.Length ? (best, words) : (words, best);
        return shorter.Length > 0 && longer.Take(shorter.Length).SequenceEqual(shorter) ? results[0].Id : null;

        static string[] Words(string text) =>
            Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+").Where(word => word.Length > 0).ToArray();
    }
}

public static class ImageCache
{
    /// <summary>
    /// Dekodierbreite für Kachelbilder in Pixeln; die App setzt sie passend zur Bildschirmskalierung,
    /// damit Bilder scharf sind, ohne mehr Speicher als nötig zu belegen.
    /// </summary>
    public static int TileWidth { get; set; } = 280;

    /// <summary>So klein dekodiert ergibt ein groß gezogenes Bild einen weichen Farbschein.</summary>
    public const int GlowWidth = 16;

    // Nur vom UI-Thread benutzt.
    private static readonly Dictionary<(string Path, int Width), ImageSource?> Images = [];

    /// <summary>Lädt ein Bild verkleinert auf <paramref name="decodeWidth"/>, damit viele Kacheln wenig Speicher brauchen.</summary>
    public static ImageSource? Load(string? path, int decodeWidth = 1600)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var key = (path.ToLowerInvariant(), decodeWidth);
        if (!Images.TryGetValue(key, out var image))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
            {
                image = null;
            }

            Images[key] = image;
        }

        return image;
    }
}

public static class IconLoader
{
    // Icons erscheinen in Kacheln mit 60 px und im Banner ohne Bild mit 112 px; 128 px bleiben in Kacheln auch bei
    // 200 % Skalierung scharf und brauchen ein Viertel von 256 px.
    private const int IconSize = 128;

    // Nur vom UI-Thread benutzt.
    private static readonly Dictionary<string, Task<ImageSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<ImageSource?> LoadAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        if (!Cache.TryGetValue(path, out var task))
        {
            Cache[path] = task = LoadCoreAsync(path);
        }

        return task;
    }

    private static async Task<ImageSource?> LoadCoreAsync(string path)
    {
        // Bei vielen gleichzeitigen Anfragen schlägt die Shell gelegentlich fehl; ein zweiter Versuch reicht dann.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await TryLoadAsync(path) is { } image)
            {
                return image;
            }

            await Task.Delay(500);
        }

        return null;
    }

    private static async Task<ImageSource?> TryLoadAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, IconSize, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            using var stream = thumbnail.AsStreamForRead();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = IconSize;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
