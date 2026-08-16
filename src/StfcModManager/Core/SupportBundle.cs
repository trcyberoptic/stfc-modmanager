using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace StfcModManager.Core;

/// <summary>Sammelt Logs, Konfigurationen und Umgebungsdaten in ein redigiertes ZIP. Muss auf
/// einer Maschine in beliebigem Zustand funktionieren: fehlender Config-Ordner, gesperrte
/// Logdatei, ein 68-MB-JSON-Cache neben den echten .cfg-Dateien in BepInEx\config (real auf der
/// Zielmaschine gemessen) -- nichts davon darf das Paket unbrauchbar gross machen oder den
/// Aufrufer mit einer rohen Ausnahme stehen lassen.</summary>
public static class SupportBundle
{
    private const long PerFileTailBytes = 5 * 1024 * 1024;
    private const long TotalBudgetBytes = 20 * 1024 * 1024;

    public static IReadOnlyList<string> PlannedContents(GameInstall game)
    {
        var list = new List<string>
        {
            game.LogOutput, game.LogOutput + ".1", game.ErrorLog,
            game.CommunityPatchLog, game.CommunityPatchToml, game.DoorstopConfig,
            Path.Combine(GameLocator.UnityLogDir(), "Player.log"),
            Path.Combine(GameLocator.UnityLogDir(), "Player-prev.log"),
            AppLog.CurrentFile
        };
        if (Directory.Exists(game.Config))
        {
            try
            {
                // NUR .cfg -- Spec §2.2b. Auf der Zielmaschine liegt neben den paar Kilobyte grossen
                // .cfg-Dateien ein 68-MB-JSON-Cache im selben Ordner; wuerde der mitgenommen, waere
                // das Paket allein dadurch zwei Groessenordnungen groesser als noetig, ohne dem
                // Support irgendetwas zu nuetzen (der Cache enthaelt keine vom Nutzer editierten
                // Einstellungen). Die Endungspruefung ist die einzige, aber ausreichende Grenze.
                list.AddRange(Directory.EnumerateFiles(game.Config, "*.cfg"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Der Ordner kann zwischen der Exists-Pruefung und der Aufzaehlung verschwinden oder
                // gesperrt sein (z. B. durch einen Virenscanner) -- der Vorschau-Dialog, der diese
                // Methode aufruft, darf dafuer nicht abstuerzen. Der Rest der Liste bleibt gueltig.
            }
        }
        return list.Where(File.Exists).ToList();
    }

    public static string Create(AppState state, GameInstall game, string destZipPath) =>
        Create(state, game, destZipPath, TotalBudgetBytes, PerFileTailBytes);

    /// <summary>Wie Create, aber mit einstellbaren Groessengrenzen -- der Selbsttest braucht kleine
    /// Grenzen, um Budget-Erschoepfung und Pro-Datei-Kappung zu pruefen, ohne tatsaechlich
    /// zweistellige Megabyte an Testdaten auf die Platte zu schreiben. Dieselbe Ueberlegung wie
    /// BepInExRuntime.SafeExtract(string, string, long, long, int) fuer die Zip-Bomb-Grenzen dort.</summary>
    internal static string Create(
        AppState state, GameInstall game, string destZipPath, long totalBudgetBytes, long perFileTailBytes)
    {
        var skipped = new StringBuilder();
        long budget = totalBudgetBytes;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destZipPath)!);
            using (var zip = ZipFile.Open(destZipPath, ZipArchiveMode.Create))
            {
                foreach (var path in PlannedContents(game))
                {
                    var size = new FileInfo(path).Length;
                    if (budget <= 0)
                    {
                        skipped.AppendLine($"{Path.GetFileName(path)}: skipped, package budget exhausted");
                        continue;
                    }

                    var (text, note) = ReadTailAsText(path, perFileTailBytes);
                    if (note is not null) skipped.AppendLine($"{Path.GetFileName(path)}: {note}");

                    var redacted = Redactor.RedactText(text);
                    var bytes = Encoding.UTF8.GetByteCount(redacted);
                    if (bytes > budget)
                    {
                        skipped.AppendLine($"{Path.GetFileName(path)}: skipped, would exceed the package budget ({size} bytes)");
                        continue;
                    }

                    budget -= bytes;
                    WriteEntry(zip, "collected/" + Path.GetFileName(path), redacted);
                }

                // Selbst erzeugte Dateien werden nicht redigiert -- sie enthalten nur eigene Daten.
                WriteEntry(zip, "inventory.json", BuildInventory(state));
                WriteEntry(zip, "environment.txt", BuildEnvironment(state, game));
                WriteEntry(zip, "health.txt", BuildHealth(state, game));
                if (skipped.Length > 0) WriteEntry(zip, "SKIPPED.txt", skipped.ToString());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Anders als eine einzelne unlesbare Quelldatei (die nur in SKIPPED.txt vermerkt wird
            // und die restliche Sammlung nicht stoppt, s. ReadTailAsText) ist ein Fehlschlag HIER --
            // Zielordner nicht anlegbar, Ziel-ZIP gesperrt, Datentraeger voll -- fatal fuer das ganze
            // Paket. Derselbe Uebersetzen-statt-durchreichen-Grundsatz wie
            // BepInExRuntime.InstallAsync: die rohe, ggf. lokalisierte OS-Meldung bleibt im Log, der
            // Aufrufer bekommt englischen, OS-freien Text.
            AppLog.Error($"support package could not be written to {destZipPath}", e);
            throw new InvalidOperationException(
                "Could not write the support package. Close the destination file if it is open " +
                "elsewhere, or choose a different location.", e);
        }

        AppLog.Info($"support package written to {destZipPath}");
        return destZipPath;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(content);
    }

    /// <summary>Liest hoechstens die letzten perFileTailBytes Bytes einer Datei.</summary>
    private static (string Text, string? Note) ReadTailAsText(string path, long perFileTailBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string? note = null;
            if (stream.Length > perFileTailBytes)
            {
                stream.Seek(-perFileTailBytes, SeekOrigin.End);
                note = $"truncated to the last {perFileTailBytes / 1024 / 1024} MB of {stream.Length} bytes";
            }
            using var reader = new StreamReader(stream);
            return (reader.ReadToEnd(), note);
        }
        // Das Spiel oder ein Virenscanner kann eine der geplanten Dateien exklusiv halten
        // (IOException) oder ihre ACL kann das Lesen verweigern (UnauthorizedAccessException) --
        // beides darf die Sammlung der uebrigen Dateien nicht stoppen. Die Meldung im Paket bleibt
        // bewusst OS-frei (kein e.Message): das Detail geht ins Manager-Log, nicht in eine Datei, die
        // der Nutzer gleich in einen Discord-Kanal einfuegt.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"support package: could not read {path}", e);
            return ("", "could not be read (locked or inaccessible)");
        }
    }

    private static string BuildInventory(AppState state)
        => JsonSerializer.Serialize(
            state.Mods.Select(m => new
            {
                m.Id, m.Name, m.Version, m.Enabled, m.SourceKind, m.Repo, m.ReleaseTag,
                m.InstalledAt, m.InstalledAgainstClientBuild,
                Files = m.Files.Select(f => new { f.Path, f.Sha256 })
            }),
            new JsonSerializerOptions { WriteIndented = true });

    private static string BuildEnvironment(AppState state, GameInstall game)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"manager version : {typeof(SupportBundle).Assembly.GetName().Version}");
        sb.AppendLine($"generated       : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"windows         : {Environment.OSVersion.VersionString} ({Environment.Is64BitOperatingSystem switch { true => "x64", false => "x86" }})");
        sb.AppendLine($"game path       : {game.Root}");
        sb.AppendLine($"client build    : {GameLocator.ReadClientBuild(game.Root)}");
        sb.AppendLine($"bepinex         : {BepInExRuntime.Detect(game) ?? "not installed"}");
        sb.AppendLine($"game running    : {GameLocator.IsGameRunning()}");
        sb.AppendLine($"version.dll     : {(File.Exists(game.VersionDll) ? "active" : File.Exists(game.VersionDllDisabled) ? "disabled" : "absent")}");
        sb.AppendLine($"managed mods    : {state.Mods.Count}");
        return sb.ToString();
    }

    private static string BuildHealth(AppState state, GameInstall game)
    {
        var sb = new StringBuilder();
        foreach (var f in HealthCheck.Run(state, game))
        {
            sb.AppendLine($"[{f.Severity}] {f.Title}");
            if (f.Remedy is not null) sb.AppendLine($"          -> {f.Remedy}");
        }
        return sb.Length == 0 ? "No findings." : sb.ToString();
    }
}
