using System.Text.RegularExpressions;

namespace StfcModManager.Core;

public sealed record LogEntry(string Level, string Source, string Message);

public static partial class LogReader
{
    [GeneratedRegex(@"^\[(?<level>Info|Debug|Message|Warning|Error|Fatal)\s*:\s*(?<src>[^\]]+)\]\s*(?<msg>.*)$",
                    RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LineFormat();

    public static LogEntry? ParseLine(string line)
    {
        var m = LineFormat().Match(line);
        return m.Success
            ? new LogEntry(m.Groups["level"].Value, m.Groups["src"].Value.Trim(), m.Groups["msg"].Value.Trim())
            : null;
    }

    /// <summary>Liest die letzten maxLines Zeilen und gibt nur Warnungen und Fehler zurueck.</summary>
    public static IReadOnlyList<LogEntry> ReadTail(string path, int maxLines = 5000)
    {
        if (!File.Exists(path)) return [];

        var tail = new Queue<string>(maxLines);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == maxLines) tail.Dequeue();
                tail.Enqueue(line);
            }
        }
        // Das Spiel kann die Datei exklusiv halten (kein FileShare.ReadWrite auf seiner Seite) oder
        // sie kann waehrend des Lesens verschwinden -- ein Health-Check darf dafuer nie abstuerzen,
        // sondern liest einfach nichts.
        catch (IOException) { return []; }

        return tail.Select(ParseLine)
                   .Where(e => e is not null && e.Level is "Warning" or "Error" or "Fatal")
                   .Select(e => e!)
                   .ToList();
    }
}
