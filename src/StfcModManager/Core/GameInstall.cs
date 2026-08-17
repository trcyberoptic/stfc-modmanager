namespace StfcModManager.Core;

/// <summary>Alle Pfade, die sich aus dem Spielordner ableiten. Reine Zeichenkettenarbeit, kein I/O.</summary>
public sealed record GameInstall(string Root)
{
    // Normalisiert EINMAL hier, statt sich darauf zu verlassen, dass jede spaetere Vergleichsstelle
    // selbst Path.GetFullPath aufruft: Path.Combine (s. Plugins usw. unten) uebernimmt die
    // Trennzeichenart des ersten Segments unveraendert -- ein Root mit Vorwaerts-Slashes (erreichbar
    // z. B. ueber eine von Hand editierte state.json) erzeugte einen gemischten Pfad, den ein rein
    // textueller Vergleich wie Installer.IsSamePath nie als gleich zum kanonisch aufgeloesten Pfad
    // erkannte. "Root" auf der rechten Seite bezeichnet den Parameter des primaeren Konstruktors, in
    // dessen Initialisierern -- keine Rekursion auf die hier neu deklarierte Eigenschaft. Installer.cs'
    // eigene text-basierte Vergleiche (IsProtectedConfig, PluginsRelative, IsSamePath) profitieren
    // automatisch mit, ohne dort selbst geaendert zu werden.
    public string Root { get; } = Path.GetFullPath(Root);

    public string Plugins            => Path.Combine(Root, "BepInEx", "plugins");
    public string PluginsDisabled    => Path.Combine(Root, "BepInEx", "plugins-disabled");
    public string Config             => Path.Combine(Root, "BepInEx", "config");
    public string LogOutput          => Path.Combine(Root, "BepInEx", "LogOutput.log");
    public string ErrorLog           => Path.Combine(Root, "BepInEx", "ErrorLog.log");
    public string CoreDll            => Path.Combine(Root, "BepInEx", "core", "BepInEx.Core.dll");
    public string WinHttp            => Path.Combine(Root, "winhttp.dll");
    public string CommunityPatchLog  => Path.Combine(Root, "community_patch.log");
    public string CommunityPatchToml => Path.Combine(Root, "community_patch_settings.toml");
    public string DoorstopConfig     => Path.Combine(Root, "doorstop_config.ini");
    public string VersionDll         => Path.Combine(Root, "version.dll");
    public string VersionDllDisabled => Path.Combine(Root, "version.dll_");
    public string PrimeExe           => Path.Combine(Root, "prime.exe");
}
