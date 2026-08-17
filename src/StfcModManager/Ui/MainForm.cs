using StfcModManager.Core;

namespace StfcModManager.Ui;

/// <summary>Hauptfenster. Bewusst ohne Designer-Datei -- der Aufbau steht im Konstruktor, das ist
/// bei einem Fenster uebersichtlicher als zwei Dateien.</summary>
public sealed class MainForm : Form
{
    private readonly Label _header = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 0) };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _mods = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
    private readonly ListView _problems = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(6) };

    private AppState _state = new();
    private GameInstall? _game;
    private bool _suppressCheckEvents;

    public MainForm()
    {
        Text = "STFC Mod Manager";
        Width = 940;
        Height = 620;
        MinimumSize = new Size(720, 480);
        AllowDrop = true;

        _mods.Columns.Add("Mod", 240);
        _mods.Columns.Add("Version", 90);
        _mods.Columns.Add("Source", 260);
        _mods.Columns.Add("Status", 220);
        _mods.ItemChecked += OnModChecked;
        _mods.MouseUp += OnModsMouseUp;

        _problems.Columns.Add("Severity", 80);
        _problems.Columns.Add("Finding", 480);
        _problems.Columns.Add("What to do", 320);

        var modsTab = new TabPage("Mods");
        modsTab.Controls.Add(_mods);
        var problemsTab = new TabPage("Problems");
        problemsTab.Controls.Add(_problems);
        _tabs.TabPages.Add(modsTab);
        _tabs.TabPages.Add(problemsTab);

        AddButton("Add from GitHub…", OnAddFromGitHub);
        AddButton("Add local…", OnAddLocal);
        AddButton("Check updates", OnCheckUpdates);
        AddButton("Rescan", (_, _) => { Rescan(); RefreshUi(); });
        // BepInExRuntime.InstallAsync existiert seit dem letzten Core-Task, wird aber von keinem der
        // Aufgaben-Entwuerfe verdrahtet -- ohne diesen Knopf haette HealthCheck.Run's Abhilfetext
        // "Press 'Install BepInEx'" (s. HealthCheck.cs, Pruefung 3) kein Ziel, auf das er verweisen
        // koennte.
        AddButton("Install BepInEx…", OnInstallBepInEx);
        AddButton("Generate support package", OnSupportPackage);
        AddButton("Change game folder…", OnChangeFolder);

        Controls.Add(_tabs);
        Controls.Add(_buttons);
        Controls.Add(_header);

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += OnDragDrop;

        Load += (_, _) => { LoadState(); Rescan(); RefreshUi(); };
    }

    private void AddButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 30 };
        b.Click += onClick;
        _buttons.Controls.Add(b);
    }

    private void LoadState()
    {
        _state = AppState.Load();
        _state.GamePath ??= GameLocator.Detect();

        if (_state.GamePath is null)
        {
            MessageBox.Show(this,
                "The Star Trek Fleet Command folder could not be found. Pick the folder that contains prime.exe.",
                "Game folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OnChangeFolder(this, EventArgs.Empty);
            return;
        }

        _game = new GameInstall(_state.GamePath);
        Directory.CreateDirectory(AppPaths.LocalMods);
    }

    /// <summary>Adoption nach Spec §7: vorhandenen Bestand uebernehmen statt daneben installieren.</summary>
    public void Rescan()
    {
        if (_game is null) return;
        AdoptFromDisk(_state, _game);
        _state.LastKnownClientBuild = GameLocator.ReadClientBuild(_game.Root);
        _state.Save();
    }

    public static void AdoptFromDisk(AppState state, GameInstall game)
    {
        var known = state.Mods.SelectMany(m => m.Files)
                              .Select(f => Path.GetFileName(f.Path))
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (dir, enabled, rel) in new[]
                 {
                     (game.Plugins,         true,  Path.Combine("BepInEx", "plugins")),
                     (game.PluginsDisabled, false, Path.Combine("BepInEx", "plugins-disabled"))
                 })
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                var name = Path.GetFileName(dll);
                if (known.Contains(name)) continue;

                var info = ModInspector.Read(dll);
                if (info is null) continue;                     // geteilte Bibliothek, nicht anfassen
                if (state.Mods.Any(m => m.Id.Equals(info.Guid, StringComparison.OrdinalIgnoreCase))) continue;

                state.Mods.Add(new ModEntry
                {
                    Id = info.Guid,
                    Name = info.Name,
                    Version = info.Version,
                    Enabled = enabled,
                    SourceKind = "adopted",
                    Files = { new InstalledFile { Path = Path.Combine(rel, name), Sha256 = Installer.Sha256File(dll) } },
                    InstalledAgainstClientBuild = GameLocator.ReadClientBuild(game.Root)
                });
                AppLog.Info($"adopted {info.Guid} {info.Version} from {rel}");
            }
        }

        // Die native Community-Mod hat keine Metadaten und wird ueber den Dateinamen erfasst.
        var nativePresent = File.Exists(game.VersionDll) || File.Exists(game.VersionDllDisabled);
        var nativeEntry = state.Mods.FirstOrDefault(m => m.SourceKind == "native");

        switch (DecideNativeModAction(nativePresent, nativeEntry is not null))
        {
            case NativeModAction.Remove:
                state.Mods.Remove(nativeEntry!);
                break;
            case NativeModAction.Add:
                state.Mods.Add(new ModEntry
                {
                    Id = "community-patch",
                    Name = "Community Mod (version.dll)",
                    Version = "—",
                    Enabled = File.Exists(game.VersionDll),
                    SourceKind = "native"
                });
                break;
            case NativeModAction.UpdateEnabled:
                nativeEntry!.Enabled = File.Exists(game.VersionDll);
                break;
            case NativeModAction.None:
                break;
        }
    }

    /// <summary>Ergebnis von DecideNativeModAction -- s. dort.</summary>
    internal enum NativeModAction { None, Remove, Add, UpdateEnabled }

    /// <summary>Reine Entscheidung fuer den Eintrag der nativen Community-Mod, von den beiden
    /// File.Exists-Aufrufen getrennt, damit sie ohne Dateisystem in SelfTest.cs pruefbar ist.
    ///
    /// Ersetzt eine Drei-Zweige-Kette, deren dritter Zweig ("!nativePresent &amp;&amp; nativeEntry is
    /// not null") unerreichbar war: der ZWEITE Zweig ("nativeEntry is not null") fing bereits JEDEN
    /// Fall mit vorhandenem Eintrag ab, unabhaengig von nativePresent -- ein geloeschtes version.dll
    /// UND version.dll_ liess den Eintrag deshalb fuer immer in state.Mods stehen, statt ihn zu
    /// entfernen. Die Vier-Fall-Tabelle hier ist vollstaendig und ueberschneidungsfrei: nativePresent
    /// entscheidet zuerst zwischen "entfernen falls vorhanden" und "vorhanden halten/anlegen",
    /// hasExistingEntry erst danach zwischen den beiden Auspraegungen je Seite.</summary>
    internal static NativeModAction DecideNativeModAction(bool nativePresent, bool hasExistingEntry)
    {
        if (!nativePresent) return hasExistingEntry ? NativeModAction.Remove : NativeModAction.None;
        return hasExistingEntry ? NativeModAction.UpdateEnabled : NativeModAction.Add;
    }

    public void RefreshUi()
    {
        if (_game is null) return;

        var running = GameLocator.IsGameRunning();
        _header.Text =
            $"Game: {_game.Root}\r\n" +
            $"Client {GameLocator.ReadClientBuild(_game.Root)}  ·  " +
            $"BepInEx {BepInExRuntime.Detect(_game) ?? "not installed"}  ·  " +
            (running ? "game is RUNNING — changes are blocked" : "game not running");

        foreach (Control c in _buttons.Controls)
            c.Enabled = !running || c.Text.StartsWith("Generate") || c.Text.StartsWith("Change");

        _suppressCheckEvents = true;
        _mods.Items.Clear();
        foreach (var m in _state.Mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var source = m.SourceKind switch
            {
                "github" => "github/" + m.Repo,
                "native" => "native",
                "adopted" => "adopted (no update source)",
                _ => "local"
            };
            var status = m.AvailableVersion is not null && m.AvailableVersion != m.Version
                ? "update to " + m.AvailableVersion
                : "ok";

            var item = new ListViewItem([m.Name, m.Version, source, status]) { Checked = m.Enabled, Tag = m };
            _mods.Items.Add(item);
        }
        _suppressCheckEvents = false;

        _problems.Items.Clear();
        var findings = HealthCheck.Run(_state, _game);
        foreach (var f in findings)
            _problems.Items.Add(new ListViewItem([f.Severity.ToString(), f.Title, f.Remedy ?? ""]));

        _tabs.TabPages[1].Text = findings.Count == 0 ? "Problems" : $"Problems ({findings.Count})";
    }

    private void OnModChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_suppressCheckEvents || _game is null) return;
        if (e.Item.Tag is not ModEntry mod) return;

        // Erneute Pruefung GENAU JETZT statt sich auf den Enabled-Zustand der Schaltflaechen aus dem
        // letzten RefreshUi zu verlassen (Punkt 1 der Aufgabenstellung): eine Haekchenspalte laesst
        // sich nicht so einfach wie ein Button deaktivieren, und zwischen zwei Aktualisierungen der
        // Kopfzeile kann das Spiel gestartet worden sein. Guard() prueft synchron, unmittelbar bevor
        // Installer.SetEnabled die erste Datei anfasst -- es gibt keinen zeitlichen Abstand dazwischen,
        // in dem der Zustand erneut veralten koennte.
        if (Guard()) { _suppressCheckEvents = true; e.Item.Checked = mod.Enabled; _suppressCheckEvents = false; return; }

        try
        {
            Installer.SetEnabled(_state, _game, mod, e.Item.Checked);
            _state.Save();
        }
        catch (InstallRollbackException ex)
        {
            // Muss VOR dem generischen IOException/UnauthorizedAccessException-Fang stehen: eine
            // InstallRollbackException ist selbst keine IOException, ein rein generischer Fang wuerde
            // sie durchlassen und dem Nutzer nie sagen, WELCHE Dateien jetzt in einem gemischten
            // Zustand stecken (Punkt 3 der Aufgabenstellung).
            AppLog.Error($"toggle of {mod.Id} left files stuck", ex);
            Dialogs.ShowRollbackFailure(this, ex, "Could not change the mod");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kein ex.Message hier: das waere eine rohe, ggf. vom Betriebssystem lokalisierte
            // Meldung (z. B. "Der Prozess kann nicht auf die Datei zugreifen..."). Details gehen
            // ausschliesslich ins Log.
            AppLog.Error($"toggle of {mod.Id} failed", ex);
            MessageBox.Show(this,
                "The mod could not be enabled or disabled. It may be locked by the game or another " +
                "program. See the log for details.",
                "Could not change the mod", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshUi();
    }

    private void OnModsMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _mods.SelectedItems.Count == 0) return;
        if (_mods.SelectedItems[0].Tag is not ModEntry mod) return;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Set update source…", null, (_, _) => Dialogs.SetUpdateSource(this, _state, mod, RefreshUi));
        menu.Items.Add("Open config file", null, (_, _) => OpenConfig(mod));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove", null, (_, _) => RemoveMod(mod));
        menu.Show(_mods, e.Location);
    }

    private void OpenConfig(ModEntry mod)
    {
        if (_game is null) return;
        var cfg = Path.Combine(_game.Config, mod.Id + ".cfg");
        if (!File.Exists(cfg))
        {
            MessageBox.Show(this, "This mod has no config file yet. Start the game once.", "No config",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cfg) { UseShellExecute = true });
    }

    private void RemoveMod(ModEntry mod)
    {
        if (_game is null || Guard()) return;
        if (MessageBox.Show(this,
                $"Remove {mod.Name}? Its config file will be kept as a backup.",
                "Remove mod", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try { Installer.Remove(_state, _game, mod); _state.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Installer.Remove wirft nie eine InstallRollbackException (sie hat kein eigenes
            // Rollback -- eine geloeschte Datei laesst sich nicht "zurueckrollen"), deshalb genuegt
            // hier der generische Fang. Wie bei OnModChecked: kein rohes ex.Message vor dem Nutzer.
            AppLog.Error($"remove of {mod.Id} failed", ex);
            MessageBox.Show(this,
                "The mod could not be fully removed. It may be locked by the game or another " +
                "program. See the log for details.",
                "Could not remove the mod", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshUi();
    }

    /// <summary>True heisst: Operation abbrechen, weil das Spiel laeuft.</summary>
    private bool Guard()
    {
        if (!GameLocator.IsGameRunning()) return false;
        MessageBox.Show(this,
            "Star Trek Fleet Command is running. Close it completely, then try again.",
            "Game is running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    private void OnChangeFolder(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select the folder that contains prime.exe" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!GameLocator.IsValid(dlg.SelectedPath))
        {
            MessageBox.Show(this, "prime.exe was not found in that folder.", "Not a game folder",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _state.GamePath = dlg.SelectedPath;
        _game = new GameInstall(dlg.SelectedPath);
        _state.Save();
        Rescan();
        RefreshUi();
    }

    private void OnAddFromGitHub(object? sender, EventArgs e)
    {
        if (_game is null || Guard()) return;
        UseWaitCursor = true;
        try { Dialogs.AddFromGitHub(this, _state, _game); }
        finally { UseWaitCursor = false; }
        RefreshUi();
    }

    private void OnAddLocal(object? sender, EventArgs e)
    {
        if (_game is null || Guard()) return;
        using var dlg = new OpenFileDialog
        {
            Title = "Select a mod file",
            Filter = "Mod files (*.dll;*.zip)|*.dll;*.zip|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Dialogs.InstallLocalPath(this, _state, _game, dlg.FileName);
        RefreshUi();
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_game is null || Guard()) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var p in paths) Dialogs.InstallLocalPath(this, _state, _game, p);
        RefreshUi();
    }

    private async void OnCheckUpdates(object? sender, EventArgs e)
    {
        if (_game is null) return;
        UseWaitCursor = true;
        try { await Dialogs.CheckUpdatesAsync(this, _state, _game); }
        finally { UseWaitCursor = false; }
        RefreshUi();
    }

    private async void OnInstallBepInEx(object? sender, EventArgs e)
    {
        if (_game is null || Guard()) return;
        await Dialogs.InstallBepInExAsync(this, _game);
        RefreshUi();
    }

    private void OnSupportPackage(object? sender, EventArgs e)
    {
        if (_game is null) return;

        var planned = SupportBundle.PlannedContents(_game);
        var preview = string.Join("\r\n", planned.Select(Path.GetFileName));
        if (MessageBox.Show(this,
                "The support package will contain these files:\r\n\r\n" + preview +
                "\r\n\r\nSecrets, e-mail addresses and player IDs are removed automatically. " +
                "File paths and your Windows user name may still appear. Continue?",
                "Generate support package", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return;

        var dest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
            $"stfc-support-{DateTime.Now:yyyyMMdd-HHmm}.zip");

        try
        {
            SupportBundle.Create(_state, _game, dest);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{dest}\""));
        }
        // SupportBundle.Create uebersetzt JEDEN I/O-Fehlschlag selbst in eine InvalidOperationException
        // mit fertigem, englischem Text (s. dortiger Kommentar) -- ein Fang auf IOException/
        // UnauthorizedAccessException wuerde diese Ausnahme NIE erreichen und den echten Fehlerfall
        // ungefangen bis zur WinForms-Message-Loop durchlassen.
        catch (InvalidOperationException ex)
        {
            AppLog.Error("support package failed", ex);
            MessageBox.Show(this, ex.Message, "Could not write the package", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
