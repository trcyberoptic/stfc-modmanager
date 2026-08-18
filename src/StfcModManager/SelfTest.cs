namespace StfcModManager;

using System.IO.Compression;
using StfcModManager.Core;
using StfcModManager.Ui;

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

        // --- AppState: Fix Round 1 (F2) -- null-Elemente innerhalb der Listen, nicht nur die Listen selbst ---
        // Eine null-Liste wurde bereits abgefangen; ein einzelnes null-Element darin (z. B. durch
        // ein verirrtes ",null" in einer handbearbeiteten Datei) ist dieselbe Fehlerklasse und muss
        // ebenso rausgefiltert werden, sonst crasht die naechste Iteration ueber die Liste.
        Eq(AppState.DeserializeFrom("""{"Mods":[null]}""").Mods.Count, 0,
           "state: a null element inside Mods is dropped, not kept as null");
        Eq(AppState.DeserializeFrom("""{"SharedFiles":[null]}""").SharedFiles.Count, 0,
           "state: a null element inside SharedFiles is dropped, not kept as null");
        Eq(AppState.DeserializeFrom("""{"TrustedRepos":[null]}""").TrustedRepos.Count, 0,
           "state: a null element inside TrustedRepos is dropped, not kept as null");

        // --- Installer: Referenzzaehlung fuer geteilte Bibliotheken (Spec §6.4) ---
        var shState = new AppState();
        Installer.RegisterShared(shState, @"BepInEx\plugins\Newtonsoft.Json.dll", "aa", "13.0.3", "modA");
        Installer.RegisterShared(shState, @"BepInEx\plugins\Newtonsoft.Json.dll", "aa", "13.0.3", "modB");
        Eq(shState.SharedFiles.Count, 1, "shared: one record for two providers");
        Eq(shState.SharedFiles[0].Providers.Count, 2, "shared: two providers");

        Eq(Installer.ReleaseShared(shState, @"BepInEx\plugins\Newtonsoft.Json.dll", "modA"), false,
           "shared: file survives while another mod needs it");
        Eq(Installer.ReleaseShared(shState, @"BepInEx\plugins\Newtonsoft.Json.dll", "modB"), true,
           "shared: file is deletable once the last provider goes");
        Eq(shState.SharedFiles.Count, 0, "shared: record removed with the last provider");
        Eq(Installer.ReleaseShared(shState, @"BepInEx\plugins\Unknown.dll", "modA"), true,
           "shared: unknown file is deletable");

        // Registrierung mit hoeherer Dateiversion gewinnt
        var vState = new AppState();
        Installer.RegisterShared(vState, @"BepInEx\plugins\X.dll", "aa", "1.0.0", "modA");
        Installer.RegisterShared(vState, @"BepInEx\plugins\X.dll", "bb", "2.0.0", "modB");
        Eq(vState.SharedFiles[0].FileVersion, "2.0.0", "shared: higher file version wins");

        // --- Installer.Remove: SharedFiles-Eintraege eines Mods werden mitentfernt (Pre-Flight-Review) ---
        // Remove() sah urspruenglich nur mod.Files vor -- Dateien, die ein Mod zusaetzlich in
        // state.SharedFiles mit sich selbst als Anbieter eintraegt (z. B. mitgelieferte
        // Abhaengigkeiten), blieben beim Deinstallieren fuer immer als "SharedFile" bestehen.
        // Ein nicht existierender Spielordner haelt diese Pruefung rein bei der Buchhaltung:
        // File.Exists liefert dafuer zuverlaessig false, File.Delete wird nie erreicht.
        var rmGame = new GameInstall(Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}"));

        var rmState = new AppState();
        var rmMod = new ModEntry
        {
            Id = "modA", Name = "A", Version = "1.0",
            Files = { new InstalledFile { Path = @"BepInEx\plugins\A.dll", Sha256 = "aa" } }
        };
        rmState.Mods.Add(rmMod);
        Installer.RegisterShared(rmState, @"BepInEx\plugins\Shared.dll", "bb", "1.0.0", "modA");
        Installer.RegisterShared(rmState, @"BepInEx\plugins\Shared.dll", "bb", "1.0.0", "modB");

        Installer.Remove(rmState, rmGame, rmMod);
        Eq(rmState.Mods.Count, 0, "remove: mod itself is dropped from state.Mods");
        Eq(rmState.SharedFiles.Count, 1, "remove: shared file survives while modB still needs it");
        Eq(rmState.SharedFiles[0].Providers.Count, 1, "remove: modA's provider entry is gone from the shared file");
        Eq(rmState.SharedFiles[0].Providers[0], "modB", "remove: remaining provider is modB");

        // Wenn modC der letzte Anbieter ist, verschwindet der SharedFiles-Eintrag komplett --
        // genau die Regel, die ReleaseShared fuer sich alleine schon garantiert.
        var rmState2 = new AppState();
        var rmMod2 = new ModEntry { Id = "modC", Name = "C", Version = "1.0" };
        rmState2.Mods.Add(rmMod2);
        Installer.RegisterShared(rmState2, @"BepInEx\plugins\Solo.dll", "cc", "1.0.0", "modC");
        Installer.Remove(rmState2, rmGame, rmMod2);
        Eq(rmState2.SharedFiles.Count, 0, "remove: shared file with no remaining providers is dropped entirely");

        // --- Installer.SetEnabled: native-Zweig darf nie werfen, wenn keine der beiden Dateien existiert ---
        var nativeGame = new GameInstall(Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}"));
        var nativeMod = new ModEntry
        {
            Id = "CommunityPatch", Name = "Community Patch", Version = "1.0",
            SourceKind = "native", Enabled = false
        };
        Installer.SetEnabled(new AppState(), nativeGame, nativeMod, true);
        Eq(nativeMod.Enabled, true,
           "SetEnabled: native toggle with neither version.dll nor version.dll_ present does not throw and still flips Enabled");

        // --- Installer: Fix Round 1, C1 -- stabiler Pfad-Schluessel, Remove() loescht keine
        // fremd benoetigte geteilte Datei mehr (Pre-Flight-Review Reproduktion) ---
        // Reproduziert die Kernaussage von C1 rein ueber die Buchfuehrung: Json.dll steht sowohl
        // in modA.Files (unter dem KANONISCHEN Pfad -- SetEnabled schreibt ihn seit diesem Fix
        // nie mehr um) als auch in state.SharedFiles mit modB als weiterem Anbieter. Ohne den
        // stabilen Schluessel haette Remove(modA) mit einem umgeschriebenen Pfad keinen Treffer in
        // state.SharedFiles gefunden, ReleaseShared haette "unbekannt, darf weg" gemeldet, und die
        // Datei waere geloescht worden, obwohl modB sie noch braucht.
        var c1Game = new GameInstall(Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}"));
        var c1State = new AppState();
        var c1ModA = new ModEntry
        {
            Id = "modA", Name = "A", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Json.dll", Sha256 = "j" } }
        };
        c1State.Mods.Add(c1ModA);
        Installer.RegisterShared(c1State, @"BepInEx\plugins\Json.dll", "j", "13.0.3", "modA");
        Installer.RegisterShared(c1State, @"BepInEx\plugins\Json.dll", "j", "13.0.3", "modB");

        Installer.Remove(c1State, c1Game, c1ModA);
        Eq(c1State.SharedFiles.Count, 1, "C1: Json.dll (still needed by modB) survives Remove(modA)");
        Eq(c1State.SharedFiles.Count == 1 ? c1State.SharedFiles[0].Providers.Count : -1, 1,
           "C1: only modA's provider entry was released");
        Eq(c1State.SharedFiles.Count == 1 ? c1State.SharedFiles[0].Providers[0] : null, "modB",
           "C1: modB remains the sole provider");

        // --- Installer: Fix Round 1, I4 -- Mod-Id-Saeuberung fuer Dateinamen ---
        // ModInspector liest die Id ungeprueft aus einer Fremd-DLL; SanitizeForFileName muss
        // Pfadtrenner und ".."-Aufstiege entschaerfen, bevor die Id Teil eines Dateinamens wird
        // (BackupConfig), sonst macht z. B. eine Id wie "..\..\evil" aus dem Konfigurations-
        // Dateinamen einen Pfad, der aus dem vorgesehenen Ordner hinauszeigt.
        var sanitizedTraversal = Installer.SanitizeForFileName(@"..\..\evil");
        Check(!sanitizedTraversal.Contains(".."), "sanitize: a mod id with \"..\\\" traversal no longer contains \"..\" after sanitizing");
        Check(!sanitizedTraversal.Contains('\\'), "sanitize: a mod id with \"..\\\" traversal no longer contains a backslash after sanitizing");

        var sanitizedSeparator = Installer.SanitizeForFileName("Some/Mod.Id");
        Check(!sanitizedSeparator.Contains('/'), "sanitize: a mod id containing a separator no longer contains '/' after sanitizing");
        Eq(sanitizedSeparator, "Some_Mod.Id", "sanitize: a forward slash is replaced with '_', everything else is left alone");

        // --- Installer.PhysicalPath: der aus dem stabilen Schluessel ABGELEITETE Ort (C1) ---
        // Reine Zeichenkettenarbeit, kein I/O: der Spielordner existiert bewusst nicht.
        var ppRoot = Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}");
        var ppGame = new GameInstall(ppRoot);
        var ppDll = new InstalledFile { Path = @"BepInEx\plugins\MyMod\Core.dll", Sha256 = "c" };
        var ppCfg = new InstalledFile { Path = @"BepInEx\config\MyMod.cfg", Sha256 = "g" };
        var ppMod = new ModEntry
        {
            Id = "nested", Name = "Nested", Version = "1.0", Enabled = true,
            Files = { ppDll, ppCfg }
        };

        Eq(Installer.PhysicalPath(ppGame, ppMod, ppDll), Path.Combine(ppRoot, @"BepInEx\plugins\MyMod\Core.dll"),
           "PhysicalPath: an enabled mod's file sits at its canonical (stored) location, subfolder included");

        ppMod.Enabled = false;
        // Die Unterordnerstruktur wird gespiegelt, nicht abgeflacht: sonst faenden zwei Mods mit
        // gleichnamigen DLLs in verschiedenen Unterordnern in plugins-disabled dieselbe Datei vor
        // und ueberschrieben sich gegenseitig (Fix Round 2, I5).
        Eq(Installer.PhysicalPath(ppGame, ppMod, ppDll), Path.Combine(ppRoot, @"BepInEx\plugins-disabled\MyMod\Core.dll"),
           "PhysicalPath: a disabled mod's dll is derived to the MIRRORED path under plugins-disabled");
        Eq(ppDll.Path, @"BepInEx\plugins\MyMod\Core.dll",
           "PhysicalPath: deriving a location never rewrites the stored canonical path");
        Eq(Installer.PhysicalPath(ppGame, ppMod, ppCfg), Path.Combine(ppRoot, @"BepInEx\config\MyMod.cfg"),
           "PhysicalPath: only .dll files move to plugins-disabled, a config keeps its place");

        // Bei einem nativen Mod wandert AUSSCHLIESSLICH version.dll; mitgelieferte Beilagen
        // (doorstop_config.ini, .toml) bleiben liegen, wo sie sind -- wuerden auch sie auf
        // version.dll abgebildet, loeschte Remove() sie nie.
        var ppNativeDll = new InstalledFile { Path = "version.dll", Sha256 = "v" };
        var ppNativeIni = new InstalledFile { Path = "doorstop_config.ini", Sha256 = "d" };
        var ppNative = new ModEntry
        {
            Id = "CommunityPatch", Name = "Community Patch", Version = "1.0",
            SourceKind = "native", Enabled = false, Files = { ppNativeDll, ppNativeIni }
        };
        Eq(Installer.PhysicalPath(ppGame, ppNative, ppNativeDll), Path.Combine(ppRoot, "version.dll_"),
           "PhysicalPath: a disabled native mod's version.dll is derived to version.dll_");
        Eq(Installer.PhysicalPath(ppGame, ppNative, ppNativeIni), Path.Combine(ppRoot, "doorstop_config.ini"),
           "PhysicalPath: a native mod's other files keep their own location instead of collapsing onto version.dll");

        // --- Installer.Apply: Vorbedingungen schlagen zu, bevor irgendetwas angefasst wird (I1/M4, I2) ---
        static string? ApplyErrorName(string root, IReadOnlyList<(string Source, string Target)> ops)
        {
            try { Installer.Apply(root, ops); return null; }
            catch (Exception e) { return e.GetType().Name; }
        }

        var apRoot = Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}");
        Eq(ApplyErrorName(apRoot, new (string, string)[] { ("a.dll", @"BepInEx\plugins\") }), "ArgumentException",
           "Apply: a target ending in a directory separator is rejected -- it is never a file");
        // Der Doppelte-Ziele-Test vergleicht die AUFGELOESTE Form: dieselbe Datei, nur einmal mit
        // '\' und einmal mit '/' geschrieben, ist trotzdem ein Duplikat.
        Eq(ApplyErrorName(apRoot, new (string, string)[]
           { ("a.dll", @"BepInEx\plugins\Dup.dll"), ("b.dll", "BepInEx/plugins/Dup.dll") }), "ArgumentException",
           "Apply: two targets differing only in separator style are still one duplicated target");
        Check(!Directory.Exists(apRoot), "Apply: a rejected op list touches nothing on disk");

        // --- Installer.SetEnabled: ein Umschalten auf den bereits bestehenden Zustand tut nichts ---
        var seRoot = Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}");
        var seMod = new ModEntry
        {
            Id = "already-on", Name = "On", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\On.dll", Sha256 = "o" } }
        };
        Installer.SetEnabled(new AppState(), new GameInstall(seRoot), seMod, true);
        Eq(seMod.Enabled, true, "SetEnabled: toggling to the state a mod is already in leaves it enabled");
        Check(!Directory.Exists(seRoot), "SetEnabled: a no-op toggle creates no directories in the game folder");

        // --- Installer, Fix Round 2: Dateien ausserhalb von BepInEx\plugins wandern nie ---
        // Ein Mod, der winhttp.dll mitbringt, haette die beim Deaktivieren sonst nach
        // plugins-disabled verschoben und damit den Doorstop-Loader -- und mit ihm SAEMTLICHE
        // Mods -- stillschweigend abgeschaltet.
        var whFile = new InstalledFile { Path = "winhttp.dll", Sha256 = "w" };
        var whMod = new ModEntry
        {
            Id = "loader-carrier", Name = "Carrier", Version = "1.0", Enabled = false,
            Files = { whFile }
        };
        Eq(Installer.PhysicalPath(ppGame, whMod, whFile), Path.Combine(ppRoot, "winhttp.dll"),
           "PhysicalPath: a game-root dll of a disabled mod stays put, it is never moved to plugins-disabled");

        // --- Installer, Fix Round 2: BepInEx\config\*.cfg ist geschuetzt ---
        // Ein Archiv darf seine eigene Default-Config mitliefern; landet die in mod.Files, darf
        // Remove() sie trotzdem nur verschieben, nie loeschen (Spec §6.6).
        var cfgGame = new GameInstall(Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}"));
        var cfgState = new AppState();
        var cfgMod = new ModEntry
        {
            Id = "cfgmod", Name = "Cfg", Version = "1.0",
            Files =
            {
                new InstalledFile { Path = @"BepInEx\plugins\Cfg.dll", Sha256 = "d" },
                new InstalledFile { Path = @"BepInEx\config\cfgmod.cfg", Sha256 = "c" }
            }
        };
        cfgState.Mods.Add(cfgMod);
        Installer.Remove(cfgState, cfgGame, cfgMod);
        Eq(cfgState.Mods.Count, 0, "remove: a mod carrying its own config still uninstalls completely");

        // --- Installer, Fix Round 2, Minor 4: ein unaufloesbarer Eintrag blockiert Remove() nie ---
        // Ein leerer Path (handbearbeitete state.json) loest nicht innerhalb des Spielordners auf.
        // Frueher warf das bei jedem Versuch an derselben Stelle -- der Mod liess sich nie
        // deinstallieren, waehrend seine frueheren Dateien laengst geloescht waren.
        var badState = new AppState();
        var badMod = new ModEntry
        {
            Id = "badpath", Name = "Bad", Version = "1.0",
            Files = { new InstalledFile { Path = "", Sha256 = "x" } }
        };
        badState.Mods.Add(badMod);
        Installer.Remove(badState, cfgGame, badMod);
        Eq(badState.Mods.Count, 0, "remove: an entry that cannot be resolved is logged and skipped, the mod still uninstalls");

        // --- Installer, Fix Round 2, I1: eine geteilte Bibliothek wird nicht weggeschoben,
        // solange ein anderer AKTIVIERTER Mod sie anbietet ---
        var lockRoot = Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}");
        var lockState = new AppState();
        var lockModA = new ModEntry
        {
            Id = "shA", Name = "A", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Json.dll", Sha256 = "j" } }
        };
        var lockModB = new ModEntry { Id = "shB", Name = "B", Version = "1.0", Enabled = true };
        lockState.Mods.Add(lockModA);
        lockState.Mods.Add(lockModB);
        Installer.RegisterShared(lockState, @"BepInEx\plugins\Json.dll", "j", "13.0.3", "shA");
        Installer.RegisterShared(lockState, @"BepInEx\plugins\Json.dll", "j", "13.0.3", "shB");

        Installer.SetEnabled(lockState, new GameInstall(lockRoot), lockModA, false);
        Eq(lockModA.Enabled, false, "SetEnabled: the mod itself still flips to disabled");
        Check(!Directory.Exists(Path.Combine(lockRoot, @"BepInEx\plugins-disabled")),
              "SetEnabled: a library another enabled mod still provides is not moved out from under it");

        // --- Installer, Fix Round 3: eine geteilte Config wird nicht weggesichert, solange ein
        // anderer Mod sie noch anbietet -- auch nicht ueber den id-abgeleiteten Weg am Ende von
        // Remove(), der die Referenzzaehlung frueher umging ---
        var scGame = new GameInstall(Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}"));
        var scState = new AppState();
        var scFoo = new ModEntry { Id = "Foo", Name = "Foo", Version = "1.0" };
        scState.Mods.Add(scFoo);
        scState.Mods.Add(new ModEntry { Id = "Bar", Name = "Bar", Version = "1.0" });
        Installer.RegisterShared(scState, @"BepInEx\config\Foo.cfg", "c", "1.0", "Foo");
        Installer.RegisterShared(scState, @"BepInEx\config\Foo.cfg", "c", "1.0", "Bar");

        Installer.Remove(scState, scGame, scFoo);
        Eq(scState.SharedFiles.Count, 1, "remove: a shared config's record survives while another mod provides it");
        Eq(scState.SharedFiles.Count == 1 ? scState.SharedFiles[0].Providers.Single() : null, "Bar",
           "remove: only the leaving mod's provider entry was released from the shared config");

        // --- Installer, Fix Round 3: ein Config-Pfad mit "."-Segment wird als Config erkannt ---
        // Die Zugehoerigkeit entscheidet sich an der aufgeloesten Form, nicht an einem Praefix --
        // sonst faellt genau diese Schreibweise durch den Schutz und wird geloescht statt gesichert.
        var dotState = new AppState();
        var dotMod = new ModEntry
        {
            Id = "dotmod", Name = "Dot", Version = "1.0",
            Files = { new InstalledFile { Path = @"BepInEx\.\config\Dot.cfg", Sha256 = "c" } }
        };
        dotState.Mods.Add(dotMod);
        Installer.Remove(dotState, scGame, dotMod);
        Eq(dotState.Mods.Count, 0, "remove: a config path spelled with a '.' segment does not break the uninstall");

        // --- Installer, Fix Round 4: der Ausweichort einer Datei ist ein eigenstaendiges Ziel ---
        // "BepInEx\plugins-disabled\Core.dll" ist ein Pfad, den ein Archiv regulaer mitbringen darf
        // (PackageMapper akzeptiert ihn), also ein Ort mit eigenem Besitzer -- und nicht der
        // Zweitname des kanonischen Pfades eines anderen Mods. Remove() muss darueber hinweggehen,
        // statt ihn als vermeintliche Zweitlage mitzuloeschen.
        var ownGame = new GameInstall(Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-nonexistent-{Guid.NewGuid():N}"));
        var ownState = new AppState();
        var ownA = new ModEntry
        {
            Id = "ownA", Name = "A", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Core.dll", Sha256 = "a" } }
        };
        var ownB = new ModEntry
        {
            Id = "ownB", Name = "B", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins-disabled\Core.dll", Sha256 = "b" } }
        };
        ownState.Mods.Add(ownA);
        ownState.Mods.Add(ownB);

        Installer.Remove(ownState, ownGame, ownA);
        Eq(ownState.Mods.Count, 1, "remove: removing one mod leaves the other installed");
        Eq(ownState.Mods.Count == 1 ? ownState.Mods[0].Id : null, "ownB",
           "remove: the mod owning the plugins-disabled path is the one still there");

        // --- GitHubClient: URL-Parser ---
        Eq(GitHubClient.ParseRepoUrl("https://github.com/trcyberoptic/STFC.NDB")?.ToString(),
           "(trcyberoptic, STFC.NDB)", "gh: plain repo url");
        Eq(GitHubClient.ParseRepoUrl("https://github.com/trcyberoptic/STFC.NDB/")?.ToString(),
           "(trcyberoptic, STFC.NDB)", "gh: trailing slash");
        Eq(GitHubClient.ParseRepoUrl("https://github.com/trcyberoptic/STFC.NDB/releases/latest")?.ToString(),
           "(trcyberoptic, STFC.NDB)", "gh: deep link is trimmed");
        Eq(GitHubClient.ParseRepoUrl("http://github.com/a/b"), null, "gh: http is rejected");
        Eq(GitHubClient.ParseRepoUrl("https://gitlab.com/a/b"), null, "gh: foreign host is rejected");
        Eq(GitHubClient.ParseRepoUrl("not a url"), null, "gh: garbage is rejected");

        // --- GitHubClient: Asset-Auswahlregel (Spec §6.2) ---
        Eq(GitHubClient.PickAsset(["A.zip", "B.dll"], "B.dll"), "B.dll", "asset: remembered name wins");
        Eq(GitHubClient.PickAsset(["only.zip", "notes.txt"], null), "only.zip", "asset: single zip wins");
        Eq(GitHubClient.PickAsset(["only.dll", "notes.txt"], null), "only.dll", "asset: single dll wins");
        Eq(GitHubClient.PickAsset(["a.zip", "b.zip"], null), null, "asset: two zips need a dialog");
        Eq(GitHubClient.PickAsset(["a.zip", "b.dll"], null), "a.zip", "asset: zip beats dll");
        Eq(GitHubClient.PickAsset(["notes.txt"], null), null, "asset: nothing installable needs a dialog");
        Eq(GitHubClient.PickAsset(["A.zip"], "gone.dll"), "A.zip", "asset: stale remembered name falls through");

        // --- GitHubClient: InstallableCandidates -- der Baustein, mit dem ein Aufrufer
        // "PickAsset lieferte null, weil ambig" von "PickAsset lieferte null, weil nichts
        // installierbar da ist" unterscheiden kann, ohne die .zip/.dll-Regel selbst nachzubauen ---
        Eq(GitHubClient.InstallableCandidates(["a.zip", "b.dll", "readme.md"]).Count, 2,
           "candidates: zip and dll both count, readme does not");
        Eq(GitHubClient.InstallableCandidates(["notes.txt"]).Count, 0,
           "candidates: empty means PickAsset's null is 'nothing installable', not 'ambiguous'");
        Eq(GitHubClient.InstallableCandidates(["a.zip", "b.zip"]).Count, 2,
           "candidates: non-empty alongside PickAsset's null means 'ambiguous, ask the user'");

        // --- GitHubClient: IsAllowedDownloadHost -- die Sicherheitsgrenze fuer Datei-Downloads.
        // Jeder Fall hier pinnt eine konkrete Umgehung, die ein Angreifer versuchen koennte. Fuer
        // eine Stichprobe wurde von Hand geprueft, dass der jeweils zustaendige Assert wirklich
        // fehlschlaegt, wenn man genau die Regel bricht, die er pinnt (s. Fix-Round-Bericht).
        Check(GitHubClient.IsAllowedDownloadHost(new Uri("https://github.com/o/r/releases/download/v1/a.zip")),
              "host: github.com is accepted");
        Check(GitHubClient.IsAllowedDownloadHost(new Uri("https://objects.githubusercontent.com/x")),
              "host: objects.githubusercontent.com is accepted");
        Check(GitHubClient.IsAllowedDownloadHost(new Uri("https://release-assets.githubusercontent.com/x")),
              "host: release-assets.githubusercontent.com is accepted");
        Check(GitHubClient.IsAllowedDownloadHost(new Uri("https://sub.githubusercontent.com/x")),
              "host: an arbitrary githubusercontent.com subdomain is accepted (spec: *.githubusercontent.com)");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://sub.github.com/x")),
              "host: github.com does NOT get the wildcard treatment -- exact match only, unlike *.githubusercontent.com");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://evil-github.com/x")),
              "host: evil-github.com is rejected (suffix confusion, no dot boundary)");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://github.com.attacker.net/x")),
              "host: github.com.attacker.net is rejected");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://githubusercontent.com.evil.net/x")),
              "host: githubusercontent.com.evil.net is rejected");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://objects.githubusercontent.com.evil.net/x")),
              "host: objects.githubusercontent.com.evil.net is rejected");
        Check(GitHubClient.IsAllowedDownloadHost(new Uri("https://user:pw@github.com/x")),
              "host: credentials in the URL do not hide the real (legitimate) host");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://github.com@evil.com/x")),
              "host: 'github.com' as userinfo does not fool the check -- the real host is evil.com");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://140.82.112.3/x")),
              "host: a bare IP literal is rejected");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://\u0261ithub.com/x")),
              "host: a unicode homoglyph (U+0261 in place of 'g') is rejected");
        // U+3002 IDEOGRAPHIC FULL STOP is a real IDNA label separator: Uri.Host keeps it literally
        // ("github\u3002com", rejected by a naive string compare), but Uri.IdnHost normalizes it to
        // "github.com" -- the form DNS/TLS actually resolve, so this URL genuinely connects to the
        // real github.com and accepting it is correct, not a hole. This is the one case in this
        // table where Host and IdnHost actually disagree (ground-truth measured for real under this
        // project's InvariantGlobalization=true before writing this assert).
        Check(GitHubClient.IsAllowedDownloadHost(new Uri("https://github\u3002com/x")),
              "host: U+3002 (IDNA label separator) normalizes to github.com via IdnHost and is accepted");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://github.com./x")),
              "host: a trailing dot (FQDN root label) is rejected");
        Check(GitHubClient.IsAllowedDownloadHost(new Uri("https://GitHub.COM/x")),
              "host: the match is still case-insensitive");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("https://github.com:22/a.zip")),
              "host: a non-default port is rejected");
        Check(GitHubClient.IsAllowedDownloadHost(new Uri("https://github.com:443/a.zip")),
              "host: an explicit DEFAULT port is still accepted");
        Check(!GitHubClient.IsAllowedDownloadHost(new Uri("http://github.com/x")),
              "host: http (not https) on the real host is still rejected");

        // --- GitHubClient: ResolveAllowedRedirect -- dieselbe Pruefung fuer jeden Redirect-Hop,
        // rein und ohne Netzwerk testbar. Deckt die Faelle ab, die DownloadAssetAsync von Hand
        // verfolgen muss, weil AllowAutoRedirect fuer den Download-Client aus ist. ---
        var redirectBase = new Uri("https://github.com/o/r/releases/download/v1/asset.zip");
        Check(GitHubClient.ResolveAllowedRedirect(redirectBase, null) is null,
              "redirect: a missing Location header is refused");
        Check(GitHubClient.ResolveAllowedRedirect(redirectBase, new Uri("//evil.com/x.zip", UriKind.RelativeOrAbsolute)) is null,
              "redirect: a protocol-relative escape to a foreign host is refused");
        Check(GitHubClient.ResolveAllowedRedirect(redirectBase, new Uri("http://github.com/x", UriKind.RelativeOrAbsolute)) is null,
              "redirect: a scheme downgrade to http is refused even on the real host");
        Check(GitHubClient.ResolveAllowedRedirect(redirectBase, new Uri("data:text/plain;base64,AAAA", UriKind.RelativeOrAbsolute)) is null,
              "redirect: a data: target is refused");
        Check(GitHubClient.ResolveAllowedRedirect(redirectBase, new Uri("file:///C:/evil.dll", UriKind.RelativeOrAbsolute)) is null,
              "redirect: a file: target is refused");
        Eq(GitHubClient.ResolveAllowedRedirect(redirectBase, new Uri("/other/path.zip", UriKind.RelativeOrAbsolute))?.Host,
           "github.com", "redirect: a relative path resolves against the same host and is accepted");
        Eq(GitHubClient.ResolveAllowedRedirect(redirectBase, new Uri("https://release-assets.githubusercontent.com/x", UriKind.RelativeOrAbsolute))?.Host,
           "release-assets.githubusercontent.com", "redirect: the real CDN target GitHub actually uses is accepted");

        // --- BepInExRuntime.IsAllowedRuntimeHost -- Fix Round 1: the runtime download previously used
        // a default HttpClient (auto-redirect on, no host check at all). Same spirit as
        // GitHubClient.IsAllowedDownloadHost above, deliberately narrower: exactly one pinned host,
        // no wildcard subdomain family (none is known for bepinex.dev's build server). ---
        Check(BepInExRuntime.IsAllowedRuntimeHost(new Uri("https://builds.bepinex.dev/projects/x/755/a.zip")),
              "runtime-host: builds.bepinex.dev over https is accepted");
        Check(BepInExRuntime.IsAllowedRuntimeHost(new Uri("https://BUILDS.BEPINEX.DEV/x")),
              "runtime-host: the match is case-insensitive");
        Check(!BepInExRuntime.IsAllowedRuntimeHost(new Uri("http://builds.bepinex.dev/x")),
              "runtime-host: http (not https) on the real host is rejected");
        Check(!BepInExRuntime.IsAllowedRuntimeHost(new Uri("https://evil.com/x")),
              "runtime-host: an unrelated host is rejected");
        Check(!BepInExRuntime.IsAllowedRuntimeHost(new Uri("https://cdn.bepinex.dev/x")),
              "runtime-host: no wildcard subdomain family -- a different bepinex.dev subdomain is rejected");
        Check(!BepInExRuntime.IsAllowedRuntimeHost(new Uri("https://builds.bepinex.dev.evil.com/x")),
              "runtime-host: builds.bepinex.dev.evil.com (suffix confusion) is rejected");
        Check(!BepInExRuntime.IsAllowedRuntimeHost(new Uri("https://evil-builds.bepinex.dev/x")),
              "runtime-host: evil-builds.bepinex.dev (prefix confusion, no dot boundary) is rejected");
        Check(!BepInExRuntime.IsAllowedRuntimeHost(new Uri("https://builds.bepinex.dev:8443/x")),
              "runtime-host: a non-default port is rejected");
        Check(BepInExRuntime.IsAllowedRuntimeHost(new Uri("https://builds.bepinex.dev:443/x")),
              "runtime-host: an explicit DEFAULT port is still accepted");

        // --- SelfUpdate.ApplicableUpdateVersion -- reine Update-Entscheidung, getrennt von
        // CheckAsync's Netzwerkzugriff getestet (dasselbe Muster wie IsAllowedDownloadHost /
        // IsAllowedRuntimeHost oben). ---
        Eq(SelfUpdate.ApplicableUpdateVersion("v1.2.3", new Version(1, 0, 0)), new Version(1, 2, 3),
           "selfupdate: a tag with a leading lowercase 'v' is parsed as a version");
        Eq(SelfUpdate.ApplicableUpdateVersion("V2.0.0", new Version(1, 0, 0)), new Version(2, 0, 0),
           "selfupdate: a tag with a leading uppercase 'V' is parsed as a version too");
        Eq(SelfUpdate.ApplicableUpdateVersion("1.5.0", new Version(1, 0, 0)), new Version(1, 5, 0),
           "selfupdate: a bare tag without any 'v' prefix still parses");
        Eq(SelfUpdate.ApplicableUpdateVersion("release-candidate", new Version(1, 0, 0)), null,
           "selfupdate: a tag that is not a version at all yields no applicable update, not a crash");
        Eq(SelfUpdate.ApplicableUpdateVersion("vNext", new Version(1, 0, 0)), null,
           "selfupdate: a 'v'-prefixed tag that still isn't a version after trimming yields no update");
        Eq(SelfUpdate.ApplicableUpdateVersion("v1.0.0", new Version(1, 0, 0)), null,
           "selfupdate: a tag equal to the current version is not an update (strictly greater required)");
        Eq(SelfUpdate.ApplicableUpdateVersion("v0.9.0", new Version(1, 0, 0)), null,
           "selfupdate: a tag older than the current version is not an update");
        Eq(SelfUpdate.ApplicableUpdateVersion("v1.0.1", new Version(1, 0, 0)), new Version(1, 0, 1),
           "selfupdate: a tag one patch version newer applies");

        // --- GitHubClient.ParseSha256Digest / ClassifyDigest -- Fix Round 1: GitHub publishes a
        // SHA-256 digest per release asset ("digest": "sha256:<64 hex chars>"); DownloadAssetAsync
        // now verifies the download against it before moving the file into place. Both the parsing
        // and the match/mismatch/absent decision are pure and tested here without any network or
        // file I/O -- the actual hashing happens only inside DownloadAssetAsync. ---
        var sampleHex = new string('a', 64);
        Eq(GitHubClient.ParseSha256Digest($"sha256:{sampleHex}"), sampleHex,
           "digest: a well-formed \"sha256:<64 hex>\" field parses to the bare hex part");
        Eq(GitHubClient.ParseSha256Digest($"SHA256:{sampleHex.ToUpperInvariant()}"), sampleHex,
           "digest: the \"sha256:\" prefix and the hex digits are both matched case-insensitively, normalized to lowercase");
        Eq(GitHubClient.ParseSha256Digest(null), null,
           "digest: a missing field (GitHub JSON has no \"digest\" property) yields null, not a throw");
        // "sha384:" is deliberately the same length (7 chars) as "sha256:" followed by 64 hex
        // chars, same as a valid case -- unlike a "sha512:"+128-hex payload (rejected by the length
        // check alone even without a prefix check), this ONLY fails if the prefix is actually
        // compared, not merely skipped by a fixed offset.
        Eq(GitHubClient.ParseSha256Digest($"sha384:{sampleHex}"), null,
           "digest: a non-sha256 algorithm prefix of the same length is rejected, not silently accepted");
        // Uses only valid hex characters ('a'), just too few of them -- isolates the length check
        // from the hex-digit check (a non-hex "tooshort" would fail either check, masking which one
        // actually caught it).
        Eq(GitHubClient.ParseSha256Digest($"sha256:{new string('a', 10)}"), null,
           "digest: a value shorter than 64 hex characters after the prefix is rejected");
        Eq(GitHubClient.ParseSha256Digest($"sha256:{sampleHex}ff"), null,
           "digest: a value longer than 64 hex characters after the prefix is rejected");
        Eq(GitHubClient.ParseSha256Digest($"sha256:{new string('g', 64)}"), null,
           "digest: 64 characters that are not all valid hex digits ('g' is not hex) is rejected");
        // Same length-matching trick as the sha384 case above: "xxxxxxx" is 7 chars like "sha256:",
        // followed by 64 valid hex chars -- a prefix check that's skipped rather than compared would
        // slice this into a passing 64-hex-char result, exposing the missing comparison.
        Eq(GitHubClient.ParseSha256Digest($"xxxxxxx{sampleHex}"), null,
           "digest: an entirely non-algorithm-shaped 7-char prefix is rejected, not silently accepted");

        Eq(GitHubClient.ClassifyDigest(null, sampleHex), DigestOutcome.Unverified,
           "digest-outcome: no published digest at all classifies as Unverified, never Mismatch");
        Eq(GitHubClient.ClassifyDigest(sampleHex, sampleHex), DigestOutcome.Verified,
           "digest-outcome: an identical actual hash classifies as Verified");
        Eq(GitHubClient.ClassifyDigest(sampleHex, sampleHex.ToUpperInvariant()), DigestOutcome.Verified,
           "digest-outcome: comparison is case-insensitive (GitHub's digest and Convert.ToHexStringLower may differ in case)");
        Eq(GitHubClient.ClassifyDigest(sampleHex, new string('b', 64)), DigestOutcome.Mismatch,
           "digest-outcome: a differing actual hash classifies as Mismatch, the case DownloadAssetAsync must refuse on");

        // --- LogReader: BepInEx-Zeilenformat (Task-Brief) ---
        var le = LogReader.ParseLine("[Error  :   Hellebarde] NullReferenceException in AutoTasksTick");
        Eq(le?.Level, "Error", "log: level parsed");
        Eq(le?.Source, "Hellebarde", "log: source trimmed");
        Eq(le?.Message, "NullReferenceException in AutoTasksTick", "log: message parsed");
        Eq(LogReader.ParseLine("[Info   :BepInEx] loading")?.Level, "Info", "log: info line");
        Eq(LogReader.ParseLine("plain text without brackets"), null, "log: non-matching line ignored");
        Eq(LogReader.ParseLine("[Warning:  Buezer] slow node scan")?.Source, "Buezer", "log: warning line");

        // --- LogReader: Haertung ueber den Pflichttest hinaus ---
        Eq(LogReader.ParseLine("[Debug:X] d")?.Level, "Debug", "log: debug level recognised");
        Eq(LogReader.ParseLine("[Message:X] m")?.Level, "Message", "log: message level recognised");
        Eq(LogReader.ParseLine("[Fatal:X] f")?.Level, "Fatal", "log: fatal level recognised");
        // Kleinschreibung wird bewusst NICHT akzeptiert -- die Regex traegt keine IgnoreCase-Option,
        // ein echtes BepInEx-Log schreibt die Stufe immer exakt so gross wie oben.
        Eq(LogReader.ParseLine("[error:X] e"), null, "log: lowercase level is not recognised (case-sensitive by design)");
        Eq(LogReader.ParseLine("[Error:X]")?.Message, "", "log: an empty message after the closing bracket parses as an empty string, not null");

        // --- HealthCheck: Community-Patch-Konflikt (Spec §8, Pruefung 6) ---
        Check(HealthCheck.CommunityPatchConflict("[patches]\ngame_version = true\nuiscalehooks = false\n"),
              "conflict: game_version=true is a conflict");
        Check(HealthCheck.CommunityPatchConflict("[patches]\nuiscalehooks = true\n"),
              "conflict: uiscalehooks=true is a conflict");
        Check(!HealthCheck.CommunityPatchConflict("[patches]\ngame_version = false\nuiscalehooks = false\n"),
              "conflict: both false is fine");
        Check(!HealthCheck.CommunityPatchConflict("[graphics]\nloader_enabled = true\n"),
              "conflict: unrelated true keys are fine");
        Check(!HealthCheck.CommunityPatchConflict("# game_version = true\n"),
              "conflict: commented-out key is fine");

        // --- Redactor (Spec §9, Task-Brief) ---
        Eq(Redactor.RedactLine("ApiKey = 1234-abcd-secret"), "ApiKey = [REDACTED]", "redact: key assignment");
        Eq(Redactor.RedactLine("deepl_api_key: abc123"), "deepl_api_key: [REDACTED]", "redact: colon separator");
        Eq(Redactor.RedactLine("Password=hunter2"), "Password=[REDACTED]", "redact: password");
        Eq(Redactor.RedactLine("Authorization: Bearer ey.J9.abc"), "Authorization: [REDACTED]",
           "redact: authorization header line");

        // Release-Review: der einzige bisherige Assert oben passiert allein ueber die
        // "authorization"-Schluesselwortregel (SecretAssignment) -- er wuerde identisch bestehen,
        // gaebe es die BearerToken-Regel gar nicht, und beweist deshalb nichts ueber sie. Diese
        // beiden Faelle haben KEINE Schluessel:Wert-Form und laufen nur ueber BearerToken selbst:
        // ein alleinstehendes "Bearer x" (der Testfall aus der Aufgabenstellung) und ein
        // Bearer-Token mitten im Fliesstext, ganz ohne "Authorization:"-Praefix.
        Eq(Redactor.RedactLine("Bearer x"), "Bearer [REDACTED]",
           "redact: standalone 'Bearer x' outside any key:value form");
        Eq(Redactor.RedactLine("sending Bearer abc123 to the api"), "sending Bearer [REDACTED] to the api",
           "redact: 'Bearer' token mid-sentence, outside any key:value form");
        Eq(Redactor.RedactLine("contact me at a.b+c@example.com now"),
           "contact me at [REDACTED-EMAIL] now", "redact: email");
        Eq(Redactor.RedactLine("user jd73d2aac9f4b81e5c6a7d8e9f01 logged in"),
           "user [REDACTED-ID] logged in", "redact: long alphanumeric id");
        Eq(Redactor.RedactLine("[Info :Hellebarde] attacking hostile level 40"),
           "[Info :Hellebarde] attacking hostile level 40", "redact: ordinary line untouched");
        Eq(Redactor.RedactLine("MaxLevel = 40"), "MaxLevel = 40", "redact: harmless assignment untouched");

        // --- Redactor: Haertung ueber die Aufgabenstellung hinaus ---
        // Die beiden brief-eigenen Testfaelle oben ("ApiKey = ...", "deepl_api_key: ...") wuerden mit
        // einem unveraenderten \b(?:key|...)\b bereits FEHLSCHLAGEN (\w schliesst den Unterstrich
        // ein, \b ignoriert Gross-/Kleinschreibung) -- die folgenden Faelle pruefen die Korrektur
        // zusaetzlich mit weiteren zusammengesetzten Bezeichnern und mit gezielten Gegenproben, damit
        // die Grenze nicht zu einer blossen Teilstring-Suche verkommt.
        Eq(Redactor.RedactLine("Passwort: geheim123"), "Passwort: [REDACTED]",
           "redact: German 'Passwort' alias is recognised");
        Eq(Redactor.RedactLine("Secret: mysecretvalue"), "Secret: [REDACTED]", "redact: standalone 'Secret' key");
        Eq(Redactor.RedactLine("DeeplApiKey = sk-abcdef1234567890"), "DeeplApiKey = [REDACTED]",
           "redact: a three-segment PascalCase compound ('Deepl' + 'Api' + 'Key') is still caught");
        Eq(Redactor.RedactLine("AuthorName = John Doe"), "AuthorName = John Doe",
           "redact: 'AuthorName' is NOT mistaken for an auth secret -- no case-transition boundary between 'Auth' and 'or'");
        Eq(Redactor.RedactLine("MaxCapital = 5000"), "MaxCapital = 5000",
           "redact: 'Capital' containing the substring 'api' is not falsely triggered");
        // Bewusst grosszuegig (s. Klassenkommentar): ein zusammengesetzter Bezeichner, der zufaellig
        // 'key' als eigenes Wortsegment enthaelt, wird trotzdem redigiert, auch wenn es hier kein
        // echtes Geheimnis ist -- der Preis fuer die harten Faelle oben ist ein paar harmlose
        // Übertreffer, was laut Aufgabenstellung besser ist als eine uebersehene ID.
        Eq(Redactor.RedactLine("HotKey = F5"), "HotKey = [REDACTED]",
           "redact: a compound word containing 'key' as its own segment is generously redacted (accepted over-masking)");
        // GUID-Haertung: LongId allein saehe eine bindestrichgetrennte Spieler-GUID NICHT, weil die
        // Bindestriche jede zusammenhaengende Folge unter die 24-Zeichen-Schwelle teilen.
        Eq(Redactor.RedactLine("player 550e8400-e29b-41d4-a716-446655440000 connected"),
           "player [REDACTED-ID] connected", "redact: a hyphenated GUID-style player id is redacted");
        // Kehrprobe fuer dieselbe GUID-Ergaenzung steht als deliberate-break-Nachweis im Taskbericht
        // (GuidLike() vorruebergehend entfernt, Assert direkt darueber schlaegt dann fehl).
        Eq(Redactor.RedactText("ApiKey = supersecret\nMaxLevel = 40\n"),
           "ApiKey = [REDACTED]" + Environment.NewLine + "MaxLevel = 40" + Environment.NewLine,
           "redact: RedactText redacts a secret line and leaves a harmless line untouched, line by line");

        // --- Redactor: Fix-Runde 1, C1 (kritisch) -- Ausnahme-/Stacktrace-Zeilen duerfen nicht
        // hinter der Schluessel-Zuweisungs-Erkennung verschwinden. Vor der Korrektur schluckte das
        // unbeschraenkte "[^=:\r\n]*" nach dem Schluesselwort alles bis zum NAECHSTEN ':' oder '='
        // irgendwo spaeter in der Zeile -- ein Laufwerksbuchstabe in einem Pfad oder der zweite
        // Doppelpunkt einer Exception.ToString()-Zeile genuegte. Diese drei Zeilen pinnen genau das
        // Gegenteil: der Text muss VOLLSTAENDIG ueberleben, nicht nur teilweise.
        const string keyNotFound =
            "System.Collections.Generic.KeyNotFoundException: The given key was not present in the dictionary.";
        Eq(Redactor.RedactLine(keyNotFound), keyNotFound,
           "redact: a KeyNotFoundException message is not swallowed by the key-assignment pattern");
        const string authStackFrame =
            @"   at UniversalTranslator.AuthTokenManager.Refresh() in C:\Users\Foo\AuthTokenManager.cs:line 42";
        Eq(Redactor.RedactLine(authStackFrame), authStackFrame,
           "redact: a stack frame with a drive-letter path ('Auth' in the type name) survives untouched");
        const string tokenStackFrame =
            @"   at MyMod.TokenValidator.Validate() in C:\Users\Foo\TokenValidator.cs:line 10";
        Eq(Redactor.RedactLine(tokenStackFrame), tokenStackFrame,
           "redact: a stack frame with 'Token' in a method/class name survives untouched");

        // --- Redactor: Fix-Runde 1, I3 -- weitere Schluesselwoerter und Bezeichner ganz ohne
        // Gross-/Kleinschreibungssignal. Die Klasse pruefte sich selbst als "bewusst grosszuegig",
        // aber "pw"/"passwd" fehlten in der Liste, und ein rein durchgehend klein- oder
        // grossgeschriebenes "apikey"/"APIKEY" hatte gar keinen klein-zu-Gross-Uebergang, an dem die
        // fruehere Grenze haette greifen koennen. ---
        Eq(Redactor.RedactLine("pw = hunter2"), "pw = [REDACTED]", "redact: the short 'pw' alias for password");
        Eq(Redactor.RedactLine("passwd = hunter2"), "passwd = [REDACTED]", "redact: the 'passwd' alias for password");
        Eq(Redactor.RedactLine("APIKEY = sekret1"), "APIKEY = [REDACTED]",
           "redact: an all-uppercase compound with no case transition ('APIKEY') still redacts");
        Eq(Redactor.RedactLine("apikey = hunter2value"), "apikey = [REDACTED]",
           "redact: an all-lowercase compound with no case transition ('apikey') still redacts");

        // --- Redactor: Fix-Runde 1, I4 -- opake Token mit internen Unterstrichen/Bindestrichen ---
        // Wie beim urspruenglichen \b-Fehler blockierte der Unterstrich vor dem eigentlichen
        // Zufallsteil eines Tokens die \b-Grenze von LongId komplett -- reale Formate (Stripe,
        // GitHub) haben genau diese Form: ein kurzes Praefix, EIN Unterstrich, dann ein langer
        // zusammenhaengender Zufallsblock ganz ohne eigene Zuweisungsform im Log.
        Eq(Redactor.RedactLine("key sk_" + "live_4eC39HqLyjWDarjtT1zdp7dc leaked"),
           "key sk_" + "live_[REDACTED-ID] leaked", "redact: a Stripe-style key with an underscore prefix is redacted");
        Eq(Redactor.RedactLine("token ghp" + "_1234567890abcdefghijklmnopqrstuvwxyz leaked"),
           "token ghp" + "_[REDACTED-ID] leaked", "redact: a GitHub-style token with an underscore prefix is redacted");
        // Gegenprobe (ausdruecklich verlangt): ein gewoehnlicher, mit Bindestrichen geschriebener
        // Ausdruck darf davon NICHT erfasst werden -- jedes einzelne Segment ist zu kurz und enthaelt
        // keine Ziffer, die 24-Zeichen-Schwelle mit Ziffernpflicht bleibt die schuetzende Grenze.
        const string ordinaryHyphenated = "ordinary-hyphenated-word-list should stay clean";
        Eq(Redactor.RedactLine(ordinaryHyphenated), ordinaryHyphenated,
           "redact: an ordinary hyphenated phrase with no digits is not mistaken for an opaque token");
        const string versionedPhrase = "windows-10-update-notes-panel should stay clean too";
        Eq(Redactor.RedactLine(versionedPhrase), versionedPhrase,
           "redact: a hyphenated phrase containing a short digit segment ('10') is not mistaken for an opaque token");

        // --- Redactor: Fix-Runde 2 -- die Fix-Runde-1-Korrektur fuer C1 (Leerzeichen/Tabs-only
        // zwischen Schluesselwort und Trenner) machte SecretAssignment blind fuer JSON-Schreibweisen:
        // ein schliessendes '"' zwischen dem Schluesselnamen und dem Doppelpunkt passte nicht mehr in
        // die Luecke. Real relevant: genau der Mod, fuer den diese Klasse geschrieben wurde
        // (UniversalTranslator), protokolliert JSON-Anfrage-/Antwortkoerper bei aktiviertem Debug-
        // Logging woertlich in LogOutput.log/Player.log -- ein durchaus realistischer Weg fuer einen
        // DeepL-Schluessel ins Supportpaket. Alle drei Schreibweisen gepinnt: mit Leerzeichen nach
        // dem Doppelpunkt (das Beispiel aus dem Fund), kompakt ohne jedes Leerzeichen, und mit
        // Leerzeichen auf BEIDEN Seiten des Doppelpunkts.
        Eq(Redactor.RedactLine("\"apiKey\": \"secretvalue123456\""), "\"apiKey\": [REDACTED]",
           "redact: JSON key/value with a space after the colon");
        Eq(Redactor.RedactLine("{\"apiKey\":\"secretvalue123456\"}"), "{\"apiKey\":[REDACTED]",
           "redact: compact JSON key/value with no spaces at all");
        Eq(Redactor.RedactLine("\"apiKey\" : \"secretvalue123456\""), "\"apiKey\" : [REDACTED]",
           "redact: JSON key/value with a space on both sides of the colon");

        // --- Redactor: Fix-Runde 2 -- "Pwd"/"PWD" folded in alongside the JSON fix (same class as
        // Fix-Runde 1's "pw"/"passwd", one more keyword, cheap to add). ---
        Eq(Redactor.RedactLine("Pwd = hunter2"), "Pwd = [REDACTED]", "redact: the 'Pwd' alias for password");
        Eq(Redactor.RedactLine("PWD = hunter2"), "PWD = [REDACTED]", "redact: the all-caps 'PWD' alias for password");

        // --- Redactor: Fix-Runde 2 -- re-confirms the exact Fix-Runde-1 C1 survival examples are
        // NOT reopened by the quote-gap addition. A letter (as in "KeyNotFoundException") still does
        // not fit the gap, only an optional quote plus whitespace does. ---
        Eq(Redactor.RedactLine(keyNotFound), keyNotFound,
           "redact: fix round 2 does not reopen C1 -- KeyNotFoundException still survives untouched");
        Eq(Redactor.RedactLine(authStackFrame), authStackFrame,
           "redact: fix round 2 does not reopen C1 -- the drive-letter stack frame still survives untouched");

        // --- MainForm.DecideNativeModAction (Task 12/13 report) -- reine Zustandsuebergangs-Logik
        // fuer den nativen Mod-Eintrag, extrahiert aus AdoptFromDisk, damit sie ohne Dateisystem
        // pruefbar ist. Pinnt die im Taskauftrag geforderte Korrektur: der urspruengliche Drei-
        // Zweige-Entwurf hatte einen unerreichbaren dritten Zweig ("!nativePresent &&
        // nativeEntry is not null" kam nie dran, weil der zweite Zweig "nativeEntry is not null"
        // schon jeden Fall mit vorhandenem Eintrag abfing) -- ein geloeschtes version.dll UND
        // version.dll_ liess den Eintrag deshalb fuer immer in state.Mods stehen. Alle vier Faelle
        // der Wahrheitstabelle werden hier einzeln gepinnt.
        Eq(MainForm.DecideNativeModAction(nativePresent: false, hasExistingEntry: false),
           MainForm.NativeModAction.None,
           "native-action: nothing present, no entry -- nothing to do");
        Eq(MainForm.DecideNativeModAction(nativePresent: false, hasExistingEntry: true),
           MainForm.NativeModAction.Remove,
           "native-action: neither version.dll nor version.dll_ present, but an entry still exists -- remove it (the fixed unreachable-branch case)");
        Eq(MainForm.DecideNativeModAction(nativePresent: true, hasExistingEntry: false),
           MainForm.NativeModAction.Add,
           "native-action: version.dll or version.dll_ present, no entry yet -- add one");
        Eq(MainForm.DecideNativeModAction(nativePresent: true, hasExistingEntry: true),
           MainForm.NativeModAction.UpdateEnabled,
           "native-action: present and an entry already exists -- just refresh Enabled");

        // --- MainForm.DecidePluginReconcileAction (Fix-Runde 1, I6) -- reine Zustandsuebergangs-
        // Logik fuer den Abgleich eines bereits bekannten Plugin-Mods mit der Platte, getrennt von
        // den beiden File.Exists-Aufrufen (ReconcilePluginFiles), damit sie ohne Dateisystem
        // pruefbar ist. Alle vier Faelle der Wahrheitstabelle einzeln gepinnt, inklusive des seltenen
        // Falls, dass die Datei an BEIDEN Orten gleichzeitig liegt (z. B. von Hand kopiert) -- dort
        // gewinnt bewusst der aktivierte Ort, statt den Fall unbehandelt zu lassen.
        Eq(MainForm.DecidePluginReconcileAction(existsEnabled: true, existsDisabled: false),
           MainForm.PluginReconcileAction.SetEnabled,
           "plugin-reconcile: file only at the enabled location -- Enabled must become true");
        Eq(MainForm.DecidePluginReconcileAction(existsEnabled: false, existsDisabled: true),
           MainForm.PluginReconcileAction.SetDisabled,
           "plugin-reconcile: file only at the disabled location -- Enabled must become false");
        Eq(MainForm.DecidePluginReconcileAction(existsEnabled: false, existsDisabled: false),
           MainForm.PluginReconcileAction.Missing,
           "plugin-reconcile: file at neither location -- surfaced as missing, not silently left enabled");
        Eq(MainForm.DecidePluginReconcileAction(existsEnabled: true, existsDisabled: true),
           MainForm.PluginReconcileAction.SetEnabled,
           "plugin-reconcile: file at both locations at once -- the enabled location wins the tie");

        // --- Dialogs.CanonicalizePluginTarget (Fix-Runde 2, Punkt 3) -- reine Umschreibung eines
        // frisch installierten Zielpfads, getrennt von Installer.Apply/ZipFile-I/O, damit sie ohne
        // Dateisystem pruefbar ist. Pinnt die Korrektur: C1's Symptom (ein Pfad unter
        // BepInEx\plugins-disabled wird woertlich als "aktivierter" Ort gespeichert) lebte nicht nur
        // in der Adoption, sondern auch auf dem Install-Pfad, weil PackageMapper einen Archiveintrag
        // wie "MyMod/BepInEx/plugins-disabled/FakeA.dll" klaglos auf genau dieses Ziel abbildet.
        Eq(Dialogs.CanonicalizePluginTarget(@"BepInEx\plugins-disabled\FakeA.dll"),
           @"BepInEx\plugins\FakeA.dll",
           "canonicalize-target: a target under plugins-disabled is rewritten to the canonical plugins location");
        Eq(Dialogs.CanonicalizePluginTarget(@"BepInEx\plugins-disabled\Sub\FakeA.dll"),
           @"BepInEx\plugins\Sub\FakeA.dll",
           "canonicalize-target: the mirrored subfolder structure is preserved, not flattened");
        Eq(Dialogs.CanonicalizePluginTarget(@"BepInEx\plugins\FakeA.dll"),
           @"BepInEx\plugins\FakeA.dll",
           "canonicalize-target: a target already under plugins passes through unchanged");
        Eq(Dialogs.CanonicalizePluginTarget("version.dll"),
           "version.dll",
           "canonicalize-target: a game-root target (version.dll) is untouched -- it is not under plugins at all");
        Eq(Dialogs.CanonicalizePluginTarget(@"BepInEx\config\FakeA.cfg"),
           @"BepInEx\config\FakeA.cfg",
           "canonicalize-target: a config target is untouched -- it is not under plugins at all");

        FileSystemChecks();

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ===================================================================================
    // Dateisystem-gestuetzte Pruefungen fuer den Installer -- den einzigen Teil der App, der
    // den Spielordner des Nutzers veraendert.
    //
    // Die reinen Buchfuehrungs-Asserts weiter oben erreichen die Loesch-, Verschiebe- und
    // Rollback-Zweige NIE: jeder Spielordner dort ist bewusst ein nicht existierender Pfad,
    // File.Exists() liefert ueberall false, und der interessante Code laeuft gar nicht erst an.
    // Rund zwanzig Befunde aus fuenf Ueberarbeitungsrunden wurden ausschliesslich von
    // Wegwerf-Testprogrammen gefunden, die es allesamt nicht mehr gibt -- ohne die folgenden
    // Pruefungen wuerde ihre Rueckkehr von nichts bemerkt. Deshalb hier: ein echter
    // Wegwerf-Spielordner unter Path.GetTempPath(), der echte Code darauf, und im finally alles
    // wieder weg (einschliesslich der Sicherungen, die der Installer in %LOCALAPPDATA% anlegt).
    // ===================================================================================
    private static void FileSystemChecks()
    {
        string root;
        try
        {
            root = Path.Combine(Path.GetTempPath(), $"stfcmm-selftest-fs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Umgebung ohne beschreibbares Temp-Verzeichnis: der Rest der Suite laeuft weiter,
            // statt hier mit einer ungefangenen Ausnahme abzubrechen.
            Console.WriteLine("filesystem checks skipped: no writable temp directory");
            return;
        }

        var backupsBefore = Entries(AppPaths.BackupDir);
        var configBackupsBefore = Entries(AppPaths.ConfigBackupDir);
        var backupDirExisted = Directory.Exists(AppPaths.BackupDir);
        var configBackupDirExisted = Directory.Exists(AppPaths.ConfigBackupDir);

        try
        {
            Guarded("config is moved, never deleted", () => CheckConfigIsMovedNeverDeleted(root));
            Guarded("Remove respects plugins-disabled ownership", () => CheckRemoveRespectsDisabledPathOwnership(root));
            Guarded("a shared library survives a toggle", () => CheckSharedLibrarySurvivesToggle(root));
            Guarded("a nested plugin round-trips", () => CheckNestedPluginRoundTrip(root));
            Guarded("Apply rejects duplicates and rolls back", () => CheckApplyRejectsDuplicatesAndRollsBack(root));
            Guarded("escaping paths are refused", () => CheckEscapingPathsAreRefused(root));

            // --- Task 8: BepInExRuntime ---
            Guarded("SafeExtract rejects the escaping entry from the task brief", () => CheckSafeExtractRejectsEscapingEntries(root));
            Guarded("SafeExtract hardening beyond the brief", () => CheckSafeExtractHardening(root));
            Guarded("SafeExtract zip-bomb caps (count, per-entry, total)", () => CheckSafeExtractZipBombCaps(root));
            Guarded("SafeExtract happy path preserves structure", () => CheckSafeExtractHappyPath(root));
            Guarded("SafeExtract rejects escape through a pre-existing junction (validation-time, nested entry)", () => CheckSafeExtractRejectsReparsePointEscape(root));
            Guarded("SafeExtract severs a pre-existing file symlink at the target itself", () => CheckSafeExtractSeversPreexistingFileSymlink(root));
            Guarded("SafeExtract severs a pre-existing hard link at the target itself", () => CheckSafeExtractSeversPreexistingHardLink(root));
            Guarded("SafeExtract rejects duplicate targets and file/directory collisions", () => CheckSafeExtractRejectsTargetCollisions(root));
            Guarded("SafeExtract rejects reserved DOS device names", () => CheckSafeExtractRejectsDosDeviceNames(root));
            Guarded("SafeExtract rejects an over-long path segment cleanly", () => CheckSafeExtractRejectsOverlongSegment(root));
            Guarded("SafeExtract requires the destination to already exist", () => CheckSafeExtractRequiresExistingDestination(root));
            Guarded("EnsureRuntimeSkeleton creates plugins and patchers", () => CheckEnsureRuntimeSkeleton(root));
            Guarded("SafeExtract keeps identity files hidden until the whole archive succeeds", () => CheckSafeExtractIdentityFilesStayHiddenUntilComplete(root));
            Guarded("Detect hardening for partial and corrupt installs", () => CheckDetectHardening(root));

            // --- Task 9: LogReader / HealthCheck ---
            Guarded("ReadTail returns nothing for a locked log file instead of throwing", () => CheckLogReaderReadTailLockedFile(root));
            Guarded("ReadTail caps on the last raw lines before filtering by level", () => CheckLogReaderReadTailLineCap(root));
            Guarded("HealthCheck reports a single finding for a missing game folder and does not throw", () => CheckHealthCheckMissingGameFolder(root));
            Guarded("HealthCheck reports BepInEx missing on an otherwise valid, empty install", () => CheckHealthCheckBepInExMissing(root));
            Guarded("HealthCheck reports a stale client build", () => CheckHealthCheckStaleClientBuild(root));
            Guarded("HealthCheck does not throw when a mod's dll was deleted behind its back", () => CheckHealthCheckMissingModDllDoesNotThrow(root));
            Guarded("HealthCheck reports the community patch conflict from a real file on disk", () => CheckHealthCheckCommunityPatchConflictOnDisk(root));
            Guarded("HealthCheck reports errors found in a real game log", () => CheckHealthCheckGameLogErrors(root));
            Guarded("HealthCheck reports orphaned files in the plugins folder", () => CheckHealthCheckOrphanFiles(root));
            Guarded("HealthCheck never throws while the game log is locked by another handle", () => CheckHealthCheckDoesNotThrowWithLockedLog(root));

            // --- Task 10: Redactor / SupportBundle ---
            Guarded("PlannedContents includes .cfg files and excludes a huge .json in the same folder", () => CheckSupportBundlePlannedContentsCfgOnly(root));
            Guarded("PlannedContents does not throw when the config folder does not exist", () => CheckSupportBundlePlannedContentsMissingConfigFolder(root));
            Guarded("Create redacts secrets, excludes the huge json, truncates the oversized log, and records what it dropped", () => CheckSupportBundleCreateRedactsAndRespectsCap(root));
            Guarded("Create enforces the total budget across multiple collected files", () => CheckSupportBundleTotalBudgetExhaustion(root));
            Guarded("Create does not throw when a source file is locked, and records it as unreadable", () => CheckSupportBundleCreateLockedSourceFile(root));
            Guarded("Create succeeds with no config folder and no BepInEx files present at all", () => CheckSupportBundleCreateOnBareGameFolder(root));

            // --- Fix-Runde 1, C2: IOException allein reicht nicht -- UnauthorizedAccessException
            // (ACL-verweigert, Virenscanner-Quarantaene) erbt NICHT davon. Pruefung mit einer ECHT
            // per ACL gesperrten Datei, nicht nur mit einem Freigabe-Konflikt (den deckten die
            // Sperr-Pruefungen oben bereits ab, aber der ist eine IOException und haette diese
            // Luecke nie aufgedeckt).
            Guarded("LogReader.ReadTail does not throw on a genuinely ACL-denied file", () => CheckLogReaderReadTailAclDenied(root));
            Guarded("HealthCheck.Run does not throw when the community-patch toml is ACL-denied", () => CheckHealthCheckAclDeniedCommunityPatchToml(root));
        }
        finally
        {
            TryDelete(root);
            DeleteNewEntries(AppPaths.BackupDir, backupsBefore);
            DeleteNewEntries(AppPaths.ConfigBackupDir, configBackupsBefore);

            // Auch die Ordner selbst wieder abraeumen, falls erst dieser Lauf sie angelegt hat --
            // ein Selbsttest soll in %LOCALAPPDATA% nichts zuruecklassen, auch nichts Leeres.
            if (!backupDirExisted) TryDelete(AppPaths.BackupDir);
            if (!configBackupDirExisted) TryDelete(AppPaths.ConfigBackupDir);
        }
    }

    /// <summary>Fuehrt eine Dateisystem-Pruefung aus und verwandelt eine unerwartete Ausnahme in
    /// einen normalen Fehlschlag. Ohne das reisst ein Regress, der WIRFT statt falsch zu antworten,
    /// den kompletten Selbsttest ab -- ohne Zusammenfassung, ohne Hinweis, welche Pruefung es war
    /// (beim Gegentest gegen aeltere Installer-Staende genau so passiert). Bewusst ungefiltert:
    /// jede Ausnahme aus dem Installer ist hier ein Befund, kein Betriebsunfall.</summary>
    private static void Guarded(string what, Action check)
    {
        try { check(); }
        catch (Exception e)
        {
            Check(false, $"fs: {what} — threw {e.GetType().Name} instead of completing: {e.Message}");
        }
    }

    private static string[] Entries(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Entfernt genau das, was dieser Lauf in einem der Anwendungsordner erzeugt hat --
    /// alles Aeltere gehoert dem Nutzer und bleibt unangetastet.</summary>
    private static void DeleteNewEntries(string dir, string[] before)
    {
        foreach (var entry in Entries(dir).Except(before, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Put(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Exception? Threw(Action action)
    {
        try { action(); return null; }
        catch (Exception e) { return e; }
    }

    /// <summary>Eine Config unterhalb von BepInEx\config wird verschoben, nie geloescht -- auch
    /// dann, wenn sie (weil das Archiv sie mitgebracht hat) in mod.Files steht, und auch in der
    /// Schreibweise mit "."-Segment.</summary>
    private static void CheckConfigIsMovedNeverDeleted(string root)
    {
        var gameRoot = Path.Combine(root, "cfg");
        var dll = Put(Path.Combine(gameRoot, @"BepInEx\plugins\Cfg.dll"), "PLUGIN");
        var cfg = Put(Path.Combine(gameRoot, @"BepInEx\config\CfgMod.cfg"), "USER_TUNED");
        var before = Entries(AppPaths.ConfigBackupDir);

        var state = new AppState();
        var mod = new ModEntry
        {
            Id = "CfgMod", Name = "Cfg", Version = "1.0",
            Files =
            {
                new InstalledFile { Path = @"BepInEx\plugins\Cfg.dll", Sha256 = "d" },
                new InstalledFile { Path = @"BepInEx\.\config\CfgMod.cfg", Sha256 = "c" }
            }
        };
        state.Mods.Add(mod);
        Installer.Remove(state, new GameInstall(gameRoot), mod);

        Check(!File.Exists(dll), "fs: Remove deletes the mod's plugin dll");
        Check(!File.Exists(cfg), "fs: Remove moves the bundled config out of BepInEx\\config");

        var added = Entries(AppPaths.ConfigBackupDir).Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
        Eq(added.Length, 1, "fs: exactly one config backup was written");
        Check(added.Length == 1 && File.ReadAllText(added[0]) == "USER_TUNED",
              "fs: the config backup holds the user's settings -- a config is never destroyed");
    }

    /// <summary>Der plugins-disabled-Ort ist ein eigenstaendig installierbares Ziel, kein
    /// Zweitname: er darf nur abgeraeumt werden, wenn ihn niemand sonst besitzt.</summary>
    private static void CheckRemoveRespectsDisabledPathOwnership(string root)
    {
        var gameRoot = Path.Combine(root, "own");
        var game = new GameInstall(gameRoot);
        var aFile = Put(Path.Combine(gameRoot, @"BepInEx\plugins\Core.dll"), "A_CONTENT");
        var bFile = Put(Path.Combine(gameRoot, @"BepInEx\plugins-disabled\Core.dll"), "B_CONTENT");

        var state = new AppState();
        var modA = new ModEntry
        {
            Id = "ownA", Name = "A", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Core.dll", Sha256 = "a" } }
        };
        var modB = new ModEntry
        {
            Id = "ownB", Name = "B", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins-disabled\Core.dll", Sha256 = "b" } }
        };
        state.Mods.Add(modA);
        state.Mods.Add(modB);

        Installer.Remove(state, game, modA);
        Check(!File.Exists(aFile), "fs: Remove deletes the file the removed mod owns");
        Check(File.Exists(bFile) && File.ReadAllText(bFile) == "B_CONTENT",
              "fs: Remove leaves a plugins-disabled file another installed mod owns untouched");

        Installer.Remove(state, game, modB);
        Check(!File.Exists(bFile), "fs: Remove does delete that same file once its own owner goes");

        // Die Kehrseite derselben Regel, und ein eigener Codepfad: hier ist der plugins-disabled-Ort
        // NICHT der kanonische Pfad des Mods, sondern nur die abgeleitete Ablage eines deaktivierten
        // Mods. Besitzt ihn niemand sonst, MUSS er mitgehen -- sonst bleibt eine Datei liegen, die
        // BepInEx weiter laedt, waehrend die Buchfuehrung den Mod fuer entfernt haelt.
        var parked = Put(Path.Combine(gameRoot, @"BepInEx\plugins-disabled\Parked.dll"), "PARKED");
        var parkedState = new AppState();
        var parkedMod = new ModEntry
        {
            Id = "parked", Name = "P", Version = "1.0", Enabled = false,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Parked.dll", Sha256 = "p" } }
        };
        parkedState.Mods.Add(parkedMod);

        Installer.Remove(parkedState, game, parkedMod);
        Check(!File.Exists(parked),
              "fs: Remove deletes a disabled mod's parked file at its derived location when nobody else owns it");
    }

    /// <summary>Eine geteilte Bibliothek darf nicht unter einem anderen, noch aktivierten Mod
    /// weggeschoben werden.</summary>
    private static void CheckSharedLibrarySurvivesToggle(string root)
    {
        var gameRoot = Path.Combine(root, "shared");
        var game = new GameInstall(gameRoot);
        var json = Put(Path.Combine(gameRoot, @"BepInEx\plugins\Json.dll"), "JSON_LIB");
        var ownA = Put(Path.Combine(gameRoot, @"BepInEx\plugins\OwnA.dll"), "A");

        var state = new AppState();
        var modA = new ModEntry
        {
            Id = "shA", Name = "A", Version = "1.0", Enabled = true,
            Files =
            {
                new InstalledFile { Path = @"BepInEx\plugins\OwnA.dll", Sha256 = "a" },
                new InstalledFile { Path = @"BepInEx\plugins\Json.dll", Sha256 = "j" }
            }
        };
        var modB = new ModEntry
        {
            Id = "shB", Name = "B", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Json.dll", Sha256 = "j" } }
        };
        state.Mods.Add(modA);
        state.Mods.Add(modB);
        Installer.RegisterShared(state, @"BepInEx\plugins\Json.dll", "j", "13.0.3", "shA");
        Installer.RegisterShared(state, @"BepInEx\plugins\Json.dll", "j", "13.0.3", "shB");

        Installer.SetEnabled(state, game, modA, false);
        Check(!File.Exists(ownA), "fs: SetEnabled moves the mod's own dll out of plugins");
        Check(File.Exists(json),
              "fs: SetEnabled leaves a library another ENABLED mod provides where it is");

        Installer.SetEnabled(state, game, modB, false);
        Check(!File.Exists(json), "fs: once no enabled provider remains, the shared library moves too");
    }

    /// <summary>Ein Plugin in einem Unterordner kehrt an genau seinen kanonischen Ort zurueck --
    /// und wird beim naechsten Deaktivieren dort auch wiedergefunden.</summary>
    private static void CheckNestedPluginRoundTrip(string root)
    {
        var gameRoot = Path.Combine(root, "nested");
        var game = new GameInstall(gameRoot);
        var canonical = Put(Path.Combine(gameRoot, @"BepInEx\plugins\MyMod\Core.dll"), "NESTED_CONTENT");

        var state = new AppState();
        var mod = new ModEntry
        {
            Id = "nested", Name = "N", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\MyMod\Core.dll", Sha256 = "n" } }
        };
        state.Mods.Add(mod);

        Installer.SetEnabled(state, game, mod, false);
        Check(File.Exists(Path.Combine(gameRoot, @"BepInEx\plugins-disabled\MyMod\Core.dll")),
              "fs: disabling mirrors the plugin's subfolder under plugins-disabled");

        Installer.SetEnabled(state, game, mod, true);
        Check(File.Exists(canonical) && File.ReadAllText(canonical) == "NESTED_CONTENT",
              "fs: enabling returns the plugin to its canonical nested path with its content intact");

        Installer.SetEnabled(state, game, mod, false);
        Check(!File.Exists(canonical), "fs: a second disable still finds the file at its derived location");
    }

    /// <summary>Apply liefert nie halb: doppelte Ziele werden vorab abgelehnt, und ein spaeter
    /// scheiternder Schritt macht alles Vorherige rueckgaengig.</summary>
    private static void CheckApplyRejectsDuplicatesAndRollsBack(string root)
    {
        var gameRoot = Path.Combine(root, "apply");
        var srcA = Put(Path.Combine(root, "src", "a.dll"), "A");
        var srcB = Put(Path.Combine(root, "src", "b.dll"), "B");

        var duplicate = Threw(() => Installer.Apply(gameRoot, new (string, string)[]
        {
            (srcA, @"BepInEx\plugins\Dup.dll"),
            (srcB, "BepInEx/plugins/Dup.dll")
        }));
        Check(duplicate is ArgumentException, "fs: Apply rejects two ops writing the same target");
        Check(!File.Exists(Path.Combine(gameRoot, @"BepInEx\plugins\Dup.dll")),
              "fs: the rejected op list wrote nothing at all");

        var existing = Put(Path.Combine(gameRoot, "Existing.dll"), "ORIGINAL");
        var missing = Path.Combine(root, "src", $"absent-{Guid.NewGuid():N}.dll");
        var rolledBack = Threw(() => Installer.Apply(gameRoot, new (string, string)[]
        {
            (srcA, "Existing.dll"),
            (missing, @"BepInEx\plugins\New.dll")
        }));
        Check(rolledBack is not null, "fs: Apply throws when a later op cannot be carried out");
        Check(File.ReadAllText(existing) == "ORIGINAL",
              "fs: rollback restored the overwritten file to its original content");
        Check(!File.Exists(Path.Combine(gameRoot, @"BepInEx\plugins\New.dll")),
              "fs: rollback removed the file that had just been written");
    }

    /// <summary>Ein Pfad, der den Spielordner verlaesst, wird von Apply, SetEnabled und Remove
    /// gleichermassen verweigert -- keiner von ihnen fasst die Datei draussen an.</summary>
    private static void CheckEscapingPathsAreRefused(string root)
    {
        var gameRoot = Path.Combine(root, "escape");
        Directory.CreateDirectory(gameRoot);
        var game = new GameInstall(gameRoot);
        var victim = Put(Path.Combine(root, "outside-victim.dll"), "OUTSIDE");
        var escaping = Path.GetRelativePath(gameRoot, victim); // "..\outside-victim.dll"
        var src = Put(Path.Combine(root, "src", "escape-src.dll"), "NEW");

        Check(Threw(() => Installer.Apply(gameRoot, new (string, string)[] { (src, escaping) })) is not null,
              "fs: Apply refuses a target that escapes the game folder");

        var state = new AppState();
        var mod = new ModEntry
        {
            Id = "escaper", Name = "E", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = escaping, Sha256 = "e" } }
        };
        state.Mods.Add(mod);

        Threw(() => Installer.SetEnabled(state, game, mod, false));
        Check(File.Exists(victim), "fs: SetEnabled never moves a file that lies outside the game folder");

        Installer.Remove(state, game, mod);
        Check(File.Exists(victim) && File.ReadAllText(victim) == "OUTSIDE",
              "fs: Remove never deletes a file that lies outside the game folder");
        Eq(state.Mods.Count, 0, "fs: Remove still completes despite the unusable entry");
    }

    // ===================================================================================
    // Task 8: BepInExRuntime -- SafeExtract entpackt ein von aussen bezogenes Archiv direkt in den
    // Spielordner, das ist eine Sicherheitsgrenze. Der Pflichttest aus dem Task-Brief (Schritt 1) ist
    // hier wortgleich in der Logik uebernommen, aber an die Konventionen dieses Abschnitts angepasst --
    // ein gemeinsamer Wegwerf-Spielordner statt eines zweiten, eigenen Temp-Mechanismus (s. Auftrag).
    // Die weiteren Pruefungen haerten SafeExtract ueber den Pflichttest hinaus; jede einzelne pinnt
    // einen konkreten Umgehungsversuch, s. Taskbericht fuer die Einordnung.
    // ===================================================================================

    private static string CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using (var zs = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zs.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
        }
        return path;
    }

    /// <summary>Task-Brief Schritt 1: ein Eintrag mit ".."-Traversal darf nicht ausserhalb des
    /// Zielordners schreiben.</summary>
    private static void CheckSafeExtractRejectsEscapingEntries(string root)
    {
        var dir = Path.Combine(root, "escape-brief");
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "evil.zip");
        using (var zs = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zs.CreateEntry("../escaped.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("x");
        }

        var target = Path.Combine(dir, "dest");
        Directory.CreateDirectory(target);
        var threw = false;
        try { BepInExRuntime.SafeExtract(zipPath, target); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "extract: rejects entries that escape the destination");
        Check(!File.Exists(Path.Combine(dir, "escaped.txt")), "extract: nothing written outside destination");
    }

    /// <summary>Haertung ueber den Pflichttest hinaus: jede dieser Formen entkommt der reinen
    /// ".."-Praefixpruefung auf einem anderen Weg als woertlichem ".."-Traversal, oder gefaehrdet den
    /// Zielordner auf andere Weise. Ein abgelehntes Archiv darf, wie der Pflichttest es fuer den
    /// Escape-Fall verlangt, ueberhaupt nichts schreiben -- SafeExtracts Zwei-Runden-Entwurf (erst
    /// alle Eintraege pruefen, dann erst schreiben) macht das zu einer scharfen Zusage, nicht nur zu
    /// "der boesartige Eintrag selbst landet nicht auf der Platte".</summary>
    private static void CheckSafeExtractHardening(string root)
    {
        var dir = Path.Combine(root, "harden");
        Directory.CreateDirectory(dir);

        void RejectsSingleEntry(string entryName, string what)
        {
            var zipPath = Path.Combine(dir, $"z-{Guid.NewGuid():N}.zip");
            CreateZip(zipPath, (entryName, "x"));
            var target = Path.Combine(dir, $"dest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(target);

            var threw = false;
            try { BepInExRuntime.SafeExtract(zipPath, target); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, $"extract: {what}");
            Check(!Directory.EnumerateFileSystemEntries(target).Any(),
                  $"extract: {what} -- the rejected archive wrote nothing at all, not even inside the destination");
        }

        // Kritischer Fund (s. Taskbericht): Path.Combine(root, entryName) IGNORIERT root komplett,
        // wenn entryName selbst gerootet ist -- die reine Praefixpruefung des Pflichttests waere hier
        // wirkungslos, weil der kombinierte Pfad root nie enthaelt.
        RejectsSingleEntry(@"C:\escaped-drive.txt", "rejects a drive-rooted absolute path");
        RejectsSingleEntry("C:/escaped-drive-fwdslash.txt", "rejects a drive-rooted path with forward slashes");
        RejectsSingleEntry(@"\\server\share\evil.dll", "rejects a UNC-style path");
        RejectsSingleEntry("//server/share/evil.dll", "rejects a UNC-style path written with forward slashes");
        RejectsSingleEntry("readme.txt:hidden.dll", "rejects an alternate-data-stream suffix (colon smuggles a second file)");
        RejectsSingleEntry("MyDir/.../evil.txt", "rejects a dots-only path segment that is not literally \"..\"");
        RejectsSingleEntry(@"..\escaped-backslash.txt", "rejects traversal expressed with backslash separators only");
        RejectsSingleEntry("bad\0name.txt", "rejects a file name containing a character illegal on Windows (NUL byte)");
        RejectsSingleEntry("evil?.txt", "rejects a file name containing a character illegal on Windows (?)");
        RejectsSingleEntry("setup.exe", "rejects an executable, refusing the whole runtime archive");
    }

    /// <summary>Zip-Bomb-Schutz, Fix Round 1 (I2): die fruehere "Stufe 2" (tatsaechlich beim Kopieren
    /// gelesene Bytes gegen die Grenze zaehlen) war toter Code -- ZipArchiveEntry.Open() schneidet den
    /// Stream nachweislich auf die DEKLARIERTE Laenge ab (empirisch gemessen: ein Eintrag, der 10 Bytes
    /// deklariert und 4.000.000 tatsaechlich enthaelt, liefert beim Lesen exakt 10 Bytes), der Wurf
    /// konnte also nie feuern. Ersetzt durch drei unabhaengige, aus reinen Metadaten geprueften
    /// Grenzen -- jede hier einzeln mit einem Positiv- und einem Negativfall gepinnt, damit ein
    /// zukuenftiger Regress (z. B. eine Grenze, die versehentlich nie greift) tatsaechlich auffaellt.
    /// Fuer jede Grenze wurde waehrend der Entwicklung von Hand geprueft, dass das Entfernen bzw.
    /// Aufweichen der jeweiligen Pruefung genau diesen Assert zum Scheitern bringt (s. Taskbericht).</summary>
    private static void CheckSafeExtractZipBombCaps(string root)
    {
        var dir = Path.Combine(root, "bomb");
        Directory.CreateDirectory(dir);

        // Gesamtgroesse: ein einzelner 2-MB-Eintrag ueber einer kuenstlich auf 1 MB gesenkten
        // Gesamtgrenze wird abgelehnt, bevor er geschrieben wird.
        var totalZip = Path.Combine(dir, "total.zip");
        CreateZip(totalZip, ("payload.bin", new string('A', 2_000_000)));
        var totalTarget = Path.Combine(dir, "total-dest");
        Directory.CreateDirectory(totalTarget);
        var totalThrew = false;
        try { BepInExRuntime.SafeExtract(totalZip, totalTarget, maxTotalUncompressedBytes: 1_000_000, maxSingleEntryBytes: 50_000_000, maxEntryCount: 100); }
        catch (InvalidOperationException) { totalThrew = true; }
        Check(totalThrew, "extract: rejects an archive whose declared TOTAL uncompressed size exceeds the configured cap (zip-bomb guard)");
        Check(!File.Exists(Path.Combine(totalTarget, "payload.bin")),
              "extract: the total-size zip-bomb guard rejects before writing the oversized entry");

        // Pro Eintrag: derselbe 2-MB-Eintrag besteht eine grosszuegige Gesamtgrenze, wird aber ueber
        // einer kuenstlich auf 1 MB gesenkten PRO-EINTRAG-Grenze trotzdem abgelehnt -- eine reine
        // Gesamtsummen-Pruefung haette das nicht erkannt.
        var perEntryZip = Path.Combine(dir, "perentry.zip");
        CreateZip(perEntryZip, ("payload.bin", new string('A', 2_000_000)));
        var perEntryTarget = Path.Combine(dir, "perentry-dest");
        Directory.CreateDirectory(perEntryTarget);
        var perEntryThrew = false;
        try { BepInExRuntime.SafeExtract(perEntryZip, perEntryTarget, maxTotalUncompressedBytes: 50_000_000, maxSingleEntryBytes: 1_000_000, maxEntryCount: 100); }
        catch (InvalidOperationException) { perEntryThrew = true; }
        Check(perEntryThrew, "extract: rejects a SINGLE archive entry whose declared size exceeds the configured per-entry cap");
        Check(!File.Exists(Path.Combine(perEntryTarget, "payload.bin")),
              "extract: the per-entry zip-bomb guard rejects before writing the oversized entry");

        // Anzahl: 20 winzige Eintraege bestehen grosszuegige Groessengrenzen, werden aber ueber einer
        // kuenstlich auf 10 gesenkten Anzahl-Grenze trotzdem abgelehnt -- reproduziert im Kleinen, was
        // bei 20.000 winzigen Eintraegen 8,8 s ohne jede Grenze kostete (Fix Round 1, I2).
        var countZip = Path.Combine(dir, "count.zip");
        using (var zs = ZipFile.Open(countZip, ZipArchiveMode.Create))
            for (var i = 0; i < 20; i++)
            {
                var entry = zs.CreateEntry($"tiny{i}.bin");
                using var w = new StreamWriter(entry.Open());
                w.Write("x");
            }
        var countTarget = Path.Combine(dir, "count-dest");
        Directory.CreateDirectory(countTarget);
        var countThrew = false;
        try { BepInExRuntime.SafeExtract(countZip, countTarget, maxTotalUncompressedBytes: 50_000_000, maxSingleEntryBytes: 50_000_000, maxEntryCount: 10); }
        catch (InvalidOperationException) { countThrew = true; }
        Check(countThrew, "extract: rejects an archive with more entries than the configured count cap");
        Check(!Directory.EnumerateFileSystemEntries(countTarget).Any(),
              "extract: the entry-count zip-bomb guard rejects before writing any of the entries");

        // Positivfall, alle drei Grenzen zugleich: ein winziges Archiv bleibt unter allen drei
        // (kuenstlich gesenkten) Grenzen unbehelligt -- die Pruefungen sind echte Grenzen, keine
        // versteckte Ablehnung von allem.
        var smallZip = Path.Combine(dir, "small.zip");
        CreateZip(smallZip, ("payload.bin", "small enough"));
        var smallTarget = Path.Combine(dir, "small-dest");
        Directory.CreateDirectory(smallTarget);
        BepInExRuntime.SafeExtract(smallZip, smallTarget, maxTotalUncompressedBytes: 1_000_000, maxSingleEntryBytes: 1_000_000, maxEntryCount: 10);
        Check(File.Exists(Path.Combine(smallTarget, "payload.bin")), "extract: a small archive is unaffected by any of the three zip-bomb caps");
    }

    /// <summary>Positivfall: ein plausibles BepInEx-Release entpackt korrekt, mit Ordnerstruktur und
    /// Inhalt intakt -- die Haertung oben darf ein legitimes Archiv nicht mit abgeschossen haben.</summary>
    private static void CheckSafeExtractHappyPath(string root)
    {
        var dir = Path.Combine(root, "happy");
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "runtime.zip");
        CreateZip(zipPath,
            ("winhttp.dll", "LOADER"),
            ("doorstop_config.ini", "[General]\nenabled=true"),
            ("BepInEx/core/BepInEx.Core.dll", "CORE"));

        var target = Path.Combine(dir, "dest");
        Directory.CreateDirectory(target);
        BepInExRuntime.SafeExtract(zipPath, target);

        var winhttp = Path.Combine(target, "winhttp.dll");
        Check(File.Exists(winhttp) && File.ReadAllText(winhttp) == "LOADER",
              "extract: a legitimate loose root file extracts with its content intact");
        Check(File.Exists(Path.Combine(target, "doorstop_config.ini")),
              "extract: a legitimate loose root file (doorstop_config.ini) extracts");
        var core = Path.Combine(target, "BepInEx", "core", "BepInEx.Core.dll");
        Check(File.Exists(core) && File.ReadAllText(core) == "CORE",
              "extract: a legitimate nested BepInEx\\core\\*.dll extracts, preserving directory structure");
    }

    /// <summary>Tiefenverteidigung gegen Reparse-Points: liegt unter dem Ziel bereits eine Junction,
    /// die nach AUSSERHALB des Zielordners zeigt, darf SafeExtract dort nicht hindurchschreiben, auch
    /// wenn der Zielpfad rein textuell innerhalb liegt. Junctions brauchen unter Windows keine
    /// Elevation; kann die Testumgebung trotzdem keine anlegen, wird uebersprungen statt die Suite
    /// scheitern zu lassen (dieselbe Konvention wie FileSystemChecks' eigener "kein beschreibbares
    /// Temp"-Ausweg).
    ///
    /// Fix Round 1, I3: der urspruengliche Test benutzte ein Archiv mit GENAU EINEM, nicht
    /// verschachtelten Eintrag ("BepInEx/evil.dll") -- damit war Directory.CreateDirectory auf der
    /// bereits vorhandenen Junction ein reiner No-Op, und der Assert bestand auch mit der (damals noch
    /// in der Schreibrunde laufenden) Pruefung, ohne die eigentliche Regel zu pinnen: die Pruefung lief
    /// dort PRO bereits angelegtem bzw. vorhandenem Verzeichnis, nicht vorab. Jetzt zwei Eintraege --
    /// ein harmloser "good.dll" direkt im Ziel und ein verschachtelter
    /// "BepInEx/plugins/evil.dll" hinter der Junction --, damit eine zu spaete Pruefung sichtbar
    /// wuerde: sie haette "outside\plugins" angelegt (das Verzeichnis unter dem Junction-Ziel, das nur
    /// der verschachtelte Eintrag braucht) UND "good.dll" bereits geschrieben, bevor sie ueberhaupt
    /// griffe.</summary>
    private static void CheckSafeExtractRejectsReparsePointEscape(string root)
    {
        var baseDir = Path.Combine(root, "rp");
        var dest = Path.Combine(baseDir, "dest");
        var outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(outside);

        var link = Path.Combine(dest, "BepInEx");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            proc!.WaitForExit(10000);
            if (proc.ExitCode != 0)
            {
                Console.WriteLine("fs: SafeExtract reparse-point check skipped: could not create a junction (mklink exited non-zero)");
                return;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Console.WriteLine($"fs: SafeExtract reparse-point check skipped: {e.GetType().Name}");
            return;
        }

        try
        {
            var zipPath = Path.Combine(baseDir, "junction.zip");
            CreateZip(zipPath, ("good.dll", "HARMLESS"), ("BepInEx/plugins/evil.dll", "PAYLOAD"));

            var threw = false;
            try { BepInExRuntime.SafeExtract(zipPath, dest); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, "extract: rejects a target reached through a pre-existing junction pointing outside the destination");
            Check(!File.Exists(Path.Combine(outside, "plugins", "evil.dll")),
                  "extract: nothing written through the junction to the outside folder");
            Check(!Directory.Exists(Path.Combine(outside, "plugins")),
                  "extract: the rejection happens before validation creates any directory through the junction "
                + "(Fix Round 1, I3 -- the old write-time check let CreateDirectory run first)");
            Check(!File.Exists(Path.Combine(dest, "good.dll")),
                  "extract: a harmless entry earlier in the archive is not written either -- the whole archive is rejected");
        }
        finally
        {
            // Die Junction muss VOR dem abschliessenden Directory.Delete(root, recursive: true) von
            // FileSystemChecks weg: eine tote Junction (ihr Ziel "outside" wird als Geschwisterordner
            // im selben Durchlauf ohnehin mitgeloescht) liess den rekursiven Loeschvorgang dort
            // scheitern und einen leeren "rp\dest"-Rest im Temp-Ordner zuruecklassen (beobachtet).
            // Directory.Delete OHNE recursive loescht bei einem Reparse-Point nur den Link selbst,
            // fasst den Zielordner nicht an.
            try { if (Directory.Exists(link)) Directory.Delete(link); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Fix Round 1, I1: File.Create/ExtractToFile(overwrite: true) folgt einem bereits
    /// vorhandenen Datei-Symlink am Zielpfad transparent und schreibt DURCH ihn hindurch -- entdeckt
    /// als "reject", jetzt korrigiert zu "erst loeschen, dann frisch anlegen" (s. SafeExtract-
    /// Kommentar bei File.Delete). Ein Datei-Symlink braucht (anders als eine Verzeichnis-Junction)
    /// SeCreateSymbolicLinkPrivilege bzw. den Windows-Entwicklermodus -- kann die Testumgebung keinen
    /// anlegen, wird uebersprungen statt die Suite scheitern zu lassen.</summary>
    private static void CheckSafeExtractSeversPreexistingFileSymlink(string root)
    {
        var baseDir = Path.Combine(root, "symlink");
        var dest = Path.Combine(baseDir, "dest");
        var outsideFile = Path.Combine(baseDir, "outside-target.txt");
        Directory.CreateDirectory(dest);
        File.WriteAllText(outsideFile, "ORIGINAL_OUTSIDE_CONTENT");

        var link = Path.Combine(dest, "winhttp.dll");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink \"{link}\" \"{outsideFile}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            proc!.WaitForExit(10000);
            if (proc.ExitCode != 0)
            {
                Console.WriteLine("fs: SafeExtract file-symlink check skipped: could not create a file symlink "
                                 + "(mklink exited non-zero, likely missing SeCreateSymbolicLinkPrivilege / Developer Mode)");
                return;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Console.WriteLine($"fs: SafeExtract file-symlink check skipped: {e.GetType().Name}");
            return;
        }

        try
        {
            var zipPath = Path.Combine(baseDir, "archive.zip");
            CreateZip(zipPath, ("winhttp.dll", "NEW_PAYLOAD"));

            BepInExRuntime.SafeExtract(zipPath, dest);
            Check(File.ReadAllText(link) == "NEW_PAYLOAD",
                  "extract: the target path now holds the archive's content as a fresh, unlinked file");
            Check(File.ReadAllText(outsideFile) == "ORIGINAL_OUTSIDE_CONTENT",
                  "extract: the file reached through the severed symlink is never touched");
        }
        finally
        {
            try { if (File.Exists(link)) File.Delete(link); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Fix Round 1, I1 -- the finding itself: a hard link (mklink /H) at a leaf target needs
    /// NO elevation, unlike a file symlink, and File.GetAttributes shows a completely ordinary
    /// "Archive" attribute for it with no reparse-point flag -- the old leaf check (gated on a reparse
    /// attribute) was structurally blind to this, the cheapest variant of the exact threat it meant to
    /// stop. Verified empirically before fixing: writing through the old (overwrite-in-place) approach
    /// changed the outside file's content; File.Delete followed by a fresh create does not. This check
    /// runs for real in every environment (no privilege needed), unlike the file-symlink case above.</summary>
    private static void CheckSafeExtractSeversPreexistingHardLink(string root)
    {
        var baseDir = Path.Combine(root, "hardlink");
        var dest = Path.Combine(baseDir, "dest");
        var outsideFile = Path.Combine(baseDir, "outside-victim.txt");
        Directory.CreateDirectory(dest);
        File.WriteAllText(outsideFile, "ORIGINAL_VICTIM_CONTENT");

        var link = Path.Combine(dest, "winhttp.dll");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{outsideFile}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using (var proc = System.Diagnostics.Process.Start(psi))
        {
            proc!.WaitForExit(10000);
            Check(proc.ExitCode == 0, "fs: mklink /H succeeds without elevation (hard link precondition for this check)");
        }

        Check(!File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint),
              "fs: a hard link carries no reparse-point attribute -- the old leaf check was structurally blind to it");

        var zipPath = Path.Combine(baseDir, "archive.zip");
        CreateZip(zipPath, ("winhttp.dll", "NEW_PAYLOAD"));

        BepInExRuntime.SafeExtract(zipPath, dest);
        Check(File.ReadAllText(link) == "NEW_PAYLOAD",
              "extract: the target path now holds the archive's content as a fresh, unlinked file");
        Check(File.ReadAllText(outsideFile) == "ORIGINAL_VICTIM_CONTENT",
              "extract: the hard-linked outside file is never touched -- File.Delete severed the link instead of writing through it");
    }

    /// <summary>Fix Round 1, kleiner Befund: PackageMapper lehnt Ziel-Kollisionen fuer Modarchive schon
    /// ab; SafeExtract tat das nicht. Ohne diese Pruefung ueberschreiben sich zwei Eintraege auf
    /// denselben Zielpfad (auch nur in Gross-/Kleinschreibung unterschiedlich) still gegenseitig, und
    /// ein Eintrag, dessen Pfad ein ANDERER Eintrag als Ordner braucht, endet je nach Reihenfolge
    /// entweder in einer rohen IOException oder in einem Teil-Install.</summary>
    private static void CheckSafeExtractRejectsTargetCollisions(string root)
    {
        var dir = Path.Combine(root, "collide");
        Directory.CreateDirectory(dir);

        void RejectsCollision(string label, (string, string)[] entries)
        {
            var zipPath = Path.Combine(dir, $"{label}.zip");
            CreateZip(zipPath, entries);
            var target = Path.Combine(dir, $"{label}-dest");
            Directory.CreateDirectory(target);

            var threw = false;
            try { BepInExRuntime.SafeExtract(zipPath, target); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, $"extract: rejects target collision '{label}'");
            Check(!Directory.EnumerateFileSystemEntries(target).Any(),
                  $"extract: target collision '{label}' -- nothing written at all");
        }

        RejectsCollision("exact-dup", new[] { ("a.dll", "A1"), ("a.dll", "A2") });
        RejectsCollision("case-dup", new[] { ("a.dll", "A1"), ("A.DLL", "A2") });
        RejectsCollision("file-then-dir", new[] { ("BepInEx", "FILE"), ("BepInEx/core/x.dll", "NESTED") });
        RejectsCollision("dir-then-file", new[] { ("BepInEx/core/x.dll", "NESTED"), ("BepInEx", "FILE") });
    }

    /// <summary>Fix Round 1, kleiner Befund: DOS-reservierte Geraetenamen (CON, NUL, COM1, ...) wurden
    /// bisher nur "zufaellig" ueber die Enthaltenseins-Pruefung abgelehnt (Path.GetFullPath bildet sie
    /// auf ein Geraet ab, das nie unter root liegt, empirisch geprueft), mit der irrefuehrenden
    /// Meldung "escapes the destination". Jetzt explizit geprueft; die Meldung nennt den richtigen
    /// Grund.</summary>
    private static void CheckSafeExtractRejectsDosDeviceNames(string root)
    {
        var dir = Path.Combine(root, "dosnames");
        Directory.CreateDirectory(dir);

        foreach (var name in new[] { "CON", "CON.dll", "NUL.txt", "COM1.dll", "LPT1.dll" })
        {
            var zipPath = Path.Combine(dir, $"z-{Guid.NewGuid():N}.zip");
            CreateZip(zipPath, (name, "x"));
            var target = Path.Combine(dir, $"dest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(target);

            InvalidOperationException? caught = null;
            try { BepInExRuntime.SafeExtract(zipPath, target); }
            catch (InvalidOperationException e) { caught = e; }
            Check(caught is not null, $"extract: rejects reserved DOS device name '{name}'");
            Check(caught is not null && caught.Message.Contains("reserved DOS device name", StringComparison.OrdinalIgnoreCase),
                  $"extract: '{name}' is rejected with the actual reason, not a misleading 'escapes the destination'");
            Check(!Directory.EnumerateFileSystemEntries(target).Any(), $"extract: '{name}' -- nothing written");
        }
    }

    /// <summary>Fix Round 1, kleiner Befund: a single path segment over 255 characters is a hard NTFS
    /// limit that Path.GetFullPath does NOT reject (measured: it succeeds even for a combined path
    /// over 5000 characters long) -- the failure only surfaces at the actual write, as a raw,
    /// OS-LOCALIZED IOException (German text observed: "Die Syntax fuer den Dateinamen... ist
    /// falsch."). Must be caught in validation, both for a clean English message and so the
    /// write-nothing guarantee holds.</summary>
    private static void CheckSafeExtractRejectsOverlongSegment(string root)
    {
        var dir = Path.Combine(root, "longname");
        Directory.CreateDirectory(dir);

        var longSegment = new string('b', 300) + ".dll";
        var zipPath = Path.Combine(dir, "archive.zip");
        CreateZip(zipPath, (longSegment, "x"));
        var target = Path.Combine(dir, "dest");
        Directory.CreateDirectory(target);

        InvalidOperationException? caught = null;
        try { BepInExRuntime.SafeExtract(zipPath, target); }
        catch (InvalidOperationException e) { caught = e; }
        Check(caught is not null, "extract: rejects a path segment longer than 255 characters");
        Check(caught is not null && !caught.Message.Contains("Datei", StringComparison.OrdinalIgnoreCase)
                                   && !caught.Message.Contains("Syntax", StringComparison.OrdinalIgnoreCase),
              "extract: the rejection message is the clean English one, not a leaked OS-localized IOException");
        Check(!Directory.EnumerateFileSystemEntries(target).Any(), "extract: nothing written for the over-long segment");
    }

    /// <summary>Fix Round 1, kleiner Befund: SafeExtract used to call Directory.CreateDirectory(destRoot)
    /// unconditionally, so a mistyped game path was silently created and "installed into" -- the user
    /// would see apparent success in a folder that is not the game at all. The destination must already
    /// exist; SafeExtract never creates it.</summary>
    private static void CheckSafeExtractRequiresExistingDestination(string root)
    {
        var missing = Path.Combine(root, "missing-dest", $"{Guid.NewGuid():N}");
        var zipPath = Path.Combine(root, "for-missing-dest.zip");
        CreateZip(zipPath, ("winhttp.dll", "x"));

        var threw = false;
        try { BepInExRuntime.SafeExtract(zipPath, missing); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "extract: rejects a destination folder that does not exist");
        Check(!Directory.Exists(missing), "extract: a rejected destination is never silently created");
    }

    /// <summary>Fix Round 1, kleiner Befund: the archive ships "BepInEx\plugins" and "BepInEx\patchers"
    /// only as empty directory entries (for user content -- BepInEx itself never puts anything there),
    /// which SafeExtract always skips (it only ever creates directories that a FILE entry needs). Cheap
    /// insurance, tested directly since InstallAsync's network call cannot run in the offline self-test.</summary>
    private static void CheckEnsureRuntimeSkeleton(string root)
    {
        var gameRoot = Path.Combine(root, "skeleton");
        Directory.CreateDirectory(gameRoot);
        var game = new GameInstall(gameRoot);

        BepInExRuntime.EnsureRuntimeSkeleton(game);
        Check(Directory.Exists(game.Plugins), "EnsureRuntimeSkeleton: BepInEx\\plugins exists afterward");
        Check(Directory.Exists(Path.Combine(gameRoot, "BepInEx", "patchers")), "EnsureRuntimeSkeleton: BepInEx\\patchers exists afterward");
    }

    /// <summary>Fix Round 2: winhttp.dll und BepInEx\core\BepInEx.Core.dll -- die beiden Dateien, aus
    /// denen Detect() die Identitaet einer Installation liest -- duerfen unter ihrem ECHTEN Namen nie
    /// halbfertig entstehen. Ein Verzeichnis, das exakt dort liegt, wo ein SPAETERER Archiveintrag als
    /// Datei landen will, erzwingt einen reinen SCHREIBRUNDEN-Fehlschlag (keine der
    /// Validierungspruefungen sieht eine Kollision mit dem bereits vorhandenen Dateisystem vorher --
    /// die Ziel-Kollisionspruefung erkennt nur Konflikte INNERHALB des Archivs, s. dort), obwohl beide
    /// Identitaetsdateien im Archiv VOR diesem Eintrag stehen und ihre Nebendateien laengst fertig
    /// geschrieben waeren.
    ///
    /// Der BepInEx.Core.dll-Eintrag traegt bewusst ECHTE PE-Bytes (die laufende StfcModManager.exe
    /// selbst, wie CheckDetectHardening/g4), nicht blossen Text: mit Text allein waere Detect() schon
    /// durch seine EIGENE, unabhaengige Haertung blind fuer den Unterschied, den dieser Test pinnen
    /// soll (eine leere/unlesbare Version meldet ohnehin "nicht installiert", Fix Round 1) -- der
    /// Detect-Assert unten wuerde dann auch mit abgeschalteter Runde-2-Umbenennung faelschlich
    /// bestehen. Mit einer echten, lesbaren Version wird der Assert zu einer echten Diskriminante.</summary>
    private static void CheckSafeExtractIdentityFilesStayHiddenUntilComplete(string root)
    {
        var dir = Path.Combine(root, "identity");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "dest");
        Directory.CreateDirectory(dest);

        // Ein echtes Verzeichnis genau dort, wo der letzte Archiveintrag als DATEI landen will.
        Directory.CreateDirectory(Path.Combine(dest, "blocked-target"));

        var zipPath = Path.Combine(dir, "archive.zip");
        using (var zs = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var winHttpEntry = zs.CreateEntry("winhttp.dll");
            using (var w = new StreamWriter(winHttpEntry.Open())) w.Write("LOADER");

            var coreEntry = zs.CreateEntry("BepInEx/core/BepInEx.Core.dll");
            using (var es = coreEntry.Open())
            {
                var selfExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
                {
                    using var fs = File.OpenRead(selfExe);
                    fs.CopyTo(es);
                }
                else
                {
                    es.Write("CORE"u8);
                }
            }

            var blockedEntry = zs.CreateEntry("blocked-target");
            using (var w = new StreamWriter(blockedEntry.Open())) w.Write("THIS ENTRY FAILS TO WRITE");
        }

        var threw = false;
        try { BepInExRuntime.SafeExtract(zipPath, dest); }
        catch (Exception) { threw = true; } // IOException/UnauthorizedAccessException from the real filesystem clash, not a clean InvalidOperationException rejection
        Check(threw, "extract: a later entry that fails to write still aborts the whole install");

        Check(!File.Exists(Path.Combine(dest, "winhttp.dll")),
              "extract: winhttp.dll never reaches its real name when a later entry fails to write, even though its own write already succeeded");
        Check(!File.Exists(Path.Combine(dest, "BepInEx", "core", "BepInEx.Core.dll")),
              "extract: BepInEx.Core.dll (a real, readable PE this time) never reaches its real name when a later entry fails to write, even though its own write already succeeded");

        var leftoverTemps = Directory.Exists(dest)
            ? Directory.EnumerateFiles(dest, "*.tmp", SearchOption.AllDirectories).ToArray()
            : Array.Empty<string>();
        Check(leftoverTemps.Length == 0,
              $"extract: no leftover identity temp files after the failed install (found: {string.Join(", ", leftoverTemps)})");

        Check(BepInExRuntime.Detect(new GameInstall(dest)) is null,
              "extract: Detect reports not-installed after a partial extraction that failed on a later entry, even though the core carries a genuinely readable version");
    }

    /// <summary>Detect() entscheidet, ob die UI eine (Neu-)Installation anbietet. Jeder dieser Faelle
    /// muss "nicht installiert" (null) melden, sonst bekommt der Nutzer nie das Reparatur-Angebot,
    /// obwohl BepInEx faktisch nicht (mehr) funktionsfaehig ist.</summary>
    private static void CheckDetectHardening(string root)
    {
        var g1 = new GameInstall(Path.Combine(root, "detect-loader-only"));
        Put(g1.WinHttp, "LOADER");
        Check(BepInExRuntime.Detect(g1) is null, "Detect: loader present but core missing reports not-installed");

        var g2 = new GameInstall(Path.Combine(root, "detect-core-only"));
        Put(g2.CoreDll, "CORE");
        Check(BepInExRuntime.Detect(g2) is null, "Detect: core present but loader missing reports not-installed");

        // Ein VORHANDENER, aber beschaedigter/abgeschnittener Kern (z. B. nach einer bei der
        // Extraktion abgebrochenen Installation): FileVersionInfo.GetVersionInfo wirft dafuer NICHT
        // (empirisch geprueft -- weder fuer eine leere Datei noch fuer zufaellige Bytes noch fuer
        // reinen Text), sondern liefert ein FileVersionInfo mit leeren Feldern. Ohne die Haertung in
        // Detect() waere das Ergebnis "" statt null -- ein Aufrufer, der auf "!= null" prueft, haette
        // einen kaputten Kern faelschlich als "installiert" gemeldet.
        var g3 = new GameInstall(Path.Combine(root, "detect-corrupt-core"));
        Put(g3.WinHttp, "LOADER");
        Put(g3.CoreDll, "this is not a valid PE file, just garbage standing in for a truncated download");
        Check(BepInExRuntime.Detect(g3) is null,
              "Detect: a present but corrupt/version-less core reports not-installed, not an empty string");

        // Positivfall: eine echte PE-Datei mit lesbarer Version wird tatsaechlich erkannt, damit die
        // Haertung oben den Erfolgsfall nicht versehentlich mit abgeschossen hat. Environment.ProcessPath
        // (nicht Assembly.Location, das in einer Single-File-Veroeffentlichung "" liefert, s. IL3000)
        // zeigt auf die gerade laufende StfcModManager.exe selbst -- eine echte, im Testlauf garantiert
        // vorhandene PE mit Versionsressource (Version aus dem <Version>-Element im csproj).
        var selfExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
        {
            var g4 = new GameInstall(Path.Combine(root, "detect-real"));
            Put(g4.WinHttp, "LOADER");
            Directory.CreateDirectory(Path.GetDirectoryName(g4.CoreDll)!);
            File.Copy(selfExe, g4.CoreDll, overwrite: true);
            var detected = BepInExRuntime.Detect(g4);
            Check(!string.IsNullOrEmpty(detected), "Detect: a real PE with a version resource is reported as installed");
        }
    }

    // ===================================================================================
    // Task 9: LogReader / HealthCheck -- HealthCheck.Run liest den Spielordner in einem beliebigen,
    // moeglicherweise kaputten Zustand (kein Ordner, kein BepInEx, gesperrtes Log, geloeschte DLL
    // hinter der Buchfuehrung). Jede Pruefung hier legt echte Dateien unter dem Wegwerf-Spielordner
    // an, damit die Zweige, die File.Exists/File.ReadAllText/Directory.EnumerateFiles tatsaechlich
    // ausfuehren, auch wirklich durchlaufen werden -- die reinen Asserts weiter oben erreichen sie
    // nie (dieselbe Ueberlegung wie im Kommentar ueber FileSystemChecks).
    // ===================================================================================

    /// <summary>Ein Spiel kann waehrend des Schreibens seine eigene Logdatei exklusiv halten (kein
    /// FileShare fuer andere Prozesse). ReadTail muss dafuer eine leere Liste liefern, nicht
    /// werfen.</summary>
    private static void CheckLogReaderReadTailLockedFile(string root)
    {
        var dir = Path.Combine(root, "log-locked");
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, "LogOutput.log");
        File.WriteAllText(logPath, "[Error :Src] boom\n[Info :Src] fine\n");

        var unlocked = LogReader.ReadTail(logPath);
        Eq(unlocked.Count, 1, "fs: ReadTail finds the one error line when the file is not locked");

        using (new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lockedResult = LogReader.ReadTail(logPath);
            Eq(lockedResult.Count, 0, "fs: ReadTail returns an empty list, not a throw, when the file is exclusively locked");
        }
    }

    /// <summary>maxLines begrenzt die zuletzt gelesenen ROHEN Zeilen, nicht die Anzahl der am Ende
    /// gefundenen Fehler -- eine Verwechslung wuerde hier unbemerkt bleiben, wenn der Test nur die
    /// Fehlerzahl prüfte statt WELCHE der drei Fehlerzeilen ueberlebt.</summary>
    private static void CheckLogReaderReadTailLineCap(string root)
    {
        var dir = Path.Combine(root, "log-cap");
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, "LogOutput.log");
        var lines = new List<string>();
        for (var i = 0; i < 7; i++) lines.Add($"[Info :Src] filler {i}");
        lines.Add("[Error :Src] first-error");
        lines.Add("[Error :Src] second-error");
        lines.Add("[Error :Src] third-error");
        File.WriteAllLines(logPath, lines);

        var uncapped = LogReader.ReadTail(logPath);
        Eq(uncapped.Count, 3, "fs: ReadTail without a tight cap finds all three error lines");

        var capped = LogReader.ReadTail(logPath, maxLines: 2);
        Eq(capped.Count, 2, "fs: ReadTail(maxLines: 2) keeps only the last two raw lines, dropping the earliest error");
        Check(capped.All(e => e.Message is "second-error" or "third-error"),
              "fs: the surviving entries are exactly the last two raw lines, not an arbitrary two errors");
    }

    /// <summary>Kein Spielordner ueberhaupt: die einzige Meldung nennt den echten Grund, und Run()
    /// kehrt sofort zurueck, statt die anderen acht Pruefungen auf einem sinnlosen Pfad zu
    /// versuchen.</summary>
    private static void CheckHealthCheckMissingGameFolder(string root)
    {
        var missingRoot = Path.Combine(root, "hc-missing", Guid.NewGuid().ToString("N"));
        var game = new GameInstall(missingRoot);
        var findings = HealthCheck.Run(new AppState(), game);

        Eq(findings.Count, 1, "fs: HealthCheck.Run on a non-existent game folder returns exactly one finding");
        Check(findings.Count == 1 && findings[0].Severity == Severity.Error,
              "fs: the single finding for a missing game folder is an Error");
        Check(findings.Count == 1 && findings[0].Title.Contains("prime.exe"),
              "fs: the finding names the missing prime.exe, not a generic message");
    }

    /// <summary>Gueltiger, beschreibbarer Spielordner, aber kein BepInEx installiert.</summary>
    private static void CheckHealthCheckBepInExMissing(string root)
    {
        var gameRoot = Path.Combine(root, "hc-nobepinex");
        Put(Path.Combine(gameRoot, "prime.exe"), "GAME");
        var game = new GameInstall(gameRoot);

        var findings = HealthCheck.Run(new AppState(), game);
        Check(findings.Any(x => x.Title == "BepInEx is not installed."),
              "fs: HealthCheck reports BepInEx missing on a valid game folder with no runtime installed");
    }

    /// <summary>Ein Mod, dessen InstalledAgainstClientBuild vom echten, aus .version gelesenen Build
    /// abweicht, loest die Warnung aus -- ein passender Build tut es nicht (Kehrprobe).</summary>
    private static void CheckHealthCheckStaleClientBuild(string root)
    {
        var gameRoot = Path.Combine(root, "hc-stale");
        Put(Path.Combine(gameRoot, "prime.exe"), "GAME");
        Put(Path.Combine(gameRoot, ".version"), "&game=254");
        var game = new GameInstall(gameRoot);

        var state = new AppState();
        state.Mods.Add(new ModEntry { Id = "m1", Name = "M1", Version = "1.0", InstalledAgainstClientBuild = "200" });
        var findings = HealthCheck.Run(state, game);
        Check(findings.Any(x => x.Severity == Severity.Warning && x.Title.Contains("changed to build 254")),
              "fs: HealthCheck reports a stale client build when a mod was installed against an older build");

        var freshState = new AppState();
        freshState.Mods.Add(new ModEntry { Id = "m1", Name = "M1", Version = "1.0", InstalledAgainstClientBuild = "254" });
        var freshFindings = HealthCheck.Run(freshState, game);
        Check(!freshFindings.Any(x => x.Title.Contains("changed to build")),
              "fs: HealthCheck does not report a stale build when the installed build matches the current one");
    }

    /// <summary>Ein Mod, dessen DLL hinter dem Ruecken des Managers geloescht wurde (Buchfuehrung
    /// zeigt noch auf sie, die Platte nicht mehr): darf Run() nicht abstuerzen lassen. ModInspector.Read
    /// faengt IOException schon selbst ab und liefert null -- HealthCheck muss diesen null-Fall
    /// ueberspringen, statt info.Incompatibilities auf null aufzurufen.</summary>
    private static void CheckHealthCheckMissingModDllDoesNotThrow(string root)
    {
        var gameRoot = Path.Combine(root, "hc-deleted-dll");
        Put(Path.Combine(gameRoot, "prime.exe"), "GAME");
        var game = new GameInstall(gameRoot);

        var state = new AppState();
        state.Mods.Add(new ModEntry
        {
            Id = "ghost", Name = "Ghost", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Ghost.dll", Sha256 = "g" } }
        });

        // Wirft diese Zeile, faengt Guarded() es als Fail ab -- das ist die eigentliche Pruefung.
        var findings = HealthCheck.Run(state, game);
        Check(!findings.Any(x => x.Title.Contains("Ghost", StringComparison.OrdinalIgnoreCase)),
              "fs: a deleted mod dll produces no phantom incompatibility/dependency finding");
    }

    /// <summary>Pruefung 6 liest die echte community_patch_settings.toml von der Platte -- inklusive
    /// Kehrprobe, dass beide Schalter auf false die Meldung nicht ausloest.</summary>
    private static void CheckHealthCheckCommunityPatchConflictOnDisk(string root)
    {
        var gameRoot = Path.Combine(root, "hc-patchconflict");
        Put(Path.Combine(gameRoot, "prime.exe"), "GAME");
        Put(Path.Combine(gameRoot, "version.dll"), "NATIVE");
        Put(Path.Combine(gameRoot, "community_patch_settings.toml"),
            "[patches]\ngame_version = true\nuiscalehooks = false\n");
        var game = new GameInstall(gameRoot);

        var findings = HealthCheck.Run(new AppState(), game);
        Check(findings.Any(x => x.Severity == Severity.Error && x.Title.Contains("Community Mod")),
              "fs: HealthCheck reads the real toml on disk and reports the community patch conflict");

        Put(Path.Combine(gameRoot, "community_patch_settings.toml"),
            "[patches]\ngame_version = false\nuiscalehooks = false\n");
        var safeFindings = HealthCheck.Run(new AppState(), game);
        Check(!safeFindings.Any(x => x.Title.Contains("Community Mod")),
              "fs: no community-patch finding when both switches are false on disk");
    }

    /// <summary>Pruefung 8 liest das echte LogOutput.log und zaehlt Fehler pro Quelle -- die Zahl im
    /// Titel muss stimmen, nicht nur "irgendeine Meldung" erscheinen.</summary>
    private static void CheckHealthCheckGameLogErrors(string root)
    {
        var gameRoot = Path.Combine(root, "hc-log");
        Put(Path.Combine(gameRoot, "prime.exe"), "GAME");
        Put(Path.Combine(gameRoot, @"BepInEx\LogOutput.log"),
            "[Info :BepInEx] loading\n[Error :Hellebarde] NullReferenceException\n[Error :Hellebarde] second failure\n");
        var game = new GameInstall(gameRoot);

        var findings = HealthCheck.Run(new AppState(), game);
        Check(findings.Any(x => x.Severity == Severity.Warning && x.Title == "Hellebarde: 2 error(s) in the game log."),
              "fs: HealthCheck counts errors from the real game log per source, exact count included");
    }

    /// <summary>Pruefung 9: eine unverwaltete Datei im echten plugins-Ordner wird gemeldet, eine vom
    /// Mod verwaltete nicht mitgezaehlt.</summary>
    private static void CheckHealthCheckOrphanFiles(string root)
    {
        var gameRoot = Path.Combine(root, "hc-orphan");
        Put(Path.Combine(gameRoot, "prime.exe"), "GAME");
        Put(Path.Combine(gameRoot, @"BepInEx\plugins\Managed.dll"), "M");
        Put(Path.Combine(gameRoot, @"BepInEx\plugins\Unmanaged.dll"), "U");
        var game = new GameInstall(gameRoot);

        var state = new AppState();
        state.Mods.Add(new ModEntry
        {
            Id = "managed", Name = "Managed", Version = "1.0", Enabled = true,
            Files = { new InstalledFile { Path = @"BepInEx\plugins\Managed.dll", Sha256 = "m" } }
        });

        var findings = HealthCheck.Run(state, game);
        Check(findings.Any(x => x.Severity == Severity.Info
                              && x.Title == "1 file(s) in the plugins folder are not managed by this app."),
              "fs: HealthCheck reports exactly the one orphaned file, not the managed one");
    }

    /// <summary>Das Spiel kann LogOutput.log waehrend Run() exklusiv halten -- derselbe Zustand wie
    /// CheckLogReaderReadTailLockedFile, hier end-to-end durch HealthCheck.Run() selbst.</summary>
    private static void CheckHealthCheckDoesNotThrowWithLockedLog(string root)
    {
        var gameRoot = Path.Combine(root, "hc-lockedlog");
        Put(Path.Combine(gameRoot, "prime.exe"), "GAME");
        var logPath = Path.Combine(gameRoot, "BepInEx", "LogOutput.log");
        Put(logPath, "[Error :Src] boom\n");
        var game = new GameInstall(gameRoot);

        using var locked = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var findings = HealthCheck.Run(new AppState(), game); // darf trotz gesperrter Logdatei nicht werfen
        Check(!findings.Any(x => x.Title.StartsWith("Src:", StringComparison.Ordinal)),
              "fs: with the log file locked, HealthCheck reports no log-derived finding instead of throwing");
    }

    // ===================================================================================
    // Task 10: Redactor / SupportBundle -- die Pruefungen hier bauen einen echten
    // Wegwerf-Spielordner mit realistischer Durchmischung (kleine .cfg mit einem falschen
    // API-Schluessel, ein grosses .json, ein uebergrosses Log) und pruefen das erzeugte ZIP von
    // aussen: Inhalt, Groessenbudget, protokollierte Auslassungen und Abwesenheit jedes rohen
    // Geheimnisses. Real auf der Zielmaschine gemessen: BepInEx\config enthaelt einen 68-MB-
    // JSON-Cache neben den paar Kilobyte grossen .cfg-Dateien -- die Endungsregel wird hier mit
    // einer kleineren, aber immer noch klar ueberproportionalen Datei nachgestellt (die Regel gilt
    // unabhaengig von der absoluten Groesse, ein Test braucht die echten 68 MB nicht).
    // ===================================================================================

    /// <summary>Nur .cfg wird geplant, ein beliebig grosses .json im selben Ordner nie.</summary>
    private static void CheckSupportBundlePlannedContentsCfgOnly(string root)
    {
        var gameRoot = Path.Combine(root, "sb-cfgonly");
        var cfgPath = Put(Path.Combine(gameRoot, @"BepInEx\config\UniversalTranslator.cfg"), "ApiKey = fake\n");
        var jsonPath = Put(Path.Combine(gameRoot, @"BepInEx\config\cache.json"), new string('x', 200_000));
        var game = new GameInstall(gameRoot);

        var planned = SupportBundle.PlannedContents(game);
        Check(planned.Contains(cfgPath), "fs: PlannedContents includes the .cfg file in BepInEx\\config");
        Check(!planned.Contains(jsonPath), "fs: PlannedContents excludes a non-.cfg file in the same folder, however large");
    }

    /// <summary>Kein BepInEx\config-Ordner ueberhaupt: PlannedContents darf nicht werfen (der
    /// Vorschau-Dialog ruft diese Methode direkt auf dem UI-Thread auf).</summary>
    private static void CheckSupportBundlePlannedContentsMissingConfigFolder(string root)
    {
        var gameRoot = Path.Combine(root, "sb-noconfig");
        Directory.CreateDirectory(gameRoot); // BepInEx\config existiert bewusst nicht
        var game = new GameInstall(gameRoot);

        var planned = SupportBundle.PlannedContents(game);
        Check(!planned.Any(p => p.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)),
              "fs: no .cfg entries appear when the config folder does not exist");
    }

    /// <summary>Die zentrale End-zu-Ende-Pruefung mit den ECHTEN, ausgelieferten Grenzen (5 MB pro
    /// Datei, 20 MB insgesamt): eine .cfg mit einem falschen Schluessel, ein grosses .json, ein
    /// Log ueber der Pro-Datei-Grenze. Prueft Inhalt, Kappung, Protokollierung der Auslassung und
    /// dass der rohe Schluessel NIRGENDS im Paket steht -- auch nicht dort, wo er ohne
    /// Zuweisungsform im Log auftaucht.</summary>
    private static void CheckSupportBundleCreateRedactsAndRespectsCap(string root)
    {
        var gameRoot = Path.Combine(root, "sb-main");
        // Rein alphanumerisch, >=24 Zeichen, mit Ziffern -- greift unabhaengig davon, ob er hinter
        // einem "ApiKey ="-artigen Schluesselnamen steht (SecretAssignment) oder frei im Log
        // auftaucht (LongId als Auffangnetz).
        const string fakeSecret = "deadbeef1234567890abcdef99887766";
        Put(Path.Combine(gameRoot, @"BepInEx\config\UniversalTranslator.cfg"),
            $"ApiKey = {fakeSecret}\nMaxLevel = 40\n");

        Put(Path.Combine(gameRoot, @"BepInEx\config\cache.json"), new string('j', 500_000));

        var logPath = Path.Combine(gameRoot, "BepInEx", "LogOutput.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using (var w = new StreamWriter(logPath))
        {
            for (var i = 0; i < 120_000; i++)
                w.WriteLine($"[Info :Filler] padding line {i} to grow the file past five megabytes of content");
            w.WriteLine($"[Error :Src] boom near the end, carries a fake token {fakeSecret} with no assignment form");
        }
        var logSize = new FileInfo(logPath).Length;
        Check(logSize > 5 * 1024 * 1024,
              $"fs: the test log is actually oversized ({logSize} bytes), or the truncation assert below would be vacuous");

        var game = new GameInstall(gameRoot);
        var destZip = Path.Combine(root, "sb-main-out", "support.zip");

        SupportBundle.Create(new AppState(), game, destZip); // die OEFFENTLICHE Ueberladung, echte Grenzen

        Check(File.Exists(destZip), "fs: Create wrote a zip file at the requested destination");

        using var zip = ZipFile.OpenRead(destZip);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Check(names.Contains("collected/UniversalTranslator.cfg"), "fs: the .cfg file was collected");
        Check(!names.Any(n => n.Contains("cache.json", StringComparison.OrdinalIgnoreCase)),
              "fs: the huge .json file never appears in the zip, regardless of its size");
        Check(names.Contains("collected/LogOutput.log"), "fs: the oversized log was still collected (tail-truncated, not dropped)");
        Check(names.Contains("SKIPPED.txt"), "fs: SKIPPED.txt exists to record what was truncated");
        Check(names.Contains("inventory.json") && names.Contains("environment.txt") && names.Contains("health.txt"),
              "fs: the three self-generated files are always present");

        var logEntry = zip.GetEntry("collected/LogOutput.log")!;
        using (var r = new StreamReader(logEntry.Open()))
        {
            var logContent = r.ReadToEnd();
            var logBytes = System.Text.Encoding.UTF8.GetByteCount(logContent);
            Check(logBytes is >= 4 * 1024 * 1024 and <= 5 * 1024 * 1024 + 8192,
                  $"fs: the collected log entry is truncated to roughly the 5 MB per-file cap ({logBytes} bytes), not the full {logSize} bytes");
        }

        var skippedEntry = zip.GetEntry("SKIPPED.txt")!;
        using (var r = new StreamReader(skippedEntry.Open()))
        {
            var skippedContent = r.ReadToEnd();
            Check(skippedContent.Contains("LogOutput.log") && skippedContent.Contains("truncated"),
                  "fs: SKIPPED.txt names the truncated log and explains why");
        }

        // Die eigentliche Zusicherung des Redactors: das gesamte Paket enthaelt den rohen Schluessel
        // NIRGENDS -- weder in der .cfg-Zuweisung noch im Log, wo er ohne Zuweisungsform auftaucht.
        foreach (var entry in zip.Entries)
        {
            using var r = new StreamReader(entry.Open());
            var content = r.ReadToEnd();
            Check(!content.Contains(fakeSecret, StringComparison.Ordinal),
                  $"fs: entry '{entry.FullName}' does not contain the raw fake secret anywhere in the package");
        }

        var cfgEntry = zip.GetEntry("collected/UniversalTranslator.cfg")!;
        using (var r = new StreamReader(cfgEntry.Open()))
        {
            var cfgContent = r.ReadToEnd();
            Check(cfgContent.Contains("ApiKey = [REDACTED]"), "fs: the collected .cfg shows the redacted form of the key");
            Check(cfgContent.Contains("MaxLevel = 40"), "fs: the collected .cfg still shows the harmless setting in the clear");
        }
    }

    /// <summary>Der Gesamt-Budget-Pfad, mit kuenstlich kleinen Grenzen ueber die interne
    /// Testueberladung -- ohne echte zweistellige Megabyte an Testdaten zu schreiben. Zwei winzige
    /// Dateien passen, eine deutlich groessere danach nicht mehr.</summary>
    private static void CheckSupportBundleTotalBudgetExhaustion(string root)
    {
        var gameRoot = Path.Combine(root, "sb-budget");
        Put(Path.Combine(gameRoot, "BepInEx", "LogOutput.log"), "hello\n");
        Put(Path.Combine(gameRoot, "BepInEx", "ErrorLog.log"), "world\n");
        Put(Path.Combine(gameRoot, "community_patch.log"), new string('x', 5000));
        var game = new GameInstall(gameRoot);
        var destZip = Path.Combine(root, "sb-budget-out", "support.zip");

        SupportBundle.Create(new AppState(), game, destZip, totalBudgetBytes: 200, perFileTailBytes: 1024 * 1024);

        using var zip = ZipFile.OpenRead(destZip);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Check(names.Contains("collected/LogOutput.log"), "fs: a small file well within budget is collected");
        Check(names.Contains("collected/ErrorLog.log"), "fs: a second small file that still fits the remaining budget is collected");
        Check(!names.Any(n => n == "collected/community_patch.log"),
              "fs: a file whose redacted size exceeds the remaining budget is dropped, not written");

        var skippedEntry = zip.GetEntry("SKIPPED.txt");
        Check(skippedEntry is not null, "fs: SKIPPED.txt exists when the budget forces a drop");
        if (skippedEntry is not null)
        {
            var skippedContent = new StreamReader(skippedEntry.Open()).ReadToEnd();
            Check(skippedContent.Contains("community_patch.log") && skippedContent.Contains("budget"),
                  "fs: SKIPPED.txt records exactly why the community patch log was dropped");
        }
    }

    /// <summary>Eine geplante Quelldatei kann waehrend des Sammelns exklusiv gesperrt sein (dasselbe
    /// Spiel-Log-Szenario wie bei HealthCheck) -- Create() darf trotzdem nicht werfen, muss die
    /// restlichen Dateien weiter sammeln und die gesperrte Datei in SKIPPED.txt vermerken.</summary>
    private static void CheckSupportBundleCreateLockedSourceFile(string root)
    {
        var gameRoot = Path.Combine(root, "sb-locked");
        var logPath = Path.Combine(gameRoot, "BepInEx", "LogOutput.log");
        Put(logPath, "[Error :Src] boom\n");
        var game = new GameInstall(gameRoot);
        var destZip = Path.Combine(root, "sb-locked-out", "support.zip");

        using (new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SupportBundle.Create(new AppState(), game, destZip); // darf trotz gesperrter Quelle nicht werfen
        }

        using var zip = ZipFile.OpenRead(destZip);
        var skippedEntry = zip.GetEntry("SKIPPED.txt");
        Check(skippedEntry is not null, "fs: SKIPPED.txt exists when a source file could not be read");
        if (skippedEntry is not null)
        {
            var content = new StreamReader(skippedEntry.Open()).ReadToEnd();
            Check(content.Contains("LogOutput.log") && content.Contains("could not be read"),
                  "fs: SKIPPED.txt names the locked file and says it could not be read");
        }
    }

    /// <summary>Ein komplett leerer Spielordner (kein BepInEx, kein Log, keine config) darf Create()
    /// nicht werfen lassen -- die drei selbst erzeugten Dateien entstehen trotzdem.</summary>
    private static void CheckSupportBundleCreateOnBareGameFolder(string root)
    {
        var gameRoot = Path.Combine(root, "sb-bare");
        Directory.CreateDirectory(gameRoot);
        var game = new GameInstall(gameRoot);
        var destZip = Path.Combine(root, "sb-bare-out", "support.zip");

        SupportBundle.Create(new AppState(), game, destZip);

        using var zip = ZipFile.OpenRead(destZip);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Check(names.Contains("inventory.json") && names.Contains("environment.txt") && names.Contains("health.txt"),
              "fs: the three self-generated files are still written even with nothing to collect");
        Check(!names.Any(n => n.StartsWith("collected/", StringComparison.Ordinal) && n.EndsWith(".cfg")),
              "fs: no .cfg entries when the game folder has no BepInEx config at all");
    }

    // ===================================================================================
    // Fix-Runde 1, C2: eine Freigabe-Sperre (FileShare.None, s. oben) ist eine IOException. Eine
    // per ACL verweigerte Datei (Berechtigungen, Virenscanner-Quarantaene) ist dagegen eine
    // UnauthorizedAccessException -- die erbt NICHT von IOException, beide stammen direkt von
    // SystemException ab. Nur ein Test mit einer ECHT per ACL gesperrten Datei deckt das auf; die
    // bisherigen Sperr-Pruefungen taten das nicht, weil sie ausschliesslich die Freigabe-Variante
    // prueften. Eine explizite Deny-ACE auf die eigene Datei zu setzen braucht KEINE Elevation --
    // der Besitzer darf sich selbst per WRITE_DAC das Lesen verweigern (anders als das Aendern des
    // Besitzers einer FREMDEN Datei, das echte Elevation braucht).
    // ===================================================================================

    /// <summary>Versucht, dem aktuellen Benutzer per expliziter Deny-ACE das Lesen der gegebenen
    /// Datei zu verweigern -- absichtlich nur ReadData, NICHT FullControl/Delete, damit die
    /// abschliessende Aufraeumroutine der Testumgebung die Datei trotzdem loeschen kann. Liefert
    /// false (statt zu werfen), wenn die Umgebung das nicht zulaesst -- der Aufrufer ueberspringt
    /// dann den Rest der Pruefung, statt die Suite scheitern zu lassen (dieselbe Konvention wie bei
    /// den Junction-/Symlink-Pruefungen weiter oben).</summary>
    private static bool TryDenyRead(string path, out Action restore)
    {
        restore = static () => { };
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity.User is null) return false;

            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                identity.User,
                System.Security.AccessControl.FileSystemRights.ReadData,
                System.Security.AccessControl.AccessControlType.Deny);
            security.AddAccessRule(rule);
            fileInfo.SetAccessControl(security);

            restore = () =>
            {
                try
                {
                    var fi = new FileInfo(path);
                    var sec = fi.GetAccessControl();
                    sec.RemoveAccessRule(rule);
                    fi.SetAccessControl(sec);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            };
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or System.Security.Principal.IdentityNotMappedException)
        {
            return false;
        }
    }

    /// <summary>Fix-Runde 1, C2: LogReader.ReadTail faengt bisher nur IOException. Eine echt per ACL
    /// gesperrte Datei wirft UnauthorizedAccessException und muss genauso leise mit [] enden.</summary>
    private static void CheckLogReaderReadTailAclDenied(string root)
    {
        var dir = Path.Combine(root, "log-acl-denied");
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, "LogOutput.log");
        File.WriteAllText(logPath, "[Error :Src] boom\n");

        if (!TryDenyRead(logPath, out var restore))
        {
            Console.WriteLine("fs: LogReader ACL-denied check skipped: could not set a deny ACE in this environment");
            return;
        }
        try
        {
            var result = LogReader.ReadTail(logPath); // darf trotz ACL-Verweigerung nicht werfen
            Eq(result.Count, 0, "fs: ReadTail returns an empty list, not a throw, for a genuinely ACL-denied file");
        }
        finally { restore(); }
    }

    /// <summary>Fix-Runde 1, C2: dieselbe Luecke im TOML-Lesepfad von HealthCheck (Pruefung 6) --
    /// ein ACL-verweigertes community_patch_settings.toml durfte den ganzen Health-Check nicht mehr
    /// zum Absturz bringen, die Pruefung faellt dann einfach aus.</summary>
    private static void CheckHealthCheckAclDeniedCommunityPatchToml(string root)
    {
        var gameRoot = Path.Combine(root, "hc-acl-denied");
        Put(Path.Combine(gameRoot, "prime.exe"), "GAME");
        Put(Path.Combine(gameRoot, "version.dll"), "NATIVE");
        var tomlPath = Put(Path.Combine(gameRoot, "community_patch_settings.toml"),
            "[patches]\ngame_version = true\n");
        var game = new GameInstall(gameRoot);

        if (!TryDenyRead(tomlPath, out var restore))
        {
            Console.WriteLine("fs: HealthCheck ACL-denied check skipped: could not set a deny ACE in this environment");
            return;
        }
        try
        {
            var findings = HealthCheck.Run(new AppState(), game); // darf trotz ACL-Verweigerung nicht werfen
            Check(!findings.Any(x => x.Title.Contains("Community Mod")),
                  "fs: with the toml ACL-denied, HealthCheck reports no community-patch finding instead of throwing");
        }
        finally { restore(); }
    }
}
