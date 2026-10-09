using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.Storage.FileProperties;
using YFTimeTracker.Core.Models;
using YFTimeTracker.Core.Services;
using YFTimeTracker.Windows.Processes;

namespace YFAppCenter;

public sealed class LibraryEntry : INotifyPropertyChanged
{
    private ImageSource? icon;

    /// <summary>"Steam:123" für erkannte Spiele, "manual:…" für selbst hinzugefügte Einträge.</summary>
    public required string Id { get; init; }
    public required string Name { get; set; }
    /// <summary>Datei, Verknüpfung oder URI (steam://…), die beim Starten geöffnet wird.</summary>
    public required string Target { get; set; }
    public string? IconPath { get; set; }
    /// <summary>Breites Titelbild und Schriftzug aus dem Steam-Cache; für das Banner der Startseite.</summary>
    public string? HeroPath { get; set; }
    public string? LogoPath { get; set; }
    /// <summary>Installationsordner erkannter Spiele; läuft eine .exe darin, gilt das Spiel als gestartet.</summary>
    public string? InstallDirectory { get; set; }
    public bool IsGame { get; set; }
    public bool IsManual { get; init; }
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
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

    [JsonIgnore]
    public string Subtitle => IsManual ? (IsGame ? "Spiel" : "Programm") : Id.Split(':')[0] switch
    {
        "Epic" => "Epic Games",
        "Gog" => "GOG",
        "BattleNet" => "Battle.net",
        "Ubisoft" => "Ubisoft Connect",
        "EaApp" => "EA app",
        var source => source
    };

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => icon;
        set
        {
            icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
    /// </summary>
    public static List<LibraryEntry> Merge(IEnumerable<LibraryEntry> existing, IEnumerable<LibraryEntry> detected)
    {
        var previous = existing.ToList();
        var byId = previous.Where(entry => !entry.IsManual).ToDictionary(entry => entry.Id);
        var merged = previous.Where(entry => entry.IsManual).ToList();
        foreach (var entry in detected)
        {
            if (byId.TryGetValue(entry.Id, out var old))
            {
                entry.CustomName = old.CustomName;
                entry.CustomTarget = old.CustomTarget;
                entry.CustomIconPath = old.CustomIconPath;
                entry.CustomCoverPath = old.CustomCoverPath;
                entry.Hidden = old.Hidden;
                entry.IsGame = old.IsGame;
                entry.Favorite = old.Favorite;
                entry.LastLaunched = old.LastLaunched;
            }

            merged.Add(entry);
        }

        return merged;
    }

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

    // ponytail: eigene Einträge zählen nur bei genau derselben .exe. Programme, die über einen Starter
    // laufen (etwa Discords Update.exe), werden nicht erkannt; bei Bedarf Startziel auf die echte .exe setzen.
    internal static bool IsRunning(LibraryEntry entry, IReadOnlyList<string> processKeys)
    {
        if (Key(entry.InstallDirectory) is { } directory)
        {
            var prefix = directory + Path.DirectorySeparatorChar;
            if (processKeys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        var executables = new[] { entry.CustomTarget, entry.Target, entry.CustomIconPath, entry.IconPath }
            .Where(path => path?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            .Select(Key)
            .OfType<string>()
            .ToHashSet();
        return processKeys.Any(executables.Contains);
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
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {MinimizedArgument}");
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
                    entries.Add(new LibraryEntry
                    {
                        Id = $"{game.Source}:{game.ExternalGameId}",
                        Name = game.Name,
                        Target = target,
                        IconPath = xboxLogo ?? registryIcon ?? executable,
                        HeroPath = steamArt is null ? null : FindSteamImage(steamArt, game.ExternalGameId, "library_hero.jpg"),
                        LogoPath = steamArt is null ? null : FindSteamImage(steamArt, game.ExternalGameId, "logo.png"),
                        InstallDirectory = game.InstallDirectory,
                        IsGame = true
                    });
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
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(name);
                    if (entry?.GetValue("InstallLocation") is not string { Length: > 0 } location
                        || entry.GetValue("DisplayIcon") is not string { Length: > 0 } icon)
                    {
                        continue;
                    }

                    // Format: "C:\Pfad\Spiel.exe",0
                    icon = icon.Split(',')[0].Trim().Trim('"');
                    if (File.Exists(icon))
                    {
                        icons.TryAdd(Path.TrimEndingDirectorySeparator(location.Trim().Trim('"')), icon);
                    }
                }
            }
        }

        return icons;
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

public static class ImageCache
{
    // Nur vom UI-Thread benutzt.
    private static readonly Dictionary<string, ImageSource?> Images = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        if (!Images.TryGetValue(path, out var image))
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

            Images[path] = image;
        }

        return image;
    }
}

public static class IconLoader
{
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
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 256, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            using var stream = thumbnail.AsStreamForRead();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
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
