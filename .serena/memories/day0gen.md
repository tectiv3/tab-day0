# Day0Gen — TAB day-0 map replay (They Are Billions on target-host)

Regenerate a day-0 **survival** save for the weekly Community Challenge seed
(`550040233`) without touching the CC save/leaderboard. See `PLAN.md` (mechanics,
dead ends) and `SPEC.md` (implementation contract). Tool: `src/Day0Gen.cs`
(single-file C#5 / net48, reflection-only). Built exe: `src/bin/Release/net48/Day0Gen.exe`.

## Target box / paths (target-host, user)

- SSH: `ssh user@target-host` → PowerShell (not cmd). See `code/.serena/memories/target-host/system.md`.
- TAB install: `C:\Program Files (x86)\Steam\steamapps\common\They Are Billions`
- Saves / root: `%USERPROFILE%\Documents\My Games\They Are Billions\` (`Saves\`, `Account.zxuser`, `ZXLog.txt`)
- Deployed `Day0Gen.exe` (+ `.config`, `Day0Gen.log`) sits in the TAB dir. Local and
  deployed exe hashes matched as of 2026-09-28 (`c1ad4ea3600d879f63c470f0800f48659513b605cd0140949d59fafcf51b1de7`).
- Game build: `TheyAreBillions, Version=1.0.14.29`. No `DXVision.dll` on disk.

## KEY FINDING: DXVision is a separate, embedded (Eazfuscator) assembly

`TheyAreBillions.exe` references a distinct assembly `DXVision, Version=1.0.0.0` that is
**not present on disk**. It is embedded as a manifest resource and decrypted/loaded by an
Eazfuscator `AssemblyResolve` handler that is installed by the **module initializer**.

Consequence (the bug that stopped the last run): `Assembly.Load("TheyAreBillions")` followed
by `GetTypes()` throws `ReflectionTypeLoadException` — 1677 total types, **507 fail** (all
DXVision-derived). The manager class (`get_GameAccount` owner) is among the failures, so
`--phase discovery` aborts with "Manager class (get_GameAccount owner) not found".

Resolver source in decompile: `vendor/decompiled/--qfkZ-KkimwzG_5GjOFAkJH7Cbg6LrXJCCJODXq7ULSO4-.cs`
(`AppDomain.CurrentDomain.AssemblyResolve += …`, registered by `_0023_003DzX6exa18_003D()`).

### Fix (verified on target-host via PowerShell)

After loading the assembly, run its module constructor **before** `GetTypes()` and before
`Assembly.Load("DXVision")`:

```csharp
Assembly tab = Assembly.Load("TheyAreBillions");
RuntimeHelpers.RunModuleConstructor(tab.ManifestModule.ModuleHandle); // installs resolver
Assembly dx = Assembly.Load("DXVision");                              // now resolves
```

Verified result: all 1677 types load; `Assembly.Load("DXVision")` returns
`DXVision, Version=1.0.0.0`; `DXVision.DXLevel`, `DXVision.DXSystem`,
`DXVision.Serialization.ZipSerializer` all resolve. Side effects: installs the resolver and
runs Eazfuscator string init. It does **not** start the game.

Note: the current `LoadAssemblies()` fallback that sets `DxAssembly = TabAssembly` on
`FileNotFoundException` is wrong for finding `DXVision.*` types — those types are not in
`TheyAreBillions.exe`'s own metadata, so `TabAssembly.GetType("DXVision.DXLevel")` returns null.
The real DXVision assembly must be loaded (via the resolver) and used.

## Status / next steps

- DONE: `RunModuleConstructor` fix implemented, local `make build` + `make audit` clean, exe
  redeployed to the TAB dir (remote hash verified). Committed as `bc0b8f4`.
- DONE (P0): `--phase discovery` **completes** on target-host — all SPEC reflection targets found.
- DONE (P1): `--phase zombie --validate-signer "…\Saves\COMMUNITY CHALLENGE.zxsav"` **passes**.
  Manager singleton ready; `GameAccount` readable; theme table initially threw
  `ZXExceptionGameDataFilesCorrupted` but the engine table loader fixed it (6 themes on retry);
  password machinery verified (flag=2, derived 57-char password on an existing save); signer
  produced `2.227699125761` == on-disk `.zxcheck` → **Signer validation PASSED**. Engine
  `ZX.Program.Main` throws a NullReferenceException in this zombie mode (expected — popup path).
  Before/after SHA256 of the whole saves dir + root: **only `ZXLog.txt` changed**.
- P2 (`--phase full`) — **in progress**, three bugs found and fixed (see below). Not yet succeeded.
  Write target: `CC 550040233.zxsav` + `.zxcheck`. External before/after SHA256 confirmed only
  `ZXLog.txt` changed across every attempt so far; **no save/account/CC file was ever touched**.

## P2 — phase-full debugging arc (all fixed in code, live re-test pending)

1. `new ZXGameState(name)` aborted with `TargetException: Non-static method requires a target`.
   Cause: `GameReflector.Invoke` called `MethodBase.Invoke(target, args)` on a `ConstructorInfo`
   (the two-arg form is invalid for ctors). Fix: special-case `if (m is ConstructorInfo)` →
   `((ConstructorInfo)m).Invoke(args)`.
2. `Activator.CreateInstance(ZXLevelState)` then failed with an opaque `TargetInvocationException`.
   Added a logged `GameReflector.CreateInstance(purpose, type, args)` that unwraps/logs the inner
   exception + stack, `LogExceptionChain` in `Main`, and forced `Environment.Exit(code)` at the end
   (the zombie engine leaves foreground threads alive, so a normal return keeps the process/SSH open).
3. **Abrupt `EXITCODE=0` right after the engine table loader.** Cause: `ZombieInit` invoked
   `ZX.Program.Main` with OUR CLI args; the bootstrap treated that as a normal launch and handed
   off to Steam (`Process.Start(...)` + `Environment.Exit`) — observed live: Steam started ~1.5 s
   after the table loader, no save produced. **Ground truth: TABSAT passes `new string[] { "" }`**
   (its method is `initialiseBillionsAndStall`) — an empty arg makes the engine settle at the
   harmless error popup and STALL in-process. `SPEC.md`'s "TABSAT passes its own args" is WRONG.
   Fix applied: invoke `Main` with `new string[] { "" }`.
4. **Steam handoff happens regardless of the args passed to `Main`** — it propagates the *process*
   command line. Real bypass (verified live): set `SteamAppId=644930` / `SteamGameId=644930` /
   `SteamClientLaunch=1` before invoking `Main`. With those set the engine initializes in-process
   (construction was reached for the first time). The tool now sets them itself.
5. With the handoff bypassed, `new ZXLevelState()` still throws `NullReferenceException` at
   `#=zOHDY2QTzgRSB()` → `DXProject.Current` is null (the zombie engine's scene/project init never
   completed). Added `WaitForProjectContext()` (poll the `DXProject.Current` static **field** up to
   90 s, fail fast if the engine thread dies) before construction.
