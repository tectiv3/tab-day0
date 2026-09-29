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

## P2.5 — phase `genprobe` (commit `80e2303`, live run pending)

Diagnostic phase to localize the generator NRE empirically (DXWorldGrid/DXNoyseLayer/DXRandom
weighted choice live in the embedded, non-decompilable DXVision). Interactive like `full`:
RefuseIfGameRunning + zombie init + password probe + WaitForProjectContext + UI-thread marshal
(marshal logic extracted into `RunOnEngineUiThread`, shared with `full`), then an 8-step probe
sequence ON the engine UI thread, each step PASS/FAIL + exception chain, never aborting:
1 theme pick (`new DXRandom(seed)` + `ChooseValueWithWeights`), 2-4 `DXWorldGrid`
Create/GetSceneSquareArea/ScenePointFromWorldCell, 5 `DXNoyseLayer` op chain (ctor, FillWithNoyse
(100,.03,.03,200,300,2), Clone, Substract, MultiplyWith, SetContrast(2), TruncateTo01(.5,1),
GetAreaNearPoint(n/2,n/2,8,8,1), GetTotalValueOnArea via FromCenter rect else SKIP), 6 `ZXMapDrawer(256)`
LayerTerrain/LayerObjects/ExtraEntities null-check, 7 template chain (FromID -> EntityTemplates
IDictionary -> ContainsKey(3153977018683405164) -> indexer -> CreateInstance(null) -> Cell=(128,128)),
8 the generator itself, 3 attempts with fresh params, 5s/10s sleeps between.

- **FCE handler** (step 0): `AppDomain.CurrentDomain.FirstChanceException` registered on the main
  thread BEFORE engine start; logs NRE/KeyNotFound/IndexOutOfRange whose stack contains the
generator class name or DXNoyseLayer/DXWorldGrid/ZXMapDrawer, plus STACKLESS exceptions of those
types (marked) — the observed NRE is stackless in catch, FCE sees it at throw time. Capped 150
events, fully try/catch-guarded, unregistered after the sequence.
- **All DXVision signatures discovered at runtime + logged** (the log becomes the signature
catalog); args adapted via `ConvertArg` (numeric width, enums, Point/PointF, Rectangle/RectangleF).
  `ChooseValueWithWeights` handled in both believed shapes: `(IEnumerable<T>, Func<T,float>)`
  (weight fn = relaxed `Delegate.CreateDelegate` of `(object)->float` reading `PW`) and the
generator's own 1-arg `Dictionary<T,float>` form.
- **Read-only**: no DirSnapshot, no save writes; only Day0Gen.log (engine still appends ZXLog.txt —
known deviation). ZXLog tail (25 lines) dumped at the end, then `GENPROBE DONE`.
- csproj gained a `System.Drawing` reference (Point/Rectangle probes). `GameReflector.SafeGetTypes`
went private->internal (used by the ChooseValueWithWeights scan).
- **Run 08:55 results:** steps 2-7 ALL PASS (DXWorldGrid.Create/GetSceneSquareArea/ScenePointFromWorldCell;
  full DXNoyseLayer op chain incl. GetTotalValueOnArea; ZXMapDrawer layers; template chain incl.
  CreateInstance(null)+Cell). Generator still NREs 3/3; FCE stack = single generator frame,
  ~40-70ms in (mostly JIT). Step-1 failure was SPURIOUS: `ChooseValueWithWeights` is an
  INSTANCE method on `DXVision.DXRandom` (MemberRef), generic, 1 arg
  `Dictionary<ZXMapTheme,float>` — generator call:
  `val.ChooseValueWithWeights<ZXMapTheme>(source.ToDictionary(k=>table[k], k=>table[k].PW))`
  with key list BR,AL,TM,DS,FA,VO — so step 1's static/extension scan can never find it.
- **Step 1b added** (runs after step 1): exact replication — fresh `DXRandom(seed)`, key list
  BR,AL,TM,DS,FA,VO via `Enum.Parse`, reflected `Dictionary<ZXMapTheme,float>` (theme objects as
  keys, `PW` as values), `ChooseValueWithWeights` found on the DXRandom type with
  Instance|Static|Public|NonPublic flags (dictionary-like single param, `IsDictionaryLikeParameter`),
  `MakeGenericMethod(MapThemeType)`, invoked ON the DXRandom instance; logs theme + MapThemeType;
  failure logs full chain then rethrows.
