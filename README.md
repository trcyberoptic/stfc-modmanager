# STFC Mod Manager

[![build](https://github.com/trcyberoptic/stfc-modmanager/actions/workflows/build.yml/badge.svg)](https://github.com/trcyberoptic/stfc-modmanager/actions/workflows/build.yml)
[![latest release](https://img.shields.io/github/v/release/trcyberoptic/stfc-modmanager)](https://github.com/trcyberoptic/stfc-modmanager/releases/latest)
[![license: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)

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
  executable. `LocalMods` isn't scanned automatically — it's simply where the
  "Add local…" file picker starts, so you have a fixed place to drop files
  before installing them.
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
- **Verifies mod downloads.** Mods downloaded from a GitHub release are
  checked against the SHA-256 digest GitHub publishes for that release asset
  before the manager touches your game folder with them. The BepInEx runtime
  comes from `builds.bepinex.dev` instead of GitHub, which doesn't publish a
  digest for it — that download is instead restricted to the pinned host over
  HTTPS, follows no redirects, and is checked for a matching `Content-Length`
  and a minimum size, but not against a hash.

## Install

Download `StfcModManager.exe` from the
[latest release](https://github.com/trcyberoptic/stfc-modmanager/releases/latest)
and run it. No installer, no .NET runtime to install separately — it's a
single self-contained file.

The executable is not code-signed, so Windows SmartScreen will warn on first
run: click *More info*, then *Run anyway*.

**You need:** 64-bit Windows 10 or 11, and Star Trek Fleet Command installed
through its own launcher. Nothing else — no .NET, no Visual C++ runtime, no
admin rights, as long as the game is not installed somewhere only an
administrator can write to.

## Using it

**Close the game first.** Windows keeps mod files locked while it runs, so
every button that changes something is greyed out until you quit. The window
tells you when that is the case, and re-enables itself on its own once the
game exits — you do not need to restart the manager.

1. **Start it.** It finds your game folder by itself. If it cannot, it asks
   you to point at the folder containing `prime.exe`, normally
   `…\STFC\default\game`.
2. **Install the runtime**, if the header says BepInEx is missing. One button,
   about 33 MB. Mods cannot load without it.
3. **Add a mod** — *Add from GitHub…* with the address of the mod's repository,
   or *Add local…* for a `.zip` or `.dll` you already have. The first time you
   use a given repository you get one confirmation dialog listing exactly what
   will be written where; check it, because that is the moment to say no.
4. **Start the game normally**, through its own launcher.

The **first game start after installing or updating the runtime takes several
minutes.** BepInEx is generating files it needs, once. It looks like a hang
and is not one — let it finish.

If mods are already installed by hand, the manager picks them up on first
start instead of installing a second copy beside them. Existing configs are
left alone.

### When something goes wrong

Open the **Problems** tab. It lists what the manager can see for itself —
missing dependencies, mods built against an older game build, errors the game
logged on its last run — usually with what to do about each.

If you need help from someone else, *Generate support package* collects the
logs, your mod list and your configs into one zip in your Downloads folder. API keys,
tokens, e-mail addresses and player IDs are stripped out before it is written,
so it is safe to share — but it does contain folder paths and your Windows
user name, and the dialog says so before it writes anything.

If a mod broke your game, disabling it takes one click and does not delete
anything.

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
- **Nothing ever updates itself.** Mods are only ever downloaded and
  installed when you click to do it, and the manager does not update itself
  at all. There is no automatic-update setting to leave switched on by
  mistake.
- **Every file an install or update replaces is kept**, not overwritten in
  place: it's moved into `%LOCALAPPDATA%\StfcModManager\backup\`, mirroring
  the game folder's own structure, and kept there for 30 days. There is
  deliberately no "Roll back" button in the app — restoring a file is a
  manual copy from that backup folder back into your game folder — but the
  files needed to undo a bad update by hand are always there.

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

Because this is a `WinExe` (a GUI-subsystem executable), running `dotnet run
--project src/StfcModManager/StfcModManager.csproj -- --selftest` from a
terminal prints nothing — `dotnet run` launches it as a child process without
inherited standard handles, so the app's own `AttachConsole` call has nothing
to attach to. This is expected, not a hang: the exit code is authoritative
(0 = every assert passed), so check `$LASTEXITCODE` (PowerShell) or `$?`
(bash) after the command instead of watching for output.

This repository ships a repo-local `nuget.config` that points at nuget.org.
It exists because the project itself references no NuGet packages, but the
self-contained/single-file publish still needs the .NET runtime packs, which
the SDK resolves as implicit NuGet dependencies — without a configured
package source, `dotnet build` fails with `NU1100`.

## License

[GNU General Public License v3.0](LICENSE), © 2026 trcyberoptic.
