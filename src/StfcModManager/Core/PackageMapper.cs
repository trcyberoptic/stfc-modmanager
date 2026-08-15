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

    // Auf Windows in Datei-/Ordnernamen verbotene Zeichen (ohne ':', das schon separat behandelt
    // wird, und ohne '/', das hier intern als Trennzeichen dient). Steuerzeichen (< 0x20, u.a. NUL)
    // sind zusaetzlich verboten -- beides wuerde sonst erst spaeter bei Installer.ResolveInside bzw.
    // beim tatsaechlichen Anlegen der Datei mit einer ungefangenen ArgumentException/IOException enden.
    private static readonly char[] IllegalWindowsChars = { '<', '>', '"', '|', '?', '*' };

    public static MapResult MapEntries(IReadOnlyList<string> entryNames)
    {
        var entries = new List<(string Raw, string Normalized)>();
        foreach (var raw in entryNames)
        {
            var e = raw.Replace('\\', '/').Trim();
            if (e.Length == 0 || e.EndsWith('/')) continue;              // Verzeichniseintrag

            // "." steht fuer "aktuelles Verzeichnis" und traegt keine Bedeutung fuer den Zielpfad
            // (Windows' eigenes tar.exe erzeugt es routinemaessig, s.u.). Segmente werden hier
            // vollstaendig entfernt statt sie nur von der Traversal-Pruefung weiter unten
            // auszunehmen: sonst ueberlebt ein Eintrag, der nur aus "." besteht, bis zur
            // Zielpfad-Berechnung -- Segments("") liefert dort ein leeres Array und
            // MapLoose(parts[^1]) stuerzt mit IndexOutOfRangeException ab -- und ein eingebettetes
            // "." erzeugt einen Zielpfad-String, der sich vom aequivalenten Pfad ohne "." unterscheidet
            // und so die Kollisionspruefung umgeht ("BepInEx/plugins/A.dll" vs.
            // "BepInEx/./plugins/A.dll" waeren sonst zwei verschiedene Ziele, obwohl
            // Path.GetFullPath beide auf dieselbe Datei aufloest). Split ohne RemoveEmptyEntries,
            // damit ein fuehrender leerer Abschnitt (absoluter Pfad, UNC) fuer die Pruefung unten
            // erhalten bleibt.
            e = string.Join('/', e.Split('/').Where(s => s != "."));
            if (e.Length == 0) continue;                                 // Eintrag bestand nur aus "."

            // ':' erlaubt sowohl Laufwerksangaben ("C:\...") als auch NTFS-Alternate-Data-Streams
            // ("datei.txt:versteckt.exe"). Beides ist ein Weg aus dem Zielordner heraus bzw. an der
            // Endungspruefung vorbei (der Stream-Name haengt hinter dem echten Dateinamen, den
            // Windows beim Anlegen trotzdem als eigenstaendige — moeglicherweise leere — Datei
            // materialisiert). Deshalb genuegt ein Doppelpunkt an beliebiger Position, nicht nur
            // an Index 1, um das ganze Paket abzulehnen.
            if (e.StartsWith('/') || e.Contains(':'))
                return new MapResult([], $"archive contains an absolute path or alternate data stream: {raw}");

            // ".." bleibt verboten (fuehrt nachweislich ins Elternverzeichnis). Reine
            // Punkte-/Leerzeichen-Segmente wie "...", "...." oder ".. " sind zusaetzlich verboten --
            // NICHT weil Windows sie beim Anlegen zu einer Eltern-Referenz zusammenzieht (das tut es
            // nachweislich nicht: "BepInEx\...\x" wirft beim Anlegen eine DirectoryNotFoundException
            // statt hochzulaufen), sondern als reine Vorsichtsmassnahme gegen Formen, die kein
            // legitimes Release traegt. Der einzelne Punkt "." ist davon ausgenommen: Windows' eigenes
            // tar.exe erzeugt ihn routinemaessig ("tar -a -cf a.zip ." liefert Eintraege wie "./sub/a.dll").
            if (e.Split('/').Any(s => s.Length > 0 && s != "." && s.TrimEnd('.', ' ').Length == 0))
                return new MapResult([], $"archive contains a path traversal: {raw}");

            // Dieselbe Trailing-Trim-Normalisierung wie oben, jetzt fuer Endungs- und
            // Zeichenpruefung: "setup.exe." bzw. "setup.exe " wuerden ohne sie als harmlos
            // durchgehen, obwohl Windows beim tatsaechlichen Anlegen wieder "setup.exe" daraus macht.
            var normalized = e.TrimEnd('.', ' ');

            if (normalized.Any(c => c < 0x20 || IllegalWindowsChars.Contains(c)))
                return new MapResult([], $"archive contains a file name with a character illegal on Windows: {raw}");

            var ext = Path.GetExtension(normalized).ToLowerInvariant();
            if (Blocked.Contains(ext))
                return new MapResult([], $"archive contains an executable file: {raw}");
            if (IgnoredNested.Contains(ext)) continue;                   // verschachtelte Archive: ignorieren

            // Der Roheintrag bleibt erhalten (nicht die normalisierte Form): ein spaeterer Aufrufer
            // sucht damit per zip.GetEntry(entry) im Original-Archiv nach der Datei, und ein zip-eigener
            // Backslash-Eintrag ("MyMod\BepInEx\...") wuerde nach der Normalisierung dort nicht mehr
            // gefunden -- stiller Teil-Install trotz zugestimmtem Vertrauensdialog.
            entries.Add((raw, normalized));
        }

        if (entries.Count == 0)
            return new MapResult([], "archive contains no installable files");

        var hasBepInEx = entries.Any(x => Segments(x.Normalized).Any(IsBepInEx));

        var files = new List<MappedFile>();
        var targetOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, e) in entries)
        {
            var parts = Segments(e);
            var idx = Array.FindIndex(parts, IsBepInEx);
            var target = hasBepInEx && idx >= 0
                ? string.Join('\\', parts[idx..])          // alles vor "BepInEx" abschneiden
                : MapLoose(parts[^1]);

            // Zwei Eintraege auf denselben Zielpfad wuerden sich beim Kopieren gegenseitig
            // ueberschreiben und die sharedFiles-Referenzzaehlung verfaelschen. Lieber das ganze
            // Paket ablehnen als eine der beiden im Vertrauensdialog gezeigten Dateien stillschweigend
            // fallenzulassen.
            if (targetOwners.TryGetValue(target, out var firstRaw))
                return new MapResult([],
                    $"archive maps multiple entries to the same target '{target}': '{firstRaw}' and '{raw}'");
            targetOwners[target] = raw;

            files.Add(new MappedFile(raw, target));
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
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException
                                    or ArgumentException)
        {
            // IOException deckt u.a. FileNotFoundException, DirectoryNotFoundException und den ganz
            // normalen Fall ab, dass die Datei direkt nach dem Download noch vom Virenscanner gesperrt
            // ist -- das darf die WinForms-Message-Loop nicht als ungefangene Exception erreichen.
            // Absichtlich ohne e.Message: das ist vom Betriebssystem lokalisiert (auf einem deutschen
            // Windows z.B. ein deutscher Satz) und wuerde sonst in eine rein englische
            // Ablehnungsmeldung durchsickern. Die genaue Ursache gehoert ins Manager-Log, nicht in
            // den Nutzertext.
            return new MapResult([], "could not read the archive: it is missing, locked by another process, inaccessible, or not a valid zip file");
        }
    }

    public static MapResult MapSingleFile(string filePath)
        => MapEntries([Path.GetFileName(filePath)]);
}