- **FCE handler upgraded**: every filtered first-chance exception (stackless or not) now also logs
  `new StackTrace(ex, true)` frame-by-frame (`FCE frame: <type>.<method> IL=0x.. native=0x.. file=..`;
  GetILOffset -1 prints as 0xFFFFFFFF) plus `TargetSite` (+MetadataToken, DeclaringType), `Source`,
  `HResult` — all individually guarded (`LogFceDiagnostics`). Decisive diagnostic: at throw time the
  runtime has the frames even when `ex.StackTrace` is still empty. Build+audit clean; NOT yet deployed.
- Params for the generator attempts come from CLI opts (defaults = CC seed 550040233 / 256 cells /
factors 1.0) with Name fixed to "probe".
- **GENERATOR NRE ROOT CAUSE FOUND + FIXED (theme-table poisoning):** IL offset 0x923
  (`ldfld`/`callvirt ZXMapTheme::get_NumDoomVillages` -> `DXRange.get_First` on null) came from OUR OWN
  early theme-getter call. `ZXMapTheme.#=z4k5FO$EclQhr()` assigns its static
  `Dictionary<ZXMapThemeType,ZXMapTheme>` field (`_0023_003DzFIlawjZp0PUe`, real name `#=zFIlawjZp0PUe`)
  FIRST with constructor-empty theme objects, THEN populates their properties via
  `TableManagerDefinitions.AutoReadPropertiesInCols` (vendor/decompiled/ZX.GameSystems/ZXMapTheme.cs
  lines ~839-861). `CheckThemeTable` invoked that getter ~0.5s after `ZX.Program.Main` start, BEFORE
  the engine's "Tables Excel Read" - the population threw mid-load but left the static non-null =>
  every later getter call returned the poisoned half-built table => NumDoomVillages stayed null =>
  generator NRE. Fix in `src/Day0Gen.cs` (build+audit clean, NOT yet deployed):
  - `CheckThemeTable()` no longer invokes the theme getter AT ALL (also dropped the engine-dead
    table-loader last resort + `TryReadThemeTable`). It passively waits for engine readiness via the
    `DXProject.FromID(ProjectId)` gate (non-null only after the engine's OnLoad/table read), keeping
    the engine-death watch: headless `--phase zombie` (engine dies at its modal popup before project
    init) now skips quietly - nothing in that phase needs themes. "Theme table OK" logging moved to
    after the rebuild.
  - New `RebuildAndVerifyThemeTable()` runs ON THE MARSHALED ENGINE UI THREAD in BOTH `full` and
    `genprobe`, right before construction / probe step 1: (a) reflectively gets the static field
    `_0023_003DzFIlawjZp0PUe` (fallback: the unique static field of type
    `Dictionary<ZXMapThemeType,ZXMapTheme>`), logging its current value (null or count); (b) sets it to
    null; (c) invokes `#=z4k5FO$EclQhr()` once (engine ready now), logging the entry count - a throw
    logs the full chain and RETHROWS (abort, never proceed); (d) verifies EVERY entry, logging
    MapThemeType/Name/PW/NumDoomVillages/DoomVillagesSize/NumTreasures (ranges as "First..Last" via
    First/Last props) and throwing `Day0GenException` on any null theme, null NumDoomVillages or
    DoomVillagesSize, or fewer than 4 entries - this validation is the guard that the fix worked.
  - genprobe step1b weights dict fixed float->int: `ZXMapTheme.PW` is `public int PW`, so the
    generator's `source.ToDictionary(k=>table[k], k=>table[k].PW)` builds
    `Dictionary<ZXMapTheme,int>` (the earlier "Dictionary<T,float>" MemberRef reading was wrong);
    step1b now builds the int dict so the closed `ChooseValueWithWeights` parameter accepts it and
    the exact pick can run.

9. **SetLevel WaitOne deadlock (dispatch change, build+audit clean, NOT deployed):** the UI-thread marshal
   from #8 was added for a MISDIAGNOSIS. `SetLevel` (#=zmTU4kueQctVr in
   vendor/decompiled/--zxRcpu6e7NYzT7tGWqPjpOkc-.cs line 1685) does
   `if (flag) { DXGame.Current.InvokeOnStartFrame(action); _0023_003DzZbef....WaitOne(); }` - it queues a
   start-frame action and BLOCKS until the engine's frame loop executes it. With our construct/generate/save
   marshaled ONTO the engine WinForms UI thread, the pump was occupied by our delegate; the engine's pending
   scene change (triggered by ZXGameState.Set, needs the pump) + the WaitOne = MUTUAL DEADLOCK (observed:
   SetLevel hung at ~3% CPU until the watchdog killed the run). The generator NRE that motivated the marshal
   was actually the poisoned theme table (see #8's root-cause fix; generator then succeeded: `INVOKE OK
   generator(params) -> DXLevel` in 215ms), and the engine demonstrably has an INDEPENDENT frame/render
   thread (ZXLog logs RenderFrame while our thread executes) - so running on OUR OWN thread lets
   InvokeOnStartFrame actions execute and WaitOne signal, matching TABSAT's arrangement. Fix in
   `src/Day0Gen.cs`: new `DispatchSequence()` (used by both `full` and `genprobe`) runs the sequence
   (RebuildAndVerifyThemeTable + RunConstructGenerateSave / GenProbeSequence) DIRECTLY on the main tool
   thread, logging `dispatch: main thread (engine threads pump freely)`; engine-UI info
   (MainWindowHandle/OpenForms) still logged as reference only, marked "not used for dispatch". The old
   `RunOnEngineUiThread`/`FindEngineUiMarshalTarget` machinery is KEPT but only reachable via the new
   valueless CLI flag `--ui-marshal` (logs `dispatch: UI-marshal (legacy, may deadlock in SetLevel)`) for
   A/B testing. Watchdog kept, same 15-min `Environment.Exit(2)`, now in `RunWithWatchdog` around the
   direct execution, stall note names the SetLevel InvokeOnStartFrame/WaitOne mode. Live re-test pending.

10. **SetLevel scene-object NRE is RECOVERABLE (tolerated in code, build+audit clean, NOT deployed/re-tested):**
    DIAGNOSED from the 17:26 run ZXLog: SetLevel NREs out of per-entity scene-object creation
    (the callvirt after log id 1452589205, source vendor/decompiled/--zxRcpu6e7NYzT7tGWqPjpOkc-.cs
    line ~1849, `level.CreateSceneObject()`): ZX.Components.CTerrainResource fails ~3x (each
    caught+logged by the engine) and one NRE escapes at DXLevel.cs:438 catch handling. CRUCIALLY
    the engine RECOVERS and COMPLETES level setup: ZXLog then shows "ZXSystem_GameLevel -
    GetMiniMapImage - End", "ZXSystem_GameLevel - LoadLevel - Minimap OK", "ZXGame - ChangeScene -
    pre-Invoke/Invoke", "ZXGame - Fade - End". Scene objects are render-layer only and NOT
    serialized into saves (rebuilt from LevelEntities on load), so the save can still be valid.
    New flow in RunConstructGenerateSave (phase full):
    - Records the ZXLog.txt byte length BEFORE the SetLevel invoke (ZxLogLengthBeforeSetLevel;
      unreadable -> 0 = whole-file scan).
    - SetLevel invoke wrapped: a Day0GenException whose INNERMOST exception is a
      NullReferenceException -> log full chain + "SetLevel threw (expected: recoverable
      scene-object failure); verifying engine-side completion..." then CONTINUE to
      verification. Any other exception type aborts as before.
    - Post-SetLevel verification, required on BOTH the swallowed and the clean path, before
      the C1 SaveState wrapper call: (a) VerifySetLevelEngineCompletion - poll up to 90s @1s
      for "LoadLevel - Minimap OK" in ONLY the ZXLog bytes beyond the recorded offset
      (ReadZxLogPortion: FileShare.ReadWrite open + seek, ASCII marker so UTF-8 decode is
      safe); logs which poll; timeout or engine-thread-death -> DumpZxLogPortion + abort.
      (b) VerifyCurrentLevelField - ReferenceEquals(field, generatedLevel) must be true for the
      game-system current-level instance field `#=zS4pP$s0UqLYY` (ILSpy-escaped
      `_0023_003DzS4pP_0024s0UqLYY`, declared line 1273, ASSIGNED line 1812 - BEFORE the
      scene-object creation, so the check stays meaningful even after the NRE). Locator:
      exact name via FindFieldUp, fallback the ONLY DXLevel-typed instance field on the game
      system type (base chain included); 0 or >1 candidates abort, no guessing.
