using System.Text.RegularExpressions;

namespace StfcModManager.Core;

/// <summary>
/// Entfernt Geheimnisse aus Text, bevor er ins Supportpaket wandert. Noetig, weil
/// zum Beispiel der UniversalTranslator seinen DeepL-Schluessel in der .cfg haelt
/// und Spiel-Logs Spieler-IDs enthalten koennen.
/// Bewusst grosszuegig: lieber eine Pruefsumme zu viel maskiert als eine ID zu wenig.
/// </summary>
public static partial class Redactor
{
    // Bezeichner-Grenze fuer zusammengesetzte Namen (PascalCase "ApiKey", snake_case "deepl_api_key").
    // Ein blosses \b reicht NICHT: \w schliesst den Unterstrich ein, also gibt es innerhalb von
    // "deepl_api_key" gar keine \b-Grenze um "api" oder "key" -- und \b ignoriert Gross-/
    // Kleinschreibung, also auch keine Grenze zwischen "Api" und "Key" in "ApiKey". Mit dem
    // urspruenglich vorgeschlagenen \b(?:key|token|...)\b haetten NICHT EINMAL die beiden
    // wortwoertlichen Beispiele aus der Aufgabenstellung ("ApiKey = ...", "deepl_api_key: ...")
    // gegriffen -- am echten .NET-Regex-Modul nachgemessen, bevor diese Fassung geschrieben wurde
    // (s. Taskbericht). Eine Grenze gilt hier, wenn davor/danach kein Buchstabe/Ziffer steht (deckt
    // Leerzeichen, Satzzeichen UND den Unterstrich ab) ODER an einem klein-zu-Gross-Uebergang (deckt
    // PascalCase/camelCase ab). "(?-i:...)" schaltet die aeussere IgnoreCase-Option innerhalb der
    // Gross-/Kleinschreibungspruefung gezielt wieder aus -- sonst wuerde [A-Z] auch Kleinbuchstaben
    // treffen und die Erkennung waere wirkungslos.
    //
    // Links und rechts sind NICHT dieselbe Pruefung, obwohl \b es waere: links muss das Zeichen
    // DAVOR kein Buchstabe/Ziffer sein (Lookbehind), rechts muss das Zeichen DANACH keiner sein
    // (Lookahead). Eine erste Fassung benutzte irrtuemlich denselben Lookbehind auf beiden Seiten --
    // damit griff die rechte Grenze nie (ein Lookbehind prueft immer nach LINKS, unabhaengig davon,
    // wo er im Muster steht), und selbst die woertlichen Beispiele der Aufgabenstellung
    // ("Password=...", "Authorization: ...") schlugen fehl, weil "password"/"authorization" am
    // Zeilenanfang zwar links, aber nie rechts als Grenze erkannt wurden. Am echten .NET-Regex-Modul
    // nachgemessen (s. Taskbericht), bevor diese Fassung geschrieben wurde.
    private const string Left = @"(?:(?<![A-Za-z0-9])|(?<=(?-i:[a-z]))(?=(?-i:[A-Z])))";
    private const string Right = @"(?:(?![A-Za-z0-9])|(?<=(?-i:[a-z]))(?=(?-i:[A-Z])))";

    [GeneratedRegex(@"^(?<head>[^=:\r\n]*" + Left
                   + @"(?:key|token|secret|password|passwort|api|auth|authorization)" + Right
                   + @"[^=:\r\n]*)(?<sep>\s*[=:]\s*)(?<val>\S.*)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // Mindestens 24 alphanumerische Zeichen mit wenigstens einer Ziffer:
    // trifft Spieler-uids, Sitzungstoken und Pruefsummen.
    [GeneratedRegex(@"\b(?=[A-Za-z0-9]*[0-9])[A-Za-z0-9]{24,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongId();

    // Haertung ueber die Aufgabenstellung hinaus: eine klassische GUID (8-4-4-4-12 Hex-Gruppen mit
    // Bindestrichen) ist unter Unity/BepInEx die uebliche Form fuer Spieler- und Sitzungskennungen.
    // LongId allein sieht sie NICHT -- die Bindestriche zerteilen jede zusammenhaengende
    // alphanumerische Folge in Stuecke von hoechstens 12 Zeichen, weit unter der 24-Zeichen-Schwelle.
    // Ohne diese Ergaenzung bliebe eine echte Spieler-GUID im Log unredigiert.
    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b",
                    RegexOptions.CultureInvariant)]
    private static partial Regex GuidLike();

    public static string RedactLine(string line)
    {
        var m = SecretAssignment().Match(line);
        if (m.Success)
            return m.Groups["head"].Value + m.Groups["sep"].Value + "[REDACTED]";

        line = Email().Replace(line, "[REDACTED-EMAIL]");
        line = GuidLike().Replace(line, "[REDACTED-ID]");
        line = LongId().Replace(line, "[REDACTED-ID]");
        return line;
    }

    public static string RedactText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) sb.AppendLine(RedactLine(line));
        return sb.ToString();
    }
}
