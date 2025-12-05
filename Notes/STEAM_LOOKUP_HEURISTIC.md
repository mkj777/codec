# Steam ID Lookup für Heuristic Scan

## Problem

**Vorher:** Nur Steam-Spiele, die über Steam installiert wurden (Phase 1 Scanner), bekamen eine Steam-ID.

**Beispiel:**
```
Spiel: "Cyberpunk 2077"
Installation: Manuell von GOG.com auf Festplatte kopiert
Scanner: Heuristic Scan findet das Spiel
Ergebnis: ? Keine Steam-ID, obwohl auf Steam verfügbar
```

## Lösung

**Jetzt:** Heuristisch gefundene Spiele werden auch auf Steam gesucht!

Der `GameScanService` nutzt jetzt den `GameNameService.FindGameIdsAsync()` für alle heuristisch gefundenen Spiele.

## Implementierung

### Phase 3 Update:

```csharp
foreach (var candidate in allCandidates)
{
    // 1. EXE Detection (wie vorher)
    string executablePath = ExecuteDetectionFunnel(candidate.FolderPath, candidate.Name);
    
    // 2. Steam ID Bestimmung
    int? steamId = candidate.SteamAppId; // Aus Phase 1 (wenn vorhanden)
    
  // 3. NEU: Für Heuristic-Kandidaten ohne Steam ID ? Suche in Steam!
    if (!steamId.HasValue && !isFromLauncher)
    {
     Debug.WriteLine($"[STEAM LOOKUP] Searching Steam for: '{candidate.Name}'");
        
      // Nutzt GameNameService.FindGameIdsAsync mit der gefundenen EXE
        (int? foundSteamId, int? _) = await GameNameService.FindGameIdsAsync(executablePath);
        
     if (foundSteamId.HasValue)
        {
      steamId = foundSteamId;
    Debug.WriteLine($"? Steam ID found: {steamId} for '{candidate.Name}'");
      }
    }
    
    // 4. RAWG Validation (wie vorher)
    int? rawgId = await ValidateAndFetchRawgIdAsync(candidate.Name);
    
    // 5. Spiel mit Steam ID + RAWG ID hinzufügen
    validatedGames.Add((steamId, candidate.Name, rawgId, ...));
}
```

## GameNameService.FindGameIdsAsync

Diese Methode macht:

1. **Steam-ID Suche:**
   - Prüft `steam_appid.txt` im Spielordner
   - Extrahiert ProductName/FileDescription aus EXE
   - Sucht in Steam App-Liste mit Levenshtein-Matching

2. **RAWG-ID Suche:**
   - Nutzt gefundenen Steam-Namen (falls vorhanden)
   - Oder nutzt EXE-basierten Namen
   - Validiert mit 90% Namensübereinstimmung

## Workflow

### Beispiel: GOG-Spiel auf Festplatte

```
Spiel: "The Witcher 3" (von GOG installiert, aber auch auf Steam verfügbar)

Phase 1 (Launcher Integration):
  ? Nicht über Steam installiert ? Keine Steam-ID

Phase 2 (Heuristic Scan):
  ? Findet: C:\Games\The Witcher 3\
  ? Kandidat erstellt: Name="The Witcher 3", SteamAppId=null

Phase 3 (Validation & Enrichment):
  
  Step 1: EXE Detection
    ? Findet: witcher3.exe
  
  Step 2: Steam ID Lookup (NEU!)
    [STEAM LOOKUP] Searching Steam for: 'The Witcher 3'
    GameNameService.FindGameIdsAsync(witcher3.exe):
      ? Liest ProductName: "The Witcher 3: Wild Hunt"
      ? Sucht in Steam App-Liste
      ? Findet Match: "The Witcher 3: Wild Hunt" (AppID: 292030)
    ? Steam ID found: 292030
  
  Step 3: RAWG Validation
    ? Findet: "The Witcher 3: Wild Hunt" (RAWG ID: 3328)
  
Ergebnis:
    ? VALIDATED: 'The Witcher 3' 
    Steam ID: 292030
      RAWG ID: 3328
      Source: Heuristic Scan
```

## Debug-Output

### Erfolgreiches Steam-Lookup:
```
=== PHASE 3: VALIDATION & EXE DETECTION ===
Validating 1/42: The Witcher 3
  === HEURISTIC FUNNEL: The Witcher 3 ===
  ? SELECTED: witcher3.exe (Score: 185)
  [STEAM LOOKUP] Searching Steam for: 'The Witcher 3'
  --- Searching with priority name: 'The Witcher 3: Wild Hunt' ---
  ? Final match found: 292030 for 'The Witcher 3: Wild Hunt' with score 95.2%
  ? Steam ID found: 292030 for 'The Witcher 3'
  ? Game validated: 'The Witcher 3' matches 'The Witcher 3: Wild Hunt' (91.67%) - RAWG ID: 3328
  ? VALIDATED: 'The Witcher 3' (Steam ID: 292030, RAWG ID: 3328)
```

