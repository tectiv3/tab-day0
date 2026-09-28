# SPEC — Day0Gen implementation contract

Deliverable: `src/Day0Gen.cs` (+ `src/Day0Gen.csproj` if needed for local build verification).
Single-file C# console tool, **C# 5 compatible**, target **net48**. No compile-time references
to game assemblies — everything game-related is resolved at runtime via reflection.

Read FIRST (reference material):
- `vendor/TABSAT/TABSAT/TABReflector/TABReflector.cs` — zombie-init + password/signing pattern to replicate.
- `vendor/decompiled/` — ILSpy decompile of the EXACT installed game build (v1.0.14). All obfuscated
  names below were verified there; decompile file given for each.
- `PLAN.md` — context, mechanics, safety.

## CLI

```
Day0Gen.exe --phase discovery|zombie|full
  [--tab-dir <dir>]     default: C:\Program Files (x86)\Steam\steamapps\common\They Are Billions
  [--saves-dir <dir>]   default: %USERPROFILE%\Documents\My Games\They Are Billions\Saves
  [--seed N]            default 550040233
  [--ncells N]          default 256
  [--duration F]        default 1.0  (FactorGameDuration)
  [--pop F]             default 1.0  (FactorZombiePopulation)
  [--name S]            default "CC 550040233"
  [--validate-signer <path-to-zxsav>]  run signer on this file, print signature (compare to its .zxcheck)
```

Phases (each strictly gated, later phases imply earlier ones):
1. `discovery` — load TAB assembly, locate and print EVERY reflection target (name + declaring type
   + signature). NO zombie init, NO writes.
2. `zombie` — initialize engine (see below), verify account readable, verify password derivation
   machinery works on a dummy path (e.g. `<saves-dir>\day0gen-probe.dummy` — do NOT create the file;
   catch and log behavior), optionally `--validate-signer` against a real file.
3. `full` — construction + generation + save + verify (below).

## Safety (hard requirements, enforce in code)

- Refuse to start if `TheyAreBillions` process is running (System.Diagnostics.Process.GetProcessesByName).
- Refuse if `<name>.zxsav` or `<name>.zxcheck` already exists in saves dir.
- NEVER write/delete/rename any file except `<name>.zxsav` and `<name>.zxcheck` and `Day0Gen.log`
  (log written to current working directory).
- Before and after phase `full`: compute and log SHA256 of every file in saves dir + parent
  `My Games\They Are Billions` dir; log a summary of anything that changed (must be only our two files).
- Log every reflection invocation (target, args, result/exception) to console + `Day0Gen.log`.
- All failures: log + abort. No partial-state retries.

## Zombie init (replicate TABSAT)

1. Copy ourselves logic-wise: our exe must run FROM the TAB dir (assembly resolution). At startup
   verify `TheyAreBillions.exe` and `DXVision.dll` exist next to us; else `Assembly.Load("TheyAreBillions")` fails —
   print clear error. (Do not copy the exe anywhere — we are already placed there by the operator.)
2. `Assembly.Load("TheyAreBillions")`; type `ZX.Program`; `Main(string[])` (static, nonpublic).
3. Invoke `Main` with our original CLI args on a background thread (TABSAT passes its own args; the
   engine shows an error popup in this mode — that is expected and harmless). Minimize the popup window
   like TABSAT does (ShowWindow SW_MINIMIZE via user32).
4. Poll for readiness: manager type's `get_GameAccount()` (static) returns non-null account object,
   with timeout (~60s). On timeout: dump what's available (DXLog tail if readable) and abort.
5. Table check: `ZXMapTheme` table method `_0023_003Dz4k5FO_0024EclQhr()` (static, returns
   Dictionary<ZXMapThemeType, ZXMapTheme>) — call it; if it throws or returns null/empty, locate and
   invoke the table loader (see "Table loading" below), then re-check. GENERATION REQUIRES THIS TABLE.

## Reflection map (verified against vendor/decompiled)

Discovery strategy per item: (a) public stable name → use directly; (b) obfuscated name from this
build → locate by name; (c) signature scan fallback. Log which strategy matched.

