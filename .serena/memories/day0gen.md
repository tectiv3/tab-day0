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
  `c45c552f…` redeployed to the TAB dir (remote hash verified).
- DONE: `--phase discovery` **completes** on target-host — all SPEC reflection targets found.
- Next: `--phase zombie` (engine init + account read + password probe; optionally
  `--validate-signer "…\Saves\COMMUNITY CHALLENGE.zxsav"` — read-only, expect `2.227699125761`),
  then `--phase full` (generate + write + verify the new save). Deploy/run/verify/commits are the
  main agent's job.

## Project memory / tooling

- SSH: `ssh user@target-host` (PowerShell). scp is broken over it (PowerShell parses the `(x86)` path);
  transfer binaries with a base64 pipe + `[IO.File]::WriteAllBytes`.
- Local build: `make build` (nix dotnet-sdk out-link `/tmp/dotnet-sdk-result`); `make audit` for C#5.
