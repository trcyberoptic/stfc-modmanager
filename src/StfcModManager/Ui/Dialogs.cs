using System.IO.Compression;
using StfcModManager.Core;

namespace StfcModManager.Ui;

/// <summary>Alle modalen Abfragen und der Installationsweg dahinter.</summary>
public static class Dialogs
{
    // ---------- kleine generische Bausteine ----------

    /// <summary>
    /// Huelle fuer die selbstgebauten Dialoge: eine Tabelle, die sich nach ihrem Inhalt bemisst,
    /// in einem Fenster, das sich nach der Tabelle bemisst. Feste Pixelhoehen standen hier dreimal
    /// und waren dreimal zu klein -- bei zweizeiligem Text, bei groesserer Schrift und auf hoher
    /// DPI schnitten sie die Schaltflaechenzeile unten ab. Die Breite bleibt vorgegeben, die war
    /// nie das Problem.
    /// </summary>
    private static (Form Form, TableLayoutPanel Body) DialogShell(string title, int contentWidth, bool controlBox = true)
    {
        var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ControlBox = controlBox,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, contentWidth));
        form.Controls.Add(body);
        return (form, body);
    }

    /// <summary>Schaltflaechenzeile, rechtsbuendig. Rechts-nach-links-Fluss heisst: das zuerst
    /// hinzugefuegte Element sitzt ganz rechts -- also Cancel zuerst, damit OK links davon steht.</summary>
    private static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 10, 0, 0)
        };
        foreach (var b in buttons)
        {
            b.AutoSize = true;
            b.MinimumSize = new Size(80, 0);
            row.Controls.Add(b);
        }
        return row;
    }

    private static string? Prompt(IWin32Window owner, string title, string label, string initial = "")
    {
        const int contentWidth = 520;
        var (form, body) = DialogShell(title, contentWidth);
        using var _ = form;
        var text = new Label { Text = label, AutoSize = true, MaximumSize = new Size(contentWidth, 0) };
        var box = new TextBox { Text = initial, Width = contentWidth, Margin = new Padding(0, 6, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        body.Controls.Add(text);
        body.Controls.Add(box);
        body.Controls.Add(ButtonRow(cancel, ok));
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    private static string? ChooseFromList(IWin32Window owner, string title, IReadOnlyList<string> options)
    {
        const int contentWidth = 440;
        var (form, body) = DialogShell(title, contentWidth);
        using var _ = form;
        var list = new ListBox { Width = contentWidth, Height = 210 };
        list.Items.AddRange(options.ToArray());
        if (options.Count > 0) list.SelectedIndex = 0;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        body.Controls.Add(list);
        body.Controls.Add(ButtonRow(cancel, ok));
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? list.SelectedItem as string : null;
    }

    /// <summary>Nur fuer diese beiden Ausnahmearten ist e.Message garantiert eine von Core selbst
    /// verfasste, englische, betriebssystemfreie Meldung -- die gesamte Core-Schicht (GitHubClient,
    /// BepInExRuntime, SupportBundle, Installer.InstallRollbackException) existiert genau dafuer,
    /// rohe I/O- und Netzwerkfehler in genau so einen Text zu uebersetzen, bevor sie eine
    /// oeffentliche Methode verlassen. Fuer jede andere Ausnahmeart (IOException,
    /// UnauthorizedAccessException, ein ArgumentException aus Installer.Apply's Vorflug,
    /// InvalidDataException aus einer beschaedigten Zip-Datei, eine rohe TaskCanceledException aus
    /// einer Zeitueberschreitung mitten im Lesen einer HTTP-Antwort, ...) waere e.Message dagegen
    /// potenziell die rohe, ggf. vom Betriebssystem lokalisierte Meldung -- genau das, was laut
    /// Vorgabe nie vor dem Nutzer landen darf. Ein fest formulierter Ausweichtext haelt diese
    /// Zusicherung unabhaengig davon ein, welche konkrete Ausnahmeart ein Aufrufer im Einzelfall
    /// tatsaechlich auffaengt; die vollen Details landen ueber AppLog.Error in jedem Fall zusaetzlich
    /// im Log.</summary>
    private static string SafeUserMessage(Exception e, string fallback) =>
        e is InvalidOperationException or InstallRollbackException ? e.Message : fallback;

    /// <summary>Menschenlesbare Groessenangabe fuer die Vertrauensanzeige (Cheap Fix, Fix-Runde 1).
    /// Formatiert nur die Zahl -- ob sie "as declared by GitHub" ist, sagt der Aufrufer dazu, weil
    /// diese Funktion selbst nichts ueber die Herkunft der Zahl weiss.</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        >= 1_000 => $"{bytes / 1_000.0:0.#} KB",
        _ => $"{bytes} bytes"
    };

    /// <summary>Fix-Runde 2, Punkt 3 (C1 lebte noch auf dem Install-Pfad): PackageMapper lehnt
    /// "plugins-disabled" als Ordnername in einem Archiveintrag nicht ab -- ein Eintrag wie
    /// "MyMod/BepInEx/plugins-disabled/FakeA.dll" bildet klaglos auf das Ziel
    /// "BepInEx\plugins-disabled\FakeA.dll" ab. Ohne diese Kanonisierung haette ApplyPackage GENAU
    /// dorthin geschrieben UND genau diesen Pfad als den (angeblich aktivierten) Ort gespeichert --
    /// exakt das Symptom, das der urspruengliche C1-Fund fuer die Adoption beschrieb: der
    /// gespeicherte Pfad liegt nicht mehr unter BepInEx\plugins, SetEnabled erkennt ihn nie als
    /// umschaltbar. Schreibt (statt nur beim Speichern umzubenennen) tatsaechlich an den kanonischen
    /// Ort um, damit geschriebene Datei und gespeicherter Pfad uebereinstimmen -- ein frischer
    /// Install gilt als aktiviert, jetzt auch fuer diesen Ort zutreffend. Alles ausserhalb von
    /// BepInEx\plugins(-disabled)\ (version.dll, Configs, Patcher, ...) geht unveraendert durch.
    /// Rein und ohne I/O testbar.</summary>
    internal static string CanonicalizePluginTarget(string target)
    {
        var disabledPrefix = Path.Combine("BepInEx", "plugins-disabled") + Path.DirectorySeparatorChar;
        return target.StartsWith(disabledPrefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine("BepInEx", "plugins", target[disabledPrefix.Length..])
            : target;
    }

    /// <summary>Zeigt eine InstallRollbackException verstaendlich an: welche Dateien betroffen sind
    /// und wo die Sicherung liegt, statt eines Stacktraces oder eines Achselzuckens. ex.Message ist
    /// hier Installer-eigener, englischer Text (s. InstallRollbackException in Installer.cs), also
    /// sicher direkt anzuzeigen; AffectedPaths und BackupDirectory sind reine Dateisystempfade, keine
    /// Fehlertexte.</summary>
    public static void ShowRollbackFailure(IWin32Window owner, InstallRollbackException ex, string title)
    {
        var paths = string.Join("\r\n", ex.AffectedPaths.Select(p => "  " + p));
        var backup = ex.BackupDirectory is not null
            ? $"\r\n\r\nA backup of the files from before this attempt is available at:\r\n  {ex.BackupDirectory}"
            : "";
        MessageBox.Show(owner,
            $"{ex.Message}\r\n\r\nAffected file(s):\r\n{paths}{backup}\r\n\r\n" +
            "Check these files by hand before using the mod manager again.",
            title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ---------- GitHub ----------

    public static void AddFromGitHub(IWin32Window owner, AppState state, GameInstall game)
    {
        var url = Prompt(owner, "Add mod from GitHub",
                         "Repository URL, for example https://github.com/owner/repo");
        if (url is null) return;

        var parsed = GitHubClient.ParseRepoUrl(url);
        if (parsed is null)
        {
            MessageBox.Show(owner, "That is not an https github.com repository URL.", "Invalid URL",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var (ownerName, repoName) = parsed.Value;
        try
        {
            var release = GitHubClient
                .GetLatestReleaseAsync(ownerName, repoName, null, state.GitHubToken, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (release is null || release.Assets.Count == 0)
            {
                MessageBox.Show(owner, "The latest release of that repository has no downloadable files.",
                                "Nothing to install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var names = release.Assets.Select(a => a.Name).ToList();

            // Fix-Runde 1, I3: InstallableCandidates existiert genau fuer diese Unterscheidung
            // (s. dortiger Kommentar in GitHubClient.cs) -- ohne sie ging jeder Assetname (.exe,
            // .txt, .sha256, Quellarchive) unveraendert an den Auswahldialog, und ein Release ganz
            // ohne etwas Installierbares sah fuer den Nutzer identisch zu einem mehrdeutigen aus: er
            // waehlte etwas aus, wartete einen Download ab, und bekam erst danach "Package refused".
            var candidates = GitHubClient.InstallableCandidates(names);
            if (candidates.Count == 0)
            {
                MessageBox.Show(owner, "The latest release of that repository has no installable .zip or .dll file.",
                                "Nothing to install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var chosen = GitHubClient.PickAsset(names, null)
                      ?? ChooseFromList(owner, "Which file should be installed?", candidates);
            if (chosen is null) return;

            var asset = release.Assets.First(a => a.Name == chosen);
            InstallFromGitHub(owner, state, game, $"{ownerName}/{repoName}", release, asset);
        }
        // GitHubClient.GetLatestReleaseAsync und DownloadAssetAsync uebersetzen jeden Netzwerk-
        // Fehlschlag bereits in eine InvalidOperationException mit fertigem Text -- IOException/
        // UnauthorizedAccessException decken hier den schmalen Rest ab, den Installer.Sha256File
        // (in InstallFromGitHub, unmittelbar nach dem Download) auf rohem Dateisystemzugriff werfen
        // kann, etwa wenn ein Virenscanner die frisch heruntergeladene Datei kurz sperrt.
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException
                                        or IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"add from github failed for {ownerName}/{repoName}", e);
            MessageBox.Show(owner,
                SafeUserMessage(e, "Could not add the mod from GitHub. Check your internet connection, " +
                    "or that the downloaded file is not locked by another program, and try again."),
                "Could not read the release", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>True, wenn der Mod tatsaechlich installiert/aktualisiert wurde -- false fuer jeden
    /// Fall, in dem ApplyPackage das Paket ablehnt (kein Plugin gefunden), OHNE dass das eine
    /// Ausnahme war. Der Rueckgabewert existiert fuer die Stapelverarbeitung (Fix-Runde 1, Cheap
    /// Fix): ohne ihn hatte ein Aufrufer wie CheckUpdatesAsync keine Moeglichkeit, "abgelehnt" von
    /// "installiert" zu unterscheiden, und liess eine AvailableVersion stehen, die bei jedem
    /// kuenftigen Versuch wieder an derselben Ablehnung scheitern wuerde.</summary>
    private static bool InstallFromGitHub(IWin32Window owner, AppState state, GameInstall game,
                                          string repo, ReleaseInfo release, ReleaseAsset asset)
    {
        var file = GitHubClient.DownloadAssetAsync(asset, AppPaths.DownloadDir, CancellationToken.None)
                               .GetAwaiter().GetResult();

        var map = asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? PackageMapper.MapArchive(file)
            : PackageMapper.MapSingleFile(file);

        if (map.Rejection is not null)
        {
            MessageBox.Show(owner, map.Rejection + "\r\n\r\nNothing was installed.",
                            "Package refused", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        var sha = Installer.Sha256File(file);
        var trusted = state.TrustedRepos.Contains(repo, StringComparer.OrdinalIgnoreCase);
        if (!trusted)
        {
            // Sicherheitskontrolle, keine Formalitaet (Punkt 2 der Aufgabenstellung): Repo, Release-Tag,
            // Asset-Name+Groesse, SHA-256 und die vollstaendige Zielliste stehen HIER, bevor
            // ApplyPackage auch nur eine einzige Datei in den Spielordner schreibt. TrustedRepos wird
            // erst NACH einem "Ja" ergaenzt, nicht vorher.
            var targets = string.Join("\r\n", map.Files.Select(m => "  " + m.Target));
            var answer = MessageBox.Show(owner,
                $"Repository : {repo}\r\nRelease    : {release.Tag}\r\n" +
                $"File       : {asset.Name} ({FormatBytes(asset.Size)}, as declared by GitHub)\r\n" +
                $"SHA-256    : {sha}\r\n\r\nThese files will be written into your game folder:\r\n{targets}\r\n\r\n" +
                "This code will run inside the game. Only continue if you trust the author. Install?",
                "Confirm installation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return false;
            state.TrustedRepos.Add(repo);

            // Sofort sichern statt auf ApplyPackage's eigenen state.Save() am Ende zu vertrauen:
            // ApplyPackage kehrt fuer ein Paket ohne erkennbares Plugin VOR seinem state.Save() zurueck
            // (z. B. ein Repository, das nur die BepInEx-Laufzeit selbst veroeffentlicht, kein Plugin --
            // genau der in der Aufgabenstellung vorgeschlagene Testfall). Ohne dieses Save() hier ging
            // die gerade erst erteilte Vertrauensentscheidung beim naechsten Programmstart wieder
            // verloren, obwohl der Nutzer bereits "Ja" gesagt hatte -- kein Sicherheitsproblem (die
            // Richtung ist "erneut fragen", nie "ungefragt vertrauen"), aber unnoetig und beim
            // Hands-on-Test mit BepInEx/BepInEx (mehrere Plattform-Assets, keines davon ein Plugin)
            // tatsaechlich beobachtet.
            state.Save();
        }

        return ApplyPackage(owner, state, game, file, map, sourceKind: "github",
                            repo: repo, tag: release.Tag, assetName: asset.Name, etag: release.ETag);
    }

    // ---------- lokal ----------

    public static void InstallLocalPath(IWin32Window owner, AppState state, GameInstall game, string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                                       .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                                || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                InstallLocalPath(owner, state, game, f);
            return;
        }

        if (!File.Exists(path)) return;

        var map = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? PackageMapper.MapArchive(path)
            : PackageMapper.MapSingleFile(path);

        if (map.Rejection is not null)
        {
            MessageBox.Show(owner, map.Rejection + "\r\n\r\nNothing was installed.",
                            "Package refused", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ApplyPackage(owner, state, game, path, map, sourceKind: "local",
                     repo: null, tag: null, assetName: null, etag: null);
    }

    // ---------- gemeinsamer Installationsweg ----------

    /// <summary>Fuehrt ein zugeordnetes Paket tatsaechlich in den Spielordner ein. Rueckgabewert s.
    /// InstallFromGitHub-Kommentar: true nur, wenn wirklich etwas installiert wurde.</summary>
    private static bool ApplyPackage(IWin32Window owner, AppState state, GameInstall game,
                                     string packagePath, MapResult map, string sourceKind,
                                     string? repo, string? tag, string? assetName, string? etag)
    {
        var staging = Path.Combine(AppPaths.DownloadDir, "staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);

            // Quelle je Zuordnung materialisieren: aus dem Zip auspacken oder die Einzeldatei kopieren.
            var ops = new List<(string Source, string Target)>();
            if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(packagePath);
                foreach (var m in map.Files)
                {
                    var entry = zip.GetEntry(m.Entry);
                    if (entry is null) continue;
                    // Fix-Runde 1, I1 (wichtig): der relative Zielpfad wird unter staging GESPIEGELT,
                    // nicht auf staging\<Dateiname> abgeflacht. PackageMapper erlaubt ausdruecklich
                    // zwei Eintraege wie "BepInEx\plugins\ModA\Core.dll" UND
                    // "BepInEx\plugins\ModB\Core.dll" -- verschiedene Zielpfade, keine Ablehnung.
                    // Beide waeren vorher in derselben Nebendatei "staging\Core.dll" gelandet und
                    // haetten sich dort gegenseitig ueberschrieben: ModInspector.Read auf der zuletzt
                    // gewinnenden Kopie konnte den Mod dann unter der FALSCHEN GUID/Version
                    // registrieren, waehrend Installer.Apply gleich darauf trotzdem den richtigen
                    // Zielpfad korrekt beschreibt -- die Buchfuehrung wich von der Platte ab, ohne
                    // dass der gespeicherte SHA (der ja zum tatsaechlich geschriebenen Inhalt passt)
                    // je etwas davon verraten haette. Dieselbe Spiegelung, die Installer.Apply fuer
                    // seine eigene Sicherung schon benutzt (s. dortiger Kommentar).
                    var tmp = Path.Combine(staging, m.Target);
                    Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
                    entry.ExtractToFile(tmp, overwrite: true);
                    ops.Add((tmp, m.Target));
                }
            }
            else
            {
                ops.Add((packagePath, map.Files[0].Target));
            }

            // Fix-Runde 2, Punkt 3 (C1 lebte noch auf dem Install-Pfad, s. CanonicalizePluginTarget-
            // Kommentar): VOR der Identitaets-/Enabled-Bestimmung umschreiben, damit "mainDll" weiter
            // unten (aus "ops" ausgewaehlt) und "installed[i].Path" (von Installer.Apply aus
            // "ops" abgeleitet) beide bereits die kanonische Form tragen -- geschriebene Datei und
            // gespeicherter Pfad laufen so nie auseinander.
            for (var i = 0; i < ops.Count; i++)
            {
                var canonicalTarget = CanonicalizePluginTarget(ops[i].Target);
                if (canonicalTarget != ops[i].Target) ops[i] = (ops[i].Source, canonicalTarget);
            }

            // Identitaet aus der ersten DLL mit BepInPlugin-Attribut.
            PluginInfo? info = null;
            string? mainDll = null;
            foreach (var (source, target) in ops.Where(o => o.Target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                info = ModInspector.Read(source);
                if (info is null) continue;
                mainDll = target;
                break;
            }

            if (info is null)
            {
                MessageBox.Show(owner,
                    "No BepInEx plugin was found in this package. Install it manually if you are sure it belongs here.",
                    "Not a plugin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // Fix-Runde 1, I2: der eigentliche Schreibpunkt. Guard() in MainForm (bzw. die Pruefung
            // im jeweiligen Aufrufer von ApplyPackage) laeuft VOR einem unter Umstaenden lange
            // offenen, unbeschraenkten Dialog dazwischen -- der OpenFileDialog bei "Add local…", der
            // URL-Prompt plus Netzwerk plus Vertrauensdialog bei "Add from GitHub…", die
            // Bestaetigung bei Remove. Der Nutzer kann das Spiel genau in diesem Fenster starten.
            // Hier, unmittelbar vor Installer.Apply, ist der letztmoegliche und damit einzig
            // verlaessliche Ort fuer diese Pruefung.
            if (GameLocator.IsGameRunning())
            {
                MessageBox.Show(owner,
                    "Star Trek Fleet Command is running. Close it completely, then try again.",
                    "Game is running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var installed = Installer.Apply(game.Root, ops);

            var existing = state.Mods.FirstOrDefault(m => m.Id.Equals(info.Guid, StringComparison.OrdinalIgnoreCase));
            var entry2 = existing ?? new ModEntry { Id = info.Guid };
            entry2.Name = info.Name;
            entry2.Version = info.Version;
            entry2.Enabled = true;
            entry2.SourceKind = sourceKind;
            entry2.Repo = repo ?? entry2.Repo;
            entry2.ReleaseTag = tag ?? entry2.ReleaseTag;
            entry2.AssetName = assetName ?? entry2.AssetName;
            entry2.ETag = etag ?? entry2.ETag;
            entry2.InstalledAt = DateTimeOffset.UtcNow;
            entry2.InstalledAgainstClientBuild = GameLocator.ReadClientBuild(game.Root);
            entry2.AvailableVersion = null;
            entry2.Files = installed.Where(f => f.Path == mainDll).ToList();
            if (existing is null) state.Mods.Add(entry2);

            // Alles andere aus dem Paket ist Beiwerk und wird geteilt verbucht.
            foreach (var f in installed.Where(f => f.Path != mainDll))
                Installer.RegisterShared(state, f.Path, f.Sha256,
                    Installer.FileVersionOf(Path.Combine(game.Root, f.Path)) ?? "0.0.0", entry2.Id);

            state.Save();
            AppLog.Info($"installed {info.Guid} {info.Version} from {sourceKind}");
            return true;
        }
        catch (InstallRollbackException ex)
        {
            // Muss vor dem generischen Fang stehen (Punkt 3 der Aufgabenstellung): Installer.Apply
            // kann mitten in der Installation scheitern UND sein eigenes Rollback nicht vollstaendig
            // durchfuehren -- der Nutzer muss dann erfahren, WELCHE Dateien betroffen sind und WO die
            // Sicherung liegt, nicht nur "etwas ist schiefgegangen".
            AppLog.Error("install left files stuck", ex);
            ShowRollbackFailure(owner, ex, "Installation failed");
            return false;
        }
        // Installer.Apply's Vorflugpruefungen werfen je nach Grund entweder InvalidOperationException
        // (ein Ziel entkaeme dem Spielordner, auch ueber einen Reparse-Point) oder ArgumentException
        // (doppeltes Ziel, ein Ziel ist bereits ein Verzeichnis, ein Ziel endet auf einen
        // Verzeichnistrenner) -- beide sind bereits als eigene Vorbedingungs-Ablehnung gedacht.
        // InvalidDataException faengt eine beschaedigte oder manipulierte Zip-Datei bei
        // ExtractToFile ab (PackageMapper.MapArchive faengt sie fuer das reine LESEN der Eintragsliste
        // bereits selbst, ExtractToFile hier ist ein zweiter, unabhaengiger Lesevorgang). IOException/
        // UnauthorizedAccessException decken das eigentliche Dateisystem ab (Zielordner nicht
        // beschreibbar, Datei vom laufenden Spiel gesperrt).
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException
                                        or InvalidDataException or ArgumentException)
        {
            AppLog.Error("install failed", e);
            MessageBox.Show(owner,
                SafeUserMessage(e, "Installation failed. The package may be corrupted, or a file could " +
                    "not be written because it is locked by the game or another program. See the log for details."),
                "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
        }
    }

    // ---------- Aktualisierung ----------

    public static async Task CheckUpdatesAsync(IWin32Window owner, AppState state, GameInstall game)
    {
        var updated = new List<string>();
        var failed = new List<string>();

        foreach (var mod in state.Mods.Where(m => m.SourceKind == "github" && m.Repo is not null).ToList())
        {
            var parsed = GitHubClient.ParseRepoUrl("https://github.com/" + mod.Repo);
            if (parsed is null) continue;

            try
            {
                var release = await GitHubClient.GetLatestReleaseAsync(
                    parsed.Value.Owner, parsed.Value.Repo, mod.ETag, state.GitHubToken, CancellationToken.None);

                if (release is null) continue;                     // 304, nichts Neues
                mod.ETag = release.ETag;
                if (release.Tag.TrimStart('v', 'V') == mod.Version) continue;

                mod.AvailableVersion = release.Tag.TrimStart('v', 'V');
                updated.Add($"{mod.Name}: {mod.Version} → {mod.AvailableVersion}");
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                AppLog.Error($"update check failed for {mod.Repo}", e);
                // Fix-Runde 1, I5: GetLatestReleaseAsync uebersetzt einen Fehlschlag beim ANFORDERN
                // der Antwort in eine InvalidOperationException mit fertigem Text -- aber NICHT eine
                // Zeitueberschreitung WAEHREND res.Content.ReadAsStringAsync (dessen eigener Fang
                // deckt nur IOException/HttpRequestException ab, s. GitHubClient.cs). Eine so
                // entstehende rohe TaskCanceledException traegt Framework-Text (auf diesem Rechner
                // deutsch) -- die eine Stelle in beiden Dateien, die bisher unter dem eigenen
                // SafeUserMessage-Grundsatz durchgerutscht war.
                failed.Add($"{mod.Name}: {SafeUserMessage(e, "could not be checked (network problem). See the log for details.")}");
            }
        }

        state.Save();

        if (updated.Count == 0 && failed.Count == 0)
        {
            MessageBox.Show(owner, "All mods are up to date.", "Check updates",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message = (updated.Count > 0 ? "Updates available:\r\n" + string.Join("\r\n", updated) + "\r\n\r\n" : "")
                    + (failed.Count > 0 ? "Could not check:\r\n" + string.Join("\r\n", failed) : "");

        if (updated.Count == 0)
        {
            MessageBox.Show(owner, message, "Check updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (MessageBox.Show(owner, message + "\r\nInstall the updates now?", "Check updates",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        await InstallPendingUpdatesAsync(owner, state, game);
    }

    /// <summary>Spec §12: eigenstaendiger "Update all"-Weg -- installiert alles, was bereits eine
    /// AvailableVersion traegt (aus einem frueheren "Check updates"), ohne selbst erneut beim GitHub
    /// abzufragen, ob es ueberhaupt etwas Neues gibt.</summary>
    public static async Task UpdateAllAsync(IWin32Window owner, AppState state, GameInstall game)
    {
        var pending = state.Mods.Where(m => m.AvailableVersion is not null).ToList();
        if (pending.Count == 0)
        {
            MessageBox.Show(owner, "No updates are pending. Use 'Check updates' first.", "Update all",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var list = string.Join("\r\n", pending.Select(m => $"  {m.Name}: {m.Version} -> {m.AvailableVersion}"));
        if (MessageBox.Show(owner, "Install these updates now?\r\n\r\n" + list, "Update all",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        await InstallPendingUpdatesAsync(owner, state, game);
    }

    /// <summary>Installiert alle Mods mit gesetzter AvailableVersion -- der gemeinsame
    /// Installationsteil von CheckUpdatesAsync (nach "Ja, jetzt installieren") und UpdateAllAsync.</summary>
    private static async Task InstallPendingUpdatesAsync(IWin32Window owner, AppState state, GameInstall game)
    {
        var installFailed = new List<string>();
        var skipped = new List<string>();
        var stoppedForRunningGame = false;

        foreach (var mod in state.Mods.Where(m => m.AvailableVersion is not null).ToList())
        {
            // Fix-Runde 1, I2: einmalig VOR der Schleife geprueft reichte nicht ("die Pruefung ist
            // an sich richtig platziert, aber nur einmal fuer einen ganzen Stapel ausgewertet") --
            // jede Iteration macht durch GitHubClient (Netzwerk) und ApplyPackage selbst Zeit
            // vergehen, in der das Spiel zwischen zwei Mods gestartet werden kann. ApplyPackage
            // prueft am eigentlichen Schreibpunkt zwar noch einmal (zweite Verteidigungslinie), aber
            // ohne diese Pruefung HIER wuerde ein spaeter in der Schleife startendes Spiel erst nach
            // einem bereits begonnenen Download bemerkt, und die Sammelmeldung am Ende erklaerte
            // nicht, warum der Rest der Liste nie versucht wurde.
            if (GameLocator.IsGameRunning()) { stoppedForRunningGame = true; break; }

            var parsed = GitHubClient.ParseRepoUrl("https://github.com/" + mod.Repo);
            if (parsed is null) continue;
            try
            {
                var release = await GitHubClient.GetLatestReleaseAsync(
                    parsed.Value.Owner, parsed.Value.Repo, null, state.GitHubToken, CancellationToken.None);
                if (release is null)
                {
                    // Cheap Fix: vorher ein stilles "continue" -- der Nutzer sah nie, dass und warum
                    // ein angekuendigtes Update nicht installiert wurde.
                    skipped.Add($"{mod.Name}: GitHub returned nothing to install");
                    continue;
                }

                var names = release.Assets.Select(a => a.Name).ToList();
                // Fix-Runde 1, I3: dieselbe Regel wie im interaktiven Pfad (s. AddFromGitHub).
                if (GitHubClient.InstallableCandidates(names).Count == 0)
                {
                    skipped.Add($"{mod.Name}: the latest release has no installable file");
                    mod.AvailableVersion = null;   // ein erneuter Versuch traefe auf dieselbe Sackgasse
                    continue;
                }

                var name = GitHubClient.PickAsset(names, mod.AssetName);
                if (name is null)
                {
                    skipped.Add($"{mod.Name}: multiple files could not be told apart automatically -- " +
                                "use 'Update' from the right-click menu to choose one");
                    mod.AvailableVersion = null;
                    continue;
                }

                var applied = InstallFromGitHub(owner, state, game, mod.Repo!, release,
                                                release.Assets.First(a => a.Name == name));
                // Cheap Fix: ApplyPackage lehnt ein Paket (z. B. "kein Plugin gefunden") ohne eigene
                // Ausnahme ab -- ohne diese Zeile blieb AvailableVersion stehen und jeder kuenftige
                // "Update all"-Lauf haette denselben Download nur wiederholt, um an derselben Stelle
                // erneut abgelehnt zu werden, ohne dass der Nutzer je erfuhr, warum nichts passiert.
                if (!applied) mod.AvailableVersion = null;
            }
            // Wie in AddFromGitHub: InstallFromGitHub ruft nach dem Download Installer.Sha256File auf
            // rohem Dateisystemzugriff auf, deshalb hier zusaetzlich zu den Netzwerk-Ausnahmen auch
            // IOException/UnauthorizedAccessException. Kein MessageBox pro fehlgeschlagenem Mod -- das
            // waere bei mehreren Aktualisierungen eine Flut von Dialogen mitten in einer Stapel-
            // verarbeitung -- stattdessen sammeln und am Ende EINE Zusammenfassung zeigen, damit der
            // Nutzer nicht einfach nur nichts passieren sieht.
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException
                                            or IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"update failed for {mod.Repo}", e);
                installFailed.Add(mod.Name);
                // Cheap Fix: derselbe Grund wie oben -- ohne diese Zeile haette der naechste
                // Update-Lauf denselben (womoeglich dauerhaften) Fehlschlag stillschweigend
                // wiederholt. Ein transientes Problem (kurzzeitig gesperrte Datei) verliert dadurch
                // seine "update to X"-Anzeige bis zum naechsten "Check updates" -- das ist der
                // bewusst in Kauf genommene, kleinere Nachteil gegenueber einer fuer immer falschen
                // Statusspalte.
                mod.AvailableVersion = null;
            }
        }

        state.Save();

        var messages = new List<string>();
        if (stoppedForRunningGame)
            messages.Add("Star Trek Fleet Command started running during the update -- the remaining updates were skipped.");
        if (installFailed.Count > 0)
            messages.Add("Some updates could not be installed:\r\n" + string.Join("\r\n", installFailed));
        if (skipped.Count > 0)
            messages.Add("Some updates were skipped:\r\n" + string.Join("\r\n", skipped));

        if (messages.Count > 0)
            MessageBox.Show(owner, string.Join("\r\n\r\n", messages) + "\r\n\r\nSee the log for details.",
                            "Check updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Spec §12: "Update" im Kontextmenue fuer genau einen Mod -- prueft und installiert,
    /// ohne den ganzen Bestand anzufassen. Zeigt (anders als der Stapelweg in
    /// InstallPendingUpdatesAsync) bei Mehrdeutigkeit den Auswahldialog statt nur zu ueberspringen,
    /// weil hier ohnehin nur ein einziger Mod im Spiel ist -- fuer genau den Fall, den der Stapelweg
    /// per "use 'Update' from the right-click menu" an diese Stelle verweist.</summary>
    public static async Task UpdateSingleAsync(IWin32Window owner, AppState state, GameInstall game, ModEntry mod)
    {
        if (mod.Repo is null) return;
        var parsed = GitHubClient.ParseRepoUrl("https://github.com/" + mod.Repo);
        if (parsed is null) return;

        ReleaseInfo? release;
        try
        {
            release = await GitHubClient.GetLatestReleaseAsync(
                parsed.Value.Owner, parsed.Value.Repo, null, state.GitHubToken, CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            AppLog.Error($"update failed for {mod.Repo}", e);
            MessageBox.Show(owner,
                SafeUserMessage(e, "Could not check for an update. Check your internet connection and try again."),
                "Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (release is null)
        {
            MessageBox.Show(owner, $"{mod.Name} is already up to date.", "Update",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var latest = release.Tag.TrimStart('v', 'V');
        if (latest == mod.Version)
        {
            MessageBox.Show(owner, $"{mod.Name} is already up to date ({mod.Version}).", "Update",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var names = release.Assets.Select(a => a.Name).ToList();
        var candidates = GitHubClient.InstallableCandidates(names);
        if (candidates.Count == 0)
        {
            MessageBox.Show(owner, "The latest release has no installable .zip or .dll file.", "Update",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var chosen = GitHubClient.PickAsset(names, mod.AssetName)
                  ?? ChooseFromList(owner, "Which file should be installed?", candidates);
        if (chosen is null) return;

        // Fix-Runde 1, I2: derselbe Grund wie ueberall sonst -- die Netzwerkabfrage plus ein
        // moeglicher Auswahldialog oben lagen zwischen dem letzten Guard() in MainForm und hier.
        if (GameLocator.IsGameRunning())
        {
            MessageBox.Show(owner,
                "Star Trek Fleet Command is running. Close it completely, then try again.",
                "Game is running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        InstallFromGitHub(owner, state, game, mod.Repo, release, release.Assets.First(a => a.Name == chosen));
    }

    public static void SetUpdateSource(IWin32Window owner, AppState state, ModEntry mod, Action onDone)
    {
        var url = Prompt(owner, "Set update source",
                         $"GitHub repository that publishes {mod.Name}",
                         mod.Repo is null ? "https://github.com/" : "https://github.com/" + mod.Repo);
        if (url is null) return;

        var parsed = GitHubClient.ParseRepoUrl(url);
        if (parsed is null)
        {
            MessageBox.Show(owner, "That is not an https github.com repository URL.", "Invalid URL",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        mod.Repo = $"{parsed.Value.Owner}/{parsed.Value.Repo}";
        mod.SourceKind = "github";
        mod.ETag = null;
        state.Save();
        onDone();
    }

    // ---------- BepInEx-Laufzeit ----------

    /// <summary>Modaler Fortschrittsdialog fuer die BepInEx-Laufzeit-Installation (rund 33 MB, s.
    /// Punkt 5 der Aufgabenstellung). Laeuft echt asynchron statt ueber .GetAwaiter().GetResult(): ein
    /// Download dieser Groesse kann Sekunden bis (bei einer langsamen Leitung) einige Minuten dauern,
    /// und ein blockierender Aufruf wuerde das Fenster fuer die gesamte Zeit vollstaendig einfrieren
    /// lassen. Stattdessen laeuft die Installation im Hintergrund, waehrend form.ShowDialog() die
    /// Nachrichtenschleife fuer DIESEN modalen Dialog am Laufen haelt -- die "Cancel"-Schaltflaeche
    /// bleibt die ganze Zeit ueber bedienbar, ein steckengebliebener Download laesst die Anwendung
    /// also nie ohne Ausweg zurueck (BepInExRuntime.DownloadArchiveAsync hat zudem ein eigenes,
    /// 20-sekuendiges Leerlauf-Zeitlimit je Datenblock als zweite Absicherung).</summary>
    public static async Task InstallBepInExAsync(IWin32Window owner, GameInstall game)
    {
        const int contentWidth = 440;
        var (form, body) = DialogShell("Installing BepInEx", contentWidth, controlBox: false);
        using var _ = form;
        // Mindesthoehe, damit der Dialog nicht bei jeder Statusmeldung springt; MaximumSize-Breite,
        // damit die lange Schlussmeldung umbricht statt abgeschnitten zu werden.
        var status = new Label
        {
            Text = "Starting…", AutoSize = true,
            MaximumSize = new Size(contentWidth, 0), MinimumSize = new Size(contentWidth, 60)
        };
        var cancel = new Button { Text = "Cancel" };
        body.Controls.Add(status);
        body.Controls.Add(ButtonRow(cancel));

        using var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => { status.Text = "Cancelling…"; cancel.Enabled = false; cts.Cancel(); };

        // Progress<T> nimmt den SynchronizationContext bei der Konstruktion auf -- hier der des
        // UI-Threads, weil dieser Konstruktor selbst auf dem UI-Thread laeuft. Jeder Report()-Aufruf
        // aus dem Hintergrund-Task landet damit korrekt ueber den Windows-Message-Loop auf dem
        // UI-Thread, nicht direkt auf dem Downloader-Thread.
        var progress = new Progress<string>(s => status.Text = s);

        Exception? failure = null;
        var cancelled = false;
        // Fix-Runde 1, I4: getrennt von "cancelled"/"failure" -- die einzige Frage, die FormClosing
        // braucht, ist "darf dieses Schliessen durch", nicht warum die Aktion endete.
        var finished = false;

        // form.Shown statt den Download vor ShowDialog() zu starten: ShowDialog() blockiert den
        // Aufrufer, bis der Dialog geschlossen wird -- der Download muss also aus dem Dialog HERAUS
        // gestartet werden, nachdem er sichtbar ist, und sich am Ende selbst schliessen.
        form.Shown += async (_, _) =>
        {
            try
            {
                await BepInExRuntime.InstallAsync(game, progress, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (InvalidOperationException ex)
            {
                // BepInExRuntime.InstallAsync uebersetzt jeden Download- oder Entpack-Fehlschlag
                // bereits in eine InvalidOperationException mit fertigem, englischem Text.
                failure = ex;
            }
            // Dies ist ein async-void-Ereignishandler (Form.Shown): jede hier nicht gefangene
            // Ausnahme verlaesst den Callback ungefangen und toetet den gesamten Prozess -- es gibt
            // keinen synchronen Aufrufer mehr, der sie stattdessen fangen koennte. Anders als bei
            // einer normal aufgerufenen Methode ist dieses Auffangnetz hier keine Bequemlichkeit,
            // sondern die einzige Moeglichkeit, ueberhaupt eine Fehlermeldung statt eines
            // kommentarlosen Absturzes zu zeigen. ex.Message wird bewusst NICHT verwendet (anders als
            // oben): eine Ausnahmeart, die BepInExRuntime.InstallAsync laut seinem eigenen Vertrag
            // nicht mehr selbst uebersetzt, kann auch rohen, OS-lokalisierten Text tragen.
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                finished = true;
                form.Close();
            }
        };

        // Fix-Runde 1, I4 (wichtig): Alt+F4 sendet WM_SYSCOMMAND/SC_CLOSE, das ShowDialog() auch bei
        // ControlBox = false sofort beendet -- reproduziert. Ohne diesen Handler kehrte ShowDialog()
        // sofort zurueck, WAEHREND BepInExRuntime.InstallAsync im Hintergrund weiter in den
        // Spielordner schreibt: weder "failure" noch "cancelled" wurden je gesetzt, der Nutzer sah
        // "BepInEx was installed" mitten in einem noch laufenden Schreibvorgang, RefreshUi meldete
        // gleich danach "not installed", und ein spaeterer Fehlschlag landete in einer Variable, die
        // niemand mehr liest. Der Handler bricht die Operation genauso ab wie der Cancel-Knopf, laesst
        // den Dialog aber offen, bis der Hintergrund-Task tatsaechlich fertig ist (erkennbar an
        // "finished", das der finally-Block oben setzt, bevor er form.Close() SELBST aufruft) -- erst
        // dieses zweite, programmatische Close() darf durch.
        form.FormClosing += (_, e) =>
        {
            if (finished) return;                    // echtes Ende: durchlassen
            e.Cancel = true;
            status.Text = "Cancelling…";
            cancel.Enabled = false;
            cts.Cancel();
        };

        form.ShowDialog(owner);

        if (failure is not null)
        {
            AppLog.Error("BepInEx install failed", failure);
            MessageBox.Show(owner,
                SafeUserMessage(failure, "Could not install BepInEx. See the log for details."),
                "Could not install BepInEx", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else if (!cancelled)
        {
            MessageBox.Show(owner,
                "BepInEx was installed. The first game start afterwards will take several minutes " +
                "while it generates its interop assemblies.",
                "BepInEx installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
