namespace StfcModManager;

using StfcModManager.Core;

/// <summary>
/// Assert-basierter Selbsttest. Bewusst kein Test-Framework: geprüft werden nur
/// die reinen Funktionen, an denen ein Fehler weh tut. Aufruf: StfcModManager.exe --selftest
/// </summary>
internal static class SelfTest
{
    private static int _failed;
    private static int _passed;

    internal static void Check(bool ok, string what)
    {
        if (ok) { _passed++; return; }
        _failed++;
        Console.Error.WriteLine("FAIL: " + what);
    }

    internal static void Eq(object? actual, object? expected, string what)
        => Check(Equals(actual, expected), $"{what} — expected '{expected}', got '{actual}'");

    internal static int Run()
    {
        Check(true, "harness works");

        // --- GameLocator: INI-Parser (Spec §2.2a) ---
        Eq(GameLocator.ParseGamePathFromIni(new[] { @"152033..GAME_PATH=C:/Games/STFC/default/game/" }),
           @"C:\Games\STFC\default\game", "ini: pid-prefixed key");
        Eq(GameLocator.ParseGamePathFromIni(new[] { @"GAME_PATH=C:/Games/STFC/default/game" }),
           @"C:\Games\STFC\default\game", "ini: bare key");
        Eq(GameLocator.ParseGamePathFromIni(new[] { @"152033..GAME_TEMP_PATH=C:/Games/STFC/default/update/" }),
           null, "ini: GAME_TEMP_PATH must not match");
        Eq(GameLocator.ParseGamePathFromIni(new[] { "[General]", "HIDE_EMAIL=true" }),
           null, "ini: no key at all");
        Eq(GameLocator.ParseGamePathFromIni(new[] { @"152033..GAME_TEMP_PATH=C:/x/update/", @"152033..GAME_PATH=D:/y/game/" }),
           @"D:\y\game", "ini: picks the right line among several");
        Eq(GameLocator.ParseGamePathFromIni(new[] { @"  152033..GAME_PATH = C:/Games/g/  " }),
           @"C:\Games\g", "ini: tolerates whitespace");
        Eq(GameLocator.ParseGamePathFromIni(new[] { "GAME_PATH=   " }),
           null, "ini: whitespace-only value yields null");

        // --- ModInspector: Attribut-Blob-Decoder ---
        // 01 00 | 03 "abc" | 02 "hi" | 05 "1.2.3"
        var blob = new byte[] { 0x01, 0x00,
                                0x03, (byte)'a', (byte)'b', (byte)'c',
                                0x02, (byte)'h', (byte)'i',
                                0x05, (byte)'1', (byte)'.', (byte)'2', (byte)'.', (byte)'3' };
        var args = ModInspector.DecodeStringArgs(blob, 3);
        Eq(args.Count, 3, "blob: three args");
        Eq(args.Count > 0 ? args[0] : null, "abc", "blob: first arg");
        Eq(args.Count > 1 ? args[1] : null, "hi", "blob: second arg");
        Eq(args.Count > 2 ? args[2] : null, "1.2.3", "blob: third arg");

        Eq(ModInspector.DecodeStringArgs(new byte[] { 0x01, 0x00, 0xFF }, 1).Count, 0,
           "blob: null string stops decoding");
        Eq(ModInspector.DecodeStringArgs(new byte[] { 0x02, 0x00, 0x01, (byte)'x' }, 1).Count, 0,
           "blob: wrong prolog yields nothing");
        Eq(ModInspector.DecodeStringArgs(new byte[] { 0x01, 0x00, 0x00 }, 1)[0], "",
           "blob: empty string is valid");

        // 2-Byte-Laengenform: 200 = 0x80,0xC8 (ECMA-335 II.23.2)
        var longBlob = new byte[] { 0x01, 0x00, 0x80, 0xC8 }
            .Concat(Enumerable.Repeat((byte)'a', 200)).ToArray();
        var longArgs = ModInspector.DecodeStringArgs(longBlob, 1);
        Eq(longArgs.Count, 1, "blob: two-byte length form decodes");
        Eq(longArgs.Count > 0 ? longArgs[0].Length : -1, 200, "blob: two-byte length yields 200 chars");

        // Deklarierte Laenge groesser als der Puffer -> kein Ergebnis, kein Absturz
        Eq(ModInspector.DecodeStringArgs(new byte[] { 0x01, 0x00, 0x05, (byte)'a', (byte)'b' }, 1).Count, 0,
           "blob: declared length overrunning the buffer is rejected");

        // 4-Byte-Form, aber nur 3 Bytes vorhanden -> kein Ergebnis, kein Absturz
        Eq(ModInspector.DecodeStringArgs(new byte[] { 0x01, 0x00, 0xC0, 0x00, 0x00 }, 1).Count, 0,
           "blob: truncated four-byte length form is rejected");

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
