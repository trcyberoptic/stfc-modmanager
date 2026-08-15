# STFC Mod Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eine Windows-Anwendung, die BepInEx-Mods für Star Trek Fleet Command installiert, aktualisiert, an-/abschaltet und im Fehlerfall ein redigiertes Supportpaket erzeugt.

**Architecture:** Eine einzige WinForms-EXE. Alle reine Logik liegt in `Core/` ohne UI-Bezug und wird über `--selftest` mit Asserts geprüft. Mods werden direkt in den Spielordner installiert; Buchführung in einer JSON-Datei unter `%LOCALAPPDATA%`. Mod-Identität kommt aus dem `[BepInPlugin]`-Attribut der DLL, gelesen mit `System.Reflection.Metadata` — kein Manifest, kein Katalog.

**Tech Stack:** C# / WinForms, `net10.0-windows`, self-contained single-file x64. Nur BCL: `System.Reflection.Metadata`, `System.IO.Compression`, `System.Text.Json`, `System.Net.Http`. Keine NuGet-Pakete, kein Test-Framework.

**Spec:** [docs/superpowers/specs/2026-08-15-stfc-modmanager-design.md](../specs/2026-08-15-stfc-modmanager-design.md)

## Global Constraints

- Zielframework `net10.0-windows`, `RuntimeIdentifier win-x64`, `SelfContained true`, `PublishSingleFile true`. Kein Trimming (mit WinForms nicht unterstützt).
- **Keine NuGet-Abhängigkeiten.** Alles aus der BCL.
- **UI-Sprache ist Englisch.** Code, Kommentare und diese Planungsdokumente sind Deutsch.
- `Nullable` und `ImplicitUsings` sind aktiviert.
- Namespace-Wurzel `StfcModManager`, Kern-Logik in `StfcModManager.Core`.
- **Nichts aus einem Release wird jemals ausgeführt** — nur kopiert.
- Jeder aufgelöste Zielpfad muss unterhalb des Spielordners liegen (Zip-Slip).
- Downloads nur über `https://`, Host `github.com` oder `*.githubusercontent.com`.
- Gepinnter BepInEx-Build: `https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip`
- Schreibende Operationen sind blockiert, solange `prime.exe` läuft.
- `BepInEx\config\*.cfg` wird nie gelöscht, nur nach `%LOCALAPPDATA%\StfcModManager\config-backup\` gesichert.
- Jeder Task endet mit einem Commit. Commit-Nachrichten Englisch, Conventional Commits.

---

## Dateistruktur

| Datei | Verantwortung |
|---|---|
| `src/StfcModManager/StfcModManager.csproj` | Projektdefinition, Publish-Eigenschaften |
| `src/StfcModManager/Program.cs` | Einstiegspunkt, `--selftest`-Weiche, Konsolen-Anbindung, Self-Update-Aufräumen |
| `src/StfcModManager/SelfTest.cs` | Assert-Harness und alle Prüfungen |
| `src/StfcModManager/Core/GameLocator.cs` | Spielordner finden und validieren, Client-Build, Prozess-Prüfung, Unity-Logpfad |
| `src/StfcModManager/Core/GameInstall.cs` | Record mit allen abgeleiteten Pfaden unterhalb des Spielordners |
| `src/StfcModManager/Core/ModInspector.cs` | `[BepInPlugin]`/`[BepInDependency]`/`[BepInIncompatibility]` aus einer DLL lesen |
| `src/StfcModManager/Core/PackageMapper.cs` | Archiv oder Einzeldatei → Zielpfade; Ablehnungsregeln |
| `src/StfcModManager/Core/AppState.cs` | `state.json`: Modell, Laden, atomares Speichern |
| `src/StfcModManager/Core/AppLog.cs` | Rollierendes Manager-Log |
| `src/StfcModManager/Core/Installer.cs` | Transaktionales Anwenden, Referenzzählung, An/Aus, Deinstallation |
| `src/StfcModManager/Core/GitHubClient.cs` | Repo-URL parsen, Release abfragen (ETag), Asset wählen, herunterladen |
| `src/StfcModManager/Core/BepInExRuntime.cs` | BepInEx erkennen und installieren |
| `src/StfcModManager/Core/LogReader.cs` | BepInEx-Logzeilen parsen |
| `src/StfcModManager/Core/HealthCheck.cs` | Die neun Prüfungen aus Spec §8 |
| `src/StfcModManager/Core/Redactor.cs` | Redaktionsmuster |
| `src/StfcModManager/Core/SupportBundle.cs` | Sammeln, redigieren, zippen |
| `src/StfcModManager/Core/SelfUpdate.cs` | Eigenes Release einspielen (Umbenenn-Tanz) |
| `src/StfcModManager/Ui/MainForm.cs` | Fenster, zwei Tabs, Adoption-Scan, Verdrahtung |
| `src/StfcModManager/Ui/Dialogs.cs` | GitHub-Eingabe, Vertrauensdialog, Asset-Auswahl |
| `.github/workflows/build.yml` | CI: bauen, `--selftest`, Release-Artefakt |
| `README.md` | Zweck, Installation, SmartScreen-Hinweis, Sicherheitsmodell |

---

## Task 1: Projektgerüst und Selbsttest-Harness

**Files:**
- Create: `src/StfcModManager/StfcModManager.csproj`
- Create: `src/StfcModManager/Program.cs`
- Create: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: nichts
- Produces: `SelfTest.Run() -> int`, `SelfTest.Check(bool, string)`, `SelfTest.Eq(object?, object?, string)` — jeder spätere Task hängt seine Prüfungen an `SelfTest.Run()` an.

- [ ] **Step 1: .NET-10-SDK installieren**

Auf diesem Rechner liegt nur SDK 6.0.428 unter `C:\dotnet6`; `dotnet` im PATH hat keinen SDK.

```powershell
winget install --id Microsoft.DotNet.SDK.10 --accept-source-agreements --accept-package-agreements
```

Danach neue Shell öffnen und prüfen:

```powershell
dotnet --list-sdks
```

Erwartet: eine Zeile mit `10.0.x`.

- [ ] **Step 2: Projektdatei anlegen**

`src/StfcModManager/StfcModManager.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>StfcModManager</RootNamespace>
    <AssemblyName>StfcModManager</AssemblyName>
    <Version>0.1.0</Version>
    <InvariantGlobalization>true</InvariantGlobalization>
    <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>
  </PropertyGroup>

  <!-- Publish-Eigenschaften: eine einzelne EXE ohne Runtime-Voraussetzung. -->
  <PropertyGroup>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>

</Project>
```

- [ ] **Step 3: Selbsttest-Harness schreiben**

`src/StfcModManager/SelfTest.cs`:

```csharp
namespace StfcModManager;

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

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
```

- [ ] **Step 4: Einstiegspunkt schreiben**

`src/StfcModManager/Program.cs`. `AttachConsole` ist nötig, weil eine `WinExe`
sonst keine Ausgabe an das aufrufende Terminal schreibt.

```csharp
using System.Runtime.InteropServices;

namespace StfcModManager;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(AttachParentProcess);
            return SelfTest.Run();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form { Text = "STFC Mod Manager", Width = 900, Height = 600 });
        return 0;
    }
}
```

`LibraryImport` verlangt `partial` an Methode **und** Klasse — `internal static partial class Program`.

- [ ] **Step 5: Bauen und Selbsttest laufen lassen**

```powershell
dotnet build src/StfcModManager/StfcModManager.csproj
dotnet run --project src/StfcModManager/StfcModManager.csproj -- --selftest
```

Erwartet: `1 passed, 0 failed`, Rückgabewert 0. Prüfen mit `echo $LASTEXITCODE`.

- [ ] **Step 6: Commit**

```bash
git add src .gitignore
git commit -m "feat: project scaffold with selftest harness"
```

---

## Task 2: Spielordner finden (GameLocator)

Der Kern-Befund aus der Spec §2.2(a): der INI-Schlüssel trägt einen
Publisher-Präfix (`152033..GAME_PATH=`), weshalb die Erkennung des bestehenden
Inno-Installers still danebengreift.

**Files:**
- Create: `src/StfcModManager/Core/GameInstall.cs`
- Create: `src/StfcModManager/Core/GameLocator.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `SelfTest.Check`, `SelfTest.Eq`
- Produces:
  - `record GameInstall(string Root)` mit `Plugins`, `PluginsDisabled`, `Config`, `LogOutput`, `ErrorLog`, `CommunityPatchLog`, `CommunityPatchToml`, `CoreDll`, `WinHttp`, `VersionDll`, `VersionDllDisabled`
  - `GameLocator.ParseGamePathFromIni(IEnumerable<string>) -> string?`
  - `GameLocator.Detect() -> string?`
  - `GameLocator.IsValid(string) -> bool`
  - `GameLocator.ReadClientBuild(string) -> string`
  - `GameLocator.IsGameRunning() -> bool`
  - `GameLocator.UnityLogDir() -> string`

- [ ] **Step 1: Failing tests schreiben**

In `SelfTest.Run()` vor der Ausgabe einfügen:

```csharp
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
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

```powershell
dotnet run --project src/StfcModManager/StfcModManager.csproj -- --selftest
```

Erwartet: Übersetzungsfehler `CS0103: The name 'GameLocator' does not exist`.

- [ ] **Step 3: GameInstall implementieren**

`src/StfcModManager/Core/GameInstall.cs`:

```csharp
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
```

- [ ] **Step 4: GameLocator implementieren**

`src/StfcModManager/Core/GameLocator.cs`:

```csharp
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace StfcModManager.Core;

