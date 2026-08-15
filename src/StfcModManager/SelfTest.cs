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

        // --- PackageMapper: Zuordnung und Ablehnung (Spec §6.3) ---
        var r1 = PackageMapper.MapEntries(new[] { "MyMod/BepInEx/plugins/MyMod.dll", "MyMod/README.md" });
        Eq(r1.Rejection, null, "map: nested BepInEx layout accepted");
        Eq(r1.Files.FirstOrDefault(f => f.Entry.EndsWith(".dll"))?.Target,
           @"BepInEx\plugins\MyMod.dll", "map: BepInEx prefix stripped");

        var r2 = PackageMapper.MapEntries(new[] { "MyMod.dll" });
        Eq(r2.Files[0].Target, @"BepInEx\plugins\MyMod.dll", "map: loose dll goes to plugins");

        var r3 = PackageMapper.MapEntries(new[] { "version.dll", "community_patch_settings.toml" });
        Eq(r3.Files[0].Target, "version.dll", "map: version.dll goes to game root");
        Eq(r3.Files[1].Target, "community_patch_settings.toml", "map: toml goes to game root");

        Check(PackageMapper.MapEntries(new[] { "../evil.dll" }).Rejection is not null,
              "map: rejects path traversal");
        Check(PackageMapper.MapEntries(new[] { "MyMod/../../evil.dll" }).Rejection is not null,
              "map: rejects nested traversal");
        Check(PackageMapper.MapEntries(new[] { @"C:\evil.dll" }).Rejection is not null,
              "map: rejects absolute path");
        Check(PackageMapper.MapEntries(new[] { "setup.exe" }).Rejection is not null,
              "map: rejects executable");
        Check(PackageMapper.MapEntries(new[] { "install.ps1" }).Rejection is not null,
              "map: rejects script");

        var r4 = PackageMapper.MapEntries(new[] { "MyMod.dll", "extra/inner.zip" });
        Eq(r4.Files.Count, 1, "map: nested archive ignored, not rejected");

        var r5 = PackageMapper.MapEntries(new[] { "MyMod/BepInEx/plugins/", "MyMod/BepInEx/plugins/A.dll" });
        Eq(r5.Files.Count, 1, "map: directory entries skipped");

        // --- PackageMapper: Haertung ueber die Spec-Beispiele hinaus (Zip-Slip-Varianten) ---
        // Windows entfernt abschliessende Punkte/Leerzeichen beim tatsaechlichen Anlegen einer Datei;
        // ohne Normalisierung vor der Endungspruefung wuerde "setup.exe." bzw. "setup.exe " als
        // harmlos durchgehen, obwohl auf der Platte wieder "setup.exe" entsteht.
        Check(PackageMapper.MapEntries(new[] { "setup.exe." }).Rejection is not null,
              "map: rejects executable disguised with a trailing dot");
        Check(PackageMapper.MapEntries(new[] { "setup.exe " }).Rejection is not null,
              "map: rejects executable disguised with a trailing space");

        // Ein Doppelpunkt im Dateinamen adressiert einen NTFS-Alternate-Data-Stream. Windows legt
        // dabei den Teil vor dem Doppelpunkt als eigene (ggf. leere) Datei an — Path.GetExtension
        // sieht nur die letzte Endung im Gesamtstring und wird so an der eigentlichen Endung vorbeigefuehrt.
        Check(PackageMapper.MapEntries(new[] { "readme.txt:hidden.dll" }).Rejection is not null,
              "map: rejects alternate-data-stream suffix (colon smuggles a second file)");
        Check(PackageMapper.MapEntries(new[] { "MyMod.exe:hidden.dll" }).Rejection is not null,
              "map: rejects blocked base name hidden behind an alternate-data-stream suffix");

        // Reine Punkte-/Leerzeichen-Segmente wie "...", "...." oder ".. " sind verboten -- NICHT
        // (wie eine fruehere Version dieses Kommentars fälschlich behauptete) weil Windows sie beim
        // tatsaechlichen Anlegen zu einer Eltern-Referenz zusammenzieht (ein Fix-Review hat das
        // empirisch widerlegt: "BepInEx\...\x" wirft DirectoryNotFoundException statt hochzulaufen),
        // sondern als reine Vorsichtsmassnahme gegen Formen, die kein legitimes Release traegt.
        Check(PackageMapper.MapEntries(new[] { "MyMod/.../evil.dll" }).Rejection is not null,
              "map: rejects a dots-only path segment that is not literally \"..\"");
        Check(PackageMapper.MapEntries(new[] { @"MyMod\..\..\evil.dll" }).Rejection is not null,
              "map: rejects traversal expressed with backslash separators only");
        Check(PackageMapper.MapEntries(new[] { @"\\server\share\evil.dll" }).Rejection is not null,
              "map: rejects a UNC-style path");

        // Path.GetExtension behandelt einen Namen, der nur aus einem fuehrenden Punkt besteht
        // (".exe"), als vollstaendige "Erweiterung" — das deckt sich hier mit der Blockliste.
        Check(PackageMapper.MapEntries(new[] { ".exe" }).Rejection is not null,
              "map: rejects a dotfile-only name that is itself a blocked extension");

        // Der einzelne Punkt "." bleibt dagegen ausdruecklich erlaubt: Windows' eigenes tar.exe
        // erzeugt ihn routinemaessig ("tar -a -cf mod.zip ." liefert Eintraege wie "./MyMod.dll").
        // Ohne diese Ausnahme wuerde ein voellig legitimes, so gebautes Release komplett abgelehnt.
        var rDotSlash = PackageMapper.MapEntries(new[] { "./MyMod.dll" });
        Eq(rDotSlash.Rejection, null, "map: leading \"./\" (tar.exe style) is accepted, not traversal");
        Eq(rDotSlash.Files[0].Target, @"BepInEx\plugins\MyMod.dll",
           "map: leading \"./\" still maps a loose dll to plugins");

        // --- PackageMapper: Fix Round 1 ---
        // Entry muss die rohe, unveraenderte Zeichenkette aus dem Archiv bleiben: ein spaeterer
        // Aufrufer sucht damit per zip.GetEntry(m.Entry) im Original-Archiv, und ein Backslash-Eintrag
        // wuerde dort nach einer Normalisierung ("/" statt "\") nicht mehr gefunden -- stiller
        // Teil-Install trotz zugestimmtem Vertrauensdialog.
        var rRawEntry = PackageMapper.MapEntries(new[] { @"MyMod\BepInEx\plugins\B.dll" });
        Eq(rRawEntry.Rejection, null, "map: backslash-separated BepInEx layout accepted");
        Eq(rRawEntry.Files[0].Entry, @"MyMod\BepInEx\plugins\B.dll",
           "map: Entry keeps the raw, unmodified name so zip.GetEntry(m.Entry) still finds it");
        Eq(rRawEntry.Files[0].Target, @"BepInEx\plugins\B.dll",
           "map: Target is still computed from the normalized form");

        // Zwei Eintraege, die auf denselben Zielpfad abbilden (hier: gleicher Dateiname unter
        // unterschiedlichen losen Ordnern), werden abgelehnt statt einen davon stillschweigend
        // fallenzulassen -- sonst wuerde eine im Vertrauensdialog gezeigte Datei nie installiert.
        Check(PackageMapper.MapEntries(new[] { "Release/Mod.dll", "Debug/Mod.dll" }).Rejection is not null,
              "map: rejects colliding targets (Release/Mod.dll vs Debug/Mod.dll both map to plugins\\Mod.dll)");

        // Ein auf Windows verbotenes Zeichen im Dateinamen (hier NUL) wuerde sonst erst spaeter bei
        // Installer.ResolveInside als ungefangene ArgumentException aus Path.GetFullPath auftauchen.
        Check(PackageMapper.MapEntries(new[] { "a\0b.dll" }).Rejection is not null,
              "map: rejects a file name containing a character illegal on Windows (NUL byte)");

        // MapArchive darf nie werfen: direkt nach einem Download kann die Datei noch fehlen oder vom
        // Virenscanner gesperrt sein (IOException) -- das muss als Rejection zurueckkommen, nicht als
        // ungefangene Exception in der WinForms-Message-Loop landen.
        var missingZip = Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-{Guid.NewGuid():N}.zip");
        Check(PackageMapper.MapArchive(missingZip).Rejection is not null,
              "map: MapArchive rejects rather than throws when the archive file cannot be opened");

        // --- PackageMapper: Fix Round 2 ---
        // Ein Eintrag, der nur aus "." besteht, muss uebersprungen werden, ohne abgelehnt zu werden
        // und ohne abzustuerzen: Segments("") liefert ein leeres Array, und MapLoose(parts[^1]) wuerde
        // sonst mit IndexOutOfRangeException in genau den Absturz laufen, den Fix Round 1 (F2) fuer
        // MapArchive schliessen sollte -- hier reintroduziert ueber unvertrauten Archivinhalt.
        // ". " landet nach dem Trim() ganz oben in der Schleife ebenfalls bei ".".
        var rDotOnly = PackageMapper.MapEntries(new[] { ".", "MyMod.dll" });
        Eq(rDotOnly.Rejection, null, "map: a \".\" entry is skipped, not rejected, and does not throw");
        Eq(rDotOnly.Files.Count, 1, "map: a \".\" entry contributes no file of its own");

        var rDotSpaceOnly = PackageMapper.MapEntries(new[] { ". ", "MyMod.dll" });
        Eq(rDotSpaceOnly.Rejection, null, "map: a \". \" entry (trims to \".\") is skipped and does not throw");
        Eq(rDotSpaceOnly.Files.Count, 1, "map: a \". \" entry contributes no file of its own");

        // Auch als einziger Eintrag darf "." nicht abstuerzen -- hier bleibt am Ende schlicht nichts
        // Installierbares uebrig, was die schon vorher bestehende, andere Ablehnung ist.
        Check(PackageMapper.MapEntries(new[] { "." }).Rejection is not null,
              "map: an archive containing only \".\" has nothing installable, and does not throw");

        // Ein eingebettetes "./"-Segment darf die Kollisionspruefung nicht umgehen: vor der
        // Zielpfad-Berechnung wird "." jetzt entfernt, nicht nur von der Traversal-Pruefung
        // ausgenommen, also erzeugen beide Eintraege denselben Zielpfad-String.
        Check(PackageMapper.MapEntries(new[] { "BepInEx/plugins/A.dll", "BepInEx/./plugins/A.dll" }).Rejection is not null,
              "map: rejects a target collision hidden behind an embedded \".\" segment");

        // Ein eingebettetes "./"-Segment in einem sonst harmlosen losen Pfad darf weder abgelehnt
        // werden noch im Zielpfad als woertliches "." landen (sonst BepInEx\plugins\. o.ae.).
        var rEmbeddedDot = PackageMapper.MapEntries(new[] { "MyMod/./A.dll" });
        Eq(rEmbeddedDot.Rejection, null, "map: an embedded \"./\" segment does not cause rejection");
        Eq(rEmbeddedDot.Files[0].Target, @"BepInEx\plugins\A.dll",
           "map: an embedded \"./\" segment is dropped before target computation");

        // --- AppState: Rundlauf durch die Serialisierung ---
        var st = new AppState { GamePath = @"C:\g", LastKnownClientBuild = "254" };
        st.Mods.Add(new ModEntry
        {
            Id = "Optimus.STFC.Berserker", Name = "Hellebarde", Version = "1.10.12",
            Enabled = true, SourceKind = "github", Repo = "trcyberoptic/STFC.Hellebarde",
            ReleaseTag = "v1.10.12", AssetName = "Hellebarde.dll",
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Hellebarde.dll", Sha256 = "aa" } }
        });
        var json = AppState.SerializeTo(st);
        var back = AppState.DeserializeFrom(json);
        Eq(back.Mods.Count, 1, "state: one mod survives the round trip");
        Eq(back.Mods[0].Files[0].Path, @"BepInEx\plugins\Hellebarde.dll", "state: file path survives");
        Eq(back.LastKnownClientBuild, "254", "state: client build survives");
        Eq(AppState.DeserializeFrom("").Mods.Count, 0, "state: empty text yields empty state");
        Eq(AppState.DeserializeFrom("{ not json").Mods.Count, 0, "state: broken json yields empty state");

        // --- AppState: Haertung gegen kaputte oder handbearbeitete Zustandsdateien ---
        // Jeder dieser Faelle darf DeserializeFrom NIE werfen lassen -- eine handbearbeitete
        // oder anderweitig kaputte state.json darf den Programmstart nie verhindern.
        Eq(AppState.DeserializeFrom("   \n\t  ").Mods.Count, 0, "state: whitespace-only text yields empty state");
        Eq(AppState.DeserializeFrom("[1,2,3]").Mods.Count, 0, "state: json array (wrong shape) yields empty state");
        Eq(AppState.DeserializeFrom("\"hello\"").Mods.Count, 0, "state: json string (wrong shape) yields empty state");

        // JsonSerializer.Deserialize<T> liefert fuer den woertlichen Text "null" tatsaechlich
        // eine CLR-null zurueck (kein Fehlschlag, keine Exception) -- genau dafuer faengt das
        // "?? new AppState()" in DeserializeFrom auf. Verifiziert statt angenommen.
        Eq(AppState.DeserializeFrom("null").Mods.Count, 0, "state: json null literal yields empty state");

        // Unbekannte Zusatzfelder werden von System.Text.Json standardmaessig stillschweigend
        // ignoriert; bekannte Felder daneben muessen trotzdem ankommen.
        Eq(AppState.DeserializeFrom("""{"UnknownField":123,"Nested":{"a":1},"GamePath":"C:\\Games\\g"}""").GamePath,
           @"C:\Games\g", "state: unknown extra properties are ignored, known fields still parse");

        // "Mods": null ueberschreibt sonst den Feldinitialisierer mit null, denn System.Text.Json
        // respektiert die Nullable-Annotation der Property beim Deserialisieren standardmaessig
        // nicht -- ohne die Korrektur in DeserializeFrom wuerde back.Mods hier zu einer erst bei
        // der naechsten Verwendung sichtbaren NullReferenceException fuehren.
        Eq(AppState.DeserializeFrom("""{"Mods":null,"TrustedRepos":null}""").Mods.Count, 0,
           "state: explicit null Mods list does not surface as null");
        Eq(AppState.DeserializeFrom("""{"Mods":null,"TrustedRepos":null}""").TrustedRepos.Count, 0,
           "state: explicit null TrustedRepos list does not surface as null");

        // Ein fuehrendes UTF-8-BOM (z. B. weil ein Editor die Datei beim Handbearbeiten so
        // gespeichert hat) darf den Inhalt nicht stillschweigend verwerfen: ohne den Trim in
        // DeserializeFrom scheitert der Utf8JsonReader daran, was zwar gefangen wird, dabei aber
        // einen echten Mod-Bestand verlieren wuerde statt ihn zu lesen.
        var bomJson = "\uFEFF" + AppState.SerializeTo(st);
        Eq(AppState.DeserializeFrom(bomJson).Mods.Count, 1, "state: leading UTF-8 BOM does not lose the parsed state");

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
