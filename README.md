# Day0Gen

A Windows CLI that drives an installed copy of *They Are Billions* in-process to generate a
**day-0 survival save** for a given map seed. The save is produced by the game's own map
generator and native save writer.

It is a reflection tool, not a save editor. It loads the game's own `TheyAreBillions` /
`DXVision` assemblies at runtime and resolves everything by name, so it only works against the
matching game build. `save` mode reads generation parameters from an existing save and
regenerates the map from them; it never modifies the source.

## Requirements

- Windows x64, .NET Framework 4.8 (in-box on current Windows 10/11).
- *They Are Billions* (Steam), build `1.0.14.29` — the build the reflected names were captured
  from. Other builds fail target discovery.
- An interactive desktop session. Engine startup creates Direct3D windows; it cannot run from a
  service/SSH session.
- The game must be closed.
- At least one existing `.zxsav` in the saves folder (used to verify the password machinery when
  the dummy-path probe fails).

There is no separate `DXVision.dll` on disk; it is embedded in `TheyAreBillions.exe` and
materialized by its module initializer.

## Build

```sh
make build      # -> src/bin/Release/net48/Day0Gen.exe
make audit      # C#5-only syntax audit (no toolchain needed)
make clean
```

`make build` fetches a .NET SDK via nix and compiles `src/` in Release. The source is C# 5 /
net48; on Windows it also compiles with in-box `csc.exe /langversion:5`. Put `Day0Gen.exe` in
the game install directory (or pass `--tab-dir`) so assembly resolution works.

## Usage

```
Day0Gen <command> [options]

Commands:
  seed       generate a day-0 survival save; requires --seed
             (legacy `--phase full` keeps its default seed)
  save       regenerate from a source save's SurvivalModeParams; requires --from
  discovery  reflection target discovery (read-only)
  zombie     engine init + account/password/signer verification (read-only)
  genprobe   interactive generator diagnostic; writes nothing besides Day0Gen.log

Common options:
  [--name <string>] [--ncells <int>] [--duration <float>] [--pop <float>]
  [--difficulty <enum>] [--theme <enum>] [--tab-dir <dir>] [--saves-dir <dir>]
  [--validate-signer <zxsav>] [--ui-marshal]
```

**`seed`** requires `--seed`. `--name` defaults to `Day0 <seed>`, `--ncells` must be in
`[64, 512]` (default `256`), `--duration` maps to `FactorGameDuration` and `--pop` to
`FactorZombiePopulation` (both default `1.0`). `--difficulty` / `--theme` take an enum name;
invalid names abort and list the valid values.

**`save`** takes `--from <path.zxsav>` and uses that save's `seed`, `ncells`, `duration`,
`pop`, `difficulty`, and `theme`. Any explicit flag overrides the save-derived value. `--name`
defaults to `<source> (Day0)`. Generation is deterministic, so the terrain matches the source,
but the output is a fresh day-0 state, not a byte-for-byte copy.

**`--phase full`** is an alias for `seed`, keeping the historical default seed `550040233` when
`--seed` is omitted. `--phase discovery|zombie|genprobe` behave as the subcommands.

**Other flags.** `--tab-dir` (default `C:\Program Files (x86)\Steam\steamapps\common\They Are
Billions`), `--saves-dir` (default discovered from the engine, falling back to
`%USERPROFILE%\Documents\My Games\They Are Billions\Saves`), `--validate-signer <zxsav>` (check
a `.zxsav` against its sibling `.zxcheck`), `--ui-marshal` (legacy dispatch onto the engine UI
thread via `Control.Invoke`; A/B diagnostic only, can deadlock — the default main-thread
dispatch is the supported path).

### Example

```bat
cd /d "C:\Program Files (x86)\Steam\steamapps\common\They Are Billions"
Day0Gen.exe seed --seed 550040233 --name "CC 550040233"
```

`run-day0.bat` at the repo root does the same. Launch from the game directory so the assemblies
resolve and `Day0Gen.log` lands next to the exe.

## Output

On success, two files are written to the saves directory: `<name>.zxsav` (a password-encrypted
zip) and `<name>.zxcheck` (the signature, `2.<number>`). The tool refuses to overwrite existing
artifacts. If post-write verification fails, it deletes what it wrote (including any
`<stem>_Crash.*` pair the engine produced) and aborts.

## How it works

1. **Assembly load.** `Assembly.Load("TheyAreBillions")`, run its module initializer to install
   the resolver for the embedded `DXVision` assembly, then `Assembly.Load("DXVision")`.
2. **Start-game envelope.** On the engine UI thread: pause the engine, disable/dispose/null the
   menu game system, set a loading flag, then run the game's own `construct → generate →
   SetLevel → adopt` sequence on a `Task`, mirroring the survival start handler.
3. **Entity snapshot.** `ZXLevelState.PreSave` rebuilds `LevelEntities` from the live registry,
   which is empty in this out-of-band flow. The tool snapshots the generated level's own
   entities (filtered to `CSalvable`), runs `PreSave`, then overwrites `LevelEntities` /
   `LevelFastSerializedEntities` with the snapshot.
4. **Native writer.** The game's own writer serializes the state; the tool produces `.zxcheck`
   and reads the save back to verify the written state before reporting success.

Logs: `Day0Gen.log` (current working directory) has the reflection/invocation trace;
`ZXLog.txt` (saves root) is the engine's own log.

## Troubleshooting

- **"TheyAreBillions process is running."** Close the game completely (check Task Manager), then retry.
- **Engine init hangs or reports a modal-dialog / `UserInteractive` error.** You are in a
  non-interactive session. Run from a logged-in desktop.
- **Target discovery fails or mentions a build mismatch.** The installed build is not
  `1.0.14.29`; the obfuscated names change between builds.
- **"Refusing to overwrite ..."** The target `.zxsav`/`.zxcheck` exists. Delete it or pass a
  different `--name`.
- **Password probe fails and the saves folder is empty.** Let the game write at least one save.
- **Need details.** Read `Day0Gen.log`, then `ZXLog.txt` in the saves root.