11. **SaveState wrapper silently skipped (17:35 run): start-screen teardown clears ZXGameState.Current.**
    DIAGNOSED: the wrapper invoke hit its `ZXGameState.Current == null -> Thread.Sleep(2000); return;`
    branch (invoke 06.451, abort 08.454 = exactly 2s; no writer exception in ZXLog). After SetLevel
    completed (Minimap OK) the engine's scene machine faded back to the START SCREEN
    ("ZXSystem_StartScreen - ShowScene/ShowSceneSuccess") and the teardown CLEARED
    ZXGameState.Current (and possibly ZXLevelState.Current / manager.CurrentGameSystem).
    Fix in `src/Day0Gen.cs` (build+audit clean, NOT deployed): new `ReAssertStateBeforeSave(gs, ls, sys)`
    in RunConstructGenerateSave, between the SetLevel verification block and the SaveState wrapper
    invoke (after the DumpZxLogTail): reads ZXGameState.Current / ZXLevelState.Current via the
    existing cached static getters (`GameStateCurrentMethod` / `LevelStateCurrentMethod`, exact-name
    `_0023_003Dzuartwoo_003D`) and `manager.CurrentGameSystem` (CurrentGameSystemProp); any null-or-not-
    ReferenceEquals(ours) value is re-Set via `GameStateSetMethod` / `LevelStateSetMethod` /
    CurrentGameSystemProp with before/after logging + recheck; a re-assert that does not stick aborts
    before the save. Compact summary log lines say which of the three needed re-assertion. Live re-test
    pending.
    - FCE handler stays registered for phase full (it already logs this NRE cleanly).
