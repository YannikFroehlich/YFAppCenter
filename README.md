# YF AppCenter

[![Release](https://img.shields.io/github/v/release/YannikFroehlich/YFAppCenter?label=Release)](https://github.com/YannikFroehlich/YFAppCenter/releases/latest)
![Plattform](https://img.shields.io/badge/Plattform-Windows%2011%20x64-0078D6)

Ein Launcher für Windows 11, der installierte Spiele aller gängigen Launcher und eigene Programme an einem Ort sammelt. Die Spielerkennung kommt aus [YFTimeTracker](https://github.com/YannikFroehlich/YFTimeTracker), damit beide Programme dieselben Spiele finden.

## Funktionen

- Erkennt installierte Spiele von Steam, Epic Games, GOG, Xbox/Microsoft Store, Battle.net, Ubisoft Connect, EA app und den übrigen Launchern, die YFTimeTracker kennt
- Eigene Programme, Spiele und Verknüpfungen per Drag & Drop oder „+“ hinzufügen
- Startseite mit Banner, „Läuft gerade“, „Zuletzt gestartet“, „Meistgespielt“ und Favoriten
- Titelbilder, Banner und Schriftzüge aus dem Steam-Cache bzw. Steam-Shop, auch für Spiele anderer Launcher; eigene Cover-Bilder möglich
- Eigene Kategorien, Favoriten, Umbenennen, Startziel ändern, Einträge ausblenden
- Zeigt, was gerade läuft, und kann laufende Spiele beenden
- Erkannte Spiele über ihren Launcher oder ihr Deinstallationsprogramm deinstallieren
- Spielzeit aus YFTimeTracker, falls installiert (nur lesend)
- Bedienung per Xbox-Controller und Tastatur (Suche, Enter startet den ersten Treffer)
- Infobereich-Symbol, optional mit Windows starten
- Automatische Updates über die GitHub-Releases

## Installation

`YFAppCenter-Setup.exe` aus dem [neuesten Release](https://github.com/YannikFroehlich/YFAppCenter/releases/latest) herunterladen und ausführen. Das Setup fragt nach dem Installationsordner, legt Verknüpfungen an und installiert bei Bedarf die .NET-Desktop-Laufzeit 10 mit. Deinstalliert wird über „Einstellungen → Apps“.

Bibliothek, Cover und Verknüpfungskopien liegen unter `%LocalAppData%\YFAppCenter`.

## Entwicklung

Voraussetzungen: Windows 11 x64 und das .NET 10 SDK. YFTimeTracker ist als Git-Submodul eingebunden, daher rekursiv klonen:

```powershell
git clone --recursive https://github.com/YannikFroehlich/YFAppCenter.git
dotnet test YFAppCenter.Tests
dotnet run --project YFAppCenter
```

Bei einem bestehenden Klon ohne Submodul: `git submodule update --init`.

## Releases

Jeder Push auf `main` baut, testet und veröffentlicht automatisch eine neue Version ([release.yml](.github/workflows/release.yml), [New-Release.ps1](scripts/New-Release.ps1)); installierte Apps aktualisieren sich daraufhin selbst. Änderungen nur an Tests oder Markdown-Dateien lösen kein Release aus, ebenso `[skip ci]` in der Commit-Nachricht.

Lokal ein Setup bauen, ohne etwas zu veröffentlichen:

```powershell
.\scripts\New-Release.ps1 -NoUpload
```