public static partial class GameLocator
{
    // Der Launcher schreibt den Schluessel mit Publisher-Praefix ("152033..GAME_PATH=").
    // Ein Muster ohne diesen optionalen Praefix greift auf echten Installationen daneben.
    [GeneratedRegex(@"^\s*(?:\d+\.\.)?GAME_PATH\s*=\s*(?<p>.+?)\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GamePathLine();

    public static string? ParseGamePathFromIni(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = GamePathLine().Match(line);
            if (!m.Success) continue;
            var value = m.Groups["p"].Value.Replace('/', '\\').TrimEnd('\\');
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    private static string LauncherIniPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Star Trek Fleet Command", "launcher_settings.ini");

    /// <summary>INI zuerst, dann der uebliche Standardpfad. Null, wenn nichts Gueltiges gefunden wurde.</summary>
    public static string? Detect()
    {
        try
        {
            if (File.Exists(LauncherIniPath))
            {
                var fromIni = ParseGamePathFromIni(File.ReadAllLines(LauncherIniPath));
                if (fromIni is not null && IsValid(fromIni)) return fromIni;
            }
        }
        catch (IOException) { /* INI unlesbar: Fallback benutzen */ }

        const string fallback = @"C:\Games\Star Trek Fleet Command\STFC\default\game";
        return IsValid(fallback) ? fallback : null;
    }

    public static bool IsValid(string root)
        => !string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, "prime.exe"));

    /// <summary>Client-Build aus ".version" (Inhalt z.B. "&amp;game=254"). "unknown", wenn nicht lesbar.</summary>
    public static string ReadClientBuild(string root)
    {
        try
        {
            var raw = File.ReadAllText(Path.Combine(root, ".version")).Trim();
            var eq = raw.LastIndexOf('=');
            return eq >= 0 && eq < raw.Length - 1 ? raw[(eq + 1)..].Trim() : raw;
        }
        catch (IOException) { return "unknown"; }
        catch (UnauthorizedAccessException) { return "unknown"; }
    }

    public static bool IsGameRunning()
        => Process.GetProcessesByName("prime").Length > 0;

    public static string UnityLogDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Digit Game Studios Ltd", "Star Trek Fleet Command");

    /// <summary>Prueft echte Schreibbarkeit, nicht nur ACLs.</summary>
    public static bool IsWritable(string root)
    {
        try
        {
            var probe = Path.Combine(root, ".stfcmm-write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
```

`SelfTest.cs` braucht dafür `using StfcModManager.Core;`.

- [ ] **Step 5: Test laufen lassen, Erfolg bestätigen**

```powershell
dotnet run --project src/StfcModManager/StfcModManager.csproj -- --selftest
```

Erwartet: `7 passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add src
git commit -m "feat(core): locate game folder from launcher ini with publisher-prefixed key"
```

---

## Task 3: Mod-Metadaten aus DLLs lesen (ModInspector)

**Files:**
- Create: `src/StfcModManager/Core/ModInspector.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `SelfTest.Eq`
- Produces:
  - `record PluginInfo(string Guid, string Name, string Version, IReadOnlyList<string> Dependencies, IReadOnlyList<string> Incompatibilities)`
  - `ModInspector.Read(string dllPath) -> PluginInfo?` — `null` bedeutet „keine Plugin-DLL"
  - `ModInspector.DecodeStringArgs(byte[] blob, int max) -> IReadOnlyList<string>` (rein, getestet)

- [ ] **Step 1: Failing tests schreiben**

Der Attribut-Blob nach ECMA-335 §II.23.3: 2 Byte Prolog `01 00`, dann je
Zeichenkette eine komprimierte Länge gefolgt von UTF-8-Bytes. `0xFF` = null.

In `SelfTest.Run()` einfügen:

```csharp
        // --- ModInspector: Attribut-Blob-Decoder ---
        // 01 00 | 03 "abc" | 02 "hi" | 05 "1.2.3"
        var blob = new byte[] { 0x01, 0x00,
                                0x03, (byte)'a', (byte)'b', (byte)'c',
                                0x02, (byte)'h', (byte)'i',
                                0x05, (byte)'1', (byte)'.', (byte)'2', (byte)'.', (byte)'3' };
        var args = ModInspector.DecodeStringArgs(blob, 3);
        Eq(args.Count, 3, "blob: three args");
        Eq(args.Count > 0 ? args[0] : null, "abc", "blob: first arg");
        Eq(args.Count > 2 ? args[2] : null, "1.2.3", "blob: third arg");

        Eq(ModInspector.DecodeStringArgs(new byte[] { 0x01, 0x00, 0xFF }, 1).Count, 0,
           "blob: null string stops decoding");
        Eq(ModInspector.DecodeStringArgs(new byte[] { 0x02, 0x00, 0x01, (byte)'x' }, 1).Count, 0,
           "blob: wrong prolog yields nothing");
        Eq(ModInspector.DecodeStringArgs(new byte[] { 0x01, 0x00, 0x00 }, 1)[0], "",
           "blob: empty string is valid");
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

Erwartet: `CS0103: The name 'ModInspector' does not exist`.

- [ ] **Step 3: Implementieren**

`src/StfcModManager/Core/ModInspector.cs`:

```csharp
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace StfcModManager.Core;

public sealed record PluginInfo(
    string Guid,
    string Name,
    string Version,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Incompatibilities);

/// <summary>
/// Liest BepInEx-Metadaten aus einer DLL, ohne sie zu laden. Damit unterscheidet
/// der Manager echte Plugins von geteilten Bibliotheken (Newtonsoft, protobuf-net,
/// UniverseLib) im selben plugins-Ordner.
/// </summary>
public static class ModInspector
{
    public static PluginInfo? Read(string dllPath)
    {
        try
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();

            string? guid = null, name = null, version = null;
            var deps = new List<string>();
            var incompat = new List<string>();

            foreach (var handle in md.CustomAttributes)
            {
                var attr = md.GetCustomAttribute(handle);
                switch (AttributeTypeName(md, attr))
                {
                    case "BepInPlugin":
                        var a = DecodeStringArgs(md.GetBlobBytes(attr.Value), 3);
                        if (a.Count == 3) { guid = a[0]; name = a[1]; version = a[2]; }
                        break;
                    case "BepInDependency":
                        var d = DecodeStringArgs(md.GetBlobBytes(attr.Value), 1);
                        if (d.Count == 1) deps.Add(d[0]);
                        break;
                    case "BepInIncompatibility":
                        var i = DecodeStringArgs(md.GetBlobBytes(attr.Value), 1);
                        if (i.Count == 1) incompat.Add(i[0]);
                        break;
                }
            }

            return guid is null
                ? null
                : new PluginInfo(guid, string.IsNullOrEmpty(name) ? guid : name,
                                 version ?? "0.0.0", deps, incompat);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        switch (attr.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var mr = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (mr.Parent.Kind != HandleKind.TypeReference) return null;
                return md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
            case HandleKind.MethodDefinition:
                var mdef = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                return md.GetString(md.GetTypeDefinition(mdef.GetDeclaringType()).Name);
            default:
                return null;
        }
    }

    /// <summary>
    /// Dekodiert die festen Zeichenketten-Argumente eines Attribut-Blobs
    /// (ECMA-335 II.23.3). Bricht ab, sobald ein Argument kein String ist.
    /// </summary>
    public static IReadOnlyList<string> DecodeStringArgs(byte[] blob, int max)
    {
        var result = new List<string>();
        if (blob.Length < 2 || blob[0] != 0x01 || blob[1] != 0x00) return result;

        var pos = 2;
        for (var i = 0; i < max; i++)
        {
            if (pos >= blob.Length) break;
            if (blob[pos] == 0xFF) break;                       // null-String: Ende
            if (!TryReadCompressedUInt(blob, ref pos, out var len)) break;
            if (pos + len > blob.Length) break;
            result.Add(Encoding.UTF8.GetString(blob, pos, (int)len));
            pos += (int)len;
        }
        return result;
    }

    private static bool TryReadCompressedUInt(byte[] b, ref int pos, out uint value)
    {
        value = 0;
        if (pos >= b.Length) return false;
        var b0 = b[pos];
        if ((b0 & 0x80) == 0) { value = b0; pos += 1; return true; }
        if ((b0 & 0xC0) == 0x80)
        {
            if (pos + 1 >= b.Length) return false;
            value = (uint)(((b0 & 0x3F) << 8) | b[pos + 1]); pos += 2; return true;
        }
        if ((b0 & 0xE0) == 0xC0)
        {
            if (pos + 3 >= b.Length) return false;
            value = (uint)(((b0 & 0x1F) << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]);
            pos += 4; return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Test laufen lassen, Erfolg bestätigen**

Erwartet: `13 passed, 0 failed`.

- [ ] **Step 5: Gegenprobe an echten DLLs**

Sofern der Spielordner vorhanden ist, einmalig von Hand prüfen — nicht als
dauerhafter Test, weil er von der lokalen Installation abhinge:

```powershell
dotnet run --project src/StfcModManager/StfcModManager.csproj -- --selftest
```

Dann in einer `dotnet script`-freien Variante: temporär in `Main` eine Schleife
über `"C:\Games\Star Trek Fleet Command\STFC\default\game\BepInEx\plugins\*.dll"`
einbauen, Ausgabe prüfen, wieder entfernen. Erwartet: acht Treffer mit GUIDs wie
`Optimus.STFC.Berserker`, `stfc.soundwave`; **kein** Treffer für
`Newtonsoft.Json.dll`, `protobuf-net.dll`, `UniverseLib.BIE.IL2CPP.Interop.dll`.

- [ ] **Step 6: Commit**

```bash
git add src
git commit -m "feat(core): read BepInPlugin metadata from mod assemblies"
```

---

## Task 4: Archiv-Zuordnung und Ablehnungsregeln (PackageMapper)

**Files:**
- Create: `src/StfcModManager/Core/PackageMapper.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `SelfTest.Eq`, `SelfTest.Check`
- Produces:
  - `record MappedFile(string Entry, string Target)` — `Target` ist relativ zum Spielordner, mit Backslashes
  - `record MapResult(IReadOnlyList<MappedFile> Files, string? Rejection)`
  - `PackageMapper.MapEntries(IReadOnlyList<string>) -> MapResult` (rein, getestet)
  - `PackageMapper.MapArchive(string zipPath) -> MapResult`
  - `PackageMapper.MapSingleFile(string filePath) -> MapResult`

- [ ] **Step 1: Failing tests schreiben**

```csharp
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
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

Erwartet: `CS0103: The name 'PackageMapper' does not exist`.

- [ ] **Step 3: Implementieren**

`src/StfcModManager/Core/PackageMapper.cs`:

```csharp
using System.IO.Compression;

namespace StfcModManager.Core;

public sealed record MappedFile(string Entry, string Target);
public sealed record MapResult(IReadOnlyList<MappedFile> Files, string? Rejection);

/// <summary>
/// Bildet den Inhalt eines Releases auf Zielpfade im Spielordner ab.
/// Der Manager fuehrt nichts aus, deshalb werden ausfuehrbare Dateien nicht
/// etwa uebersprungen, sondern das ganze Paket abgelehnt.
/// </summary>
public static class PackageMapper
{
    private static readonly string[] Blocked =
        { ".exe", ".bat", ".cmd", ".ps1", ".psm1", ".msi", ".scr", ".vbs", ".js", ".jar", ".com", ".lnk" };

    private static readonly string[] IgnoredNested = { ".zip", ".7z", ".rar", ".tar", ".gz" };

    private static readonly string[] GameRootFiles =
        { "version.dll", "version.dll_", "winhttp.dll", "doorstop_config.ini" };

    public static MapResult MapEntries(IReadOnlyList<string> entryNames)
    {
        var entries = new List<string>();
        foreach (var raw in entryNames)
        {
            var e = raw.Replace('\\', '/').Trim();
            if (e.Length == 0 || e.EndsWith('/')) continue;              // Verzeichniseintrag

            if (e.StartsWith('/') || (e.Length > 1 && e[1] == ':'))
                return new MapResult([], $"archive contains an absolute path: {raw}");
            if (e.Split('/').Any(s => s == ".."))
                return new MapResult([], $"archive contains a path traversal: {raw}");

            var ext = Path.GetExtension(e).ToLowerInvariant();
            if (Blocked.Contains(ext))
                return new MapResult([], $"archive contains an executable file: {raw}");
            if (IgnoredNested.Contains(ext)) continue;                   // verschachtelte Archive: ignorieren

            entries.Add(e);
        }

        if (entries.Count == 0)
            return new MapResult([], "archive contains no installable files");

        var hasBepInEx = entries.Any(e => Segments(e).Any(IsBepInEx));

        var files = new List<MappedFile>();
        foreach (var e in entries)
        {
            var parts = Segments(e);
            var idx = Array.FindIndex(parts, IsBepInEx);
            var target = hasBepInEx && idx >= 0
                ? string.Join('\\', parts[idx..])          // alles vor "BepInEx" abschneiden
                : MapLoose(parts[^1]);
            files.Add(new MappedFile(e, target));
        }

        return new MapResult(files, null);
    }

    private static string[] Segments(string entry) => entry.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsBepInEx(string s) => s.Equals("BepInEx", StringComparison.OrdinalIgnoreCase);

    private static string MapLoose(string fileName)
        => GameRootFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)
           || fileName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : Path.Combine("BepInEx", "plugins", fileName);

    public static MapResult MapArchive(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return MapEntries(zip.Entries.Select(e => e.FullName).ToList());
        }
        catch (InvalidDataException)
        {
            return new MapResult([], "file is not a readable zip archive");
        }
    }

    public static MapResult MapSingleFile(string filePath)
        => MapEntries([Path.GetFileName(filePath)]);
}
```

- [ ] **Step 4: Test laufen lassen, Erfolg bestätigen**

Erwartet: `26 passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add src
git commit -m "feat(core): map release contents to game paths, reject executables and zip-slip"
```

---

## Task 5: Zustand und Manager-Log (AppState, AppLog)

**Files:**
- Create: `src/StfcModManager/Core/AppState.cs`
- Create: `src/StfcModManager/Core/AppLog.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `SelfTest.Eq`
- Produces:
  - `AppPaths.Root`, `AppPaths.StateFile`, `AppPaths.LogDir`, `AppPaths.BackupDir`, `AppPaths.ConfigBackupDir`, `AppPaths.LocalMods`
  - `class AppState` mit `SchemaVersion, GamePath, LastKnownClientBuild, Mods, SharedFiles, TrustedRepos`
  - `class ModEntry` mit `Id, Name, Version, Enabled, SourceKind, Repo, ReleaseTag, AssetName, ETag, AutoUpdate, Files, InstalledAt, InstalledAgainstClientBuild`
  - `class InstalledFile { string Path; string Sha256; }`
  - `class SharedFile { string Path; string Sha256; string FileVersion; List<string> Providers; }`
  - `AppState.Load() -> AppState`, `AppState.Save()`, `AppState.SerializeTo/DeserializeFrom` (rein, getestet)
  - `AppLog.Info/Warn/Error(string)`, `AppLog.CurrentFile`

- [ ] **Step 1: Failing tests schreiben**

```csharp
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
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

Erwartet: `CS0103: The name 'AppState' does not exist`.

- [ ] **Step 3: AppState implementieren**

`src/StfcModManager/Core/AppState.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StfcModManager.Core;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StfcModManager");

    public static string StateFile       => Path.Combine(Root, "state.json");
    public static string LogDir          => Path.Combine(Root, "logs");
    public static string BackupDir       => Path.Combine(Root, "backup");
    public static string ConfigBackupDir => Path.Combine(Root, "config-backup");
    public static string DownloadDir     => Path.Combine(Root, "download");

    /// <summary>Ablageordner fuer lokale Mods, neben der EXE.</summary>
    public static string LocalMods => Path.Combine(AppContext.BaseDirectory, "LocalMods");
}

public sealed class InstalledFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class SharedFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string FileVersion { get; set; } = "";
    public List<string> Providers { get; set; } = [];
}

public sealed class ModEntry
{
    /// <summary>BepInPlugin-GUID, sonst der Dateiname.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>"github" | "local" | "adopted" | "native"</summary>
    public string SourceKind { get; set; } = "local";
    public string? Repo { get; set; }
    public string? ReleaseTag { get; set; }
    public string? AssetName { get; set; }
    public string? ETag { get; set; }
    public bool AutoUpdate { get; set; }

    public List<InstalledFile> Files { get; set; } = [];
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public string InstalledAgainstClientBuild { get; set; } = "unknown";

    /// <summary>Nur zur Laufzeit gefuellt, nicht persistiert.</summary>
    [JsonIgnore] public string? AvailableVersion { get; set; }
}

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public string? GamePath { get; set; }
    public string? LastKnownClientBuild { get; set; }
    public List<ModEntry> Mods { get; set; } = [];
    public List<SharedFile> SharedFiles { get; set; } = [];
    public List<string> TrustedRepos { get; set; } = [];
    public string? GitHubToken { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SerializeTo(AppState state) => JsonSerializer.Serialize(state, Options);

    /// <summary>Ein kaputter oder leerer Zustand darf den Start nie verhindern.</summary>
    public static AppState DeserializeFrom(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppState();
        try { return JsonSerializer.Deserialize<AppState>(json, Options) ?? new AppState(); }
        catch (JsonException) { return new AppState(); }
    }

    public static AppState Load()
    {
        try
        {
            return File.Exists(AppPaths.StateFile)
                ? DeserializeFrom(File.ReadAllText(AppPaths.StateFile))
                : new AppState();
        }
        catch (IOException) { return new AppState(); }
    }

    /// <summary>Atomar: erst in eine Nebendatei, dann verschieben. Ein Absturz mittendrin
    /// laesst die alte Datei intakt statt eine halbe zu hinterlassen.</summary>
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.StateFile + ".tmp";
        File.WriteAllText(tmp, SerializeTo(this));
        File.Move(tmp, AppPaths.StateFile, overwrite: true);
    }
}
```

- [ ] **Step 4: AppLog implementieren**

`src/StfcModManager/Core/AppLog.cs`:

```csharp
namespace StfcModManager.Core;

/// <summary>Taggeweise rollierendes Log. Faellt still aus, wenn nicht geschrieben werden kann —
/// ein defektes Log darf die Anwendung nie stoppen.</summary>
public static class AppLog
{
    private static readonly Lock Gate = new();

    public static string CurrentFile =>
        Path.Combine(AppPaths.LogDir, $"manager-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string m) => Write("INFO ", m);
    public static void Warn(string m) => Write("WARN ", m);
    public static void Error(string m) => Write("ERROR", m);

    public static void Error(string m, Exception e) => Write("ERROR", $"{m}: {e}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(CurrentFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
                PruneOldLogs();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void PruneOldLogs()
    {
        var cutoff = DateTime.Now.AddDays(-30);
        foreach (var f in Directory.EnumerateFiles(AppPaths.LogDir, "manager-*.log"))
            if (File.GetLastWriteTime(f) < cutoff)
                try { File.Delete(f); } catch (IOException) { }
    }
}
```

- [ ] **Step 5: Test laufen lassen, Erfolg bestätigen**

Erwartet: `31 passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add src
git commit -m "feat(core): persistent state file and rolling manager log"
```

---

## Task 6: Transaktionale Installation (Installer)

**Files:**
- Create: `src/StfcModManager/Core/Installer.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `AppState`, `ModEntry`, `SharedFile`, `InstalledFile`, `GameInstall`, `AppLog`, `AppPaths`
- Produces:
  - `Installer.Sha256File(string) -> string`
  - `Installer.Apply(string gameRoot, IReadOnlyList<(string Source, string Target)> ops) -> IReadOnlyList<InstalledFile>`
  - `Installer.RegisterShared(AppState, string relPath, string sha, string fileVersion, string modId)`
  - `Installer.ReleaseShared(AppState, string relPath, string modId) -> bool` (rein, getestet; `true` = Datei darf gelöscht werden)
  - `Installer.SetEnabled(GameInstall, ModEntry, bool)`
  - `Installer.Remove(AppState, GameInstall, ModEntry)`

- [ ] **Step 1: Failing tests schreiben**

```csharp
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
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

Erwartet: `CS0103: The name 'Installer' does not exist`.

- [ ] **Step 3: Implementieren**

`src/StfcModManager/Core/Installer.cs`:

```csharp
using System.Diagnostics;
using System.Security.Cryptography;

namespace StfcModManager.Core;

/// <summary>
/// Fuehrt Dateiplaene transaktional aus: erst sichern, dann kopieren. Schlaegt ein
/// Schritt fehl, werden die Sicherungen zurueckgespielt und nichts bleibt halb installiert.
/// </summary>
public static class Installer
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string? FileVersionOf(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>Wirft, wenn ein Ziel den Spielordner verliesse. Zweite Verteidigungslinie
    /// hinter PackageMapper — der Pfad kann zwischen Pruefung und Anwendung nicht mehr wandern.</summary>
    private static string ResolveInside(string gameRoot, string relativeTarget)
    {
        var full = Path.GetFullPath(Path.Combine(gameRoot, relativeTarget));
        var root = Path.GetFullPath(gameRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"target escapes the game folder: {relativeTarget}");
        return full;
    }

    public static IReadOnlyList<InstalledFile> Apply(
        string gameRoot, IReadOnlyList<(string Source, string Target)> ops)
    {
        var opId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupDir = Path.Combine(AppPaths.BackupDir, opId);
        var restored = new List<(string Backup, string Original)>();
        var written = new List<string>();
        var result = new List<InstalledFile>();

        try
        {
            Directory.CreateDirectory(backupDir);

            foreach (var (source, target) in ops)
            {
                var full = ResolveInside(gameRoot, target);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);

                if (File.Exists(full))
                {
                    var backup = Path.Combine(backupDir, target.Replace('\\', '_'));
                    File.Copy(full, backup, overwrite: true);
                    restored.Add((backup, full));
                }
                else
                {
                    written.Add(full);
                }

                File.Copy(source, full, overwrite: true);
                result.Add(new InstalledFile { Path = target, Sha256 = Sha256File(full) });
            }

            AppLog.Info($"applied {ops.Count} file(s), backup {opId}");
            return result;
        }
        catch (Exception e)
        {
            AppLog.Error($"install failed, rolling back {opId}", e);
            foreach (var (backup, original) in restored)
                try { File.Copy(backup, original, overwrite: true); } catch (IOException) { }
            foreach (var f in written)
                try { File.Delete(f); } catch (IOException) { }
            throw;
        }
    }

    public static void RegisterShared(AppState state, string relPath, string sha, string fileVersion, string modId)
    {
        var existing = state.SharedFiles.FirstOrDefault(
            f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            state.SharedFiles.Add(new SharedFile
            {
                Path = relPath, Sha256 = sha, FileVersion = fileVersion, Providers = [modId]
            });
            return;
        }

        if (!existing.Providers.Contains(modId)) existing.Providers.Add(modId);

        // Bei Versionskonflikt gewinnt die hoehere Dateiversion.
        if (CompareVersions(fileVersion, existing.FileVersion) > 0)
        {
            AppLog.Warn($"shared file {relPath}: {modId} provides {fileVersion}, superseding {existing.FileVersion}");
            existing.FileVersion = fileVersion;
            existing.Sha256 = sha;
        }
    }

    /// <summary>Entfernt einen Anbieter. True heisst: kein Mod braucht die Datei mehr, sie darf weg.</summary>
    public static bool ReleaseShared(AppState state, string relPath, string modId)
    {
        var existing = state.SharedFiles.FirstOrDefault(
            f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return true;

        existing.Providers.Remove(modId);
        if (existing.Providers.Count > 0) return false;

        state.SharedFiles.Remove(existing);
        return true;
    }

    internal static int CompareVersions(string a, string b)
        => Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)
            ? va.CompareTo(vb)
            : string.CompareOrdinal(a, b);

    /// <summary>An/Aus per Verschieben — die Konvention, die der Bestand schon benutzt.</summary>
    public static void SetEnabled(GameInstall game, ModEntry mod, bool enabled)
    {
        if (mod.SourceKind == "native")
        {
            var from = enabled ? game.VersionDllDisabled : game.VersionDll;
            var to   = enabled ? game.VersionDll : game.VersionDllDisabled;
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
            mod.Enabled = enabled;
            return;
        }

        Directory.CreateDirectory(game.PluginsDisabled);
        foreach (var f in mod.Files.Where(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(f.Path);
            var active   = Path.Combine(game.Plugins, name);
            var inactive = Path.Combine(game.PluginsDisabled, name);
            var from = enabled ? inactive : active;
            var to   = enabled ? active : inactive;
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
            f.Path = enabled
                ? Path.Combine("BepInEx", "plugins", name)
                : Path.Combine("BepInEx", "plugins-disabled", name);
        }
        mod.Enabled = enabled;
        AppLog.Info($"{mod.Id} {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>Deinstalliert. Configs werden nie geloescht, nur gesichert (Spec §6.6).</summary>
    public static void Remove(AppState state, GameInstall game, ModEntry mod)
    {
        foreach (var f in mod.Files)
        {
            if (!ReleaseShared(state, f.Path, mod.Id)) continue;
            var full = Path.Combine(game.Root, f.Path);
            try { if (File.Exists(full)) File.Delete(full); }
            catch (IOException e) { AppLog.Error($"could not delete {full}", e); throw; }
        }

        BackupConfig(game, mod.Id);
        state.Mods.Remove(mod);
        AppLog.Info($"removed {mod.Id}");
    }

    private static void BackupConfig(GameInstall game, string modId)
    {
        var cfg = Path.Combine(game.Config, modId + ".cfg");
        if (!File.Exists(cfg)) return;
        Directory.CreateDirectory(AppPaths.ConfigBackupDir);
        var dest = Path.Combine(AppPaths.ConfigBackupDir,
                                $"{modId}-{DateTime.Now:yyyyMMdd-HHmmss}.cfg");
        File.Move(cfg, dest);
        AppLog.Info($"config for {modId} moved to {dest}");
    }

    /// <summary>Sicherungen aelter als 30 Tage aufraeumen. Beim Start aufgerufen.</summary>
    public static void PruneBackups()
    {
        if (!Directory.Exists(AppPaths.BackupDir)) return;
        var cutoff = DateTime.Now.AddDays(-30);
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.BackupDir))
            if (Directory.GetLastWriteTime(dir) < cutoff)
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
```

- [ ] **Step 4: Test laufen lassen, Erfolg bestätigen**

Erwartet: `38 passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add src
git commit -m "feat(core): transactional install with rollback and shared-file refcounting"
```

---

## Task 7: GitHub-Quelle (GitHubClient)

**Files:**
- Create: `src/StfcModManager/Core/GitHubClient.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `AppLog`, `AppPaths`
- Produces:
  - `record ReleaseAsset(string Name, string DownloadUrl, long Size)`
  - `record ReleaseInfo(string Tag, IReadOnlyList<ReleaseAsset> Assets, string? ETag)`
  - `GitHubClient.ParseRepoUrl(string) -> (string Owner, string Repo)?` (rein, getestet)
  - `GitHubClient.PickAsset(IReadOnlyList<string> names, string? remembered) -> string?` (rein, getestet; `null` = Auswahl nötig)
  - `GitHubClient.GetLatestReleaseAsync(string owner, string repo, string? etag, string? token, CancellationToken) -> Task<ReleaseInfo?>` (`null` = unverändert, HTTP 304)
  - `GitHubClient.DownloadAssetAsync(ReleaseAsset, string destDir, CancellationToken) -> Task<string>`
  - `GitHubClient.RateLimitHint` — letzte gelesene Rücksetzzeit, für die Fehlermeldung

- [ ] **Step 1: Failing tests schreiben**

```csharp
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
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

Erwartet: `CS0103: The name 'GitHubClient' does not exist`.

- [ ] **Step 3: Implementieren**

`src/StfcModManager/Core/GitHubClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StfcModManager.Core;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);
public sealed record ReleaseInfo(string Tag, IReadOnlyList<ReleaseAsset> Assets, string? ETag);

public static class GitHubClient
{
    private static readonly HttpClient Http = CreateClient();

    public static string? RateLimitHint { get; private set; }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StfcModManager", "0.1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static (string Owner, string Repo)? ParseRepoUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                              ? parts[1][..^4] : parts[1]);
    }

    /// <summary>Erste zutreffende Regel gewinnt. Null heisst: der Aufrufer muss fragen.</summary>
    public static string? PickAsset(IReadOnlyList<string> names, string? remembered)
    {
        if (remembered is not null &&
            names.FirstOrDefault(n => n.Equals(remembered, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;

        var zips = names.Where(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count == 1) return zips[0];
        if (zips.Count > 1) return null;

        var dlls = names.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        return dlls.Count == 1 ? dlls[0] : null;
    }

    /// <summary>Null bedeutet HTTP 304 — das gemerkte ETag ist noch gueltig, nichts Neues.</summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(
        string owner, string repo, string? etag, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/releases/latest");

        if (!string.IsNullOrWhiteSpace(etag))
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await Http.SendAsync(req, ct);
        NoteRateLimit(res);

        if (res.StatusCode == HttpStatusCode.NotModified) return null;
        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"{owner}/{repo} has no published release");
        if ((int)res.StatusCode == 403 && RateLimitHint is not null)
            throw new InvalidOperationException(
                $"GitHub rate limit reached. Resets at {RateLimitHint}. Add a personal access token in Settings to raise it.");
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            throw new InvalidOperationException("latest release is a pre-release and is ignored");

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr))
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (name is not null && url is not null) assets.Add(new ReleaseAsset(name, url, size));
            }
        }

        return new ReleaseInfo(tag, assets, res.Headers.ETag?.Tag);
    }

    private static void NoteRateLimit(HttpResponseMessage res)
    {
        if (!res.Headers.TryGetValues("X-RateLimit-Remaining", out var rem)) return;
        if (rem.FirstOrDefault() != "0") { RateLimitHint = null; return; }
        if (res.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var epoch))
            RateLimitHint = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("HH:mm");
    }

    private static readonly string[] AllowedDownloadHosts =
        { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    public static async Task<string> DownloadAssetAsync(ReleaseAsset asset, string destDir, CancellationToken ct)
    {
        var uri = new Uri(asset.DownloadUrl);
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedDownloadHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                                        || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"refusing to download from {uri.Host}");

        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, asset.Name);

        using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using (var target = File.Create(dest))
            await res.Content.CopyToAsync(target, ct);

        AppLog.Info($"downloaded {asset.Name} ({new FileInfo(dest).Length} bytes)");
        return dest;
    }
}
```

`ParseRepoUrl` gibt ein `ValueTuple` zurück; dessen `ToString()` liefert genau
`(owner, repo)`, worauf die Tests prüfen.

- [ ] **Step 4: Test laufen lassen, Erfolg bestätigen**

Erwartet: `51 passed, 0 failed`.

- [ ] **Step 5: Einmalige Handprobe gegen die echte API**

```powershell
curl.exe -s -H "User-Agent: StfcModManager/0.1.0" https://api.github.com/repos/BepInEx/BepInEx/releases/latest | Select-String '"tag_name"'
```

Erwartet: eine Zeile mit einem Tag. Bestätigt Erreichbarkeit und Formfeld-Namen.

- [ ] **Step 6: Commit**

```bash
git add src
git commit -m "feat(core): github release source with etag caching and asset selection"
```

---

## Task 8: BepInEx erkennen und installieren (BepInExRuntime)

**Files:**
- Create: `src/StfcModManager/Core/BepInExRuntime.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `GameInstall`, `AppLog`, `AppPaths`
- Produces:
  - `BepInExRuntime.PinnedUrl` (const)
  - `BepInExRuntime.Detect(GameInstall) -> string?` — Versionszeichenkette oder `null`
  - `BepInExRuntime.InstallAsync(GameInstall, IProgress<string>, CancellationToken) -> Task`
  - `BepInExRuntime.SafeExtract(string zipPath, string destRoot)` — Zip-Slip-sicheres Entpacken (rein genug für den Test über einen erzeugten Zip)

- [ ] **Step 1: Failing test schreiben**

Der Test baut ein Zip mit einem bösartigen Eintrag im Temp-Ordner und erwartet
eine Ablehnung:

```csharp
        // --- BepInExRuntime: Entpacken darf nicht ausbrechen ---
        var tmpDir = Path.Combine(Path.GetTempPath(), "stfcmm-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var zipPath = Path.Combine(tmpDir, "evil.zip");
            using (var zs = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zs.CreateEntry("../escaped.txt");
                using var w = new StreamWriter(entry.Open());
                w.Write("x");
            }

            var target = Path.Combine(tmpDir, "dest");
            Directory.CreateDirectory(target);
            var threw = false;
            try { BepInExRuntime.SafeExtract(zipPath, target); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, "extract: rejects entries that escape the destination");
            Check(!File.Exists(Path.Combine(tmpDir, "escaped.txt")), "extract: nothing written outside destination");
        }
        finally { try { Directory.Delete(tmpDir, true); } catch (IOException) { } }
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

Erwartet: `CS0103: The name 'BepInExRuntime' does not exist`.

- [ ] **Step 3: Implementieren**

`src/StfcModManager/Core/BepInExRuntime.cs`:

```csharp
using System.Diagnostics;
using System.IO.Compression;

namespace StfcModManager.Core;

/// <summary>
/// Erkennt und installiert die BepInEx-IL2CPP-Laufzeit. Der Build ist gepinnt —
/// beim Anheben muss die URL hier mitwandern (Spec §16).
/// </summary>
public static class BepInExRuntime
{
    public const string PinnedVersion = "6.0.0-be.755";

    public const string PinnedUrl =
        "https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip";

    private const long MinimumArchiveBytes = 20_000_000;   // echte Datei ~33 MB; faengt Fehlerseiten ab

    /// <summary>Versionszeichenkette der installierten Laufzeit, sonst null.</summary>
    public static string? Detect(GameInstall game)
    {
        if (!File.Exists(game.WinHttp) || !File.Exists(game.CoreDll)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(game.CoreDll);
            return string.IsNullOrWhiteSpace(v.ProductVersion) ? v.FileVersion : v.ProductVersion;
        }
        catch (FileNotFoundException) { return null; }
    }

    public static async Task InstallAsync(GameInstall game, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Downloading BepInEx runtime (about 33 MB)…");

        Directory.CreateDirectory(AppPaths.DownloadDir);
        var zipPath = Path.Combine(AppPaths.DownloadDir, "bepinex.zip");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        using (var res = await http.GetAsync(PinnedUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var target = File.Create(zipPath);
            await res.Content.CopyToAsync(target, ct);
        }

        var size = new FileInfo(zipPath).Length;
        if (size < MinimumArchiveBytes)
            throw new InvalidOperationException(
                $"The runtime download is incomplete ({size} bytes). Check your internet connection and try again.");

        progress.Report("Extracting runtime…");
        SafeExtract(zipPath, game.Root);
        File.Delete(zipPath);

        AppLog.Info($"BepInEx {PinnedVersion} installed into {game.Root}");
        progress.Report("Runtime installed. The first game start will take several minutes "
                      + "while BepInEx generates its interop assemblies.");
    }

    /// <summary>Entpackt und weigert sich, ausserhalb des Zielordners zu schreiben.</summary>
    public static void SafeExtract(string zipPath, string destRoot)
    {
        var root = Path.GetFullPath(destRoot);
        using var zip = ZipFile.OpenRead(zipPath);

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;

            var full = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"archive entry escapes the destination: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: true);
        }
    }
}
```

- [ ] **Step 4: Test laufen lassen, Erfolg bestätigen**

Erwartet: `53 passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add src
git commit -m "feat(core): detect and bootstrap the pinned BepInEx runtime"
```

---

## Task 9: Logs lesen und Zustand prüfen (LogReader, HealthCheck)

**Files:**
- Create: `src/StfcModManager/Core/LogReader.cs`
- Create: `src/StfcModManager/Core/HealthCheck.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `GameInstall`, `GameLocator`, `AppState`, `ModEntry`, `BepInExRuntime`, `ModInspector`
- Produces:
  - `record LogEntry(string Level, string Source, string Message)`
  - `LogReader.ParseLine(string) -> LogEntry?` (rein, getestet)
  - `LogReader.ReadTail(string path, int maxLines) -> IReadOnlyList<LogEntry>`
  - `enum Severity { Info, Warning, Error }`
  - `record Finding(Severity Severity, string Title, string? Remedy)`
  - `HealthCheck.Run(AppState, GameInstall) -> IReadOnlyList<Finding>`
  - `HealthCheck.CommunityPatchConflict(string tomlText) -> bool` (rein, getestet)

- [ ] **Step 1: Failing tests schreiben**

```csharp
        // --- LogReader: BepInEx-Zeilenformat ---
        var le = LogReader.ParseLine("[Error  :   Hellebarde] NullReferenceException in AutoTasksTick");
        Eq(le?.Level, "Error", "log: level parsed");
        Eq(le?.Source, "Hellebarde", "log: source trimmed");
        Eq(le?.Message, "NullReferenceException in AutoTasksTick", "log: message parsed");
        Eq(LogReader.ParseLine("[Info   :BepInEx] loading")?.Level, "Info", "log: info line");
        Eq(LogReader.ParseLine("plain text without brackets"), null, "log: non-matching line ignored");
        Eq(LogReader.ParseLine("[Warning:  Buezer] slow node scan")?.Source, "Buezer", "log: warning line");

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
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

Erwartet: `CS0103: The name 'LogReader' does not exist`.

- [ ] **Step 3: LogReader implementieren**

`src/StfcModManager/Core/LogReader.cs`:

```csharp
using System.Text.RegularExpressions;

namespace StfcModManager.Core;

public sealed record LogEntry(string Level, string Source, string Message);

public static partial class LogReader
{
    [GeneratedRegex(@"^\[(?<level>Info|Debug|Message|Warning|Error|Fatal)\s*:\s*(?<src>[^\]]+)\]\s*(?<msg>.*)$",
                    RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LineFormat();

    public static LogEntry? ParseLine(string line)
    {
        var m = LineFormat().Match(line);
        return m.Success
            ? new LogEntry(m.Groups["level"].Value, m.Groups["src"].Value.Trim(), m.Groups["msg"].Value.Trim())
            : null;
    }

    /// <summary>Liest die letzten maxLines Zeilen und gibt nur Warnungen und Fehler zurueck.</summary>
    public static IReadOnlyList<LogEntry> ReadTail(string path, int maxLines = 5000)
    {
        if (!File.Exists(path)) return [];

        var tail = new Queue<string>(maxLines);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == maxLines) tail.Dequeue();
                tail.Enqueue(line);
            }
        }
        catch (IOException) { return []; }

        return tail.Select(ParseLine)
                   .Where(e => e is not null && e.Level is "Warning" or "Error" or "Fatal")
                   .Select(e => e!)
                   .ToList();
    }
}
```

- [ ] **Step 4: HealthCheck implementieren**

`src/StfcModManager/Core/HealthCheck.cs`:

```csharp
using System.Text.RegularExpressions;

namespace StfcModManager.Core;

public enum Severity { Info, Warning, Error }

public sealed record Finding(Severity Severity, string Title, string? Remedy = null);

/// <summary>Die neun Pruefungen aus Spec §8.</summary>
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

    private static PluginInfo? FirstPluginInfo(GameInstall game, ModEntry mod)
    {
        var dll = mod.Files.FirstOrDefault(x => x.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        return dll is null ? null : ModInspector.Read(Path.Combine(game.Root, dll.Path));
    }
}
```

- [ ] **Step 5: Test laufen lassen, Erfolg bestätigen**

Erwartet: `64 passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add src
git commit -m "feat(core): parse bepinex logs and run installation health checks"
```

---

## Task 10: Redaktion und Supportpaket (Redactor, SupportBundle)

**Files:**
- Create: `src/StfcModManager/Core/Redactor.cs`
- Create: `src/StfcModManager/Core/SupportBundle.cs`
- Modify: `src/StfcModManager/SelfTest.cs`

**Interfaces:**
- Consumes: `AppState`, `GameInstall`, `GameLocator`, `HealthCheck`, `BepInExRuntime`, `AppLog`, `AppPaths`
- Produces:
  - `Redactor.RedactLine(string) -> string` (rein, getestet)
  - `Redactor.RedactText(string) -> string`
  - `SupportBundle.Create(AppState, GameInstall, string destZipPath) -> string` — Rückgabe ist der erzeugte Pfad
  - `SupportBundle.PlannedContents(GameInstall) -> IReadOnlyList<string>` — für den Vorschau-Dialog

- [ ] **Step 1: Failing tests schreiben**

```csharp
        // --- Redactor (Spec §9) ---
        Eq(Redactor.RedactLine("ApiKey = 1234-abcd-secret"), "ApiKey = [REDACTED]", "redact: key assignment");
        Eq(Redactor.RedactLine("deepl_api_key: abc123"), "deepl_api_key: [REDACTED]", "redact: colon separator");
        Eq(Redactor.RedactLine("Password=hunter2"), "Password=[REDACTED]", "redact: password");
        Eq(Redactor.RedactLine("Authorization: Bearer ey.J9.abc"), "Authorization: [REDACTED]",
           "redact: authorization header line");
        Eq(Redactor.RedactLine("contact me at a.b+c@example.com now"),
           "contact me at [REDACTED-EMAIL] now", "redact: email");
        Eq(Redactor.RedactLine("user jd73d2aac9f4b81e5c6a7d8e9f01 logged in"),
           "user [REDACTED-ID] logged in", "redact: long alphanumeric id");
        Eq(Redactor.RedactLine("[Info :Hellebarde] attacking hostile level 40"),
           "[Info :Hellebarde] attacking hostile level 40", "redact: ordinary line untouched");
        Eq(Redactor.RedactLine("MaxLevel = 40"), "MaxLevel = 40", "redact: harmless assignment untouched");
```

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestätigen**

Erwartet: `CS0103: The name 'Redactor' does not exist`.

- [ ] **Step 3: Redactor implementieren**

`src/StfcModManager/Core/Redactor.cs`:

```csharp
using System.Text.RegularExpressions;

namespace StfcModManager.Core;

/// <summary>
/// Entfernt Geheimnisse aus Text, bevor er ins Supportpaket wandert. Noetig, weil
/// zum Beispiel der UniversalTranslator seinen DeepL-Schluessel in der .cfg haelt
/// und Spiel-Logs Spieler-IDs enthalten koennen.
/// Bewusst grosszuegig: lieber eine Pruefsumme zu viel maskiert als eine ID zu wenig.
/// </summary>
public static partial class Redactor
{
    [GeneratedRegex(@"^(?<head>[^=:\r\n]*\b(?:key|token|secret|password|passwort|api|auth|authorization)\b[^=:\r\n]*)(?<sep>\s*[=:]\s*)(?<val>\S.*)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // Mindestens 24 alphanumerische Zeichen mit wenigstens einer Ziffer:
    // trifft Spieler-uids, Sitzungstoken und Pruefsummen.
    [GeneratedRegex(@"\b(?=[A-Za-z0-9]*[0-9])[A-Za-z0-9]{24,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongId();

    public static string RedactLine(string line)
    {
        var m = SecretAssignment().Match(line);
        if (m.Success)
            return m.Groups["head"].Value + m.Groups["sep"].Value + "[REDACTED]";

        line = Email().Replace(line, "[REDACTED-EMAIL]");
        line = LongId().Replace(line, "[REDACTED-ID]");
        return line;
    }

    public static string RedactText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) sb.AppendLine(RedactLine(line));
        return sb.ToString();
    }
}
```

- [ ] **Step 4: SupportBundle implementieren**

`src/StfcModManager/Core/SupportBundle.cs`:

```csharp
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace StfcModManager.Core;

/// <summary>Sammelt Logs, Konfigurationen und Umgebungsdaten in ein redigiertes ZIP.</summary>
public static class SupportBundle
{
    private const long PerFileTailBytes = 5 * 1024 * 1024;
    private const long TotalBudgetBytes = 20 * 1024 * 1024;

    public static IReadOnlyList<string> PlannedContents(GameInstall game)
    {
        var list = new List<string>
        {
            game.LogOutput, game.LogOutput + ".1", game.ErrorLog,
            game.CommunityPatchLog, game.CommunityPatchToml, game.DoorstopConfig,
            Path.Combine(GameLocator.UnityLogDir(), "Player.log"),
            Path.Combine(GameLocator.UnityLogDir(), "Player-prev.log"),
            AppLog.CurrentFile
        };
        if (Directory.Exists(game.Config))
            list.AddRange(Directory.EnumerateFiles(game.Config, "*.cfg"));   // NUR .cfg — Spec §2.2b
        return list.Where(File.Exists).ToList();
    }

    public static string Create(AppState state, GameInstall game, string destZipPath)
    {
        var skipped = new StringBuilder();
        long budget = TotalBudgetBytes;

        Directory.CreateDirectory(Path.GetDirectoryName(destZipPath)!);
        using (var zip = ZipFile.Open(destZipPath, ZipArchiveMode.Create))
        {
            foreach (var path in PlannedContents(game))
            {
                var size = new FileInfo(path).Length;
                if (budget <= 0)
                {
                    skipped.AppendLine($"{Path.GetFileName(path)}: skipped, 20 MB package budget exhausted");
                    continue;
                }

                var (text, note) = ReadTailAsText(path);
                if (note is not null) skipped.AppendLine($"{Path.GetFileName(path)}: {note}");

                var redacted = Redactor.RedactText(text);
                var bytes = Encoding.UTF8.GetByteCount(redacted);
                if (bytes > budget)
                {
                    skipped.AppendLine($"{Path.GetFileName(path)}: skipped, would exceed the package budget ({size} bytes)");
                    continue;
                }

                budget -= bytes;
                WriteEntry(zip, "collected/" + Path.GetFileName(path), redacted);
            }

            // Selbst erzeugte Dateien werden nicht redigiert — sie enthalten nur eigene Daten.
            WriteEntry(zip, "inventory.json", BuildInventory(state));
            WriteEntry(zip, "environment.txt", BuildEnvironment(state, game));
            WriteEntry(zip, "health.txt", BuildHealth(state, game));
            if (skipped.Length > 0) WriteEntry(zip, "SKIPPED.txt", skipped.ToString());
        }

        AppLog.Info($"support package written to {destZipPath}");
        return destZipPath;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(content);
    }

    /// <summary>Liest hoechstens die letzten 5 MB einer Datei.</summary>
    private static (string Text, string? Note) ReadTailAsText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string? note = null;
            if (stream.Length > PerFileTailBytes)
            {
                stream.Seek(-PerFileTailBytes, SeekOrigin.End);
                note = $"truncated to the last {PerFileTailBytes / 1024 / 1024} MB of {stream.Length} bytes";
            }
            using var reader = new StreamReader(stream);
            return (reader.ReadToEnd(), note);
        }
        catch (IOException e) { return ("", "could not be read: " + e.Message); }
    }

    private static string BuildInventory(AppState state)
        => JsonSerializer.Serialize(
            state.Mods.Select(m => new
            {
                m.Id, m.Name, m.Version, m.Enabled, m.SourceKind, m.Repo, m.ReleaseTag,
                m.InstalledAt, m.InstalledAgainstClientBuild,
                Files = m.Files.Select(f => new { f.Path, f.Sha256 })
            }),
            new JsonSerializerOptions { WriteIndented = true });

    private static string BuildEnvironment(AppState state, GameInstall game)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"manager version : {typeof(SupportBundle).Assembly.GetName().Version}");
        sb.AppendLine($"generated       : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"windows         : {Environment.OSVersion.VersionString} ({Environment.Is64BitOperatingSystem switch { true => "x64", false => "x86" }})");
        sb.AppendLine($"game path       : {game.Root}");
        sb.AppendLine($"client build    : {GameLocator.ReadClientBuild(game.Root)}");
        sb.AppendLine($"bepinex         : {BepInExRuntime.Detect(game) ?? "not installed"}");
        sb.AppendLine($"game running    : {GameLocator.IsGameRunning()}");
        sb.AppendLine($"version.dll     : {(File.Exists(game.VersionDll) ? "active" : File.Exists(game.VersionDllDisabled) ? "disabled" : "absent")}");
        sb.AppendLine($"managed mods    : {state.Mods.Count}");
        return sb.ToString();
    }

    private static string BuildHealth(AppState state, GameInstall game)
    {
        var sb = new StringBuilder();
        foreach (var f in HealthCheck.Run(state, game))
        {
            sb.AppendLine($"[{f.Severity}] {f.Title}");
            if (f.Remedy is not null) sb.AppendLine($"          -> {f.Remedy}");
        }
        return sb.Length == 0 ? "No findings." : sb.ToString();
    }
}
```

- [ ] **Step 5: Test laufen lassen, Erfolg bestätigen**

Erwartet: `72 passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add src
git commit -m "feat(core): redacted support package with size budget"
```

---

## Task 11: Selbst-Update (SelfUpdate)

**Files:**
- Create: `src/StfcModManager/Core/SelfUpdate.cs`
- Modify: `src/StfcModManager/Program.cs`

**Interfaces:**
- Consumes: `GitHubClient`, `AppLog`, `AppPaths`
- Produces:
  - `SelfUpdate.RepoOwner`, `SelfUpdate.RepoName` (const)
  - `SelfUpdate.CleanupOldExecutable()`
  - `SelfUpdate.CheckAsync(string? token, CancellationToken) -> Task<ReleaseInfo?>`
  - `SelfUpdate.ApplyAsync(ReleaseAsset, CancellationToken) -> Task` — startet den Prozess neu

- [ ] **Step 1: Implementieren**

Keine Assert-Prüfung möglich: der Umbenenn-Tanz lässt sich nur an einer echten
laufenden EXE beobachten. Stattdessen Handprobe in Step 3.

`src/StfcModManager/Core/SelfUpdate.cs`:

```csharp
using System.Diagnostics;
using System.Reflection;

namespace StfcModManager.Core;

/// <summary>
/// Ersetzt die eigene EXE. Eine laufende EXE laesst sich nicht ueberschreiben,
/// aber umbenennen — daraus besteht der ganze Trick. Kein Hilfsprozess noetig.
/// </summary>
public static class SelfUpdate
{
    public const string RepoOwner = "trcyberoptic";
    public const string RepoName = "stfc-modmanager";

    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("cannot determine own executable path");

    private static string OldPath => ExePath + ".old";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Beim Start aufrufen: raeumt die umbenannte Vorgaengerversion weg.</summary>
    public static void CleanupOldExecutable()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); }
        catch (IOException) { /* noch gesperrt, naechster Start versucht es erneut */ }
    }

    public static async Task<ReleaseInfo?> CheckAsync(string? token, CancellationToken ct)
    {
        var release = await GitHubClient.GetLatestReleaseAsync(RepoOwner, RepoName, null, token, ct);
        if (release is null) return null;

        var tag = release.Tag.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var latest)) return null;
        return latest > CurrentVersion ? release : null;
    }

    public static async Task ApplyAsync(ReleaseAsset asset, CancellationToken ct)
    {
        var downloaded = await GitHubClient.DownloadAssetAsync(asset, AppPaths.DownloadDir, ct);
        var staged = ExePath + ".new";
        File.Copy(downloaded, staged, overwrite: true);

        CleanupOldExecutable();
        File.Move(ExePath, OldPath);        // erlaubt, auch waehrend der Prozess laeuft
        File.Move(staged, ExePath);

        AppLog.Info($"self-update applied, restarting into {asset.Name}");
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });
        Application.Exit();
    }
}
```

- [ ] **Step 2: In Program.cs einhängen**

`Main` erweitern — direkt vor `ApplicationConfiguration.Initialize()`:

```csharp
        Core.SelfUpdate.CleanupOldExecutable();
        Core.Installer.PruneBackups();
```

- [ ] **Step 3: Handprobe des Umbenenn-Tanzes**

```powershell
dotnet publish src/StfcModManager/StfcModManager.csproj -c Release -o publish
```

`publish\StfcModManager.exe` starten. Während sie läuft, in einer zweiten Shell:

```powershell
Rename-Item publish\StfcModManager.exe StfcModManager.exe.old
Copy-Item publish\StfcModManager.exe.old publish\StfcModManager.exe
```

Erwartet: das Umbenennen gelingt trotz laufendem Prozess. Danach beide Dateien
wieder aufräumen. Bestätigt die Annahme, auf der `ApplyAsync` beruht.

- [ ] **Step 4: Commit**

```bash
git add src
git commit -m "feat(core): self-update via rename of the running executable"
```

---

## Task 12: Hauptfenster (MainForm)

**Files:**
- Create: `src/StfcModManager/Ui/MainForm.cs`
- Modify: `src/StfcModManager/Program.cs`

**Interfaces:**
- Consumes: alle `Core`-Typen
- Produces:
  - `MainForm.Rescan()` — Adoption nach Spec §7
  - `MainForm.RefreshUi()`
  - `MainForm.AdoptFromDisk(AppState, GameInstall)` — statisch, damit Task 13 sie nach einer Installation aufrufen kann

- [ ] **Step 1: Implementieren**

`src/StfcModManager/Ui/MainForm.cs`. Bewusst ohne Designer-Datei — der Aufbau
steht im Konstruktor, das ist bei einem Fenster übersichtlicher als zwei Dateien.

```csharp
using StfcModManager.Core;

namespace StfcModManager.Ui;

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
        if (nativePresent && nativeEntry is null)
            state.Mods.Add(new ModEntry
            {
                Id = "community-patch",
                Name = "Community Mod (version.dll)",
                Version = "—",
                Enabled = File.Exists(game.VersionDll),
                SourceKind = "native"
            });
        else if (nativeEntry is not null)
            nativeEntry.Enabled = File.Exists(game.VersionDll);
        else if (!nativePresent && nativeEntry is not null)
            state.Mods.Remove(nativeEntry);
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
        if (Guard()) { _suppressCheckEvents = true; e.Item.Checked = mod.Enabled; _suppressCheckEvents = false; return; }

        try
        {
            Installer.SetEnabled(_game, mod, e.Item.Checked);
            _state.Save();
        }
        catch (IOException ex)
        {
            AppLog.Error("toggle failed", ex);
            MessageBox.Show(this, ex.Message, "Could not change the mod", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Could not remove the mod", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        Dialogs.AddFromGitHub(this, _state, _game);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("support package failed", ex);
            MessageBox.Show(this, ex.Message, "Could not write the package", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
```

- [ ] **Step 2: Program.cs auf MainForm umstellen**

```csharp
        Application.Run(new Ui.MainForm());
```

- [ ] **Step 3: Bauen und starten**

```powershell
dotnet build src/StfcModManager/StfcModManager.csproj
dotnet run --project src/StfcModManager/StfcModManager.csproj
```

Erwartet auf einem Rechner mit installiertem Spiel: Kopfzeile zeigt Pfad, Client
`254` und die BepInEx-Version; der Mods-Tab listet die acht adoptierten Plugins
und den Eintrag „Community Mod (version.dll)"; **keine** Zeilen für
`Newtonsoft.Json.dll`, `protobuf-net*.dll`, `UniverseLib*.dll`.

- [ ] **Step 4: Selbsttest erneut laufen lassen**

```powershell
dotnet run --project src/StfcModManager/StfcModManager.csproj -- --selftest
```

Erwartet: unverändert `72 passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add src
git commit -m "feat(ui): main window with adoption scan, health tab and support package"
```

---

## Task 13: Dialoge, Installation und Aktualisierung (Dialogs)

**Files:**
- Create: `src/StfcModManager/Ui/Dialogs.cs`

**Interfaces:**
- Consumes: `AppState`, `GameInstall`, `GitHubClient`, `PackageMapper`, `ModInspector`, `Installer`, `AppLog`, `AppPaths`
- Produces:
  - `Dialogs.AddFromGitHub(IWin32Window, AppState, GameInstall)`
  - `Dialogs.InstallLocalPath(IWin32Window, AppState, GameInstall, string path)`
  - `Dialogs.CheckUpdatesAsync(IWin32Window, AppState, GameInstall) -> Task`
  - `Dialogs.SetUpdateSource(IWin32Window, AppState, ModEntry, Action onDone)`

- [ ] **Step 1: Implementieren**

`src/StfcModManager/Ui/Dialogs.cs`:

```csharp
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
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            AppLog.Error($"add from github failed for {ownerName}/{repoName}", e);
            MessageBox.Show(owner, e.Message, "Could not read the release",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            var targets = string.Join("\r\n", map.Files.Select(m => "  " + m.Target));
            var answer = MessageBox.Show(owner,
                $"Repository : {repo}\r\nRelease    : {release.Tag}\r\nFile       : {asset.Name} ({asset.Size} bytes)\r\n" +
                $"SHA-256    : {sha}\r\n\r\nThese files will be written into your game folder:\r\n{targets}\r\n\r\n" +
                "This code will run inside the game. Only continue if you trust the author. Install?",
                "Confirm installation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            state.TrustedRepos.Add(repo);
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
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Error("install failed", e);
            MessageBox.Show(owner, e.Message, "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                AppLog.Error($"update failed for {mod.Repo}", e);
            }
        }
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
}
```

- [ ] **Step 2: Bauen und Selbsttest**

```powershell
dotnet build src/StfcModManager/StfcModManager.csproj
dotnet run --project src/StfcModManager/StfcModManager.csproj -- --selftest
```

Erwartet: Übersetzung fehlerfrei, `72 passed, 0 failed`.

- [ ] **Step 3: Ende-zu-Ende-Probe**

Anwendung starten und der Reihe nach prüfen:

1. „Add from GitHub…" mit `https://github.com/BepInEx/BepInEx` → erwartete
   Meldung, dass kein BepInEx-Plugin im Paket ist (das Repo liefert die
   Laufzeit, kein Plugin). Nichts wird installiert.
2. Eine echte Plugin-DLL per Drag & Drop aufs Fenster ziehen → Vertrauensdialog
   entfällt (lokal), Mod erscheint in der Liste mit korrektem Namen und Version.
3. Häkchen entfernen → Datei liegt danach in `BepInEx\plugins-disabled\`.
4. Rechtsklick → Remove → Datei weg, `.cfg` unter
   `%LOCALAPPDATA%\StfcModManager\config-backup\` vorhanden.
5. „Generate support package" → ZIP im Downloads-Ordner, Explorer öffnet sich.
   ZIP öffnen und prüfen: **keine** `.json`-Datei aus `BepInEx\config\`, und in
   `collected\Optimus.STFC.UniversalTranslator.cfg` steht kein Klartext-Schlüssel.

- [ ] **Step 4: Commit**

```bash
git add src
git commit -m "feat(ui): install, trust confirmation and update flow"
```

---

## Task 14: README, CI und erste Veröffentlichung

**Files:**
- Create: `README.md`
- Create: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: alles Vorige
- Produces: veröffentlichungsfähiges Artefakt `StfcModManager.exe`

- [ ] **Step 1: README schreiben**

`README.md` (Englisch, weil öffentlich):

```markdown
# STFC Mod Manager

A small Windows app that installs, updates and toggles BepInEx mods for
**Star Trek Fleet Command**, and collects a redacted support package when
something goes wrong.

## What it does

- Finds your game folder automatically (reads the launcher's own settings file)
- Installs the BepInEx IL2CPP runtime if it is missing
- Adds mods from a GitHub repository (latest release) or from a local file
- Adopts mods you already installed by hand instead of fighting them
- Enables and disables mods without deleting them
- Warns before known-bad combinations, missing dependencies and after a game update
- Generates a support package with logs, configs and environment data —
  API keys, e-mail addresses and player IDs are removed automatically

## Install

Download `StfcModManager.exe` from the
[latest release](https://github.com/trcyberoptic/stfc-modmanager/releases/latest)
and run it. No installation, no .NET runtime required.

The executable is not code-signed, so Windows SmartScreen will warn on first
run: *More info* → *Run anyway*.

## Safety

The manager never executes anything from a mod release — it only copies files.
Packages containing executables or scripts are refused outright, as are archives
that try to write outside the game folder. Every new repository must be
confirmed once, showing the exact files and their SHA-256 before anything is
written. Auto-update is off by default.

Mods run inside the game process. Only install mods from authors you trust.

## Development

Requires the .NET 10 SDK.

    dotnet build src/StfcModManager/StfcModManager.csproj
    dotnet run --project src/StfcModManager/StfcModManager.csproj -- --selftest
    dotnet publish src/StfcModManager/StfcModManager.csproj -c Release -o publish

`--selftest` runs the assert-based checks and exits non-zero on failure.

## License

GNU General Public License v3.0
```

- [ ] **Step 2: CI-Arbeitsablauf schreiben**

`.github/workflows/build.yml`:

```yaml
name: build

on:
  push:
    branches: [main]
    tags: ['v*']
  pull_request:

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Build
        run: dotnet build src/StfcModManager/StfcModManager.csproj -c Release

      - name: Self-test
        run: dotnet run --project src/StfcModManager/StfcModManager.csproj -c Release -- --selftest

      - name: Publish single file
        run: dotnet publish src/StfcModManager/StfcModManager.csproj -c Release -o publish

      - uses: actions/upload-artifact@v4
        with:
          name: StfcModManager
          path: publish/StfcModManager.exe

      - name: Attach to release
        if: startsWith(github.ref, 'refs/tags/v')
        uses: softprops/action-gh-release@v2
        with:
          files: publish/StfcModManager.exe
```

- [ ] **Step 3: Lizenzdatei anlegen**

`LICENSE` mit dem GPL-3.0-Text, Rechteinhaber `trcyberoptic`, Jahr 2026.

- [ ] **Step 4: Veröffentlichen**

Das Anlegen des öffentlichen Repositorys ist eine nach außen wirkende Handlung
und braucht eine ausdrückliche Bestätigung des Nutzers, bevor sie ausgeführt
wird. Erst danach:

```bash
gh repo create trcyberoptic/stfc-modmanager --public --source=. --remote=origin --push
git tag v0.1.0
git push origin v0.1.0
```

CI-Lauf abwarten, `StfcModManager.exe` aus dem Release herunterladen, auf einem
Rechner **ohne** .NET starten und prüfen, dass das Fenster erscheint.

- [ ] **Step 5: Commit**

```bash
git add README.md LICENSE .github
git commit -m "docs: readme, license and CI workflow"
```

---

## Selbstprüfung des Plans

**Abdeckung der Spec.**

| Spec | Task |
|---|---|
| §2.2a INI-Präfix | 2 |
| §2.2b Config-Datencaches | 10 |
| §2.2c Plugin vs. Bibliothek | 3, 12 |
| §3 Stack und Publish | 1 |
| §5 Komponenten | 2–11 |
| §6.1 Zustand | 5 |
| §6.2 GitHub hinzufügen | 7, 13 |
| §6.3 Zuordnung und Ablehnung | 4 |
| §6.4 geteilte Dateien | 6 |
| §6.5 an/aus | 6, 12 |
| §6.6 Deinstallation, Config-Sicherung | 6, 12 |
| §6.7 Transaktionalität | 6 |
| §7 Adoption | 12 |
| §8 Health-Checks | 9 |
| §9 Supportpaket, Redaktion | 10, 12 |
| §10 Updates, Selbst-Update | 11, 13 |
| §11 Sicherheit | 4, 7, 8, 13 |
| §12 Oberfläche | 12, 13 |
| §13 Fehlerbehandlung | 5, 12, 13 |
| §14 Tests | 1–10 |
| §16 offene Punkte | 14 |

Ohne eigenen Task, bewusst: der optionale GitHub-PAT ist im Zustand als
`GitHubToken` vorgesehen und wird von `GitHubClient` verwendet, hat aber noch
keine Eingabemöglichkeit in der Oberfläche. Nachziehen, sobald jemand ans
Stundenlimit stößt — bis dahin reicht die Fehlermeldung mit der Rücksetzzeit.

**Typ-Abgleich.** `PluginInfo`, `MapResult`, `MappedFile`, `ModEntry`,
`InstalledFile`, `SharedFile`, `ReleaseInfo`, `ReleaseAsset`, `LogEntry`,
`Finding` und `GameInstall` werden in genau einem Task definiert und in späteren
Tasks unter denselben Namen benutzt. `Installer.Apply` gibt
`IReadOnlyList<InstalledFile>` zurück, wie Task 13 es erwartet.
`GitHubClient.PickAsset` gibt `string?` zurück, `null` bedeutet an beiden
Aufrufstellen „Dialog nötig".
