using System.IO.Compression;

namespace StfcModManager.Core;

public sealed record MappedFile(string Entry, string Target);
public sealed record MapResult(IReadOnlyList<MappedFile> Files, string? Rejection);

/// <summary>
/// Bildet den Inhalt eines Releases auf Zielpfade im Spielordner ab.
/// Der Manager fuehrt nichts aus, deshalb werden ausfuehrbare Dateien nicht
/// etwa uebersprungen, sondern das ganze Paket abgelehnt.
/// </summary>
public static class PackageMapper
{
    private static readonly string[] Blocked =
        { ".exe", ".bat", ".cmd", ".ps1", ".psm1", ".msi", ".scr", ".vbs", ".js", ".jar", ".com", ".lnk" };

    private static readonly string[] IgnoredNested = { ".zip", ".7z", ".rar", ".tar", ".gz" };

    private static readonly string[] GameRootFiles =
        { "version.dll", "version.dll_", "winhttp.dll", "doorstop_config.ini" };

    public static MapResult MapEntries(IReadOnlyList<string> entryNames)
    {
        var entries = new List<string>();
        foreach (var raw in entryNames)
        {
            var e = raw.Replace('\\', '/').Trim();
            if (e.Length == 0 || e.EndsWith('/')) continue;              // Verzeichniseintrag

            // ':' erlaubt sowohl Laufwerksangaben ("C:\...") als auch NTFS-Alternate-Data-Streams
            // ("datei.txt:versteckt.exe"). Beides ist ein Weg aus dem Zielordner heraus bzw. an der
            // Endungspruefung vorbei (der Stream-Name haengt hinter dem echten Dateinamen, den
            // Windows beim Anlegen trotzdem als eigenstaendige — moeglicherweise leere — Datei
            // materialisiert). Deshalb genuegt ein Doppelpunkt an beliebiger Position, nicht nur
            // an Index 1, um das ganze Paket abzulehnen.
            if (e.StartsWith('/') || e.Contains(':'))
                return new MapResult([], $"archive contains an absolute path or alternate data stream: {raw}");

            // Windows entfernt beim tatsaechlichen Anlegen einer Datei/eines Ordners jeden
            // abschliessenden Lauf aus Punkten/Leerzeichen. Dadurch wird ein Segment auch dann zur
            // echten Eltern-Referenz oder verschwindet ganz, wenn es nicht woertlich ".." ist:
            // "..", "...", ".. " und "." landen nach dem Trim alle bei "" bzw. bei "..".
            if (e.Split('/').Any(s => s.Length > 0 && s.TrimEnd('.', ' ').Length == 0))
                return new MapResult([], $"archive contains a path traversal: {raw}");

            // Dieselbe Normalisierung, jetzt fuer die Endungspruefung: "setup.exe." oder
            // "setup.exe " wuerden ohne sie als harmlos durchgehen, obwohl Windows beim
            // tatsaechlichen Anlegen wieder "setup.exe" daraus macht.
            var normalized = e.TrimEnd('.', ' ');
            var ext = Path.GetExtension(normalized).ToLowerInvariant();
            if (Blocked.Contains(ext))
                return new MapResult([], $"archive contains an executable file: {raw}");
            if (IgnoredNested.Contains(ext)) continue;                   // verschachtelte Archive: ignorieren

            entries.Add(normalized);
        }

        if (entries.Count == 0)
            return new MapResult([], "archive contains no installable files");

        var hasBepInEx = entries.Any(e => Segments(e).Any(IsBepInEx));

        var files = new List<MappedFile>();
        foreach (var e in entries)
        {
            var parts = Segments(e);
            var idx = Array.FindIndex(parts, IsBepInEx);
            var target = hasBepInEx && idx >= 0
                ? string.Join('\\', parts[idx..])          // alles vor "BepInEx" abschneiden
                : MapLoose(parts[^1]);
            files.Add(new MappedFile(e, target));
        }

        return new MapResult(files, null);
    }

    private static string[] Segments(string entry) => entry.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsBepInEx(string s) => s.Equals("BepInEx", StringComparison.OrdinalIgnoreCase);

    private static string MapLoose(string fileName)
        => GameRootFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)
           || fileName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : Path.Combine("BepInEx", "plugins", fileName);

    public static MapResult MapArchive(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return MapEntries(zip.Entries.Select(e => e.FullName).ToList());
        }
        catch (InvalidDataException)
        {
            return new MapResult([], "file is not a readable zip archive");
        }
    }

    public static MapResult MapSingleFile(string filePath)
        => MapEntries([Path.GetFileName(filePath)]);
}
