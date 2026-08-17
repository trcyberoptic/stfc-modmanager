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
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(6) };
    private readonly Button _removeAllButton = new() { Text = "Remove all mods", AutoSize = true, Height = 28 };

    private AppState _state = new();
    private GameInstall? _game;
    private bool _suppressCheckEvents;

    // Fix-Runde 1, cheap fix -- korrigiert nach einem Hands-on-Fund: die urspruengliche Fassung
    // entsorgte das Menue synchron in seinem EIGENEN Closed-Ereignis ("menu.Closed += (_, _) =>
    // menu.Dispose();"). Reproduziert: das wirft eine ObjectDisposedException, sobald
    // ToolStripDropDown im selben Aufrufstapel (HandleItemClick -> OnItemClicked -> SetVisibleCore)
    // noch einmal auf das gerade entsorgte native Handle zugreift, um das Dropdown zu schliessen --
    // vom neuen globalen Ausnahme-Handler (Fix-Runde 1, I8) zwar sauber als "Unexpected error"
    // abgefangen, aber das Kontextmenue haette den Klick des Nutzers dann nie ausgefuehrt. Die alte
    // Instanz wird stattdessen erst beim NAECHSTEN Rechtsklick entsorgt (oder beim Schliessen des
    // Fensters, s. Dispose(bool) unten) -- ein paar kurzlebige native Handles zwischen zwei
    // Rechtsklicks sind unbedenklich, eine Ausnahme mitten im Klick-Ereignis nicht.
    private ContextMenuStrip? _modContextMenu;

    // Fix-Runde 1, I9: waehrend eine der genuin asynchronen Aktionen (Check updates, Update all,
    // Update pro Mod, BepInEx-Installation) laeuft, bleibt das Fenster zwischen den "await"-Punkten
    // voll bedienbar. Ohne diese Sperre konnte ein zweiter Klick auf "Check updates" einen
    // ueberlappenden Durchlauf ueber denselben AppState starten, oder ein Haekchen/"Remove" liess
    // Installer parallel dazu laufen -- beide haetten anschliessend gespeichert. SetBusy() sperrt
    // sowohl die Symbolleiste als auch die Modliste; die rein synchronen Aktionen (Umschalten,
    // Entfernen, lokal hinzufuegen) brauchen die Sperre nicht, weil die Windows-Nachrichtenschleife
    // ohnehin nur eine Nachricht gleichzeitig verarbeitet -- zwischen Klick und Rueckkehr aus einem
    // rein synchronen Aufruf kann kein zweites Ereignis dazwischenfunken.
    private bool _busy;

    // Fix-Runde 1, I6: von Rescan() befuellt (korrigiert Enabled anhand der Platte UND meldet eine
    // Datei, die an keinem der beiden moeglichen Orte mehr existiert), von RefreshUi() zusaetzlich zu
    // HealthCheck.Run() angezeigt. Getrennt von HealthCheck.Run gehalten, weil diese Aufgabe nur
    // Ui/MainForm.cs und Ui/Dialogs.cs anfassen darf -- HealthCheck.cs bleibt unveraendert, Finding
    // selbst ist aber ein oeffentlicher Core-Record und laesst sich von hier aus genauso bauen.
    private List<Finding> _reconcileFindings = [];

    // Fix-Runde 2, Punkt 1 (wichtig) -- Guard()'s RefreshUi() spiegelt nur den Zustand GENAU JETZT,
    // im Moment des Blocks; danach pollt niemand mehr nach. Verifiziert per A/B gegen den Vor-Fix-
    // Stand: dort blieben alle Knoepfe ununterbrochen bedienbar (kein Timer, kein Activated-Handler
    // existierte), waehrend die Fix-Runde-1-Fassung sechs von neun Knoepfen -- einschliesslich
    // "Rescan", dem naheliegendsten manuellen Ausweg -- dauerhaft deaktiviert liess, auch drei
    // Sekunden und ein programmatisches Minimieren/Wiederherstellen spaeter. Dieser Timer ist der
    // fehlende Nachpoll-Mechanismus: er laeuft unabhaengig von Fensteraktivierung (Activated allein
    // reicht nicht -- ein Nutzer, der einfach nur wartet, ohne das Fenster zu wechseln, bekaeme sonst
    // nie ein Update) und behebt in derselben Bewegung die spiegelbildliche Staleness der Kopfzeile
    // (die vorher faelschlich "game not running" weiterzeigte, nachdem das Spiel laengst gestartet war).
    private readonly System.Windows.Forms.Timer _gameStateTimer = new() { Interval = 2000 };
    private bool? _lastKnownGameRunning;

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

        // Spec §12: "Remove all mods" gehoert auf den Problems-Tab, nicht in die Hauptleiste. Fill
        // zuerst hinzufuegen, dann Bottom -- dieselbe Reihenfolge, die im Hauptfenster (s. u.)
        // schon nachweislich funktioniert.
        _removeAllButton.Click += OnRemoveAllMods;
        var problemsButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        problemsButtons.Controls.Add(_removeAllButton);
        var problemsTab = new TabPage("Problems");
        problemsTab.Controls.Add(_problems);
        problemsTab.Controls.Add(problemsButtons);

        _tabs.TabPages.Add(modsTab);
        _tabs.TabPages.Add(problemsTab);

        AddButton("Add from GitHub…", OnAddFromGitHub);
        AddButton("Add local…", OnAddLocal);
        AddButton("Check updates", OnCheckUpdates);
        AddButton("Update all", OnUpdateAll);
        AddButton("Rescan", (_, _) => { Rescan(); RefreshUi(); });
        // BepInExRuntime.InstallAsync existiert seit dem letzten Core-Task, wird aber von keinem der
        // Aufgaben-Entwuerfe verdrahtet -- ohne diesen Knopf haette HealthCheck.Run's Abhilfetext
        // "Press 'Install BepInEx'" (s. HealthCheck.cs, Pruefung 3) kein Ziel, auf das er verweisen
        // koennte.
        AddButton("Install BepInEx…", OnInstallBepInEx);
        AddButton("Open local mods folder…", OnOpenLocalModsFolder);
        AddButton("Generate support package", OnSupportPackage);
        AddButton("Change game folder…", OnChangeFolder);

        Controls.Add(_tabs);
        Controls.Add(_buttons);
        Controls.Add(_header);

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += OnDragDrop;

        Load += (_, _) => { LoadState(); Rescan(); RefreshUi(); };

        // Spec §12: rescan when the window regains focus -- picks up changes the user made by hand
        // (or another program made) while the manager sat in the background, without making them
        // wait for an explicit "Rescan" click. Guarded by _busy: Activated can fire while a genuinely
        // asynchronous action (Check updates, ...) is between await points, and running Rescan's own
        // Installer/AppState.Save calls concurrently with that would be exactly the I9 race again.
        Activated += (_, _) => { if (_busy || _game is null) return; Rescan(); RefreshUi(); };

        // Fix-Runde 2, Punkt 1: der fehlende Nachpoll-Mechanismus (s. Feldkommentar bei
        // _gameStateTimer). Bewusst NICHT auf _busy geprueft: RefreshUi() selbst loest weder
        // Installer noch AppState.Save aus (nur HealthCheck.Run und ein Neuaufbau der ListView aus
        // dem bereits im Speicher stehenden _state), das ist also nicht dieselbe Race-Klasse wie
        // Rescan() im Activated-Handler oder ein zweiter Klick auf einen asynchronen Knopf -- und
        // selbst wenn der Tick waehrend einer Sperre feuert, gewinnt SetBusy(true)'s
        // Panel-Deaktivierung ohnehin gegenueber dem, was RefreshUi() an einzelnen Buttons setzt
        // (ein deaktivierter Container macht seine Kinder unbedienbar, unabhaengig von deren eigenem
        // Enabled). Nur EIN RefreshUi()-Aufruf pro tatsaechlicher ZustandsAENDERUNG, nicht pro Tick.
        _gameStateTimer.Tick += (_, _) =>
        {
            if (_game is null) return;
            var running = GameLocator.IsGameRunning();
            if (_lastKnownGameRunning == running) return;
            _lastKnownGameRunning = running;
            RefreshUi();
        };
        _gameStateTimer.Start();
    }

    private void AddButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 30 };
        b.Click += onClick;
        _buttons.Controls.Add(b);
    }

    /// <summary>Sperrt/entsperrt die Bedienelemente fuer die Dauer einer echt asynchronen Aktion
    /// (s. Feldkommentar bei _busy).
    ///
    /// Fix-Runde 2, Punkt 2: zwei offene Tueren gefunden und geschlossen. "Remove all mods" haengt
    /// am Problems-Tab, nicht an _buttons -- das Deaktivieren der Symbolleiste liess den Knopf
    /// unberuehrt bedienbar, weil weder er noch das umgebende TabControl je gesperrt wurden.
    /// AllowDrop blieb ebenfalls durchgehend true, der formularweite Drag-and-Drop-Handler war also
    /// die ganze Zeit ueber live. Beide erreichen Installer/state.Save() und haetten mit einer
    /// laufenden Aktualisierung um denselben AppState konkurrieren koennen (I9 aus Fix-Runde 1).</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _buttons.Enabled = !busy;
        _mods.Enabled = !busy;
        _removeAllButton.Enabled = !busy;
        AllowDrop = !busy;
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

    /// <summary>Adoption nach Spec §7: vorhandenen Bestand uebernehmen statt daneben installieren.
    /// Gleicht danach auch den bereits bekannten Bestand mit der Platte ab (Fix-Runde 1, I6).</summary>
    public void Rescan()
    {
        if (_game is null) return;
        AdoptFromDisk(_state, _game);
        _reconcileFindings = ReconcilePluginFiles(_state, _game);
        _state.LastKnownClientBuild = GameLocator.ReadClientBuild(_game.Root);
        _state.Save();
    }

    public static void AdoptFromDisk(AppState state, GameInstall game)
    {
        // Fix-Runde 1, I7: der Schluessel ist der VOLLE kanonische relative Pfad, nicht nur der
        // Dateiname -- sonst wuerde ein zweites, gleichnamiges "Core.dll" in einem ANDEREN Unterordner
        // faelschlich als "schon bekannt" uebersprungen. InstalledFile.Path ist seit dem C1-Fix
        // unten immer schon die kanonische Form ("BepInEx\plugins\...").
        //
        // Fix-Runde 2, Punkt 4 (Geister-Adoption): state.SharedFiles gehoert mit in die Menge --
        // eine Beilage, die ApplyPackage als SharedFile statt als eigenen Mod verbucht hat (z. B.
        // eine zweite, ebenfalls BepInPlugin-markierte DLL im selben Paket), wurde sonst bei JEDEM
        // Rescan erneut als vermeintlich neuer, eigenstaendiger Mod adoptiert -- "Remove" darauf loescht
        // nichts (die Datei gehoert ja weiterhin einem echten Mod als Provider) und das Haekchen tut
        // nichts, weil SetEnabled fuer eine geteilte, von einem anderen aktivierten Mod noch
        // benoetigte Datei bewusst nichts verschiebt. Beide Pfadformen sind bereits kanonisch, die
        // Vereinigung ist eine einzige zusaetzliche Zeile.
        var known = state.Mods.SelectMany(m => m.Files)
                              .Select(f => f.Path)
                              .Concat(state.SharedFiles.Select(f => f.Path))
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (dir, enabled) in new[]
                 {
                     (game.Plugins,         true),
                     (game.PluginsDisabled, false)
                 })
        {
            if (!Directory.Exists(dir)) continue;

            // Fix-Runde 1, I7: rekursiv statt nur die oberste Ebene -- die gespiegelte
            // Unterordnerstruktur ist in Installer (PhysicalPathFor) und PackageMapper (haengt
            // "BepInEx\plugins\..." unveraendert an) erstklassig, eine flache Aufzaehlung sah
            // "plugins\MyMod\FakeMod.dll" nie.
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories))
            {
                // Fix-Runde 1, C1 (kritisch): das Ziel ist IMMER unter BepInEx\plugins zu speichern,
                // unabhaengig davon, in welchem der beiden Ordner die Datei GERADE liegt.
                // InstalledFile.Path ist per Vertrag die aktivierte Lage (s. Kommentar dort);
                // "Enabled" druckt aus, dass sie momentan im deaktivierten Zweig liegt. Vorher
                // speicherte diese Methode fuer einen aus plugins-disabled adoptierten Mod woertlich
                // "BepInEx\plugins-disabled\X.dll" als Files[0].Path -- SetEnabled sah darin einen
                // Pfad, der nicht unter BepInEx\plugins liegt, ruehrte ihn nie an (nur .dll-Dateien
                // UNTER plugins wandern) und liess Enabled klanglos auf true stehen, ganz gleich, was
                // der Nutzer anklickte: der Mod liess sich nie aktivieren.
                var relFromDir = Path.GetRelativePath(dir, dll);
                var canonicalRel = Path.Combine("BepInEx", "plugins", relFromDir);
                if (known.Contains(canonicalRel)) continue;

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
                    Files = { new InstalledFile { Path = canonicalRel, Sha256 = Installer.Sha256File(dll) } },
                    InstalledAgainstClientBuild = GameLocator.ReadClientBuild(game.Root)
                });
                // Fix-Runde 2, Punkt 4: "known" wurde vorher nur EINMAL vor der Schleife gebaut --
                // dieselbe Rescan-Schleife durchlaeuft aber BEIDE Ordner (plugins UND
                // plugins-disabled) nacheinander, und ohne diese Zeile konnten zwei unterschiedliche
                // Dateien (verschiedene GUID, aber derselbe relative Name je Ordner) denselben
                // kanonischen Pfad beanspruchen -- zwei ModEntry-Objekte mit identischem Files[0].Path,
                // und ein Umschalten des einen bewegte physisch die Datei des anderen mit.
                known.Add(canonicalRel);
                AppLog.Info($"adopted {info.Guid} {info.Version} from {(enabled ? "plugins" : "plugins-disabled")}");
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

    /// <summary>Ergebnis von DecidePluginReconcileAction -- s. dort.</summary>
    internal enum PluginReconcileAction { SetEnabled, SetDisabled, Missing }

    /// <summary>Reine Entscheidung fuer EINEN bereits bekannten Plugin-Mod, getrennt von den beiden
    /// File.Exists-Aufrufen, damit sie ohne Dateisystem pruefbar ist (Fix-Runde 1, I6). Existiert die
    /// Datei nur am aktivierten Ort, muss Enabled=true gelten (auch wenn state es anders fuehrt --
    /// z. B. von Hand zurueckverschoben); nur am deaktivierten Ort entsprechend Enabled=false; an
    /// KEINEM der beiden Orte heisst: die Datei wurde hinter dem Ruecken des Managers geloescht, das
    /// ist ein Befund, kein Umschalten. Existieren (der seltene, per Hand herbeigefuehrte Fall) BEIDE
    /// gleichzeitig, gewinnt der aktivierte Ort -- derselbe Vorrang, den Installer.PhysicalCandidates
    /// fuer den kanonischen Schluessel ohnehin schon setzt.</summary>
    internal static PluginReconcileAction DecidePluginReconcileAction(bool existsEnabled, bool existsDisabled)
    {
        if (!existsEnabled && existsDisabled) return PluginReconcileAction.SetDisabled;
        if (!existsEnabled && !existsDisabled) return PluginReconcileAction.Missing;
        return PluginReconcileAction.SetEnabled;
    }

    /// <summary>Gleicht bereits bekannte Plugin-Eintraege mit der Platte ab (Fix-Runde 1, I6):
    /// korrigiert Enabled aus dem tatsaechlichen Ort der Datei -- eine von Hand nach
    /// plugins-disabled verschobene DLL blieb sonst fuer immer als "aktiviert, ok" gelistet, ohne
    /// dass BepInEx sie je laedt -- und liefert eine Problem-Zeile fuer jeden Eintrag, dessen Datei
    /// an KEINEM der beiden moeglichen Orte mehr existiert, statt ihn weiter als installiert
    /// auszugeben. Native Mods bleiben aussen vor, die reconciled bereits AdoptFromDisk selbst ueber
    /// DecideNativeModAction.</summary>
    public static List<Finding> ReconcilePluginFiles(AppState state, GameInstall game)
    {
        var pluginsPrefix = Path.Combine("BepInEx", "plugins") + Path.DirectorySeparatorChar;
        var disabledPrefix = Path.Combine("BepInEx", "plugins-disabled") + Path.DirectorySeparatorChar;

        var findings = new List<Finding>();
        foreach (var mod in state.Mods.Where(m => m.SourceKind != "native"))
        {
            // Fix-Runde 2, Punkt 3 (C1 lebte noch weiter): vorher nur Pfade unter BepInEx\plugins
            // gesehen -- ein Eintrag, dessen Files[0].Path (aus einer Version vor dem Install-Pfad-
            // Fix in ApplyPackage, oder einer von Hand bearbeiteten state.json) noch woertlich unter
            // BepInEx\plugins-disabled steht, wurde hier stillschweigend uebersprungen: genau die
            // Luecke, die der urspruengliche C1-Fund fuer die Adoption schon einmal beschrieb, jetzt
            // im eigenen Sicherheitsnetz.
            var dll = mod.Files.FirstOrDefault(f =>
                f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                (f.Path.StartsWith(pluginsPrefix, StringComparison.OrdinalIgnoreCase) ||
                 f.Path.StartsWith(disabledPrefix, StringComparison.OrdinalIgnoreCase)));
            if (dll is null) continue;

            // Den gespeicherten Pfad JETZT heilen, bevor Installer.PhysicalPath ihn als Basis fuer
            // beide Kandidatenorte benutzt -- ohne das liefert PhysicalPath fuer einen bereits
            // falschen ("...plugins-disabled\...") Pfad zwei IDENTISCHE Kandidaten (PluginsRelative
            // erkennt "plugins-disabled\..." nicht als "unter plugins liegend" und faellt in beiden
            // Faellen auf denselben kanonischen Pfad zurueck), und die Pruefung unten haette Enabled
            // dann faelschlich auf true gesetzt, obwohl die Datei nie umgezogen ist -- exakt das
            // urspruengliche C1-Symptom, nur ueber den Reconcile-Pfad erneut hereingelassen.
            if (dll.Path.StartsWith(disabledPrefix, StringComparison.OrdinalIgnoreCase))
                dll.Path = pluginsPrefix + dll.Path[disabledPrefix.Length..];

            var wasEnabled = mod.Enabled;
            string enabledPath, disabledPath;
            try
            {
                // Installer.PhysicalPath leitet aus mod.Enabled ab -- kurzzeitig beide Zustaende
                // durchspielen, statt Installer's Spiegelungsregeln (Unterordner werden gespiegelt,
                // nicht abgeflacht) hier ein zweites Mal nachzubauen und so unbemerkt von
                // Installer.cs abzuweichen.
                mod.Enabled = true;
                enabledPath = Installer.PhysicalPath(game, mod, dll);
                mod.Enabled = false;
                disabledPath = Installer.PhysicalPath(game, mod, dll);
            }
            catch (InvalidOperationException)
            {
                // Ein gespeicherter Pfad, der nicht innerhalb des Spielordners aufloest (z. B. eine
                // von Hand kaputt bearbeitete state.json) -- Rescan darf daran nicht scheitern,
                // dieser eine Eintrag wird einfach uebersprungen.
                continue;
            }
            finally
            {
                // Fix-Runde 2, Punkt 4: vorher stand die Wiederherstellung als letzte Zeile IM
                // try-Block -- ein Wurf aus dem zweiten PhysicalPath-Aufruf (mod.Enabled bereits auf
                // false gesetzt) liess sie nie erreichen und der catch-Zweig gab ohne Wiederherstellung
                // per "continue" zum naechsten Mod weiter. Verifiziert: das kippte Enabled dauerhaft
                // von true auf false auf dem Fehlerpfad, und der anschliessende state.Save() in
                // Rescan() persistierte diesen falschen Wert. finally laeuft garantiert, auch beim
                // "continue" oben.
                mod.Enabled = wasEnabled;
            }

            switch (DecidePluginReconcileAction(File.Exists(enabledPath), File.Exists(disabledPath)))
            {
                case PluginReconcileAction.SetEnabled:
                    mod.Enabled = true;
                    break;
                case PluginReconcileAction.SetDisabled:
                    mod.Enabled = false;
                    break;
                case PluginReconcileAction.Missing:
                    findings.Add(new Finding(Severity.Warning,
                        $"{mod.Name}: its file is missing from the game folder.",
                        "Reinstall it, or remove it from the list."));
                    break;
            }
        }
        return findings;
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
            c.Enabled = !running || c.Text.StartsWith("Generate") || c.Text.StartsWith("Change")
                                  || c.Text.StartsWith("Open");
        _removeAllButton.Enabled = !running;

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
        // Fix-Runde 1, I6: HealthCheck.Run allein sieht nicht, dass ein bekannter Mod seine Datei
        // verloren hat -- das reconciled nur Rescan()/_reconcileFindings, hier zusammengefuehrt.
        var findings = HealthCheck.Run(_state, _game).Concat(_reconcileFindings).ToList();
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
        // Fix-Runde 2, Punkt 4: SetEnabled kann das ueber ResolveInside werfen (z. B. ein
        // Reparse-Point innerhalb BepInEx\plugins, der physisch aus dem Spielordner hinausfuehrt --
        // dieselbe Tiefenverteidigung, die Installer ueberall sonst durchzieht). Ohne diesen Fang
        // landete die Ausnahme ungefangen im globalen Handler (Fix-Runde 1, I8): eine generische
        // "Unexpected error"-Meldung statt einer erklaerenden, UND das abschliessende RefreshUi()
        // unten wurde uebersprungen -- das Haekchen blieb visuell umgeschaltet, obwohl
        // Installer.SetEnabled vor dem Wurf abgebrochen war und mod.Enabled sich nie geaendert hatte.
        catch (InvalidOperationException ex)
        {
            AppLog.Error($"toggle of {mod.Id} failed: a stored path escapes the game folder", ex);
            MessageBox.Show(this,
                "The mod could not be enabled or disabled: one of its files has a path that leaves " +
                "the game folder. See the log for details.",
                "Could not change the mod", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        // s. Feldkommentar bei _modContextMenu: die VORHERIGE Instanz entsorgen, nicht die gerade
        // benutzte in ihrem eigenen Closed-Ereignis.
        _modContextMenu?.Dispose();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Update", null, (_, _) => OnUpdateSingleMod(mod));
        menu.Items.Add("Set update source…", null, (_, _) => Dialogs.SetUpdateSource(this, _state, mod, RefreshUi));
        menu.Items.Add("Open config file", null, (_, _) => OpenConfig(mod));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove", null, (_, _) => RemoveMod(mod));
        _modContextMenu = menu;
        menu.Show(_mods, e.Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _modContextMenu?.Dispose();
            _gameStateTimer.Dispose();
        }
        base.Dispose(disposing);
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

        // Fix-Runde 1, I2: der Bestaetigungsdialog oben ist unbeschraenkt -- das Spiel kann waehrend
        // der Nutzer ueberlegt gestartet worden sein. Der Guard() oben ist deshalb nicht mehr der
        // letzte; hier, unmittelbar vor Installer.Remove, ist der eigentliche Schreibpunkt.
        if (Guard()) return;

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

    /// <summary>Spec §12: kompletter Neuanfang -- entfernt jeden verwalteten Mod, einschliesslich des
    /// nativen Eintrags (der hat kein Files-Element, Remove() traegt ihn dann nur aus state.Mods aus,
    /// ohne version.dll anzufassen -- ein anschliessendes Rescan adoptiert ihn neu, falls die Datei
    /// noch da ist).</summary>
    private void OnRemoveAllMods(object? sender, EventArgs e)
    {
        if (_game is null || Guard()) return;
        if (_state.Mods.Count == 0) return;
        if (MessageBox.Show(this,
                $"Remove all {_state.Mods.Count} mod(s)? Their config files will be kept as backups.",
                "Remove all mods", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        // Fix-Runde 1, I2: derselbe unbeschraenkte-Dialog-Grund wie in RemoveMod.
        if (Guard()) return;

        var failed = new List<string>();
        foreach (var mod in _state.Mods.ToList())
        {
            try { Installer.Remove(_state, _game, mod); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"remove-all: could not remove {mod.Id}", ex);
                failed.Add(mod.Name);
            }
        }
        _state.Save();

        if (failed.Count > 0)
            MessageBox.Show(this,
                "Some mods could not be fully removed:\r\n" + string.Join("\r\n", failed) +
                "\r\n\r\nSee the log for details.",
                "Remove all mods", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        RefreshUi();
    }

    /// <summary>True heisst: Operation abbrechen, weil das Spiel laeuft.</summary>
    private bool Guard()
    {
        if (!GameLocator.IsGameRunning()) return false;

        // Fix-Runde 1 (Hands-on-Fund beim Testen von I2): RefreshUi() HIER, nicht nur am Ende des
        // jeweiligen Aufrufers -- den ein fruehes "return" nach einem Guard()-Treffer ja gerade
        // ueberspringt. Ohne diese Zeile blieb die Symbolleiste im Zustand VOR dieser Pruefung
        // eingefroren: schliesst der Nutzer danach das Spiel, bleiben alle nicht ausgenommenen
        // Knoepfe -- einschliesslich "Rescan", dem naheliegendsten Weg, die Anzeige selbst
        // aufzufrischen -- deaktiviert, bis das Fenster zufaellig den Fokus verliert und
        // zurueckbekommt (Activated). Reproduziert beim Testen des zweiten Guard()-Aufrufs in
        // RemoveMod: nach dem Schliessen des "Game is running"-Dialogs blieb "Install BepInEx…"
        // deaktiviert, obwohl der Scheinprozess laengst beendet war.
        RefreshUi();

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
        // Spec §12: LocalMods (angelegt in LoadState) ist der vorgesehene Ablageordner -- der
        // Dateiauswahldialog startet dort, statt in einem beliebigen Windows-Standardordner.
        using var dlg = new OpenFileDialog
        {
            Title = "Select a mod file",
            Filter = "Mod files (*.dll;*.zip)|*.dll;*.zip|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(AppPaths.LocalMods)
                ? AppPaths.LocalMods
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
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

    /// <summary>Spec §12: eigener Knopf, um den lokalen Ablageordner im Explorer zu oeffnen -- vorher
    /// legte LoadState() den Ordner nur an, ohne dass die Oberflaeche ihn je erwaehnte.
    /// Process.Start faengt sich absichtlich nicht selbst ab: der neue globale Ausnahme-Handler in
    /// Program.cs (Fix-Runde 1, I8) deckt genau diesen Aufruf mit ab.</summary>
    private void OnOpenLocalModsFolder(object? sender, EventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LocalMods);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.LocalMods}\""));
    }

    private async void OnCheckUpdates(object? sender, EventArgs e)
    {
        if (_game is null || _busy) return;
        SetBusy(true);
        UseWaitCursor = true;
        try { await Dialogs.CheckUpdatesAsync(this, _state, _game); }
        finally { UseWaitCursor = false; SetBusy(false); }
        RefreshUi();
    }

    /// <summary>Spec §12: installiert alles, was bereits eine AvailableVersion traegt (aus einem
    /// frueheren "Check updates"), ohne selbst neu beim GitHub abzufragen.</summary>
    private async void OnUpdateAll(object? sender, EventArgs e)
    {
        if (_game is null || Guard() || _busy) return;
        SetBusy(true);
        UseWaitCursor = true;
        try { await Dialogs.UpdateAllAsync(this, _state, _game); }
        finally { UseWaitCursor = false; SetBusy(false); }
        RefreshUi();
    }

    /// <summary>Spec §12: "Update" im Kontextmenue fuer genau einen Mod.</summary>
    private async void OnUpdateSingleMod(ModEntry mod)
    {
        if (_game is null || Guard() || _busy) return;
        if (mod.SourceKind != "github" || mod.Repo is null)
        {
            MessageBox.Show(this, "This mod has no GitHub update source. Use 'Set update source…' first.",
                            "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetBusy(true);
        UseWaitCursor = true;
        try { await Dialogs.UpdateSingleAsync(this, _state, _game, mod); }
        finally { UseWaitCursor = false; SetBusy(false); }
        RefreshUi();
    }

    private async void OnInstallBepInEx(object? sender, EventArgs e)
    {
        if (_game is null || Guard() || _busy) return;
        // Kein SetBusy(true) noetig: form.ShowDialog(owner) in InstallBepInExAsync macht das
        // Hauptfenster ohnehin unbedienbar, solange der modale Fortschrittsdialog offen ist --
        // _busy schuetzt hier nur noch den (seltenen) Fall, dass Activated trotzdem feuert.
        _busy = true;
        try { await Dialogs.InstallBepInExAsync(this, _game); }
        finally { _busy = false; }
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
