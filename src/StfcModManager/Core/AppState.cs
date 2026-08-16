using System.Text.Json;
using System.Text.Json.Serialization;

namespace StfcModManager.Core;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StfcModManager");

    public static string StateFile       => Path.Combine(Root, "state.json");
    public static string LogDir          => Path.Combine(Root, "logs");
    public static string BackupDir       => Path.Combine(Root, "backup");
    public static string ConfigBackupDir => Path.Combine(Root, "config-backup");
    public static string DownloadDir     => Path.Combine(Root, "download");

    /// <summary>Ablageordner fuer lokale Mods, neben der EXE.</summary>
    public static string LocalMods => Path.Combine(AppContext.BaseDirectory, "LocalMods");
}

public sealed class InstalledFile
{
    /// <summary>Kanonischer, aktivierter Ort relativ zum Spielordner (z. B.
    /// "BepInEx\plugins\X.dll"). Wird von Installer.SetEnabled NIE umgeschrieben, wenn eine
    /// Datei zwischen plugins und plugins-disabled wandert -- Installer.PhysicalPath leitet den
    /// tatsaechlichen Ort aus diesem stabilen Schluessel plus ModEntry.Enabled ab. Ohne diese
    /// Stabilitaet laufen Buchfuehrung (state.SharedFiles, per Pfad referenziert) und Wirklichkeit
    /// auseinander, sobald ein Mod deaktiviert wird (Pre-Flight-Review Fix Round 1, C1).</summary>
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class SharedFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string FileVersion { get; set; } = "";
    public List<string> Providers { get; set; } = [];
}

public sealed class ModEntry
{
    /// <summary>BepInPlugin-GUID, sonst der Dateiname.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>"github" | "local" | "adopted" | "native"</summary>
    public string SourceKind { get; set; } = "local";
    public string? Repo { get; set; }
    public string? ReleaseTag { get; set; }
    public string? AssetName { get; set; }
    public string? ETag { get; set; }
    public bool AutoUpdate { get; set; }

    public List<InstalledFile> Files { get; set; } = [];
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public string InstalledAgainstClientBuild { get; set; } = "unknown";

