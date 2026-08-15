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
            return state;
        }
        catch (JsonException) { return new AppState(); }
    }

    public static AppState Load()
    {
        try
        {
            return File.Exists(AppPaths.StateFile)
                ? DeserializeFrom(File.ReadAllText(AppPaths.StateFile))
                : new AppState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new AppState(); }
    }

    /// <summary>Atomar: erst in eine Nebendatei, dann verschieben. Ein Absturz mittendrin
    /// laesst die alte Datei intakt statt eine halbe zu hinterlassen.
    /// Absichtlich ohne Catch: schlaegt das Schreiben fehl (Datentraeger voll, Datei durch
    /// einen Editor gesperrt, schreibgeschuetztes Profil), muss der Aufrufer das erfahren --
    /// sonst glaubt der Manager, ein Mod sei registriert, obwohl der Zustand nie persistiert wurde.</summary>
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.StateFile + ".tmp";
        File.WriteAllText(tmp, SerializeTo(this));
        File.Move(tmp, AppPaths.StateFile, overwrite: true);
    }
}
