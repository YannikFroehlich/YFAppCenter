using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace YFAppCenter.Tests;

[TestClass]
public sealed class LibraryTests
{
    [TestMethod]
    public void Merge_keeps_manual_entries_and_user_changes_and_drops_uninstalled_games()
    {
        var existing = new List<LibraryEntry>
        {
            new() { Id = "manual:1", Name = "G HUB", Target = @"C:\lghub.exe", IsManual = true },
            new() { Id = "Steam:1", Name = "Old", Target = "steam://rungameid/1", IsGame = true, Hidden = true, Favorite = true, Category = "Rennspiele", LastLaunched = new DateTime(2026, 10, 1), CustomCoverPath = @"C:\cover.png", CustomName = "Mein Spiel", CustomTarget = @"C:\x.exe" },
            new() { Id = "Steam:2", Name = "Uninstalled", Target = "steam://rungameid/2", IsGame = true }
        };
        var detected = new List<LibraryEntry>
        {
            new() { Id = "Steam:1", Name = "New", Target = "steam://rungameid/1", IsGame = true },
            new() { Id = "Epic:3", Name = "Fresh", Target = "com.epicgames.launcher://apps/x", IsGame = true }
        };

        var merged = Library.Merge(existing, detected);

        CollectionAssert.AreEquivalent(new[] { "manual:1", "Steam:1", "Epic:3" }, merged.Select(entry => entry.Id).ToArray());
        var game = merged.Single(entry => entry.Id == "Steam:1");
        Assert.AreEqual("New", game.Name);
        Assert.AreEqual("Mein Spiel", game.DisplayName);
        Assert.AreEqual(@"C:\x.exe", game.LaunchTarget);
        Assert.IsTrue(game.Hidden);
        Assert.IsTrue(game.Favorite);
        Assert.AreEqual("Rennspiele", game.Category);
        Assert.AreEqual(new DateTime(2026, 10, 1), game.LastLaunched);
        Assert.AreEqual(@"C:\cover.png", game.CustomCoverPath);
    }

    [TestMethod]
    public void Merge_keeps_games_on_a_missing_drive()
    {
        var usedLetters = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        var missingDrive = "ZYXWVUTSRQPONMLKJIHGFED".First(letter => !usedLetters.Contains(letter));
        var existing = new List<LibraryEntry>
        {
            new() { Id = "Steam:1", Name = "Offline", Target = "steam://rungameid/1", IsGame = true, Favorite = true, InstallDirectory = $@"{missingDrive}:\Steam\common\Offline" },
            new() { Id = "Steam:2", Name = "Uninstalled", Target = "steam://rungameid/2", IsGame = true, InstallDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }
        };

        var merged = Library.Merge(existing, []);

        Assert.AreEqual("Steam:1", merged.Single().Id);
        Assert.IsTrue(merged.Single().Favorite);
    }

    [TestMethod]
    public void IsRunning_matches_processes_inside_install_directory_or_exact_executable()
    {
        var game = new LibraryEntry { Id = "Steam:1", Name = "Game", Target = "steam://rungameid/1", InstallDirectory = @"D:\Games\Game" };
        var app = new LibraryEntry { Id = "manual:1", Name = "G HUB", Target = @"C:\x\copy.lnk", IconPath = @"C:\Program Files\LGHUB\lghub.exe", IsManual = true };

        Assert.IsTrue(RunningDetector.IsRunning(game, [@"D:\GAMES\GAME\BIN\GAME.EXE"]));
        Assert.IsFalse(RunningDetector.IsRunning(game, [@"D:\GAMES\GAME2\GAME2.EXE"]), "Nachbarordner mit gleichem Anfang zählt nicht.");
        Assert.IsTrue(RunningDetector.IsRunning(app, [@"C:\PROGRAM FILES\LGHUB\LGHUB.EXE"]));
        Assert.IsFalse(RunningDetector.IsRunning(app, [@"C:\PROGRAM FILES\LGHUB\LGHUB_AGENT.EXE"]), "Hintergrunddienste im selben Ordner zählen nicht.");
    }

    [TestMethod]
    public void CreateManual_copies_shortcut_and_takes_icon_from_its_target()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var shortcut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(shortcut);
        link.TargetPath = notepad;
        link.Save();
        try
        {
            var entry = Library.CreateManual(shortcut, isGame: false);
            File.Delete(shortcut);

            Assert.IsTrue(File.Exists(entry.Target), "Die Kopie muss das Löschen des Originals überleben.");
            Assert.AreEqual(notepad, entry.IconPath, ignoreCase: true);
            Library.DeleteImportedFile(entry.Target);
        }
        finally
        {
            File.Delete(shortcut);
        }
    }

    [TestMethod]
    public void FindMainExecutable_prefers_shallow_non_helper_executables()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            void Exe(string relative, int size)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[size]);
            }

            Exe("unins000.exe", 5000);
            Exe("UnityCrashHandler64.exe", 4000);
            Exe("Game.exe", 100);
            Exe(@"Game\Binaries\Win64\Game-Win64-Shipping.exe", 9000);

            Assert.AreEqual(Path.Combine(root, "Game.exe"), GameScanner.FindMainExecutable(root, "Unknown"));

            Exe(@"Binaries\Win64\RocketLeague.exe", 50);
            Assert.AreEqual(Path.Combine(root, @"Binaries\Win64\RocketLeague.exe"), GameScanner.FindMainExecutable(root, "Rocket League"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