    /// <summary>Nur zur Laufzeit gefuellt, nicht persistiert.</summary>
    [JsonIgnore] public string? AvailableVersion { get; set; }
}

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public string? GamePath { get; set; }
    public string? LastKnownClientBuild { get; set; }
    public List<ModEntry> Mods { get; set; } = [];
    public List<SharedFile> SharedFiles { get; set; } = [];
    public List<string> TrustedRepos { get; set; } = [];
    public string? GitHubToken { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SerializeTo(AppState state) => JsonSerializer.Serialize(state, Options);

    /// <summary>Ein kaputter oder leerer Zustand darf den Start nie verhindern.</summary>
    public static AppState DeserializeFrom(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppState();

        // Windows-Editoren (u. a. Notepad) speichern Textdateien beim manuellen Bearbeiten
        // gerne mit fuehrendem UTF-8-BOM. Der Utf8JsonReader haelt ein BOM fuer keinen
        // gueltigen Werteanfang und wirft sonst -- ohne den Trim hier wuerde eine so
        // gespeicherte Datei den kompletten Mod-Bestand des Nutzers stillschweigend loeschen.
        const char Utf8Bom = (char)0xFEFF;
        if (json.Length > 0 && json[0] == Utf8Bom) json = json[1..];

        try
        {
            var state = JsonSerializer.Deserialize<AppState>(json, Options) ?? new AppState();

            // System.Text.Json respektiert die "Nullable"-Annotationen der Properties beim
            // Deserialisieren standardmaessig nicht: ein explizites "Mods": null in einer
            // handbearbeiteten Datei ueberschreibt den Feldinitialisierer und wuerde sonst
            // eine NullReferenceException erst bei der naechsten Verwendung ausloesen.
            state.Mods ??= [];
            state.SharedFiles ??= [];
            state.TrustedRepos ??= [];

            // Dieselbe Luecke gilt fuer einzelne Listenelemente: "Mods": [null] liefert eine
            // Liste der Laenge 1 mit einem null-Eintrag -- jede spaetere Iteration, die ein
            // Feld des Elements liest (z. B. mod.Id), stuerzt dann mit NullReferenceException ab.
            state.Mods.RemoveAll(m => m is null);
            state.SharedFiles.RemoveAll(f => f is null);
            state.TrustedRepos.RemoveAll(r => r is null);
            return state;
        }
        catch (JsonException) { return new AppState(); }
    }

    public static AppState Load()
    {
        try
        {
            // Ein harter Absturz oder Stromausfall zwischen File.WriteAllText und File.Move in
            // Save() (also VOR dem Delete-im-Catch, s. dort) kann eine eindeutig benannte
            // Nebendatei hinterlassen, die niemand mehr referenziert -- die F1-Namenseindeutigkeit
            // bedeutet, dass kein spaeterer Save()-Aufruf sie je wiederverwendet. Deshalb hier
            // gefegt, unabhaengig davon, ob ueberhaupt eine state.json existiert.
            SweepLeftoverTempFiles();

            if (!File.Exists(AppPaths.StateFile)) return new AppState();

            var text = File.ReadAllText(AppPaths.StateFile);
            var state = DeserializeFrom(text);

            // DeserializeFrom liefert fuer "Datei war leer" und "Datei war kaputt" bewusst
            // denselben leeren AppState zurueck (die reine Funktion darf den Unterschied nicht
            // kennen muessen). Load() braucht den Unterschied aber: eine kaputte, nicht-leere
            // Datei darf nicht klanglos verschwinden und dann vom naechsten Save() ueberschrieben
            // werden -- der Nutzer wuerde seinen kompletten Mod-Bestand ohne jede Spur verlieren.
            if (!string.IsNullOrWhiteSpace(text) && !ParsesAsValidJson(text))
            {
                AppLog.Error($"Zustandsdatei ist beschaedigt und wird beiseite gelegt: {AppPaths.StateFile}");
                SetAsideCorruptStateFile();
            }

            return state;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new AppState(); }
    }

    /// <summary>Nur zur Fehlererkennung in Load(): true, wenn JsonSerializer den Text ohne
    /// Ausnahme verarbeitet (unabhaengig vom Ergebnis-Shape). DeserializeFrom selbst bleibt
    /// unveraendert und faengt JsonException weiterhin fuer sich allein ab.</summary>
    private static bool ParsesAsValidJson(string json)
    {
        try { JsonSerializer.Deserialize<AppState>(json, Options); return true; }
        catch (JsonException) { return false; }
    }

    /// <summary>Verschiebt eine als kaputt erkannte state.json aus dem Weg, damit der naechste
    /// Save() sie nicht ueberschreibt -- von Hand ist sie danach noch inspizierbar. Best-effort:
    /// schlaegt das Verschieben selbst fehl, darf das den Start trotzdem nicht verhindern.</summary>
    private static void SetAsideCorruptStateFile()
    {
        try
        {
            var target = $"{AppPaths.StateFile}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(AppPaths.StateFile, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Raeumt Nebendateien auf, die ein fruehere Save()-Aufruf hinterlassen hat, ohne sie
    /// selbst wieder loeschen zu koennen (harter Absturz, Stromausfall). Die eindeutigen Namen aus
    /// dem F1-Fix bedeuten: KEIN spaeterer Save() referenziert eine bestehende Nebendatei je wieder,
    /// sie waeren sonst fuer immer liegen geblieben. Best-effort und komplett selbst-gefangen --
    /// ein Fehler beim Fegen darf weder den Rest von Load() noch den Programmstart verhindern.</summary>
    private static void SweepLeftoverTempFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Root, "state.json.*.tmp"))
            {
                try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Atomar: erst in eine Nebendatei, dann verschieben. Ein Absturz mittendrin
    /// laesst die alte Datei intakt statt eine halbe zu hinterlassen.
    /// Absichtlich ohne Catch, die den Fehlschlag verschluckt: schlaegt das Schreiben fehl
    /// (Datentraeger voll, Datei durch einen Editor gesperrt, schreibgeschuetztes Profil), muss
    /// der Aufrufer das erfahren -- sonst glaubt der Manager, ein Mod sei registriert, obwohl der
    /// Zustand nie persistiert wurde. Der einzige Catch hier raeumt nur die eigene Nebendatei auf
    /// und wirft danach unveraendert weiter (siehe Kommentar dort).</summary>
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);

        // Der Dateiname muss pro Prozess UND pro Aufruf eindeutig sein: nichts in der Anwendung
        // verhindert eine zweite laufende Instanz, und ein fester ".tmp"-Name liesse Prozess B
        // die noch ausstehende Nebendatei von Prozess A ueberschreiben. Prozess A wuerde danach
        // klanglos den Inhalt von B verschieben (kein Fehler, kein Log-Eintrag) -- oder umgekehrt
        // mit FileNotFoundException scheitern, weil der geteilte Name schon wegverschoben wurde.
        var tmp = $"{AppPaths.StateFile}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, SerializeTo(this));
            File.Move(tmp, AppPaths.StateFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Die eindeutigen Namen aus dem F1-Fix haben eine Kehrseite: kein spaeterer Save()
            // ueberschreibt/referenziert diese Nebendatei je wieder, ein fehlgeschlagener Schreib-
            // oder Umbenennungsvorgang wuerde also fuer immer eine Leiche in %LOCALAPPDATA%
            // hinterlassen. Das Aufraeumen hier ist best-effort (eigener gefilterter Catch) und
            // darf die urspruengliche Exception nie verschlucken -- deshalb das abschliessende
            // "throw;" statt eines neuen Wurfs oder eines stillen Rueckgabewerts.
            try { File.Delete(tmp); } catch (Exception ce) when (ce is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
