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
}
