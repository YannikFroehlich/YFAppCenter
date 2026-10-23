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
    public async Task StopAsync_ends_processes_inside_the_install_directory_only()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var game = Path.Combine(root, "Game", "ping.exe");
        var neighbour = Path.Combine(root, "Other", "ping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        Directory.CreateDirectory(Path.GetDirectoryName(neighbour)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), game);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), neighbour);
        using var gameProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(game, "-n 60 127.0.0.1") { CreateNoWindow = true })!;
        using var otherProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(neighbour, "-n 60 127.0.0.1") { CreateNoWindow = true })!;
        try
        {
            var entry = new LibraryEntry { Id = "Steam:1", Name = "Game", Target = "steam://rungameid/1", InstallDirectory = Path.GetDirectoryName(game) };

            Assert.IsTrue(await RunningDetector.StopAsync(entry));
            Assert.IsTrue(gameProcess.HasExited);
            Assert.IsFalse(otherProcess.HasExited, "Gleichnamiger Prozess in einem anderen Ordner läuft weiter.");
        }
        finally
        {
            gameProcess.Kill();
            otherProcess.Kill();
            otherProcess.WaitForExit();
            Directory.Delete(root, recursive: true);
        }
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
    public void MatchAppId_prefers_exact_names_and_accepts_whole_word_prefixes_of_the_best_result()
    {
        (string, int)[] plagueTale = [("A Plague Tale: Innocence - Coats of Arms DLC", 2), ("A Plague Tale: Innocence", 1)];
        Assert.AreEqual(1, StoreArt.MatchAppId("A Plague Tale: Innocence", plagueTale));
        Assert.AreEqual(3, StoreArt.MatchAppId("EA SPORTS FC 26", [("EA SPORTS FC™ 26", 3)]));
        Assert.AreEqual(4, StoreArt.MatchAppId("Overwatch", [("Overwatch® 2", 4)]));
        Assert.AreEqual(5, StoreArt.MatchAppId("Cities: Skylines II - PC Edition", [("Cities: Skylines II", 5)]));
        Assert.IsNull(StoreArt.MatchAppId("Rust", [("Rusty Lake: Roots", 6)]), "Nur ganze Wörter zählen.");
        Assert.IsNull(StoreArt.MatchAppId("Forza Horizon 6", [("Forza Horizon 5", 7)]));
        Assert.IsNull(StoreArt.MatchAppId("Minecraft Launcher", [("Minecraft Dungeons", 8)]));
        Assert.IsNull(StoreArt.MatchAppId("VALORANT", []));
    }

    [TestMethod]
    public void PlaytimeReader_adds_baseline_stored_and_running_sessions_like_the_tracker()
    {
        var database = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        long Ticks(int minutesAgo) => now.AddMinutes(-minutesAgo).UtcTicks;
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    CREATE TABLE Games (Id INTEGER PRIMARY KEY, Source INTEGER, ExternalGameId TEXT, BaselinePlaytimeMinutes INTEGER);
                    CREATE TABLE GameSessions (Id INTEGER PRIMARY KEY, GameId INTEGER, StartedAtUtc INTEGER, EndedAtUtc INTEGER, DurationSeconds INTEGER);
                    INSERT INTO Games VALUES (1, 1, '570', 60), (2, 2, 'abc', NULL), (3, 0, NULL, 600), (4, 3, 'short', NULL);
                    INSERT INTO GameSessions VALUES
                        (1, 1, {Ticks(200)}, {Ticks(170)}, 1800),
                        (2, 1, {Ticks(10)}, NULL, NULL),
                        (3, 2, {Ticks(5)}, {Ticks(3)}, NULL),
                        (4, 4, {Ticks(1)}, {Ticks(0)}, 30);
                    """;
                command.ExecuteNonQuery();
            }

            var playtime = PlaytimeReader.Read(database, now);

            Assert.AreEqual(TimeSpan.FromMinutes(60 + 30 + 10), playtime["Steam:570"], "Basis, gespeicherte und laufende Sitzung.");
            Assert.AreEqual(TimeSpan.FromMinutes(2), playtime["Epic:abc"], "Beendete Sitzung ohne gespeicherte Dauer.");
            Assert.AreEqual(2, playtime.Count, "Ohne Kennung kein Treffer, unter einer Minute nichts anzeigen.");
        }
        finally
        {
            File.Delete(database);
        }
    }

    [TestMethod]
    public void SplitCommand_separates_quoted_and_unquoted_programs_from_arguments()
    {
        Assert.AreEqual((@"C:\ProgramData\Battle.net\Agent\Blizzard Uninstaller.exe", @"--uid=wow --displayname=""World of Warcraft"""),
            Uninstaller.SplitCommand(@"""C:\ProgramData\Battle.net\Agent\Blizzard Uninstaller.exe"" --uid=wow --displayname=""World of Warcraft"""));
        Assert.AreEqual((@"D:\GOG Games\Witcher 3\unins000.exe", ""), Uninstaller.SplitCommand(@"""D:\GOG Games\Witcher 3\unins000.exe"""));
        Assert.AreEqual((@"C:\Program Files\Game\Uninstall.EXE", "/S"), Uninstaller.SplitCommand(@" C:\Program Files\Game\Uninstall.EXE /S"));
        Assert.AreEqual(("MsiExec.exe", "/X{1234}"), Uninstaller.SplitCommand("MsiExec.exe /X{1234}"));
        Assert.AreEqual(("uplay://uninstall/635", ""), Uninstaller.SplitCommand("uplay://uninstall/635"));
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

    [TestMethod]
    public void Halo_takes_the_vivid_icon_color_and_fades_to_transparent_edges()
    {
        // Rotes Feld mit breitem grauem Rahmen auf durchsichtigem Grund: Rot soll sich gegen das Grau durchsetzen.
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var red = x is >= 8 and < 24 && y is >= 8 and < 24;
                var gray = !red && x is >= 4 and < 28 && y is >= 4 and < 28;
                byte[] bgra = red ? [0, 0, 200, 255] : gray ? [128, 128, 128, 255] : [0, 0, 0, 0];
                bgra.CopyTo(pixels, (y * size + x) * 4);
            }
        }

        var icon = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, size * 4);
        var halo = LibraryEntry.Halo(icon);

        var result = new byte[halo.PixelWidth * halo.PixelHeight * 4];
        halo.CopyPixels(result, halo.PixelWidth * 4, 0);
        var center = (halo.PixelHeight / 2 * halo.PixelWidth + halo.PixelWidth / 2) * 4;
        Assert.AreEqual(255, result[center + 3], "Mitte voll deckend.");
        Assert.IsTrue(result[center + 2] > 200 && result[center + 1] < 60 && result[center] < 60, "Mitte in der kräftigen Farbe (Rot).");
        Assert.AreEqual(0, result[3], "Ecke durchsichtig.");
        Assert.AreEqual(0, result[(halo.PixelWidth / 2) * 4 + 3], "Rand oben durchsichtig.");
    }
}