| Purpose | Kind | Name / strategy | Verified in |
|---|---|---|---|
| Engine entry | type+method | `ZX.Program`, `Main(string[])` static nonpublic | TABSAT + all builds |
| Manager class | type | the type having method `get_GameAccount` (static) | `--z4RevDP3eECXqXS6JRA--.cs` |
| Manager current | static prop/method | `_0023_003Dzuartwoo_003D` (returns manager) | same file (used as `ZXGameState`-style singleton accessor on all singletons) |
| GameAccount | static method | `get_GameAccount` on manager | same |
| Signing (zxcheck) | static method | on manager type, params (String, Int32) → String; invoke `(path, 2)`; result written verbatim to `.zxcheck` | `_0023_003DzX5dOGo9W_0024Dop(string,int)` + `_0023_003Dz9QdntbHPY7Me` in same file; format `"<mode>.<number>"` |
| Saves folder | instance method | `_0023_003DzND5ul2zfzAnWdSnC0A_003D_003D()` on manager → string | same file (save list uses it with GetFiles) |
| Save list | instance method | `_0023_003DzegKkTm3kHc6FhM_EOg_003D_003D()` → List<ZXGameStateInfo> | same file |
| GameState type | public type | `ZX.ZXGameState` | `ZX/ZXGameState.cs` |
| GameState.Set / Current | static method/prop | `_0023_003Dz2SXmL2Q_003D(ZXGameState)` / `_0023_003Dzuartwoo_003D()` | same |
| GameState props | public instance | `Name`, `GameMode`, `SurvivalModeParams`, `ChallengeType`, `ChallengeID` (plain names survive — serialization) | same |
| GameState ctor | ctor | `(string name)` | used by survival/CC handlers |
| LevelState | type | `ZX.ZXLevelState`; Set/Current same pattern; instance init `_0023_003DzF1YHTRUZSNRa()` | `ZX/ZXLevelState.cs` + handlers |
| Params type | public type | `ZX.GameSystems.ZXRandomLevelParams`; props `Seed`,`NCells`,`ThemeType`,`FactorGameDuration`,`FactorZombiePopulation`,`DifficultyType`,`Name`,`ScoreFactorFixed`; ctor `()` | `ZX.GameSystems/ZXRandomLevelParams.cs` |
| Enums | public | `ZX.ZXGameModeType` (Survival), `ZX.ZXMapThemeType` (None), `ZX.ZXGameChallengeType` (Default) | decompile |
| Generator | static method | in internal static class `_0023_003Dzyl_NPjjlA7DRfVtsRJCX1kN4BxSr`: `_0023_003DzEzgd90E_003D(ZXRandomLevelParams)` → DXLevel. Signature-scan fallback: static method, 1 param ZXRandomLevelParams, returns DXLevel | `--zyl_NPjjlA7DRfVtsRJCX1kN4BxSr.cs` |
| Theme table | static method | `ZXMapTheme._0023_003Dz4k5FO_0024EclQhr()` → Dictionary | same generator file |
| Game system type | type | `_0023_003DzxRcpu6e7NYzT7tGWqPjpOkc_003D` (the game-level system class; 7915-line file) | `--zxRcpu6e7NYzT7tGWqPjpOkc-.cs` |
| System load | static method | `DXVision.DXSystem.Load<T>(bool)` — MakeGenericMethod(game system type) | used by handlers |
| Manager.CurrentGameSystem | instance prop | assignment target of DXSystem.Load result | handlers |
| SetLevel | instance method | `_0023_003DzmTU4kueQctVr(DXLevel)` on game system | handlers |
| Save writer | instance method | `_0023_003DzxjEnyeUCASiC(string path)` on ZXGameState (from `ZXFile<T>`); writes crypted zip entries Info+Data | `ZX/ZXFile.cs` |
| ZipSerializer | type+statics | `DXVision.Serialization.ZipSerializer`: statics `Read/ReadCrypted/Write/WriteCrypted`, static prop `Current`, instance prop `Password` | `ZX/ZXFile.cs`, TABSAT |
| Password flag | static method | (String)→Int32 — per TABSAT `getFlagMethod` scan | TABSAT TABReflector.cs |
| Password generator | static method | (String, Int32, Boolean) — per TABSAT; side effect sets `ZipSerializer.Current` + `.Password` | TABSAT TABReflector.cs |

## Construction sequence (phase `full`) — mirror the game's own survival-start handler

