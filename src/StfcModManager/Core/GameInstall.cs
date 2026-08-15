namespace StfcModManager.Core;

/// <summary>Alle Pfade, die sich aus dem Spielordner ableiten. Reine Zeichenkettenarbeit, kein I/O.</summary>
public sealed record GameInstall(string Root)
{
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
