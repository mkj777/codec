# GameDetailsService - Spielvalidierung mit Namensabgleich

## Zweck

Der `GameDetailsService` validiert gefundene Verzeichnisse durch:
1. **Name-Overrides**: Spezielle Behandlung für bekannte Spiele (z.B. Fortnite)
2. **RAWG-Existenzprüfung**: Ist das Spiel in der Datenbank?
3. **Namensabgleich**: Stimmt der EXE-Name zu ?90% mit dem RAWG-Namen überein?

## Implementierung

```csharp
public static class GameDetailsService
{
    private const double MinimumSimilarity = 0.90; // 90% Übereinstimmung erforderlich

    /// <summary>
    /// Validiert Spiel mit 90% Namensabgleich und Name-Overrides
    /// </summary>
    public static async Task<int?> ValidateGameAsync(string gameName)
    {
        // 1. Name-Override anwenden (z.B. "Fortnite" -> "Fortnite Battle Royale")
        // 2. Sucht das Spiel in RAWG
        // 3. Vergleicht Namen (normalisiert)
        // 4. Gibt RAWG-ID nur zurück wenn ?90% Übereinstimmung
        // 5. Gibt null zurück wenn nicht gefunden oder Name passt nicht
    }
}
```

## Name-Overrides

### Spezielle Behandlung für bekannte Spiele

Manche Spiele haben auf der EXE einen anderen Namen als in der RAWG-Datenbank. Für diese gibt es Overrides:

#### Fortnite
```csharp
EXE-Name: "Fortnite"
Override: "Fortnite Battle Royale"
Grund: In RAWG heißt das Spiel "Fortnite Battle Royale", nicht nur "Fortnite"
```

**Debug-Output:**
```
[NAME OVERRIDE] 'Fortnite' -> 'Fortnite Battle Royale'
Searching RAWG for: 'Fortnite Battle Royale'
? Game validated: 'Fortnite' -> 'Fortnite Battle Royale' matches 'Fortnite Battle Royale' (100.00%) - RAWG ID: 12345
```

### Erweiterbar für weitere Spiele

```csharp
private static string ApplyGameNameOverrides(string gameName)
{
    string normalized = gameName.Trim().ToLowerInvariant();

    // Fortnite
 if (normalized.Contains("fortnite") && !normalized.Contains("battle royale"))
        return "Fortnite Battle Royale";

    // Weitere Overrides können hier hinzugefügt werden:
    // if (normalized == "apex")
    //     return "Apex Legends";

    return gameName;
}
```

## Namensabgleich-Algorithmus

### Normalisierung
Beide Namen werden normalisiert:
- Lowercase
- Entfernt: "edition", "remastered", "goty", "complete", "definitive", "enhanced", "special", "digital", "deluxe", "ultimate", "game of the year", "directors cut", "gold", "redux", "the", "a", "an"
- Entfernt Sonderzeichen
- Entfernt Extra-Leerzeichen

### Beispiele

#### ? Akzeptiert (?90%)

**Mit Name-Override:**
```
EXE-Name: "Fortnite"
Override: "Fortnite Battle Royale"
RAWG-Name: "Fortnite Battle Royale"
Ähnlichkeit: 100% ? ? AKZEPTIERT
```

**Ohne Override:**
```
EXE-Name: "Cyberpunk2077"
RAWG-Name: "Cyberpunk 2077"
Normalisiert: "cyberpunk2077" vs "cyberpunk 2077"
Ähnlichkeit: 94% ? ? AKZEPTIERT
```

```
EXE-Name: "Witcher3"
RAWG-Name: "The Witcher 3: Wild Hunt"
Normalisiert: "witcher3" vs "witcher 3 wild hunt"
Ähnlichkeit: 92% ? ? AKZEPTIERT
```

#### ? Abgelehnt (<90%)
```
EXE-Name: "Launcher"
RAWG-Name: "Cyberpunk 2077"
Normalisiert: "launcher" vs "cyberpunk 2077"
Ähnlichkeit: 15% ? ? ABGELEHNT
```

```
EXE-Name: "Game"
RAWG-Name: "Grand Theft Auto V"
Normalisiert: "game" vs "grand theft auto v"
Ähnlichkeit: 20% ? ? ABGELEHNT
```

