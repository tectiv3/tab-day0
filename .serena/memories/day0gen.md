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
   completed; in a headless SSH session the engine thread throws its own NRE). Added a
   `WaitForProjectContext()` (poll `DXProject.Current` up to 90 s) before construction. Whether the
   interactive engine ever completes scene init is the next live unknown.

Live re-test of `--phase full` pending (interactive, via `run-day0-full.bat`, run by the user).

## Project memory / tooling

- SSH: `ssh user@target-host` (PowerShell) or `user@target-host` over tailnet when mDNS is down. scp is
  broken over it (PowerShell parses the `(x86)` path); transfer files with a base64 pipe +
  `[IO.File]::WriteAllBytes`.
- Running the engine in-process (`ZX.Program.Main`) must use `new string[] { "" }` (TABSAT parity),
  NOT real args — real args cause a Steam handoff + process exit. Interactive runs (a `.bat` on the
  user's desktop, `run-day0-full.bat`) behave differently from SSH session-0 runs.
- Local build: `make build` (nix dotnet-sdk out-link `/tmp/dotnet-sdk-result`); `make audit` for C#5.