12. **PreSave NRE root cause = skipped level adoption (17:39 run):** SaveState's PreSave
    `ZXLevelState.#=zQXHqcVh9mGZZ()` NRE'd because the SetLevel scene-object NRE (item 10)
    escaped BEFORE the new-level branch's NEXT statement (~line 1851 of
    `vendor/decompiled/--zxRcpu6e7NYzT7tGWqPjpOkc-.cs`):
    `ZXLevelState.Current.#=zf9PbDap0F6OC(level)` - the level ADOPTION into ZXLevelState.
    Source (`vendor/decompiled/ZX/ZXLevelState.cs` 1597-1621) is the game's own day-0
    start-state setup: `IDCurrentMission = level.ID;` then
    `if (!DXLevel.Current.IsInProject) CurrentGeneratedLevel = DXLevel.Current;` (the very
    state PreSave dereferences), `LevelEntities = null;` (rebuilt by PreSave), LayerFog/
    LayerActivity, `DXGame.Current.SetGameTime(0.0)`, `Gold += 100; Wood += 20;` starting
    resources. Fix in `src/Day0Gen.cs` (build+audit clean, NOT deployed): new step
    "adopt level into ZXLevelState (SetLevel continuation)" in RunConstructGenerateSave
    after the SetLevel verification block, BEFORE ReAssertStateBeforeSave
    (`AdoptLevelIntoLevelState(ls, level)`):
    - Precondition: static `DXLevel.Current` (adoption dereferences it for the IsInProject
      gate + CurrentGeneratedLevel source) must ReferenceEquals our level; discovered at
      use-site as settable static property or writable static field (`FindDxLevelCurrentProp`/
      `FindDxLevelCurrentField`), abort if neither settable; set with before/after + recheck.
    - Invoke `_0023_003Dzf9PbDap0F6OC(DXLevel)` (public instance on ZXLevelState,
      exact-name-discovered in DiscoverAll as `AdoptLevelMethod`) on OUR ls, guarded by
      SetLevel's own branch condition (IDCurrentMission != level.ID) so a clean path never
      double-applies the +100/+20; on invoke throw: full chain (`ADOPT LEVEL`) + abort
      (phase-full FCE captures the IL offset).
    - Verify: IDCurrentMission == level.ID (ulong compare, both logged);
      CurrentGeneratedLevel ReferenceEquals level; LevelEntities null; log Gold/Wood
      (expect +100/+20 on top of ctor defaults - log only, NOT asserted). Abort on any
      check failure. Live re-test pending.