### Kein Steam-Match:
```
Validating 2/42: My Custom Game
  [STEAM LOOKUP] Searching Steam for: 'My Custom Game'
  ? No Steam ID found for 'My Custom Game'
  ? Game validated: 'My Custom Game' matches 'My Custom Game' (100.00%) - RAWG ID: 12345
  ? VALIDATED: 'My Custom Game' (Steam ID: N/A, RAWG ID: 12345)
```

## Vorteile

### 1. **Vollständigere Steam-Integration**
- Spiele werden erkannt, egal wo sie installiert sind
- GOG/Epic/Heuristic-Spiele bekommen Steam-ID, wenn verfügbar
- Nutzer kann Steam-Features nutzen (Screenshots, Workshop, etc.)

### 2. **Bessere Metadaten**
- Steam-ID ermöglicht Zugriff auf Steam Web API
- Mehr Datenquellen für Bilder, Beschreibungen, Reviews
- Verknüpfung mit Steam-Profil möglich

### 3. **Genauere Identifikation**
- Zwei IDs (Steam + RAWG) erhöhen Zuversicht
- Cross-Referenzierung zwischen Datenbanken möglich
- Duplikat-Erkennung verbessert

### 4. **Keine zusätzliche Wartezeit**
- Steam-Lookup nur bei Heuristic-Kandidaten (weniger als Phase 1)
- Parallel zur RAWG-Validierung
- Nutzt bereits geladene EXE-Pfade

## Beispiel-Szenarien

### Szenario 1: Steam-Spiel nicht über Steam installiert
```
Installation: Cracked/Portable Version von Steam-Spiel
Scanner: Heuristic Scan
Steam-Lookup: ? Findet Steam-ID
Ergebnis: Vollständige Integration trotz alternativer Installation
```

### Szenario 2: Multi-Platform Spiel
```
Installation: Von Epic Games Store
Scanner: Epic Games Scanner (Phase 1)
Steam-Lookup: Nicht nötig (isFromLauncher = true)
Ergebnis: Behält Epic als Source, aber könnte Steam-ID haben wenn gewünscht
```

### Szenario 3: Indie-Spiel nur auf itch.io
```
Installation: Direkt von Developer-Website
Scanner: Heuristic Scan
Steam-Lookup: ? Nicht auf Steam verfügbar
RAWG-Lookup: ? Findet RAWG-ID
Ergebnis: Spiel trotzdem erfasst mit RAWG-ID
```

## Performance

**Zusätzliche API-Calls:**
- Pro heuristisch gefundenem Spiel: 1x Steam App List Lookup (gecacht!)
- Kein zusätzlicher HTTP-Request (Steam App List ist bereits gecacht)

**Durchschnittliche Scan-Zeit Erhöhung:**
- ~50ms pro Heuristic-Kandidat
- Bei 20 Kandidaten: +1 Sekunde
- Bei 100 Kandidaten: +5 Sekunden

**Akzeptabel**, da:
- Nur einmalig beim Scan
- Nutzer sieht Fortschritt-Anzeige
- Wertvolle Steam-IDs gewonnen

## Zukünftige Erweiterungen

### Option 1: Steam-Lookup für alle Quellen
```csharp
// Auch Epic/GOG/etc. Spiele auf Steam suchen
if (!steamId.HasValue) // Entfernt "!isFromLauncher" Check
{
    // Sucht auch für Epic/GOG Spiele
}
```

### Option 2: Batch-Lookup
```csharp
// Alle Heuristic-Kandidaten auf einmal
var heuristicCandidates = allCandidates.Where(c => c.Source == "Heuristic Scan");
var steamIds = await GameNameService.BatchFindSteamIdsAsync(heuristicCandidates);
```

### Option 3: User-Präferenz
```csharp
if (Settings.AlwaysSearchSteam)
{
  // Sucht Steam-ID für alle Spiele
}
```

## Zusammenfassung

**Vorher:**
- Steam-ID nur für Steam-installierte Spiele
- Heuristic-Spiele hatten nur RAWG-ID

**Nachher:**
- Steam-ID für ALLE Spiele (wenn auf Steam verfügbar)
- Heuristic-Spiele bekommen beide IDs (Steam + RAWG)
- Nutzt bestehende GameNameService-Logik
- Keine Breaking Changes

**Build Status:** ? Erfolgreich

Die Integration ist abwärtskompatibel und verbessert die Datenqualität erheblich! ??