From `vendor/decompiled/--zLs-VX1eoAapVflmW_w--.cs` lines ~2760-2800 (survival Play) and CC handler
lines ~750-790. DO NOT deviate:

```
name   = --name
gs     = new ZXGameState(name)
ZXGameState.Set(gs)
gs.GameMode          = ZXGameModeType.Survival
p = new ZXRandomLevelParams()
p.NCells             = --ncells        // 256
p.Seed               = --seed          // 550040233
p.FactorGameDuration = --duration      // 1f
p.FactorZombiePopulation = --pop       // 1f
p.Name               = name
p.ThemeType          = ZXMapThemeType.None   // generator resolves theme deterministically from seed (CC behavior)
// DifficultyType: leave at default (None) — exactly what the CC handler does
// ChallengeType: leave Default. NEVER set CommunityChallenge. No leaderboard calls anywhere.
gs.SurvivalModeParams = p
ls = new ZXLevelState()
ZXLevelState.Set(ls)
ls._0023_003DzF1YHTRUZSNRa()                    // Init
mgr.CurrentGameSystem = DXSystem.Load<gamesystem>(false)
level = _0023_003DzEzgd90E_003D(p)              // generator; engine logs "Random Map Creation with seed: 550040233"
mgr.CurrentGameSystem._0023_003DzmTU4kueQctVr(level)   // SetLevel
```

## Save + check writing

```
target = Path.Combine(savesDir, name + ".zxsav")
// password for target path (TABSAT order):
flag = flagMethod(target)
generatorMethod(target, flag, true)             // sets ZipSerializer.Current.Password
gs._0023_003DzxjEnyeUCASiC(target)              // WriteCrypted Info+Data
sig = signingMethod(target, 2)
File.WriteAllText(Path.ChangeExtension(target, ".zxcheck"), sig)
// verify:
ZXFile read: ZipSerializer-based `_0023_003DzyuVTytDlXbMA(path)` equivalent → non-null ZXGameState
mgr save-list `_0023_003DzegKkTm3kHc6FhM_EOg_003D_003D()` contains an entry named `name`
```

Signer validation (phase `zombie` with `--validate-signer <zxsav>`): signature must equal the
content of the sibling `.zxcheck`. Use on `vendor/samples/COMMUNITY CHALLENGE.zxsav` —
expected `2.227699125761`. (A pure-file-hash reproduction was already DISPROVEN — the signer
mixes in the path; do not attempt local reimplementation.)

## Table loading (fallback if theme table empty at zombie readiness)

The engine's own startup logs `Tables Excel Read: Start/OK` and reads `ZXRules.dat`, `ZXStrings.dat`,
`ZXCampaign.dat` etc. from the TAB dir. If the zombie stalls before tables: grep
`vendor/decompiled` for the loader (methods referencing these .dat files / "Excel"), invoke it
via reflection, re-check theme table. Document what you find in `notes/reflection-notes.md`.
DXLog file is `<Documents>\My Games\They Are Billions\ZXLog.txt` — read its tail for diagnosis
(read-only!).

## Build verification (on this Mac, no MSBuild)

Preferred: create a minimal net48 console project (`src/Day0Gen.csproj`, `dotnet build` via
`nix shell nixpkgs#dotnet-sdk -c dotnet build -c Release`). Add
`Microsoft.NETFramework.ReferenceAssemblies` (PrivateAssets) so net48 compiles on macOS.
Compile-time deps: only System. Fallback if toolchain fails: ensure C#5-only syntax
(no `?.`, no `$""`, no `nameof`, no expression-bodied members, no tuples, no async) — the
operator compiles on-Windows via in-box csc.exe (Framework 4.8, `/langversion:5`).
Reference assemblies only — `/r` flags are resolved at runtime; the binary must run on target-host
from inside the TAB dir.

## Definition of done (worker scope)

- `src/Day0Gen.cs` + csproj compile clean (local `dotnet build` verified, or C#5 syntax audit if no toolchain).
- All phases implemented per spec; every reflection target wrapped in a helper that logs
  discovery strategy, invocation, and errors.
- `notes/reflection-notes.md` with: any name mismatches found while implementing, table-loader
  findings, anything uncertain.
- Do NOT ssh anywhere, do NOT deploy, do NOT touch vendor/, samples, or anything outside src/ + notes/.