## Project memory / tooling

- SSH: `ssh user@target-host` (PowerShell) or `user@target-host` over tailnet when mDNS is down. scp is
  broken over it (PowerShell parses the `(x86)` path); transfer files with a base64 pipe +
  `[IO.File]::WriteAllBytes`.
- Running the engine in-process (`ZX.Program.Main`) must use `new string[] { "" }` (TABSAT parity),
  NOT real args — real args cause a Steam handoff + process exit. Interactive runs (a `.bat` on the
  user's desktop, `run-day0-full.bat`) behave differently from SSH session-0 runs.
- Local build: `make build` (nix dotnet-sdk out-link `/tmp/dotnet-sdk-result`); `make audit` for C#5.

## Code-review fixes (notes/code-review-1.md, commit after 384f13c) — build+audit clean, NOT deployed

All blocking + recommended findings from the adversarial review implemented in `src/Day0Gen.cs`:

- **C1 (critical)**: the save now goes through the game's own SaveState wrapper
  `_0023_003DzSV0_oCta8rEv(name, callback, showWindow, preSave)` (instance on the manager,
  discovered by exact name + (String, Action, Boolean, Boolean) signature), invoked with
  `(opts.Name, null, false, true)` on managerInstance → FixFileName + engine pause + ZXLevelState
  PreSave + native writer + unpause, byte-for-byte game-native. The wrapper SWALLOWS writer
  exceptions (DXLog + error dialog + return), so post-invoke File.Exists checks on target+zxcheck
  are mandatory and abort. Low-level writer/manual composition kept only as drift fallback with
  M1 null-guards (abort on any null MethodBase/manager, no NREs).
- **H1**: after-run allow-list compares FULL absolute paths (case-insensitive): target, checkPath,
  ZXLog.txt in saves root only. No filename matching.
- **H2**: DirSnapshot enumeration failure now throws (no partial snapshots); after a full run the
  diff MUST contain both new artifacts (empty diff or missing artifact → abort).
- **H3**: Account.zxuser existence gate moved to the very top of ZombieInit (before the engine
  thread starts) — the zombie manager ctor auto-creates it otherwise.
- **H4**: password flag/set/clear discovered by exact name only (blind probe + unprobed clearer
  fallback deleted along with ProbePasswordCandidate); TryPasswordDerivation now requires the full
  cycle: set → ZipSerializer.Password non-empty, clear → password empty again, else abort.
- **H5**: new ReVerifyThemeTableQuick() (reads the static theme-table FIELD directly; non-null,
  >=4 entries, every NumDoomVillages non-null) called immediately before the generator invoke in
  BOTH full and genprobe (step 8) — closes the re-poisoning window after the rebuild.
- **H6**: both watchdogs abort via WatchdogAbort(): Console.Error + File.AppendAllText to
  Day0Gen-watchdog.log (never Log.Write / the shared gate lock), then Environment.Exit(2).
- **M4**: watchdog completion flags now volatile (VolatileBool holder; C#5 locals can't be volatile).
- **M3 partial**: phase-full before/after snapshots now include the TAB install dir, excluding our
  own artifacts (Day0Gen.exe/.exe.config/.pdb/.log/-watchdog.log, run-day0-*.bat) — any other
  engine write there aborts the run. Steam userdata subtree still NOT covered.
