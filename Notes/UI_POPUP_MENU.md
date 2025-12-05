# UI Update: Add Games Popup Menu

## Änderungen

### 1. Layout Redesign
- **Entfernt:** StackPanel mit 3 Buttons in der Mitte (Row 1)
- **Hinzugefügt:** Bottom Bar (Row 2) mit "? Add Games" Button unten links
- **Effekt:** Cleaner, minimalistischer Interface

### 2. Neue Popup-Navigation
Wenn der Nutzer auf "? Add Games" klickt, erscheint ein Popup mit **3 Optionen**:

```
???????????????????????????????
?      Add Games      ?
???????????????????????????????
? Choose how you want to add  ?
? games to your library:      ?
?    ?
? [Scan PC for Games]         ?
? [Add Executable]        ?
? [Cancel]    ?
???????????????????????????????
```

### 3. Button-Funktionen

| Button | Aktion |
|--------|--------|
| **Scan PC for Games** | Startet vollständigen 3-Phase Game Scan (Phase 1, 2, 3) |
| **Add Executable** | Öffnet File Picker um einzelne .exe auszuwählen |
| **Cancel** | Schließt das Popup |

## Code-Struktur

### XAML-Änderungen

**Vorher:**
```xaml
<!-- Row 1: 3 Buttons in StackPanel -->
<StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Center">
    <Button Content="Add Game with .exe" Click="AddGame_Click" />
    <Button Content="Scan PC for Games" Click="ScanGames_Click" />
    <Button Content="Scan Folder" Click="ScanFolder_Click" />
</StackPanel>

<!-- Row 2: ScrollViewer mit Games -->
<ScrollViewer Grid.Row="2" Margin="20">
```

**Nachher:**
```xaml
<!-- Row 1: ScrollViewer mit Games -->
<ScrollViewer Grid.Row="1" Margin="20">

<!-- Row 2: Bottom Bar mit "Add Games" Button -->
<Grid Grid.Row="2" Height="60" Padding="20" Background="{ThemeResource LayerFillColorDefaultBrush}">
    <Button x:Name="AddGamesButton" Content="? Add Games" Click="AddGames_Click" />
</Grid>
```

### C# Code-Struktur

**Neue Methoden:**

1. **`AddGames_Click` (Event Handler)**
   - Zeigt das Popup-Dialog
   - Handelt die Benutzer-Auswahl

2. **`AddGameAsync` (Private Task)**
   - Lauert durch `AddGame_Click` aufgerufen
   - Öffnet File Picker
   - Ruft `GameNameService.FindGameIdsAsync()` auf

3. **`ScanGamesAsync` (Private Task)**
   - Lauert durch `ScanGames_Click` aufgerufen
   - Oder durch Popup aufgerufen (Parameter: `button = null`)
   - Führt 3-Phase Scan durch

**Vorher vs. Nachher:**

```csharp
// Vorher: Event Handlers waren async void
private async void AddGame_Click(object sender, RoutedEventArgs e)
{
    // ... logik ...
}

// Nachher: Event Handlers sind void, Task Methods sind async Task
private async void AddGame_Click(object sender, RoutedEventArgs e)
{
    await AddGameAsync(); // Delegiert an Task Method
}

private async Task AddGameAsync()
{
    // ... logik ...
}
```

**Warum diese Struktur?**
- Event Handlers müssen `void` sein (WinUI Standard)
- Aber wir brauchen `Task` um sie zu `await`en im Popup
- Lösung: Event Handler ruft `Task Method` auf

## Popup-Implementierung

```csharp
private async void AddGames_Click(object sender, RoutedEventArgs e)
{
    // Erstelle Popup-Dialog
    var menuDialog = new ContentDialog
    {
        Title = "Add Games",
        Content = new StackPanel
        {
      Spacing = 12,
      Children =
            {
            new TextBlock
 {
 Text = "Choose how you want to add games to your library:",
        TextWrapping = TextWrapping.Wrap,
       Margin = new Thickness(0, 0, 0, 8)
                }
            }
        },
        PrimaryButtonText = "Scan PC for Games",     // Linker Button
        SecondaryButtonText = "Add Executable",      // Rechter Button
        CloseButtonText = "Cancel",          // Mittlerer Button
        XamlRoot = this.Content.XamlRoot
  };

    // Zeige Popup und warte auf Resultat
    var result = await menuDialog.ShowAsync();

  // Handele Resultat
    if (result == ContentDialogResult.Primary)
    {
        await ScanGamesAsync();  // Scan PC
    }
    else if (result == ContentDialogResult.Secondary)
  {
        await AddGameAsync();       // Add Executable
    }
    // CloseButtonText (Cancel) braucht keine Logik
}
```

## UX-Verbesserungen

### Vorher
- 3 Buttons immer sichtbar
- Nimmt Platz oben weg
- Nicht klar, wo man "Games hinzufügen" anfängt

### Nachher
- Clean Interface, nur Spiele-Liste sichtbar
- "? Add Games" Button unten links (Windows Standard)
- Klar strukturiertes Popup-Menü
- Bessere Platznutzung
- Professioneller Look

## Workflow für Nutzer

### Szenario 1: PC Scan
```
1. Klick auf "? Add Games" Button
2. Popup erscheint
3. Klick auf "Scan PC for Games"
4. 3-Phase Scan startet
5. Spiele werden hinzugefügt
6. Completion Dialog zeigt Statistik
```

### Szenario 2: Einzelne .exe
```
1. Klick auf "? Add Games" Button
2. Popup erscheint
3. Klick auf "Add Executable"
4. File Picker öffnet
5. .exe auswählen
6. Game ID Lookup Results erscheinen
```

### Szenario 3: Abbrechen
```
1. Klick auf "? Add Games" Button
2. Popup erscheint
3. Klick auf "Cancel"
4. Popup schließt, nichts passiert
```

## Zukünftige Features

Für später können wir noch hinzufügen:
- **"Scan Folder"** Button im Popup (war vorher vorhanden)
- **Drag & Drop** Support (Folder direkt in Liste ziehen)
- **Recent Games** Quick-add Button
- **Settings** im Popup (z.B. Scan-Optionen)

## Build Status

? **Erfolgreich**

Alle Changes kompilieren ohne Fehler!
