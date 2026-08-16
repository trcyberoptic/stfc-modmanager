using System.Text.RegularExpressions;

namespace StfcModManager.Core;

public enum Severity { Info, Warning, Error }

public sealed record Finding(Severity Severity, string Title, string? Remedy = null);

/// <summary>Die neun Pruefungen aus Spec §8. Laeuft auf einer Maschine in beliebigem Zustand --
/// kein Spielordner, kein BepInEx, ein laufendes Spiel, eine gesperrte Logdatei, ein Mod, dessen
/// DLL hinter dem Ruecken des Managers geloescht wurde: keiner dieser Zustaende darf Run() werfen
/// lassen, ein abstuerzender Health-Check ist schlimmer als einer, der nichts meldet.</summary>
public static partial class HealthCheck
{
    // Nur diese beiden Schalter der Community-Mod kollidieren mit Plugin-Hooks.
    [GeneratedRegex(@"^\s*(game_version|uiscalehooks)\s*=\s*true\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ConflictingToml();

    public static bool CommunityPatchConflict(string tomlText) => ConflictingToml().IsMatch(tomlText);

    public static IReadOnlyList<Finding> Run(AppState state, GameInstall game)
    {
        var f = new List<Finding>();

        // 1 Spielordner
        if (!GameLocator.IsValid(game.Root))
        {
            f.Add(new Finding(Severity.Error, "Game folder is not valid — prime.exe was not found.",
                              "Use 'Change' to pick the folder that contains prime.exe."));
            return f;                                   // ohne gueltigen Ordner ist der Rest sinnlos
        }
        if (!GameLocator.IsWritable(game.Root))
            f.Add(new Finding(Severity.Error, "Game folder is not writable.",
                              "Close the game, or move the installation out of a protected location."));

        // 2 Spiel laeuft
        if (GameLocator.IsGameRunning())
            f.Add(new Finding(Severity.Info, "The game is running — changes are blocked.",
                              "Close Star Trek Fleet Command completely, then press Refresh."));

        // 3 BepInEx vorhanden
        if (BepInExRuntime.Detect(game) is null)
            f.Add(new Finding(Severity.Error, "BepInEx is not installed.",
                              "Press 'Install BepInEx' to download the pinned runtime."));

        // 4 Client-Aktualisierung
        var build = GameLocator.ReadClientBuild(game.Root);
        var stale = state.Mods.Where(m => m.InstalledAgainstClientBuild != "unknown"
                                       && m.InstalledAgainstClientBuild != build).ToList();
        if (stale.Count > 0)
            f.Add(new Finding(Severity.Warning,
                $"The game client changed to build {build} since {stale.Count} mod(s) were installed — they may be broken.",
                "Check for mod updates. The first start after a client update takes several minutes "
              + "while BepInEx regenerates its interop assemblies."));

        // 5 erklaerte Unvertraeglichkeiten
        var active = state.Mods.Where(m => m.Enabled).ToList();
        foreach (var mod in active)
        {
            var info = FirstPluginInfo(game, mod);
            if (info is null) continue;
            foreach (var bad in info.Incompatibilities)
                if (active.Any(o => o.Id.Equals(bad, StringComparison.OrdinalIgnoreCase)))
                    f.Add(new Finding(Severity.Error,
                        $"{mod.Name} declares it is incompatible with {bad}, and both are enabled.",
                        $"Disable {mod.Name} or {bad}."));

            // 7 fehlende Abhaengigkeiten
            foreach (var dep in info.Dependencies)
                if (!active.Any(o => o.Id.Equals(dep, StringComparison.OrdinalIgnoreCase)))
                    f.Add(new Finding(Severity.Error,
                        $"{mod.Name} requires {dep}, which is not installed or not enabled.",
                        $"Install or enable {dep}."));
        }

        // 6 Community-Mod-Doppelhook
        if (File.Exists(game.VersionDll) && File.Exists(game.CommunityPatchToml))
        {
            try
            {
                if (CommunityPatchConflict(File.ReadAllText(game.CommunityPatchToml)))
                    f.Add(new Finding(Severity.Error,
                        "The Community Mod (version.dll) has game_version or uiscalehooks enabled. "
                      + "These hook the same functions as several plugins and crash the game natively at login.",
                        "Set both keys to false in community_patch_settings.toml, section [patches]."));
            }
            catch (IOException) { /* nicht lesbar: Pruefung entfaellt */ }
        }

        // 8 Fehler aus dem Spiel-Log
        foreach (var group in LogReader.ReadTail(game.LogOutput).GroupBy(e => e.Source))
        {
            var errors = group.Count(e => e.Level is "Error" or "Fatal");
            if (errors > 0)
                f.Add(new Finding(Severity.Warning,
                    $"{group.Key}: {errors} error(s) in the game log.",
                    "Generate a support package to share the details."));
        }

        // 9 unverwaltete Dateien
        if (Directory.Exists(game.Plugins))
        {
            var managed = state.Mods.SelectMany(m => m.Files)
                                    .Select(x => Path.GetFileName(x.Path))
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphans = Directory.EnumerateFiles(game.Plugins)
                                   .Select(Path.GetFileName)
                                   .Where(n => n is not null && !managed.Contains(n))
                                   .ToList();
            if (orphans.Count > 0)
                f.Add(new Finding(Severity.Info,
                    $"{orphans.Count} file(s) in the plugins folder are not managed by this app.",
                    "They are left untouched. Use Rescan to adopt any that are plugins."));
        }

        return f;
    }

    /// <summary>Liefert null, statt zu werfen, wenn die DLL eines Mods hinter dem Ruecken des
    /// Managers geloescht oder verschoben wurde (ModInspector.Read faengt IOException bereits
    /// selbst ab) -- der Aufrufer ueberspringt einen solchen Mod dann einfach fuer die
    /// Kompatibilitaets-/Abhaengigkeitspruefung, statt den gesamten Health-Check abzureissen.</summary>
    private static PluginInfo? FirstPluginInfo(GameInstall game, ModEntry mod)
    {
        var dll = mod.Files.FirstOrDefault(x => x.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        return dll is null ? null : ModInspector.Read(Path.Combine(game.Root, dll.Path));
    }
}
