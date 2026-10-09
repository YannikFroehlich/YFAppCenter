using System.IO;
using Velopack;
using Velopack.Sources;

namespace YFAppCenter;

/// <summary>Automatische Updates über die GitHub-Releases des Projekts.</summary>
public static class AppUpdater
{
    private const string RepositoryUrl = "https://github.com/YannikFroehlich/YFAppCenter";

    private static readonly UpdateManager? Manager = CreateManager();

    public static string CurrentVersion => Manager?.CurrentVersion?.ToNormalizedString()
        ?? typeof(AppUpdater).Assembly.GetName().Version?.ToString(3)
        ?? "?";

    /// <summary>
    /// Lädt eine neue Version im Hintergrund herunter und liefert deren Nummer. Die Version wird beim Schließen
    /// der App installiert, oder sofort über <see cref="ApplyAndRestart"/>. Ohne Velopack-Installation
    /// (etwa beim Entwickeln) passiert nichts.
    /// </summary>
    public static async Task<string?> DownloadUpdateAsync()
    {
#pragma warning disable CS0618 // IsInstalled ist der dokumentierte Weg zu erkennen, ob Velopack die App installiert hat.
        if (Manager is null || !Manager.IsInstalled)
#pragma warning restore CS0618
        {
            return null;
        }

        if (Manager.UpdatePendingRestart is { } pending)
        {
            return pending.Version.ToNormalizedString();
        }

        var update = await Manager.CheckForUpdatesAsync();
        if (update is null)
        {
            return null;
        }

        await Manager.DownloadUpdatesAsync(update);
        Manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
        return update.TargetFullRelease.Version.ToNormalizedString();
    }

    public static void ApplyAndRestart()
    {
        if (Manager?.UpdatePendingRestart is { } pending)
        {
            Manager.ApplyUpdatesAndRestart(pending);
        }
    }

    /// <summary>
    /// Hält die Versionsnummer unter „Apps“ aktuell, wenn die App über den eigenen Installer eingerichtet wurde
    /// und sich seitdem per Update erneuert hat.
    /// </summary>
    public static void SyncInstalledVersion()
    {
        if (Manager?.CurrentVersion is not { } version)
        {
            return;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\YFAppCenter", writable: true);
        // Installationsordner = Elternordner von "current", in dem die App läuft.
        var installRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (key?.GetValue("InstallLocation") is string location
            && string.Equals(Path.TrimEndingDirectorySeparator(location), installRoot, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue("DisplayVersion", version.ToNormalizedString());
        }
    }

    private static UpdateManager? CreateManager()
    {
        try
        {
            return new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch (Exception)
        {
            // Ohne Velopack-Umgebung (z. B. beim Entwickeln) gibt es eben keine Updates.
            return null;
        }
    }
}
