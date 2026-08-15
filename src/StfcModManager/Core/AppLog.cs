namespace StfcModManager.Core;

/// <summary>Taggeweise rollierendes Log. Faellt still aus, wenn nicht geschrieben werden kann —
/// ein defektes Log darf die Anwendung nie stoppen.</summary>
public static class AppLog
{
    private static readonly Lock Gate = new();

    public static string CurrentFile =>
        Path.Combine(AppPaths.LogDir, $"manager-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string m) => Write("INFO ", m);
    public static void Warn(string m) => Write("WARN ", m);
    public static void Error(string m) => Write("ERROR", m);

    public static void Error(string m, Exception e) => Write("ERROR", $"{m}: {e}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(CurrentFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
                PruneOldLogs();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void PruneOldLogs()
    {
        var cutoff = DateTime.Now.AddDays(-30);
        foreach (var f in Directory.EnumerateFiles(AppPaths.LogDir, "manager-*.log"))
            if (File.GetLastWriteTime(f) < cutoff)
                try { File.Delete(f); } catch (IOException) { }
    }
}
