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
}
