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
    // Die Schluesselwoerter selbst, EINMAL definiert und in Left/Right/SecretAssignment
    // wiederverwendet (s. dort) -- zwei unabhaengig gepflegte Listen wuerden ueber Zeit
    // auseinanderlaufen. "pw" und "passwd" (Fix-Runde 1, I3) sowie "pwd" (Fix-Runde 2, billig
    // mitgezogen -- dieselbe Fehlerklasse, ein einziges zusaetzliches Wort) sind gaengige Kurzformen
    // von "password", die sonst unredigiert durchgingen.
    private const string Keywords = "key|token|secret|password|passwd|pwd|pw|passwort|api|auth|authorization";

    // Bezeichner-Grenze fuer zusammengesetzte Namen (PascalCase "ApiKey", snake_case "deepl_api_key").
    // Ein blosses \b reicht NICHT: \w schliesst den Unterstrich ein, also gibt es innerhalb von
    // "deepl_api_key" gar keine \b-Grenze um "api" oder "key" -- und \b ignoriert Gross-/
    // Kleinschreibung, also auch keine Grenze zwischen "Api" und "Key" in "ApiKey". Mit dem
    // urspruenglich vorgeschlagenen \b(?:key|token|...)\b haetten NICHT EINMAL die beiden
    // wortwoertlichen Beispiele aus der Aufgabenstellung ("ApiKey = ...", "deepl_api_key: ...")
    // gegriffen -- am echten .NET-Regex-Modul nachgemessen, bevor diese Fassung geschrieben wurde
    // (s. Taskbericht).
    //
    // Drei Klauseln, nicht zwei (Fix-Runde 1, I3): (1) davor/danach kein Buchstabe/Ziffer (deckt
    // Leerzeichen, Satzzeichen UND den Unterstrich ab), (2) ein klein-zu-Gross-Uebergang (deckt
    // PascalCase/camelCase wie "ApiKey" ab), (3) unmittelbar angrenzend an ein ANDERES Schluesselwort
    // aus derselben Liste (deckt zusammengesetzte Namen OHNE jedes Gross-/Kleinschreibungssignal ab,
    // z. B. "APIKEY" oder "apikey" -- dort gibt es zwischen "API"/"api" und "KEY"/"key" gar keinen
    // Uebergang, den Klausel 2 erkennen koennte). "(?-i:...)" schaltet die aeussere IgnoreCase-Option
    // innerhalb der Gross-/Kleinschreibungspruefung gezielt wieder aus -- sonst wuerde [A-Z] auch
    // Kleinbuchstaben treffen und die Erkennung waere wirkungslos. Klausel 3 bleibt dagegen bewusst
    // IgnoreCase-empfindlich (kein "(?-i:...)" dort), damit sie "API" genauso wie "api" erkennt.
    //
    // Links und rechts sind NICHT dieselbe Pruefung, obwohl \b es waere: links muss das Zeichen
    // DAVOR kein Buchstabe/Ziffer sein (Lookbehind), rechts muss das Zeichen DANACH keiner sein
    // (Lookahead). Eine erste Fassung benutzte irrtuemlich denselben Lookbehind auf beiden Seiten --
    // damit griff die rechte Grenze nie (ein Lookbehind prueft immer nach LINKS, unabhaengig davon,
    // wo er im Muster steht), und selbst die woertlichen Beispiele der Aufgabenstellung
    // ("Password=...", "Authorization: ...") schlugen fehl. Am echten .NET-Regex-Modul nachgemessen
    // (s. Taskbericht), bevor diese Fassung geschrieben wurde -- ebenso wie Klausel 3 unten.
    private const string Left = @"(?:(?<![A-Za-z0-9])|(?<=(?-i:[a-z]))(?=(?-i:[A-Z]))|(?<=" + Keywords + "))";
    private const string Right = @"(?:(?![A-Za-z0-9])|(?<=(?-i:[a-z]))(?=(?-i:[A-Z]))|(?=" + Keywords + "))";

    // Fix-Runde 1, C1 (kritisch): die fruehere Fassung liess nach dem Schluesselwort ein
    // UNBESCHRAENKTES "[^=:\r\n]*" bis zum naechsten Trenner laufen -- auf einer normalen
    // Ausnahme- oder Stacktrace-Zeile kann DAS ein voelliger anderer, viel spaeterer ':' oder '='
    // sein (ein Laufwerksbuchstabe in einem Pfad, ein zweiter Doppelpunkt in einer
    // Exception.ToString()-Zeile), und ALLES dazwischen -- die eigentliche Fehlermeldung, der
    // Dateipfad -- verschwand hinter "[REDACTED]". "System.Collections.Generic.
    // KeyNotFoundException: The given key was not present..." und Stacktraces wie
    // "...AuthTokenManager.cs:line 42" wurden so bis zur Unbrauchbarkeit verstuemmelt, obwohl in
    // ihnen gar keine Zuweisung steht -- genau die Information, fuer die ein Supportpaket ueberhaupt
    // existiert. Deshalb duerfen zwischen dem Schluesselwort und dem eigentlichen Trenner nur noch
    // Leerzeichen/Tabs stehen (hoechstens acht, als zusaetzliche Vorsichtsgrenze).
    //
    // Fix-Runde 2: ein einzelnes optionales Anfuehrungszeichen VOR den Leerzeichen/Tabs -- die genau
    // dieselbe Ursache hatte die C1-Korrektur ihrerseits zu eng gemacht: eine im UniversalTranslator
    // selbst dokumentierte Realitaet, JSON-Anfrage-/Antwortkoerper landen bei aktiviertem Debug-
    // Logging woertlich im LogOutput.log/Player.log, und dort steht zwischen dem Schluesselnamen und
    // dem Doppelpunkt ein schliessendes '"' -- "apiKey": "wert", {"apiKey":"wert"} und
    // "apiKey" : "wert" wurden ohne diese Ergaenzung KOMPLETT unredigiert durchgereicht (ein 16
    // Zeichen langer Wert liegt unter der 24-Zeichen-Schwelle von LongId, das Auffangnetz greift
    // hier also nicht). Das Anfuehrungszeichen ist bewusst nur EIN optionales Zeichen, kein
    // Freibrief fuer beliebige Satzzeichen -- es oeffnet keine neue Luecke fuer Fliesstext, weil ein
    // Buchstabe (wie in "KeyNotFoundException") weiterhin nicht dazu passt und die Pruefung an genau
    // derselben Stelle scheitert wie zuvor. Am echten .NET-Regex-Modul mit dem vollstaendigen
    // Ueberlebens-Korpus aus Fix-Runde 1 UND allen drei JSON-Schreibweisen gegengeprueft, bevor diese
    // Fassung geschrieben wurde (s. Taskbericht).
    [GeneratedRegex(@"^(?<head>[^=:\r\n]*" + Left + "(?:" + Keywords + ")" + Right
                   + "[\"']?" + @"[ \t]{0,8})(?<sep>\s*[=:]\s*)(?<val>\S.*)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // Deckt ein "Bearer <Token>" ab, das NICHT als Schluessel:Wert-Zeile auftritt -- z. B.
    // "sending Bearer abc123 to the api" oder ein alleinstehendes "Bearer x" mitten im Fliesstext
    // (dem Spec-eigenen Testfall). "Authorization: Bearer ..." wird bereits ueber SecretAssignment
    // oben abgedeckt (das Schluesselwort "authorization" traegt die ganze Zeile), diese Regel ist
    // die zweite, unabhaengige Verteidigungslinie fuer den Fall, dass "Bearer" gar nicht hinter
    // einem der bekannten Schluesselwoerter steht. \S+ statt eines Laengen-/Zeichenklassen-Filters:
    // ein Bearer-Token kann kurz sein ("Bearer x", s. o.), die 24-Zeichen-Schwelle von LongId greift
    // dafuer nicht, und ein zu enger Zeichensatz wuerde ein Token mit Punkten (JWT) abschneiden.
    [GeneratedRegex(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    // Mindestens 24 alphanumerische Zeichen mit wenigstens einer Ziffer:
    // trifft Spieler-uids, Sitzungstoken und Pruefsummen.
    //
    // Fix-Runde 1, I4: \b hat hier dasselbe Problem wie bei SecretAssignment -- der Unterstrich ist
    // ein \w-Zeichen, also gibt es KEINE \b-Grenze zwischen "_" und dem eigentlichen Zufallsteil
    // eines Tokens wie "sk_live_" + 24 Zufallszeichen oder "ghp_" + 36 Zufallszeichen --
    // beides reale API-Schluessel-Formate (Stripe, GitHub), beide mit genau EINEM trennenden
    // Unterstrich vor einem laengeren, in sich zusammenhaengenden Zufallsteil. Ersetzt durch dieselbe
    // Grenze wie bei Left/Right oben (Klausel 1: kein Buchstabe/Ziffer davor/danach), aber OHNE die
    // Gross-/Kleinschreibungs- oder Nachbarwort-Klauseln -- die braucht LongId nicht, es geht nur um
    // eine zusammenhaengende Zeichenfolge, nicht um zusammengesetzte Bezeichner. "ordinary-hyphenated-
    // word-list" bleibt trotzdem unberuehrt: jedes einzelne Segment darin ist zu kurz UND enthaelt
    // keine Ziffer, die Schwelle von 24 zusammenhaengenden Zeichen mit Ziffer greift nur bei einem
    // tatsaechlich opaken Block (am echten .NET-Regex-Modul gegengeprueft, s. Taskbericht).
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?=[A-Za-z0-9]*[0-9])[A-Za-z0-9]{24,}(?![A-Za-z0-9])",
                    RegexOptions.CultureInvariant)]
    private static partial Regex LongId();

    // Haertung ueber die Aufgabenstellung hinaus: eine klassische GUID (8-4-4-4-12 Hex-Gruppen mit
    // Bindestrichen) ist unter Unity/BepInEx die uebliche Form fuer Spieler- und Sitzungskennungen.
    // LongId allein sieht sie NICHT -- die Bindestriche zerteilen jede zusammenhaengende
    // alphanumerische Folge in Stuecke von hoechstens 12 Zeichen, weit unter der 24-Zeichen-Schwelle.
    // Ohne diese Ergaenzung bliebe eine echte Spieler-GUID im Log unredigiert.
    //
    // Dieselbe Unterstrich-Grenzen-Korrektur wie bei LongId (Fix-Runde 1, I4 -- hier nicht explizit
    // verlangt, aber derselbe Fehlerklasse im selben Codepfad: "session_id_550e8400-...-000_active"
    // saehe mit \b die GUID sonst ebenfalls nicht, aus demselben Grund.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![A-Za-z0-9])",
                    RegexOptions.CultureInvariant)]
    private static partial Regex GuidLike();

    public static string RedactLine(string line)
    {
        var m = SecretAssignment().Match(line);
        if (m.Success)
            return m.Groups["head"].Value + m.Groups["sep"].Value + "[REDACTED]";

        line = BearerToken().Replace(line, "Bearer [REDACTED]");
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