6. **Root cause of #5: a race in `CheckThemeTable()`.** It invoked the engine table loader while the
   engine's own `Main` was still running "Tables Excel Read" inside the manager ctor; the concurrent
   load corrupted shared static tables and the ctor NRE'd (`ZXLog`: `ZXGame Creation Failed`,
   `#=z$RSG5DA=` ← `.ctor()`). Fix: `CheckThemeTable()` now waits **passively** for the engine's own
   table load (poll, no loader call) and only invokes the loader as a last resort once
   `engineThreadDead`. After the fix the ctor completes and tables load via the engine.
7. Headless (SSH/session 0) **cannot** finish engine init: the engine throws
   `InvalidOperationException: Showing a modal dialog box ... not ... UserInteractive`. So `--phase
   full` must be run **interactively** (the user's desktop, via `run-day0-full.bat`). Also switched the
   engine invocation from `{ "" }` (TABSAT's args-error stall) to `new string[0]` (normal launch), so
   the engine reaches the main menu instead of parking at the args-error modal.

RESULT (interactive): the engine **fully initializes** - ZXLog shows `Steamworks IDapp=644930`,
`SteamAPI.Init OK`, `Steam Validation OK`, `Platform Init OK`, `Direct3D Creation Success`,
`Tables Excel Read: OK`; `DXProject.Current` becomes non-null (~24 polls); construction runs clean
(`new ZXLevelState()` OK, `Init()` OK, `DXSystem.Load` OK). Remaining failure: the **generator**
`#=zEzgd90E=(params)` NREs right after logging `Random Map Creation with seed: 550040233`
(generator source: `vendor/decompiled/--zyl_NPjjlA7DRfVtsRJCX1kN4BxSr.cs` line 20, ~line 84 uses the
theme table). Added generator stack-trace logging + effective-params logging; next run should
localize it.
8. **Generator NRE root cause (diagnosed from ZXLog interleaving, run 00:47):** the zombie engine
reaches the MAIN MENU (`ZXGame - ShowStartScreen`, live WinForms pump: `Window - ShowDialog` /
`DXGame - RenderFrame`). Our construction ran on the TOOL's thread; ZXLog shows the engine's own
`ZXGame - ChangeScene - Init/Paused/Fade` (reaction to `ZXGameState.Set` + `CurrentGameSystem`
assignment) interleaved BETWEEN the generator's `Random Map Creation with seed` log and our NRE
(131 ms apart). In the real game the whole construct->generate->save sequence runs ON the engine's
UI thread (inside a WinForms click handler). Fix (commit `68dcc3d`): marshal
`RunConstructGenerateSave()` onto the engine UI thread via `Control.Invoke` on the engine main form
(`Application.OpenForms` match on `MainWindowHandle`, fallback `Control.FromHandle`, fallback
old inline path with WARNING); 15-min watchdog `Environment.Exit(2)` protects against a
non-pumping engine loop (deadlock); extra ZXLog dump before generation brackets engine-side logs.
ASCII-only log strings (in-box csc codepage hazard). Deployed exe sha256 `b2fce975dd736b4b257730e7...
` (`b2fce975dd736b4b257730e72b6923a3a1cfe21823693cddad858e751c16267e`). Live re-test pending.

## Project memory / tooling

- SSH: `ssh user@target-host` (PowerShell) or `user@target-host` over tailnet when mDNS is down. scp is
  broken over it (PowerShell parses the `(x86)` path); transfer files with a base64 pipe +
  `[IO.File]::WriteAllBytes`.
- Running the engine in-process (`ZX.Program.Main`) must use `new string[] { "" }` (TABSAT parity),
  NOT real args — real args cause a Steam handoff + process exit. Interactive runs (a `.bat` on the
  user's desktop, `run-day0-full.bat`) behave differently from SSH session-0 runs.
- Local build: `make build` (nix dotnet-sdk out-link `/tmp/dotnet-sdk-result`); `make audit` for C#5.
