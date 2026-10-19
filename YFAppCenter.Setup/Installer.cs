using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace YFAppCenter.Setup;

public sealed class InstallOptions
{
    public string Directory { get; set; } = Installer.DefaultDirectory;
    public bool StartMenuShortcut { get; set; } = true;
    public bool DesktopShortcut { get; set; }
    public bool Autostart { get; set; }
}

public sealed class InstalledInfo
{
    public InstalledInfo(string directory, string version, InstallOptions options)
    {
        Directory = directory;
        Version = version;
        Options = options;
    }

    public string Directory { get; }
    public string Version { get; }
    public InstallOptions Options { get; }
}

/// <summary>
/// Installiert pro Benutzer: entpackt das eingebettete Velopack-Paket (dadurch funktionieren die
/// automatischen Updates der App), legt Verknüpfungen an und trägt die App unter „Apps“ ein.
/// </summary>
public static class Installer
{
    public const string AppName = "YF AppCenter";
    public const string UninstallerName = "Uninstall.exe";

    // Velopack nennt den Starter im Paket nach dem Titel; er startet immer die aktuelle Version in "current".
    private const string StubName = "YF AppCenter.exe";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YFAppCenter";
    private const string LegacyUninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YFAppCenter.App";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "YFAppCenter";
    private const string RuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DefaultDirectory => Path.Combine(LocalAppData, "Programs", AppName);

    public static string Version => typeof(Installer).Assembly.GetName().Version.ToString(3);

    /// <summary>Bibliothek, Favoriten und Cover der App; liegt bewusst außerhalb des Installationsordners.</summary>
    public static string DataFolder => Path.Combine(LocalAppData, "YFAppCenter");

    private static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    private static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static bool HasPackage => typeof(Installer).Assembly.GetManifestResourceInfo("app.zip") is not null;

    public static InstalledInfo? FindInstallation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        if (key?.GetValue("InstallLocation") is not string directory || !System.IO.Directory.Exists(directory))
        {
            return null;
        }

