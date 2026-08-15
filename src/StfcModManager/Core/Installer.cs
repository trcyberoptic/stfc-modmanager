using System.Diagnostics;
using System.Security.Cryptography;

namespace StfcModManager.Core;

/// <summary>
/// Fuehrt Dateiplaene transaktional aus: erst sichern, dann kopieren. Schlaegt ein
/// Schritt fehl, werden die Sicherungen zurueckgespielt und nichts bleibt halb installiert.
/// </summary>
public static class Installer
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string? FileVersionOf(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>Wirft, wenn ein Pfad das gegebene Wurzelverzeichnis verliesse. Zweite
    /// Verteidigungslinie hinter PackageMapper — der Pfad kann zwischen Pruefung und Anwendung
    /// nicht mehr wandern. Wird sowohl fuer den Spielordner als auch fuer das Sicherungsverzeichnis
    /// benutzt: ein relatives Ziel, das PackageMapper schon als "bleibt im Spielordner" bestaetigt
    /// hat, kann denselben relativen Anteil nicht anders aufloesen, nur weil die Basis wechselt.</summary>
    private static string ResolveUnder(string root, string relativeTarget, string errorContext)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativeTarget));
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{errorContext}: {relativeTarget}");
        return full;
    }

    private static string ResolveInside(string gameRoot, string relativeTarget)
        => ResolveUnder(gameRoot, relativeTarget, "target escapes the game folder");

    /// <summary>Legt das Zielverzeichnis an und merkt sich, welche Ebenen dabei neu entstanden
    /// sind (tiefste zuerst). Der Rollback in Apply() will genau diese Ebenen wieder entfernen,
    /// wenn sie am Ende leer bleiben -- eine bereits vorhandene Ebene (z. B. "BepInEx\plugins")
    /// darf dabei niemals angefasst werden.</summary>
    private static List<string> CreateDirectoryTracked(string dir)
    {
        var created = new List<string>();
        var probe = dir;
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
        {
            created.Add(probe);
            probe = Path.GetDirectoryName(probe);
        }
        Directory.CreateDirectory(dir);
        return created;
    }

    public static IReadOnlyList<InstalledFile> Apply(
        string gameRoot, IReadOnlyList<(string Source, string Target)> ops)
    {
        var opId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupDir = Path.Combine(AppPaths.BackupDir, opId);
        var restored = new List<(string Backup, string Original)>();
        var written = new List<string>();
        var createdDirs = new List<string>();
        var result = new List<InstalledFile>();

        try
        {
            Directory.CreateDirectory(backupDir);

            foreach (var (source, target) in ops)
            {
                var full = ResolveInside(gameRoot, target);
                createdDirs.AddRange(CreateDirectoryTracked(Path.GetDirectoryName(full)!));

                if (File.Exists(full))
                {
                    // Die Sicherung spiegelt die relative Struktur des Ziels, statt sie mit '_'
                    // zu einem flachen Dateinamen zu verschmelzen: "a\b_c.dll" und "a_b\c.dll"
                    // wuerden mit target.Replace('\\','_') beide zu "a_b_c.dll" und sich beim
                    // Sichern gegenseitig ueberschreiben -- eine der beiden Originaldateien waere
                    // dann beim Rollback unwiederbringlich durch die falsche ersetzt. Ein
                    // gespiegelter Pfad ist dagegen so eindeutig wie das Ziel selbst.
                    var backup = ResolveUnder(backupDir, target, "backup path escapes the backup folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(full, backup, overwrite: true);
                    restored.Add((backup, full));
                }
                else
                {
                    written.Add(full);
                }

                File.Copy(source, full, overwrite: true);
                result.Add(new InstalledFile { Path = target, Sha256 = Sha256File(full) });
            }

            AppLog.Info($"applied {ops.Count} file(s), backup {opId}");
            return result;
        }
        catch (Exception e)
        {
            AppLog.Error($"install failed, rolling back {opId}", e);
            var rollbackFailures = new List<string>();

            foreach (var (backup, original) in restored)
            {
                try { File.Copy(backup, original, overwrite: true); }
                catch (Exception re) when (re is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add($"could not restore '{original}' from backup: {re.Message}");
                }
            }
            foreach (var f in written)
            {
                try { File.Delete(f); }
                catch (Exception re) when (re is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add($"could not delete newly written '{f}': {re.Message}");
                }
            }

            // Rein kosmetisch: neu angelegte, jetzt wieder leere Verzeichnisse aufraeumen.
            // Bewusst NICHT Teil von rollbackFailures -- ein liegen gebliebener leerer Ordner
            // ist kein Datenverlust, anders als die beiden Faelle oben. Tiefste zuerst, sonst
            // scheitert das Loeschen der Elternebene, weil das (noch nicht entfernte) Kind sie
            // nicht leer erscheinen laesst.
            foreach (var dir in createdDirs.Distinct()
                         .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception de) when (de is IOException or UnauthorizedAccessException) { }
            }

            if (rollbackFailures.Count > 0)
            {
                // Ein Rollback, der selbst scheitert, darf nie klanglos im normalen Fehlerpfad
                // untergehen: der Spielordner kann jetzt Dateien enthalten, die weder das
                // Original noch das neue Release sind. Eigener Ausnahmetyp, damit der Aufrufer
                // (bzw. die UI) diesen Fall von einem sauber zurueckgerollten Fehlschlag
                // unterscheiden und dem Nutzer die betroffenen Pfade zeigen kann.
                var summary = string.Join("; ", rollbackFailures);
                AppLog.Error($"rollback for {opId} incomplete, game folder may be left modified: {summary}");
                throw new InstallRollbackException(
                    $"Installation failed and could not be fully rolled back. The game folder may be " +
                    $"left in a modified state. Backup for this attempt is at '{backupDir}'. Details: {summary}",
                    e, rollbackFailures);
            }

            throw;
        }
    }

    public static void RegisterShared(AppState state, string relPath, string sha, string fileVersion, string modId)
    {
        var existing = state.SharedFiles.FirstOrDefault(
            f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            state.SharedFiles.Add(new SharedFile
            {
                Path = relPath, Sha256 = sha, FileVersion = fileVersion, Providers = [modId]
            });
            return;
        }

        if (!existing.Providers.Contains(modId)) existing.Providers.Add(modId);

        // Bei Versionskonflikt gewinnt die hoehere Dateiversion.
        if (CompareVersions(fileVersion, existing.FileVersion) > 0)
        {
            AppLog.Warn($"shared file {relPath}: {modId} provides {fileVersion}, superseding {existing.FileVersion}");
            existing.FileVersion = fileVersion;
            existing.Sha256 = sha;
        }
    }

    /// <summary>Entfernt einen Anbieter. True heisst: kein Mod braucht die Datei mehr, sie darf weg.</summary>
    public static bool ReleaseShared(AppState state, string relPath, string modId)
    {
        var existing = state.SharedFiles.FirstOrDefault(
            f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return true;

        existing.Providers.Remove(modId);
        if (existing.Providers.Count > 0) return false;

        state.SharedFiles.Remove(existing);
        return true;
    }

    internal static int CompareVersions(string a, string b)
        => Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)
            ? va.CompareTo(vb)
            : string.CompareOrdinal(a, b);

    /// <summary>An/Aus per Verschieben — die Konvention, die der Bestand schon benutzt.</summary>
    public static void SetEnabled(GameInstall game, ModEntry mod, bool enabled)
    {
        if (mod.SourceKind == "native")
        {
            var from = enabled ? game.VersionDllDisabled : game.VersionDll;
            var to   = enabled ? game.VersionDll : game.VersionDllDisabled;
            // Eine einzelne Umbenennung ist auf demselben Volume atomar: sie gelingt ganz oder
            // gar nicht, ein halb verschobenes version.dll gibt es nicht. Existiert weder die
            // aktive noch die deaktivierte Datei (z. B. Community Patch nie installiert), ist das
            // kein Fehler -- File.Exists faengt das ab, bevor File.Move ueberhaupt gerufen wird.
            if (File.Exists(from))
            {
                try { File.Move(from, to, overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error($"could not move {from} to {to} while toggling {mod.Id}", e);
                    throw;
                }
            }
            mod.Enabled = enabled;
            AppLog.Info($"{mod.Id} {(enabled ? "enabled" : "disabled")}");
            return;
        }

        Directory.CreateDirectory(game.PluginsDisabled);

        // Mehrdatei-Mods sollen nicht in einem Mischzustand enden, wenn eine mittlere Datei
        // scheitert (z. B. vom laufenden Spiel gesperrt): jede erfolgreiche Verschiebung wird
        // zusammen mit ihrem alten f.Path gemerkt, damit ein spaeterer Fehlschlag alle bereits
        // bewegten Dateien wieder zurueckschieben kann, bevor mod.Enabled ueberhaupt angefasst wird.
        var moved = new List<(InstalledFile File, string OldPath, string From, string To)>();
        try
        {
            foreach (var f in mod.Files.Where(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(f.Path);
                var active   = Path.Combine(game.Plugins, name);
                var inactive = Path.Combine(game.PluginsDisabled, name);
                var from = enabled ? inactive : active;
                var to   = enabled ? active : inactive;
                if (!File.Exists(from)) continue; // schon im Zielzustand oder fehlt -- nichts zu tun

                File.Move(from, to, overwrite: true);
                var oldPath = f.Path;
                f.Path = enabled
                    ? Path.Combine("BepInEx", "plugins", name)
                    : Path.Combine("BepInEx", "plugins-disabled", name);
                moved.Add((f, oldPath, from, to));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"toggling {mod.Id} failed after moving {moved.Count} file(s), rolling back", e);
            var stuck = new List<string>();
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                var (f, oldPath, from, to) = moved[i];
                try
                {
                    File.Move(to, from, overwrite: true);
                    f.Path = oldPath;
                }
                catch (Exception re) when (re is IOException or UnauthorizedAccessException)
                {
                    // f.Path bleibt bewusst auf dem NEUEN Wert stehen: der Move nach 'to' war
                    // erfolgreich, das spiegelt also weiterhin exakt, wo die Datei jetzt wirklich
                    // liegt. mod.Enabled unten wird trotzdem nicht gesetzt -- der Mod haengt fuer
                    // diese Datei zwischen den Zustaenden, und die Ausnahme sagt dem Aufrufer genau das.
                    stuck.Add(to);
                    AppLog.Error($"could not move {to} back to {from} while rolling back {mod.Id}", re);
                }
            }

            if (stuck.Count > 0)
                throw new InstallRollbackException(
                    $"Toggling '{mod.Id}' failed and {stuck.Count} file(s) could not be moved back: " +
                    $"{string.Join(", ", stuck)}. Their recorded path still matches their real location, " +
                    "but the mod is now in a mixed enabled/disabled state.",
                    e, stuck);

            throw;
        }

        mod.Enabled = enabled;
        AppLog.Info($"{mod.Id} {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>Deinstalliert. Configs werden nie geloescht, nur gesichert (Spec §6.6).</summary>
    public static void Remove(AppState state, GameInstall game, ModEntry mod)
    {
        foreach (var f in mod.Files)
        {
            if (!ReleaseShared(state, f.Path, mod.Id)) continue;
            DeleteIfExists(game, f.Path, mod.Id);
        }

        // Ein Mod bringt oft mehr Dateien mit, als sein eigener Files-Eintrag zeigt: die
        // Hauptdatei steht in mod.Files, alles Weitere (mitgelieferte Abhaengigkeiten) landet
        // stattdessen in state.SharedFiles mit diesem Mod als einem von womoeglich mehreren
        // Anbietern. Ohne diese Schleife saehe Remove() solche Dateien nie, sie blieben fuer
        // immer auf der Platte liegen. Snapshot per ToList() vor der Iteration: ReleaseShared
        // entfernt Eintraege aus state.SharedFiles, eine Live-Iteration darueber wuerfe eine
        // InvalidOperationException.
        foreach (var shared in state.SharedFiles.Where(s => s.Providers.Contains(mod.Id)).ToList())
        {
            var (path, sha, ver) = (shared.Path, shared.Sha256, shared.FileVersion);
            if (!ReleaseShared(state, path, mod.Id)) continue;

            try
            {
                DeleteIfExists(game, path, mod.Id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Der letzte Anbieter ist oben schon ausgetragen (der Eintrag existiert in
                // state.SharedFiles jetzt nicht mehr) -- aber die Datei liegt noch auf der
                // Platte, weil das Loeschen fehlgeschlagen ist (z. B. vom laufenden Spiel
                // gesperrt). Anders als bei mod.Files oben ist das hier NICHT von selbst
                // reparierbar: ein erneuter Remove()-Versuch wuerde diesen Eintrag nie
                // wiederfinden, weil er nicht mehr in state.SharedFiles steht. Deshalb hier
                // wiederherstellen. Providers wird bewusst wieder nur mit mod.Id gefuellt:
                // ReleaseShared liefert 'true' ausschliesslich dann, wenn mod.Id tatsaechlich
                // der letzte verbliebene Anbieter war -- alle anderen sind laengst weg.
                RegisterShared(state, path, sha, ver, mod.Id);
                throw;
            }
        }

        BackupConfig(game, mod.Id);
        state.Mods.Remove(mod);
        AppLog.Info($"removed {mod.Id}");
    }

    private static void DeleteIfExists(GameInstall game, string relPath, string modId)
    {
        var full = Path.Combine(game.Root, relPath);
        try { if (File.Exists(full)) File.Delete(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"could not delete {full} while removing {modId}", e);
            throw;
        }
    }

    private static void BackupConfig(GameInstall game, string modId)
    {
        var cfg = Path.Combine(game.Config, modId + ".cfg");
        if (!File.Exists(cfg)) return;

        try
        {
            Directory.CreateDirectory(AppPaths.ConfigBackupDir);
            var dest = Path.Combine(AppPaths.ConfigBackupDir,
                                    $"{modId}-{DateTime.Now:yyyyMMdd-HHmmss}.cfg");
            File.Move(cfg, dest);
            AppLog.Info($"config for {modId} moved to {dest}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Scheitert nur die Config-Sicherung (z. B. Zielordner nicht beschreibbar), bleibt
            // die Config an ihrem Originalort liegen -- kein Datenverlust, denn sie wird nie
            // geloescht, nur verschoben (Spec §6.6). Die eigentliche Deinstallation (die Mod-
            // Dateien sind zu diesem Zeitpunkt schon weg) darf daran trotzdem nicht scheitern,
            // sonst wuerde state.Mods den Mod weiterhin fuehren, obwohl seine Dateien bereits
            // von der Platte verschwunden sind -- state.json und Platte liefen dann auseinander.
            AppLog.Error($"could not back up config for {modId}, leaving it at {cfg}", e);
        }
    }

    /// <summary>Sicherungen aelter als 30 Tage aufraeumen. Beim Start aufgerufen.</summary>
    public static void PruneBackups()
    {
        if (!Directory.Exists(AppPaths.BackupDir)) return;
        var cutoff = DateTime.Now.AddDays(-30);
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.BackupDir))
            if (Directory.GetLastWriteTime(dir) < cutoff)
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Wird geworfen, wenn Apply() oder SetEnabled() sowohl scheitern als auch ihr eigenes
/// Rollback nicht vollstaendig durchfuehren koennen. Der Aufrufer MUSS das dem Nutzer sichtbar
/// machen -- der Spielordner kann jetzt Dateien enthalten, die weder dem alten noch dem neuen
/// Zustand entsprechen.</summary>
public sealed class InstallRollbackException : Exception
{
    /// <summary>Menschenlesbare Beschreibung jedes einzelnen Rollback-Schritts, der nicht
    /// gelungen ist (Pfad + Fehlermeldung). Fuer eine Fehleranzeige in der UI gedacht.</summary>
    public IReadOnlyList<string> RollbackFailures { get; }

    public InstallRollbackException(string message, Exception inner, IReadOnlyList<string> rollbackFailures)
        : base(message, inner)
    {
        RollbackFailures = rollbackFailures;
    }
}
