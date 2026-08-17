using System.IO.Compression;
using StfcModManager.Core;

namespace StfcModManager.Ui;

/// <summary>Alle modalen Abfragen und der Installationsweg dahinter.</summary>
public static class Dialogs
{
    // ---------- kleine generische Bausteine ----------

    private static string? Prompt(IWin32Window owner, string title, string label, string initial = "")
    {
        using var form = new Form
        {
            Text = title, Width = 560, Height = 170,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false
        };
        var text = new Label { Text = label, Left = 12, Top = 12, Width = 520 };
        var box = new TextBox { Left = 12, Top = 40, Width = 520, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 356, Top = 76, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 452, Top = 76, Width = 80 };
        form.Controls.AddRange([text, box, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    private static string? ChooseFromList(IWin32Window owner, string title, IReadOnlyList<string> options)
    {
        using var form = new Form
        {
            Text = title, Width = 480, Height = 320,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false
        };
        var list = new ListBox { Left = 12, Top = 12, Width = 440, Height = 210 };
        list.Items.AddRange(options.ToArray());
        if (options.Count > 0) list.SelectedIndex = 0;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 276, Top = 234, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 372, Top = 234, Width = 80 };
        form.Controls.AddRange([list, ok, cancel]);
        form.AcceptButton = ok;
        return form.ShowDialog(owner) == DialogResult.OK ? list.SelectedItem as string : null;
    }

    /// <summary>Nur fuer diese beiden Ausnahmearten ist e.Message garantiert eine von Core selbst
    /// verfasste, englische, betriebssystemfreie Meldung -- die gesamte Core-Schicht (GitHubClient,
    /// BepInExRuntime, SupportBundle, Installer.InstallRollbackException) existiert genau dafuer,
    /// rohe I/O- und Netzwerkfehler in genau so einen Text zu uebersetzen, bevor sie eine
    /// oeffentliche Methode verlassen. Fuer jede andere Ausnahmeart (IOException,
    /// UnauthorizedAccessException, ein ArgumentException aus Installer.Apply's Vorflug,
    /// InvalidDataException aus einer beschaedigten Zip-Datei, ...) waere e.Message dagegen
    /// potenziell die rohe, ggf. vom Betriebssystem lokalisierte Meldung -- genau das, was laut
    /// Vorgabe nie vor dem Nutzer landen darf. Ein fest formulierter Ausweichtext haelt diese
    /// Zusicherung unabhaengig davon ein, welche konkrete Ausnahmeart ein Aufrufer im Einzelfall
    /// tatsaechlich auffaengt; die vollen Details landen ueber AppLog.Error in jedem Fall zusaetzlich
    /// im Log.</summary>
    private static string SafeUserMessage(Exception e, string fallback) =>
        e is InvalidOperationException or InstallRollbackException ? e.Message : fallback;

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
            var chosen = GitHubClient.PickAsset(names, null)
                      ?? ChooseFromList(owner, "Which file should be installed?", names);
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

    private static void InstallFromGitHub(IWin32Window owner, AppState state, GameInstall game,
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
            return;
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
                $"Repository : {repo}\r\nRelease    : {release.Tag}\r\nFile       : {asset.Name} ({asset.Size} bytes)\r\n" +
                $"SHA-256    : {sha}\r\n\r\nThese files will be written into your game folder:\r\n{targets}\r\n\r\n" +
                "This code will run inside the game. Only continue if you trust the author. Install?",
                "Confirm installation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
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

        ApplyPackage(owner, state, game, file, map, sourceKind: "github",
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

    private static void ApplyPackage(IWin32Window owner, AppState state, GameInstall game,
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
                    var tmp = Path.Combine(staging, Path.GetFileName(m.Target));
                    entry.ExtractToFile(tmp, overwrite: true);
                    ops.Add((tmp, m.Target));
                }
            }
            else
            {
                ops.Add((packagePath, map.Files[0].Target));
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
                return;
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
        }
        catch (InstallRollbackException ex)
        {
            // Muss vor dem generischen Fang stehen (Punkt 3 der Aufgabenstellung): Installer.Apply
            // kann mitten in der Installation scheitern UND sein eigenes Rollback nicht vollstaendig
            // durchfuehren -- der Nutzer muss dann erfahren, WELCHE Dateien betroffen sind und WO die
            // Sicherung liegt, nicht nur "etwas ist schiefgegangen".
            AppLog.Error("install left files stuck", ex);
            ShowRollbackFailure(owner, ex, "Installation failed");
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
            // GetLatestReleaseAsync uebersetzt jeden Fehlschlag in eine InvalidOperationException mit
            // fertigem Text -- e.Message ist hier deshalb sicher direkt anzuzeigen.
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                AppLog.Error($"update check failed for {mod.Repo}", e);
                failed.Add($"{mod.Name}: {e.Message}");
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

        // Punkt 1 der Aufgabenstellung: JEDE destruktive Aktion muss blockiert sein, waehrend das
        // Spiel laeuft. Der erste Teil dieser Methode (Releases abfragen) ist reine Netzwerkarbeit
        // und braucht diese Pruefung nicht -- aber ab hier werden tatsaechlich Dateien in den
        // Spielordner geschrieben, und MainForm.OnCheckUpdates ruft (anders als bei jeder anderen
        // destruktiven Aktion) kein Guard() auf, weil das Pruefen selbst nicht blockiert werden soll.
        // Die Pruefung gehoert deshalb GENAU HIERHIN: unmittelbar vor der ersten schreibenden
        // Operation, nicht schon vor dem Abfragen der Releases (das wuerde harmlose Anfragen unnoetig
        // verweigern) und nicht in MainForm (das wuerde auch das reine Pruefen blockieren).
        if (GameLocator.IsGameRunning())
        {
            MessageBox.Show(owner,
                "Star Trek Fleet Command is running. Close it completely, then check for updates again.",
                "Game is running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var installFailed = new List<string>();
        foreach (var mod in state.Mods.Where(m => m.AvailableVersion is not null).ToList())
        {
            var parsed = GitHubClient.ParseRepoUrl("https://github.com/" + mod.Repo);
            if (parsed is null) continue;
            try
            {
                var release = await GitHubClient.GetLatestReleaseAsync(
                    parsed.Value.Owner, parsed.Value.Repo, null, state.GitHubToken, CancellationToken.None);
                if (release is null) continue;

                var name = GitHubClient.PickAsset(release.Assets.Select(a => a.Name).ToList(), mod.AssetName);
                if (name is null) continue;

                InstallFromGitHub(owner, state, game, mod.Repo!, release,
                                  release.Assets.First(a => a.Name == name));
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
            }
        }

        if (installFailed.Count > 0)
            MessageBox.Show(owner,
                "Some updates could not be installed:\r\n" + string.Join("\r\n", installFailed) +
                "\r\n\r\nSee the log for details.",
                "Check updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        using var form = new Form
        {
            Text = "Installing BepInEx", Width = 480, Height = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ControlBox = false
        };
        var status = new Label { Left = 12, Top = 16, Width = 440, Height = 60, Text = "Starting…" };
        var cancel = new Button { Text = "Cancel", Left = 380, Top = 84, Width = 80 };
        form.Controls.AddRange([status, cancel]);

        using var cts = new CancellationTokenSource();
        cancel.Click += (_, _) => { status.Text = "Cancelling…"; cancel.Enabled = false; cts.Cancel(); };

        // Progress<T> nimmt den SynchronizationContext bei der Konstruktion auf -- hier der des
        // UI-Threads, weil dieser Konstruktor selbst auf dem UI-Thread laeuft. Jeder Report()-Aufruf
        // aus dem Hintergrund-Task landet damit korrekt ueber den Windows-Message-Loop auf dem
        // UI-Thread, nicht direkt auf dem Downloader-Thread.
        var progress = new Progress<string>(s => status.Text = s);

        Exception? failure = null;
        var cancelled = false;

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
                form.Close();
            }
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