        return new InstalledInfo(directory, key.GetValue("DisplayVersion") as string ?? "?", new InstallOptions
        {
            Directory = directory,
            StartMenuShortcut = Equals(key.GetValue("StartMenuShortcut"), 1),
            DesktopShortcut = Equals(key.GetValue("DesktopShortcut"), 1),
            Autostart = IsAutostartEnabled()
        });
    }

    /// <summary>Größe der App nach dem Entpacken in Bytes.</summary>
    public static long PackageSize()
    {
        using var archive = OpenPackage();
        return archive.Entries.Sum(entry => entry.Length);
    }

    /// <summary>Liefert eine Fehlermeldung, wenn in diesen Ordner nicht installiert werden kann.</summary>
    public static string? ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
        {
            return "Bitte einen vollständigen Ordnerpfad angeben, z. B. C:\\Spiele\\YF AppCenter.";
        }

        try
        {
            var full = Path.GetFullPath(directory);
            if (Path.GetPathRoot(full) == full)
            {
                return "Bitte einen Unterordner wählen, nicht das ganze Laufwerk.";
            }

            if (System.IO.Directory.Exists(full)
                && System.IO.Directory.EnumerateFileSystemEntries(full).Any()
                && !File.Exists(Path.Combine(full, StubName)))
            {
                return "Der Ordner ist nicht leer. Bitte einen leeren oder neuen Ordner wählen.";
            }

            // Schreibrechte prüfen, ohne etwas zurückzulassen: nächsten vorhandenen Elternordner testen.
            var existing = full;
            while (!System.IO.Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
                if (existing is null)
                {
                    return "Dieses Laufwerk gibt es nicht.";
                }
            }

            var probe = Path.Combine(existing, ".yfappcenter-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Keine Schreibrechte in diesem Ordner. Bitte einen Ordner im eigenen Benutzerbereich wählen, "
                + "damit auch die automatischen Updates funktionieren.";
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return "Dieser Ordner kann nicht verwendet werden: " + exception.Message;
        }
    }

    public static bool IsRuntimeInstalled()
    {
        var shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        return System.IO.Directory.Exists(shared)
            && System.IO.Directory.EnumerateDirectories(shared, "10.*").Any();
    }

    public static async Task InstallAsync(InstallOptions options, IProgress<(double Percent, string Status)> progress)
    {
        var directory = Path.GetFullPath(options.Directory);

        if (!IsRuntimeInstalled())
        {
            await InstallRuntimeAsync(progress);
        }

        await Task.Run(() =>
        {
            progress.Report((40, "Beende laufendes YF AppCenter …"));
            CloseRunningApp(directory);
            RemoveLegacyInstall(progress);

            progress.Report((42, "Kopiere Dateien …"));
            System.IO.Directory.CreateDirectory(directory);
            // Alte Programmdateien entfernen, damit nach einem Update keine Reste liegen bleiben.
            var current = Path.Combine(directory, "current");
            if (System.IO.Directory.Exists(current))
            {
                System.IO.Directory.Delete(current, recursive: true);
            }

            ExtractPackage(directory, percent => progress.Report((42 + percent * 0.48, "Kopiere Dateien …")));

            progress.Report((92, "Richte Verknüpfungen ein …"));
            var self = Assembly.GetExecutingAssembly().Location;
            var uninstaller = Path.Combine(directory, UninstallerName);
            if (!string.Equals(Path.GetFullPath(self), uninstaller, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(self, uninstaller, overwrite: true);
            }

            var stub = Path.Combine(directory, StubName);
            SetShortcut(StartMenuShortcutPath, options.StartMenuShortcut, stub, directory);
            SetShortcut(DesktopShortcutPath, options.DesktopShortcut, stub, directory);
            SetAutostart(options.Autostart ? stub : null);

            progress.Report((97, "Trage YF AppCenter unter „Apps“ ein …"));
            Register(directory, options);
            progress.Report((100, "Fertig"));
        });
    }

    public static async Task UninstallAsync(bool deleteData, IProgress<(double Percent, string Status)> progress)
    {
        var installation = FindInstallation();
        await Task.Run(() =>
        {
            progress.Report((10, "Beende laufendes YF AppCenter …"));
            if (installation is not null)
            {
                CloseRunningApp(installation.Directory);
            }

            progress.Report((30, "Entferne Verknüpfungen …"));
            DeleteFile(StartMenuShortcutPath);
            DeleteFile(DesktopShortcutPath);
            SetAutostart(null);

            progress.Report((50, "Entferne Programmdateien …"));
            // Sicherheitsnetz: nur einen Ordner löschen, der wirklich YF AppCenter enthält.
            if (installation is not null && File.Exists(Path.Combine(installation.Directory, StubName)))
            {
                DeleteInstallDirectory(installation.Directory);
            }

            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            if (deleteData && System.IO.Directory.Exists(DataFolder))
            {
                progress.Report((85, "Lösche Bibliothek …"));
                System.IO.Directory.Delete(DataFolder, recursive: true);
            }

            progress.Report((100, "Fertig"));
        });
    }

    public static void LaunchApp(string directory) =>
        Process.Start(new ProcessStartInfo(Path.Combine(directory, StubName)) { UseShellExecute = true, WorkingDirectory = directory })?.Dispose();

    private static ZipArchive OpenPackage()
    {
        var stream = typeof(Installer).Assembly.GetManifestResourceStream("app.zip")
            ?? throw new InvalidOperationException("Dieses Setup enthält kein Programmpaket. Bitte über scripts\\New-Release.ps1 bauen.");
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static void ExtractPackage(string directory, Action<double> reportPercent)
    {
        using var archive = OpenPackage();
        var total = Math.Max(1, archive.Entries.Sum(entry => entry.Length));
        long done = 0;
        var root = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(directory, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Ungültiger Pfad im Programmpaket: " + entry.FullName);
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                System.IO.Directory.CreateDirectory(target);
                continue;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            done += entry.Length;
            reportPercent(done * 100.0 / total);
        }
    }

    private static async Task InstallRuntimeAsync(IProgress<(double Percent, string Status)> progress)
    {
        progress.Report((2, "Lade .NET-Desktop-Laufzeit herunter …"));
        var file = Path.Combine(Path.GetTempPath(), "windowsdesktop-runtime-10-win-x64.exe");
        using (var client = new HttpClient())
        using (var response = await client.GetAsync(RuntimeUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength ?? 0;
            using var input = await response.Content.ReadAsStreamAsync();
            using var output = File.Create(file);
            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await output.WriteAsync(buffer, 0, count);
                read += count;
                if (length > 0)
                {
                    progress.Report((2 + read * 28.0 / length, $"Lade .NET-Desktop-Laufzeit herunter … {read / 1048576} von {length / 1048576} MB"));
                }
            }
        }

        progress.Report((32, "Installiere .NET-Desktop-Laufzeit (bitte Nachfrage bestätigen) …"));
        try
        {
            using var installer = Process.Start(new ProcessStartInfo(file, "/install /quiet /norestart") { UseShellExecute = true, Verb = "runas" })!;
            await Task.Run(installer.WaitForExit);
            // 3010: erfolgreich, Neustart empfohlen.
            if (installer.ExitCode is not (0 or 3010))
            {
                throw new InvalidOperationException($"Die .NET-Laufzeit konnte nicht installiert werden (Code {installer.ExitCode}).");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("YF AppCenter braucht die .NET-Desktop-Laufzeit 10. Die Installation wurde abgebrochen.");
        }
        finally
        {
            DeleteFile(file);
        }
    }

    private static void CloseRunningApp(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("YFAppCenter").Concat(Process.GetProcessesByName("YF AppCenter")))
        {
            using (process)
            {
                try
                {
                    if (!process.MainModule!.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!process.CloseMainWindow() || !process.WaitForExit(3000))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Schon beendet oder nicht unser Prozess.
                }
            }
        }
    }

    private static void SetShortcut(string path, bool create, string target, string workingDirectory)
    {
        if (!create)
        {
            DeleteFile(path);
            return;
        }

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = target + ",0";
        shortcut.Description = "Spiele und Programme starten";
        shortcut.Save();
    }

    // Gleicher Eintrag, den die App selbst über „Mit Windows starten“ setzt.
    private static void SetAutostart(string? executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)!;
        if (executable is null)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(RunValueName, $"\"{executable}\" --minimized");
        }
    }

    /// <summary>
    /// Version 0.2.0 wurde mit Velopacks Ein-Klick-Setup installiert (anderer Ordner, eigener Eintrag unter „Apps“).
    /// Diese Installation wird entfernt, damit nicht zwei Versionen nebeneinander liegen. Die Bibliothek bleibt.
    /// </summary>
    private static void RemoveLegacyInstall(IProgress<(double Percent, string Status)> progress)
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyUninstallKeyPath);
        if (key?.GetValue("InstallLocation") is not string legacyDirectory)
        {
            return;
        }

        var updater = Path.Combine(legacyDirectory, "Update.exe");
        if (!File.Exists(updater))
        {
            return;
        }

        progress.Report((41, "Entferne die bisherige Installation …"));
        CloseRunningApp(legacyDirectory);
        using var uninstall = Process.Start(new ProcessStartInfo(updater, "--uninstall --silent") { UseShellExecute = false, CreateNoWindow = true });
        uninstall?.WaitForExit(60000);
    }

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private static void Register(string directory, InstallOptions options)
    {
        var size = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        var uninstaller = Path.Combine(directory, UninstallerName);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath)!;
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "YannikFroehlich");
        key.SetValue("DisplayIcon", Path.Combine(directory, StubName) + ",0");
        key.SetValue("InstallLocation", directory);
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --silent");
        key.SetValue("URLInfoAbout", "https://github.com/YannikFroehlich/YFAppCenter");
        key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("StartMenuShortcut", options.StartMenuShortcut ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("DesktopShortcut", options.DesktopShortcut ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Löscht den Installationsordner. Läuft die Deinstallation aus diesem Ordner, kann sich die laufende
    /// Datei nicht selbst löschen; den Rest erledigt dann ein kurzer Befehl nach dem Beenden.
    /// </summary>
    private static void DeleteInstallDirectory(string directory)
    {
        var self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        foreach (var sub in System.IO.Directory.GetDirectories(directory))
        {
            System.IO.Directory.Delete(sub, recursive: true);
        }

        foreach (var file in System.IO.Directory.GetFiles(directory))
        {
            if (!string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }

        if (self.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
        {
            // Erst beim Beenden löschen (siehe DeleteLeftoversAfterExit), solange ist die Datei gesperrt.
            directoryToDeleteOnExit = directory;
        }
        else
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static string? directoryToDeleteOnExit;

    /// <summary>Beim Programmende aufrufen: entfernt den Ordner samt laufender Uninstall.exe, kurz nachdem sie beendet ist.</summary>
    public static void DeleteLeftoversAfterExit()
    {
        if (directoryToDeleteOnExit is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{directoryToDeleteOnExit}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath()
        })?.Dispose();
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