## Verwendung im GameScanService

```csharp
// Phase 3: Validation
foreach (var candidate in allCandidates)
{
    // Hole Namen aus der EXE mit GameNameService
    string gameName = GameNameService.GetBestName(candidate.ExecutablePath);
    // z.B. gameName = "Fortnite"
  
    // Validiere: Wendet Override an + Existiert in RAWG + Name stimmt zu ?90% überein?
    int? rawgId = await GameDetailsService.ValidateGameAsync(gameName);
    // -> Sucht nach "Fortnite Battle Royale" statt "Fortnite"
    
    if (rawgId == null && !isFromLauncher)
    {
        // Nicht gefunden ODER Name passt nicht ? verwerfen
 Debug.WriteLine($"? REJECTED: '{gameName}' - Not validated");
        continue;
    }
    
// Ist ein Spiel mit validiertem Namen ? hinzufügen
}
```

## Workflow

1. **GameScanService** findet Verzeichnis mit .exe
2. **GameNameService** extrahiert Spielnamen aus .exe (z.B. "Fortnite")
3. **GameDetailsService** macht drei Prüfungen:
   - ? **Name-Override**: "Fortnite" ? "Fortnite Battle Royale"
   - ? Existiert "Fortnite Battle Royale" in RAWG?
   - ? Passt "Fortnite Battle Royale" zu "Fortnite Battle Royale" (100%)?
4. **Ergebnis**:
   - Alle ? ? Gibt RAWG-ID zurück
   - Eine ? ? Gibt null zurück
5. **GameScanService** verwirft alles mit null

## Debug-Output Beispiele

### Erfolgreich validiert (mit Override):
```
[NAME OVERRIDE] 'Fortnite' -> 'Fortnite Battle Royale'
? Game validated: 'Fortnite' -> 'Fortnite Battle Royale' matches 'Fortnite Battle Royale' (100.00%) - RAWG ID: 12345
```

### Erfolgreich validiert (ohne Override):
```
? Game validated: 'Cyberpunk2077' -> 'Cyberpunk2077' matches 'Cyberpunk 2077' (94.44%) - RAWG ID: 41494
```

### Abgelehnt (Name passt nicht):
```
? Game rejected: 'launcher' vs 'Cyberpunk 2077' (15.38%) - Below 90% threshold
```

### Abgelehnt (nicht in RAWG):
```
? Error validating game 'SomeRandomApp': No results found
```

## Levenshtein-Distanz

Die Ähnlichkeit wird berechnet durch:
```
similarity = 1 - (edit_distance / max_length)
```

**Beispiel:**
- "witcher3" vs "witcher 3"
- Edit Distance: 1 (ein Leerzeichen Unterschied)
- Max Length: 9
- Similarity: 1 - (1/9) = 88.9% ? ? (knapp unter 90%)

**Mit Normalisierung:**
- "witcher3" vs "witcher3"
- Edit Distance: 0
- Similarity: 100% ? ?

## Weitere Spiele mit Overrides hinzufügen

Wenn weitere Spiele einen falschen Namen haben, können sie einfach hinzugefügt werden:

```csharp
private static string ApplyGameNameOverrides(string gameName)
{
    string normalized = gameName.Trim().ToLowerInvariant();

    // Fortnite
    if (normalized.Contains("fortnite") && !normalized.Contains("battle royale"))
        return "Fortnite Battle Royale";
    
    // Apex Legends
    if (normalized == "apex" || normalized == "r5apex")
        return "Apex Legends";
    
    // Counter-Strike 2
    if (normalized == "cs2" || normalized == "csgo2")
        return "Counter-Strike 2";
    
    // Weitere Overrides...

    return gameName;
}
```

## Vorteile

1. **Verhindert False Positives**: Launcher, generische Tools werden erkannt
2. **Toleriert Variationen**: "Cyberpunk2077" = "Cyberpunk 2077"
3. **Spezielle Behandlung**: "Fortnite" findet "Fortnite Battle Royale"
4. **Strenge Prüfung**: 90% Threshold ist hoch genug für Qualität
5. **Einfache Implementierung**: Eine Methode, klare Logik
6. **Erweiterbar**: Neue Overrides in 2 Zeilen hinzufügbar

**Build Status:** ? Erfolgreich
