using System.Diagnostics;
using System.Text.RegularExpressions;

namespace StfcModManager.Core;

public static partial class GameLocator
{
    // Der Launcher schreibt den Schluessel mit Publisher-Praefix ("152033..GAME_PATH=").
    // Ein Muster ohne diesen optionalen Praefix greift auf echten Installationen daneben.
    [GeneratedRegex(@"^\s*(?:\d+\.\.)?GAME_PATH\s*=\s*(?<p>.+?)\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GamePathLine();

    public static string? ParseGamePathFromIni(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = GamePathLine().Match(line);
            if (!m.Success) continue;
            // Das gierige \s* vor der traegen Erfassungsgruppe kann bei reinem Leerraum-Wert
            // zurueckbacktracken und der Gruppe ein einzelnes Leerzeichen ueberlassen -> hier trimmen.
            var raw = m.Groups["p"].Value.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = raw.Replace('/', '\\').TrimEnd('\\');
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    private static string LauncherIniPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Star Trek Fleet Command", "launcher_settings.ini");

    /// <summary>INI zuerst, dann der uebliche Standardpfad. Null, wenn nichts Gueltiges gefunden wurde.</summary>
    public static string? Detect()
    {
        try
        {
            if (File.Exists(LauncherIniPath))
            {
                var fromIni = ParseGamePathFromIni(File.ReadAllLines(LauncherIniPath));
                if (fromIni is not null && IsValid(fromIni)) return fromIni;
            }
        }
        catch (IOException) { /* INI unlesbar: Fallback benutzen */ }
        catch (UnauthorizedAccessException) { /* INI unlesbar: Fallback benutzen */ }

        const string fallback = @"C:\Games\Star Trek Fleet Command\STFC\default\game";
        return IsValid(fallback) ? fallback : null;
    }

    public static bool IsValid(string root)
        => !string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, "prime.exe"));

    /// <summary>Client-Build aus ".version" (Inhalt z.B. "&amp;game=254"). "unknown", wenn nicht lesbar.</summary>
    public static string ReadClientBuild(string root)
    {
        try
        {
            var raw = File.ReadAllText(Path.Combine(root, ".version")).Trim();
            var eq = raw.LastIndexOf('=');
            return eq >= 0 && eq < raw.Length - 1 ? raw[(eq + 1)..].Trim() : raw;
        }
        catch (IOException) { return "unknown"; }
        catch (UnauthorizedAccessException) { return "unknown"; }
    }

    public static bool IsGameRunning()
        => Process.GetProcessesByName("prime").Length > 0;

    public static string UnityLogDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Digit Game Studios Ltd", "Star Trek Fleet Command");

    /// <summary>Prueft echte Schreibbarkeit, nicht nur ACLs.</summary>
    public static bool IsWritable(string root)
    {
        try
        {
            var probe = Path.Combine(root, ".stfcmm-write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
