# STFC Mod Manager

A small Windows app that installs, updates and toggles [BepInEx](https://github.com/BepInEx/BepInEx)
mods for **Star Trek Fleet Command**, and can put together a redacted support
package when something goes wrong.

## What it does

- **Finds your game folder automatically.** It reads the STFC launcher's own
  `launcher_settings.ini`, falling back to the standard install path if the
  launcher's setting is missing or unreadable.
- **Installs the BepInEx IL2CPP runtime if it is missing.** The runtime build
  is pinned to a specific, known-good release rather than "whatever is
  latest".
- **Adds mods from three sources**: a GitHub repository's latest release, a
  local `.zip`/`.dll` file, or a `LocalMods` folder that sits next to the
  executable and is scanned automatically.
- **Adopts mods you already installed by hand**, instead of fighting them or
  installing a duplicate copy next to them. This includes mods placed in
  subfolders under `BepInEx\plugins` and mods that are currently disabled.
- **Enables and disables mods by moving files**, not deleting them — a
  disabled mod's DLLs move from `BepInEx\plugins` to
  `BepInEx\plugins-disabled` and back. If a shared library (a DLL more than
  one mod depends on, e.g. a common JSON or networking library) is still
  needed by another *enabled* mod, it is left in place rather than moved.
- **Never deletes your configs.** Files under `BepInEx\config` are moved to a
  timestamped backup, never removed, even when the mod that owns them is
  uninstalled.
- **Warns you before trouble, not after.** A set of health checks flags
  known-bad mod combinations, missing dependencies, and mods that were
  installed against a game client build that has since changed.
- **Builds a redacted support package** on request: manager and game logs,
  BepInEx configs, and basic environment data, collected into a zip. API
  keys, tokens, e-mail addresses and player IDs are stripped out
  automatically before anything is written to disk.
- **Verifies every download.** Mod and runtime downloads are checked against
  the SHA-256 digest GitHub publishes for the release asset before the
  manager touches your game folder with them.
- **Updates itself.** New versions of the manager replace its own running
  executable atomically — no separate installer, no leftover temp copies.

## Install

Download `StfcModManager.exe` from the
[latest release](https://github.com/trcyberoptic/stfc-modmanager/releases/latest)
and run it. No installer, no .NET runtime to install separately — it's a
single self-contained file.

The executable is not code-signed, so Windows SmartScreen will warn on first
run: click *More info*, then *Run anyway*.

## Safety

- The manager **never executes anything** it downloads or installs — it only
  copies files into your game folder. There is no code path that runs a mod
  archive's contents.
- A mod package containing an executable, script, installer or similar
  (`.exe`, `.bat`, `.cmd`, `.ps1`, `.msi`, `.scr`, `.vbs`, `.js`, `.jar`, …)
  is **refused outright** — the whole package, not just the offending file.
- Archive entries that try to write outside the game folder (path traversal,
  absolute paths, reparse points/junctions used to escape the target
  directory, alternate data streams, and similar tricks) are rejected before
  a single byte is written.
- **Every new GitHub repository must be confirmed once**, before install: a
  dialog shows the exact release asset, its size, its SHA-256 hash, and the
  full list of files that will be written into your game folder.
- **Auto-update is off by default**, both for the manager itself and for
  individual mods — nothing updates without you asking it to, unless you
  turn it on.

None of this changes the fact that **mods run inside the game process**. The
manager can stop a bad *package* from ever reaching disk, but it cannot
review what a mod's code does once BepInEx loads it. Only install mods from
authors you trust.

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build src/StfcModManager/StfcModManager.csproj
dotnet run --project src/StfcModManager/StfcModManager.csproj -- --selftest
dotnet publish src/StfcModManager/StfcModManager.csproj -c Release -o publish
```

`--selftest` runs the project's assert-based test suite and exits non-zero on
any failure — this is also what CI runs on every push and pull request. The
`publish` command above produces a single self-contained `win-x64` file,
`publish\StfcModManager.exe`, with no external runtime dependency.

This repository ships a repo-local `nuget.config` that points at nuget.org.
It exists because the project itself references no NuGet packages, but the
self-contained/single-file publish still needs the .NET runtime packs, which
the SDK resolves as implicit NuGet dependencies — without a configured
package source, `dotnet build` fails with `NU1100`.

## License

[GNU General Public License v3.0](LICENSE), © 2026 trcyberoptic.
