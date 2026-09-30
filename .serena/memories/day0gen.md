# Day0Gen — TAB day-0 map replay (They Are Billions)

Regenerate a day-0 **survival** save for the weekly Community Challenge seed
(`550040233`) without touching the CC save/leaderboard. See `PLAN.md` (mechanics,
dead ends) and `SPEC.md` (implementation contract). Tool: `src/Day0Gen.cs`
(single-file C#5 / net48, reflection-only). Built exe: `src/bin/Release/net48/Day0Gen.exe`.

## Target box / paths

- Remote shell is PowerShell (not cmd).
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

### Fix (verified via PowerShell on the target host)

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
- DONE (P0): `--phase discovery` **completes** on the target host — all SPEC reflection targets found.
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
13. **Adopt NRE root cause = missing fog system (17:53 run):** the adoption
    `#=zf9PbDap0F6OC` NRE'd 3ms in at `LayerFog = DXSystem.Get<fogsys>().#=zwJQaMvq$T3G4`
    (ZXLevelState.cs ~1606) because `DXSystem.Get<fogsys>()` returned NULL - the fog
    system does not exist. What creates it in the real game: the game system's OnLoad
    (vendor/decompiled/--zxRcpu6e7NYzT7tGWqPjpOkc-.cs line 1626): `base.OnLoad();
    Enabled=false; DXSystem.Dispose<menu-system>(); DXSystem.Dispose<fogsys>();
    DXSystem.Load<fogsys>(true); DXSystem.Get<fogsys>().Enabled=false;` plus dispose/load
    of two more systems. We created the game system via `DXSystem.Load<gamesystem>(false)`
    (deferred, like the real survival click handler), but the engine never ran OnLoad for
    our out-of-band instance - deferred Load(false) means OnLoad is ENGINE-triggered in
    the real game. Note OnLoad also DISPOSES the menu UI system - that is the game's own
    enter-game transition; acceptable: our process exits right after saving. Fix in
    `src/Day0Gen.cs` (build+audit clean, NOT deployed): `AdoptLevelIntoLevelState` now
    takes the game system instance; after the DXLevel.Current precondition and before
    the adopt invoke it (b) invokes OnLoad on the game system (exact name "OnLoad",
    instance, public, 0 args, DeclaredOnly on the game system type - discovered as
    `GameSystemOnLoadMethod`) with the full chain logged on failure, then verifies
    `DXSystem.Get<fogsys>()` non-null (Get = DXSystem static generic 0-arg,
    `DxSystemGetMethod`; fog type = TabAssembly global type
    `_0023_003DzJme8KFhmikprnkg_2CDeiQE_003D`, fallback: unique loaded type whose full
    name contains "zJme8KFhmikprnkg"); null aborts (adopt would NRE). FCE stack filter
    extended with "ZXLevelState" / "zJme8KFhmikprnkg" / "DXSystem" so any further NRE in
    this chain logs its IL offset. Live re-test pending.

## Project memory / tooling

- Access is over SSH to a PowerShell shell. `scp` breaks on the `(x86)` install path; transfer
  files with a base64 pipe + `[IO.File]::WriteAllBytes`.
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

## Code-review fixes (notes/code-review-2.md) — deployed, discovery VERIFIED on the target host

Blocking C1/C2/C3 from the second adversarial review implemented in `src/Day0Gen.cs`.
Deployed exe sha256 `76453345F14A4C4D565A1EB0E930737E24BD199A616184B7BC11475732D9E1BC`
(local `make build` == remote; `--phase full` re-test still PENDING):

- **C1 (fog name decode)**: the escaped constant `_0023_003DzJme8KFhmikprnkg_2CDeiQE_003D`
  carries a LITERAL `_2CDe` run (ILSpy escapes non-identifier chars with UPPERCASE hex only), so
  `Unescape` produced `#=zJme8KFhmikprnkg<U+2CDE>iQE=` and could never hit. New raw constant
  `N_FOG_SYSTEM_TYPE_RAW = "#=zJme8KFhmikprnkg_2CDeiQE="`; resolution now tries raw literal first,
  then `Unescape(...)`, against BOTH `TabAssembly` and `DxAssembly`, logging every attempt
  (`FOG NAME TRY: ...`). Deployed abort was `src/Day0Gen.cs:810`.
- **C2 (marker fallback)**: `ResolveFogSystemByMarker()` scans BOTH assemblies unconditionally,
  de-dups by object reference, filters to `!IsNested` and no `<>`/`<` in `FullName`, then keeps the
  type whose base chain contains `#=zsW2J3r72Cu83` (`N_SYSTEM_BASE_TYPE`) or `DXVision.DXSystem`
  (name-matched, so it runs before `DxSystemType` discovery). Exactly one → use; zero or >1 →
  abort AFTER logging `FOG MARKER SCAN` counts + every `FOG MARKER CANDIDATE`
  (FullName/IsNested/base-chain). `DxAssembly` scan is no longer short-circuited by Tab hits.
- **C3 (gate OnLoad)**: `AdoptLevelIntoLevelState` now fetches `DXSystem.Get<fogsys>()` FIRST; runs
  the game system's `OnLoad` ONLY when it is null (logs `fog system already present -> skipped
  OnLoad` / `fog system missing -> running game-system OnLoad`), re-fetches, and aborts with the
  existing message if still null. WHY comment added: OnLoad disposes the live survival/CC menu
  system + other singletons, so it must run on demand only. Adopt guard and all other logic unchanged.

VERIFIED on the target host (2026-09-29 19:39, `--phase discovery`, exit 0):
`FOG NAME TRY: TheyAreBillions.GetType("#=zJme8KFhmikprnkg_2CDeiQE=") -> #=zJme8KFhmikprnkg_2CDeiQE=`
followed by `FOUND [exact-name] fog system type` and `PHASE discovery COMPLETE`. This confirms the
C1 decode (the `_2CDe` is literal; the type IS in TheyAreBillions/TabAssembly) and that the old
abort at `src/Day0Gen.cs:810` is gone. C2 fallback did not need to run (exact hit).

## Instrumentation pass: read-back reader + CC fail-closed + crash-save allow-list (build+audit clean, NOT committed/deployed)

`src/Day0Gen.cs` only; no behavior fix (no early `DXLevel.Current`, no manual entity registration).
Root cause localized: `ZXLevelState.PreSave` (ZXLevelState.cs:1633-1657) CLEARS the generated level's
`Entities` + CSalvable ExtraEntities and rebuilds `LevelEntities` from the LIVE
`DXGame.Current.ComponentsOfType<CSalvable>()`; if the CC is absent there it is silently dropped.

- **Reader fix** (`ReadBackState`, ~4773): prefer `refl.ZipReadMethod` with entry `"Data"` (the game's
  own load path) as PRIMARY; `ZXFile<ZXGameState>.read` only if Zip read is null/throws. Logs which
  reader produced the state. Password flag/set/clear cycle unchanged. Prior reader returned null.
- **Fail-closed CC assertion** (`AssertReadBackHasCommandCenter`, ~4541; call ~4065): after read-back
  Name check, navigates `ZXGameState.LevelState` -> `ZXLevelState.LevelEntities` (IDictionary), counts
  and detects `ZX.Entities.CommandCenter` (`IsCommandCenter`, ~4663; type-by-name with base-chain name
  fallback). Logs `Read-back LevelEntities count=N, commandCenterPresent=<bool>`; throws `Day0GenException`
  ("saved LevelEntities has no CommandCenter (count=..., CC found=false) - the save would load without a
  command center") when LevelState null / entities null/empty / no CC.
- **Pre-save diagnostics** (`LogCommandCenterDiagnostics`, ~4575; call ~3992 after `ReAssertStateBeforeSave`):
  all probes try/catch, never abort, `(skipped: ...)` on failure. (a) `DXVision.DXGame.Current` +
  `ComponentsOfType<CSalvable>()` -> `CC DIAG: live DXGame CSalvable components = N, CommandCenter present = <bool>`;
  (b) generated `level.Entities` + `level.Extension.MapDrawer.ExtraEntities` -> `CC DIAG: generated level
  Entities=..., ExtraEntities=...`; (c) `ZXLevelState.Current.LevelEntities` null/non-null.
- **Allow-list tolerance** (~3826): derived `<stem>_Crash.zxsav`/`.zxcheck` next to target are allowed
  via `CHANGE (engine crash save, allowed): <path>`; `targetSeen`/`checkSeen` still required.

UNVERIFIED: which reader actually returns non-null on the target; whether the live registry contains the
CC (the diagnostics answer this on the next run); whether `ComponentsOfType<T>()` is on `DXGame`/base vs an
extension method (probe logs skipped if not found).

## Entity-default-params readiness gate (build+audit clean, NOT committed/deployed)

Race fix for the generator `KeyNotFoundException` (`ZXEntity.#=zthu8vuk=()` ->
`DXEntity.CreateInstance()`): the manager's `Dictionary<string, ZXEntityDefaultParams>` field
`#=zpPzF4$N2I2orD2rOLw==` is populated inside `#=zCr_j9C0uHkPXZ$SDGA==` (a
`PostMethods_OnStartFrame` action) which runs AFTER `CurrentProject = DXProject.LoadFromFile(...)`.
`WaitForProjectContext` (FromID non-null) passes at the LoadFromFile boundary, so the generator
could run while the dict was still filling.

New `WaitForEntityDefaultParamsGate()` (`src/Day0Gen.cs` ~1958), called in
`RunConstructGenerateSave` right after `ReVerifyThemeTableQuick()` and before the generator
invoke:
- **Dict field discovery**: unique INSTANCE field on `refl.ManagerType` (base chain walked) whose
  field type is generic `Dictionary<,>` with a value type whose name ends with
  `ZXEntityDefaultParams` (or equals the discovered `ZX.ZXEntityDefaultParams` type; exact name
  tried first, then `ZX.EntityDefaultParams`, then simple-name-suffix scan over both assemblies).
  No hardcoded escaped field name.
- **Expected count**: computed once from `DXProject.EntityTemplates` (`FindPropertyUp`/`FindFieldUp`,
  `FromID(ProjectId)` primary, `Current` fallback) = number of template values whose `Entity`
  property is a `ZX.Entities.ZXEntity` (`IsInstanceOfType`; exact name then suffix scan).
- **Poll** up to 120 s @500 ms until `dict.Count >= expected` (and, when the CC template name at
  `EntityTemplates[GenProbeCommandCenterTemplateId]` resolves, `dict.Contains(ccName)`). Every dict
  read is try/catch wrapped; progress logged every ~5 s as
  `ENTITY PARAMS GATE: dict count=N / expected=M (poll k)`. Timeout/engine-death -> `Day0GenException`.
- **Fail-open only on discovery failure**: logs
  `ENTITY PARAMS GATE: skipped (<reason>) - cannot verify manager entity-default-params readiness`
  and returns (the generator still fails closed on its own).

UNVERIFIED: live readiness (dict count vs expected, CC key present) and whether a real run now wins
the race; expected-count computation assumes `EntityTemplates` values expose an `Entity` property.

## Start-screen settle gate (build+audit clean, NOT committed/deployed)

Root cause of the wiped level (latest interactive ZXLog): the engine's STARTUP
fade-to-start-screen transition completes AFTER `SetLevel`. Its `onFinish` runs
`ChangeScene -> ZXSystem_StartScreen - Load/ShowScene`, whose teardown tears down the game
level and wipes every entity before the save. Order in ZXLog: `ZXGame - ShowStartScreen` /
`ChangeScene - Init/Paused/Fade - True With onFinish` ... our generator + `SetLevel`
(`GameLevel - LoadLevel - Begin ... Minimap OK`) ... `ZXGame - Fade - onFinish` /
`ChangeScene - loadScene -> ZXSystem_StartScreen - Load -> ShowScene -> ShowSceneSuccess`.

New `WaitForStartScreenSettled(savesDir)` in `src/Day0Gen.cs`, called in
`RunConstructGenerateSave` immediately AFTER `WaitForEntityDefaultParamsGate()` and BEFORE the
generator invoke: polls ZXLog.txt (WHOLE-FILE scan via `ReadZxLogPortion(savesDir, 0)` - the
transition may already have completed during earlier readiness waits; the engine truncates the
log on start so whole-file is correct) every 500 ms up to 90 s for the ASCII marker
`ZXSystem_StartScreen - ShowSceneSuccess`. Logs `STARTSCREEN GATE: waiting for
'<marker>' (poll k)` every ~10 polls, and `STARTSCREEN GATE: engine settled on the start screen
after k poll(s).` on success. FAIL-OPEN: timeout / unreadable ZXLog logs
`STARTSCREEN GATE: marker not seen after 90s (engine may still be transitioning); proceeding -
the save CC assertion will catch an empty level` and returns; `engineThreadDead` likewise
returns. The downstream `AssertReadBackHasCommandCenter` is the fail-closed backstop. No other
logic changed; ASCII-only log strings; no commit/deploy.

UNVERIFIED: whether waiting removes the teardown race on a live run (the read-back CC assertion
will prove it).

## Fix: reorder construction until AFTER the start-screen settle gate (build+audit clean, NOT committed/deployed)

`src/Day0Gen.cs`, `RunConstructGenerateSave` only. Root cause (see `notes/real-survival-path.md`):
the construct->generate->SetLevel block mirrored the real survival/CC handler but ran during engine
startup, before the start screen settled. The engine's startup `ZXSystem_StartScreen` teardown then
nulls `ZXGameState.Current`/`ZXLevelState.Current`; `gamesystem.SetLevel(level)` subsequently NREs in
`ZXLevelState.Set` (`ZXLevelState.cs:1167`, `ZXGameState.Current.LevelState = <new>`).

Change: moved the whole construction block (new `ZXGameState` + `Set`, `GameMode=Survival`,
`ZXRandomLevelParams` + all `SetProp`s, `gs.SurvivalModeParams`, `new ZXLevelState` + `Set` + `Init`,
`DXSystem.Load<gamesystem>(false)`, `manager.CurrentGameSystem=sys`, effective-params read-back log)
to immediately AFTER `WaitForStartScreenSettled(effectiveSavesDir)` and before the generator invoke.
`LogProjectDiagnostics`, `DumpZxLogTail`, `ReVerifyThemeTableQuick`, `WaitForEntityDefaultParamsGate`
and `WaitForStartScreenSettled` stay in place ahead of it. New order: diagnostics / theme re-verify /
entity-params gate / start-screen gate -> construction -> generate -> SetLevel. Added a WHY comment at
the block referencing `notes/real-survival-path.md`. Internal order, names (`gs`/`p`/`ls`/`sys`) and
log strings unchanged; `make build` and `make audit` clean. Live re-test pending.

## Strategy change: drive construct/generate/save through the engine loading dialog (build+audit clean, NOT deployed)

Per `notes/code-review-3.md` C1/E + `notes/loading-dialog-contract.md`, the out-of-band main-thread
sequence is gone. `RunConstructGenerateSave` now keeps the readiness gates
(`LogProjectDiagnostics`, `DumpZxLogTail`, `ReVerifyThemeTableQuick`, `WaitForEntityDefaultParamsGate`,
`WaitForStartScreenSettled`) BEFORE the dialog, then invokes the manager loading dialog
`#=z9DPDdq9qP9lZ(int delay, Action create, Action saveAfter, List<string> messages, bool showLoading)`
(`--z4RevDP3eECXqXS6JRA--.cs:2914`) with `(20, create, saveAfter, new List<string>(), true)`.

- **Discovery**: exact escaped name `N_LOADING_DIALOG` + a strict 5-arg signature check
  `IsLoadingDialogSignature`; on name miss/mismatch a signature scan over `ManagerType`; null aborts
  at the invoke site (`InvokeManagerLoadingDialog`).
- **create** = `CreateGameStateAndLevel()` (Task thread): construct ZXGameState/params/ZXLevelState
  + `DXSystem.Load<gamesystem>(false)` + `manager.CurrentGameSystem=sys` + generator + `SetLevel
  (with the existing NRE tolerance) + `VerifySetLevelEngineCompletion` + `VerifyCurrentLevelField` +
  `AdoptLevelIntoLevelState`. Results in static `dialogGs/dialogParams/dialogLs/dialogSys/dialogLevel`;
  on throw it logs the chain, stores `dialogCreateException` and RETHROWS so the dialog catches it and
  never runs saveAfter (fail closed); `finally` sets `dialogCreateDone`.
- **saveAfter** = `SaveGeneratedState()` (engine finish frame): `ReAssertStateBeforeSave` +
  `LogCommandCenterDiagnostics` + SaveState-wrapper invoke + the mandatory existence checks; stores
  `dialogSaveException` and sets `dialogSaveDone` in `finally` (never throws into the frame loop).
- The dialog is invoked on the engine UI thread via the existing `RunOnEngineUiThread`/`
  FindEngineUiMarshalTarget` machinery (the synchronous part touches `DXGame.Current.Scene`);
  `showLoading=true` keeps create on a Task so `SetLevel`'s `InvokeOnStartFrame`+`WaitOne` still
  signals. Main thread then `WaitOne(600000)` on `dialogSaveDone` (15-min `RunWithWatchdog` is the
  outer backstop) and only then runs zxcheck / `ReadBackState` / `AssertReadBackHasCommandCenter` /
  save-list on the main thread.
- **C2**: `ReAssertStateBeforeSave` now also re-asserts `DXLevel.Current == dialogLevel` via
  `FindDxLevelCurrentProp`/`FindDxLevelCurrentField` and aborts if it will not stick (PreSave
  `ZXLevelState.cs:1630` early-returns on a null `DXLevel.Current`).
- **C4**: post-save verification failure (and the saveAfter-failed path) calls
  `CleanupWrittenArtifacts(target, checkPath)` to delete the written target, zxcheck and the
  `<stem>_Crash.zxsav/.zxcheck` pair before throwing; the success path never deletes.

UNVERIFIED: no live run. Whether the dialog is callable from a foreign thread without racing the
scene machine, whether the finish-frame handoff actually fires `saveAfter` in this process, and
whether `DXLevel.Current`/fog survive to `PreSave` are all DXVision-dependent and need a live
`--phase full` re-test. `make build` (0 warnings) + `make audit` clean.

## Envelope fix: replace the animation-gated loading dialog with a direct start-game envelope (build+audit clean, NOT committed/deployed)

`src/Day0Gen.cs` only. The live run invoked the manager loading dialog `#=z9DPDdq9qP9lZ` but its
dispatch is gated on an overlay fade animation (`#=zXAMaTPI_`, `--z4RevDP3eECXqXS6JRA--.cs:3191`):
it runs `create` only on the animation's `OnFinished` and drops the delegate when `DXGame.Scene ==
null`. After a `ChangeScene` the callback never fired, so `create` never started and the tool idled
to the 600 s timeout. The dialog is no longer invoked.

New `InvokeStartGameEnvelope(Action create, Action saveAfter)` replicates the dialog's
non-animation lifecycle:
1. `RunOnEngineUiThread`: `DXGame.Current.Paused = true`; if `CurrentGameSystem != null` then
   `Enabled = false`, `Dispose()`, `CurrentGameSystem = null`; `IsLoading = true` (optional).
2. `Task.Factory.StartNew`: `create()` then `DXGame.Current.InvokeOnStartFrame(saveAfter)` - the
   same engine frame queue `SetLevel` already relies on. On throw it stores `dialogCreateException`
   and sets `dialogSaveDone` (fail closed).
3. Main thread still `WaitOne(DialogCompletionTimeoutMs)` on `dialogSaveDone`; existing
   `dialogCreateException`/`dialogSaveException`, post-save verification and `CleanupWrittenArtifacts`
   unchanged. If `InvokeOnStartFrame` never runs, `saveAfter` never fires, the 600 s wait times out
   and the run aborts (the 15-min `RunWithWatchdog` is the outer backstop).

Discovery is runtime-only via stable engine names (no escaped names): `FindTypeAnyOrder` for
`DXVision.DXGame`; `FindPropertyUp`/`FindFieldUp` for static `Current`, writable bool `Paused`, and
manager `IsLoading`; a base-chain scan for the instance `InvokeOnStartFrame(Action)`; the existing
`refl.CurrentGameSystemProp` for `CurrentGameSystem`. Any required miss (`DXGame.Current`, `Paused`,
`InvokeOnStartFrame`, `CurrentGameSystem`) aborts with a `Day0GenException` naming it - never an
inline fallback. `LoadingDialogMethod` discovery is kept but nothing calls the dialog.

UNVERIFIED: no live run. Whether `DXGame.Current` is non-null at envelope time, whether the engine
start-frame queue actually runs the queued `saveAfter`, and whether the pause/dispose envelope
avoids the previous crash are all DXVision-dependent and need a live `--phase full` re-test.
`make build` (0 warnings) + `make audit` clean.

## P2 fix (2026-09-29): envelope save runs directly on the create Task

Live run proved `InvokeStartGameEnvelope`'s create path works (`SetLevel` + adopt verified,
`LoadLevel - End`, `CREATE: ... complete`), but the save was queued via
`DXGame.Current.InvokeOnStartFrame(saveAfterAction)` from the Task thread and **never ran**: the
envelope pauses the engine (`Paused = true`), so the start-frame queue is not drained (CPU near-idle,
black screen), and the tool idled to the 600 s timeout. Fix in `src/Day0Gen.cs`:
`Task.Factory.StartNew` now calls `saveAfterAction()` **directly on the same Task right after
`createAction()`** - no frame-queue dependency. The engine is already paused, so `SaveGeneratedState`
(`ReAssertStateBeforeSave` + `LogCommandCenterDiagnostics` + the game's SaveState wrapper) is safe
there, matching how pre-envelope runs produced valid saves off-frame. Removed the
`InvokeOnStartFrame` discovery block, its required-miss abort, the `invokeOnStartFrame` local, the
`gameNow == null` re-fetch/guard, and the now-inaccurate references/log strings/comments in this
path (`InvokeOnStartFrame` references in the `SetLevel` comments are unchanged). Required-member
aborts that remain: `DXVision.DXGame`, `DXGame.Current`, `DXGame.Paused`, manager `CurrentGameSystem`,
and `managerInstance` - all still fail closed with no inline create fallback. C2/ C4 / gates /
`RunConstructGenerateSave`'s 600 s wait unchanged. `make build` (0 warnings) + `make audit` clean.
Not committed, not deployed, no live run.

## Entity-snapshot save path: PreSave -> overwrite -> native writer (build+audit clean, NOT committed/deployed)

`src/Day0Gen.cs` only. Live run proved the envelope create->generate->SetLevel->adopt sequence
completes and writes a save, but the save is EMPTY (`CC DIAG: live DXGame CSalvable components = 0`;
`generated level Entities=Count=61467, CommandCenter=True`; read-back `LevelEntities count=0`).
Root cause: `ZXLevelState.PreSave` (`vendor/decompiled/ZX/ZXLevelState.cs:1627-1674`) clears
`CurrentGeneratedLevel.Entities`, removes CSalvable `ExtraEntities`, then rebuilds `LevelEntities =
DXGame.Current.ComponentsOfType<CSalvable>()...`. That live registry is populated only by scene
registration (`CreateSceneObject`/`AddToScene`), which the tolerated `SetLevel` scene-object NRE
aborts, so the rebuild yields nothing and the SaveState wrapper writes an empty save.

Fix (deterministic; does NOT drive the scene machine), in `SaveGeneratedState`:
1. `BuildPreSaveEntitySnapshot(level)` snapshots the generated level's OWN `Entities` list BEFORE
   PreSave clears it and builds the two dictionaries with PreSave's own predicate
   (`ZXLevelState.cs:1644-1672`): `LevelEntities` keyed by `entity.ID` for not-fast entities with
   the dead/unbuilt filter (a ZXEntity whose life `IsAlive == false` is kept only when it is a
   `Structure` with `IsBeingBuilt == true`); `LevelFastSerializedEntities` keyed by
   `entity.IDTemplate.Value`, grouping `(ID, Position)` into `DXTupla2<ulong,PointF>` for
   not-dead fast entities. Reflected members: `ZX.Entities.ZXEntity` / `Structure`,
   `ZXEntity.UseFastSerializing` (property), the life accessor `#=zS6TvFuwF_68l()`, the life
   object's `IsAlive` (property/field), `Structure.IsBeingBuilt`, entity `ID` / `IDTemplate` /
   `Position`, and `DXVision.DXTupla2`2` (2-arg ctor, else `A`/`B` field/property fallback).
   Missing filter members log and fail open (not fast / keep); an unbuildable `DXTupla2` logs and
   leaves the fast dict empty (never aborts). The two required properties are discovered
   fail-closed.
2. `refl.PreSaveMethod` (exact escaped name `_0023_003DzQXHqcVh9mGZZ`, instance 0-arg void, walked
   over the `LevelStateType` base chain, discovered in `DiscoverAll`) is invoked explicitly on `ls`.
3. `ls.LevelEntities` / `ls.LevelFastSerializedEntities` are overwritten with the snapshot.
4. `refl.SaveWriterMethod` (`#=zMtGuEM2lBSlZ5BGWvg==`, instance `(string)->void`) is invoked on
   `managerInstance` with `dialogTarget`. It serializes `ZXGameState.Current` (already re-asserted
   by `ReAssertStateBeforeSave`), so the overwritten dictionaries are what is written. The SaveState
   wrapper is NOT called on this path (its PreSave would re-wipe the dictionaries); its discovery
   remains but is unused.

Fail-closed: a missing `PreSaveMethod` or `SaveWriterMethod` aborts with a named
`Day0GenException` before the save; `managerInstance == null` aborts; the existing
`dialogTarget`/`dialogCheckPath` existence checks, `SAVEAFTER` log, stored-exception behavior, C2
(`ReAssertStateBeforeSave`) and C4 (`CleanupWrittenArtifacts`) are unchanged. Log strings/comments
updated to describe the snapshot->PreSave->overwrite->native-writer path and WHY (live registry is
empty because the tolerated scene-object NRE aborts engine registration; the native writer
serializes the static game state, so a hand-built `LevelEntities` is honored).

UNVERIFIED: no live run (tool not executed; do NOT deploy/run per task). The save's in-game
loadability is unverified - the tool only read-backs the ZIP (`ZipSerializer.Read(path,"Data")`).
`make build` (0 warnings) + `make audit` clean.

FIX: BuildPreSaveEntitySnapshot resolves the fast-entity IDTemplate key robustly - a nullable/ulong IDTemplate boxes as a plain System.UInt64 (no "Value" member), so read it directly when integral, else via ReadMemberValue(.,"Value"); entity.ID/Position are non-nullable and unchanged. Local only, no commit/deploy/run.
FIX: `BuildPreSaveEntitySnapshot` now keeps ONLY entities with the `CSalvable` component (mirrors `ComponentsOfType<CSalvable>()`): resolves `DXVision.DXEntity.HasComponent<T>()` via `FindGenericBoolMethodUp` + `MakeGenericMethod(ZX.Components.CSalvable)` and aborts (named `Day0GenException`) if either is missing; filter runs before the fast/not-fast split so both branches are filtered; logs `SAVE SNAPSHOT: level.Entities=<n>, CSalvable kept=<k>, skipped=<n-k>`. Local only, no commit/deploy/run.

## VERIFIED 2026-09-30 - day-0 CommandCenter save produced AND loads in-game
Build `dce5f440` (commit `f79ed67`). Live run: `SAVE SNAPSHOT: level.Entities=61508, CSalvable
kept=6749, skipped=54759` -> `LevelEntities=41 (CommandCenter=True), fast-serialized entities=6708
in 8 template group(s)`; read-back `LevelEntities count=41, commandCenterPresent=True`; `PHASE full
COMPLETE`. Artifacts: `CC 550040233.zxsav` ~247 KB + `.zxcheck`, no `_Crash` pair. User loaded it
in-game: the level loads WITH the Command Center.

Chain that makes it work: (1) the engine-owned start-game envelope (pause + dispose/nul
`CurrentGameSystem` + `IsLoading`, create+save on one Task) replaces the animation-gated manager
loading dialog that stalled; (2) `PreSave`'s live `DXGame.Current.ComponentsOfType<CSalvable>()`
registry is empty because the tolerated `SetLevel` scene-object NRE aborts engine registration, so
the tool snapshots the generated level's own entities, filters to `CSalvable`, runs `PreSave`,
overwrites `LevelEntities`/`LevelFastSerializedEntities`, then calls the native writer directly;
(3) filtering to `CSalvable` excludes terrain (`Cliff` etc., which the game regenerates from the map
layers) - including them double-created terrain and crashed on `Cliff.OnSceneAdded`.

## Universal CLI (subcommands seed/save + diagnostics) - build+audit clean; later deployed and live-tested (see save-mode fix below)

`src/Day0Gen.cs` only. Adds a friendly subcommand CLI on top of the verified generation path:
`seed`, `save`, plus the unchanged diagnostics `discovery`/`zombie`/`genprobe`. `--phase full` is
kept as a backward-compatible alias for `seed`; `--phase discovery|zombie|genprobe` unchanged.

- `Options` gained `Command` (resolved from `args[0]` when it is not a `--flag`, else from
  `--phase`; `full` folds to `seed`), `FromSave`, `Difficulty`/`Theme` (enum-name strings),
  `Override*` (Seed/NCells/Duration/Pop/Difficulty/Theme) markers, and a per-command default
  `Name` (seed -> `Day0 <seed>`, save -> `<sourceBaseName> (Day0)`, diagnostics -> `Day0`).
  `seed` requires `--seed` unless the legacy `--phase full` form (keeps default 550040233).
  `save` requires `--from` and the file must exist (checked in Parse).
- `save` param extraction: `RunFull` calls new `ApplySaveParams()` after engine init +
  `ProbePasswordMachinery()` and BEFORE construction/generation. It reuses the renamed
  `ReadGameStateFromSave(path)` (former `ReadBackState`, same verified flag->set password->ZIP
  "Data" read->clear path), gets `ZXGameState.SurvivalModeParams`, and reflectively reads the six
  properties. Missing/null `SurvivalModeParams` or any missing/null member aborts with a named
  `Day0GenException` (fail closed). Extracted values are logged before and after overrides.
  Explicit `--seed/--ncells/--duration/--pop/--difficulty/--theme` override the save-derived value.
- Difficulty/theme: `refl.DifficultyEnum` is discovered in `DiscoverAll` from
  `ZXRandomLevelParams.DifficultyType.PropertyType` (never hardcoded; aborts if not an enum).
  `CreateGameStateAndLevel` sets `ThemeType` to `None` when `opts.Theme` is null (today's behavior)
  and parses `opts.Theme`/`opts.Difficulty` via new `ParseEnumOrAbort` (case-insensitive, invalid
  names abort listing `Enum.GetNames`). `DifficultyType` is untouched unless `--difficulty` is set.
- UNCHANGED verified path: envelope, `BuildPreSaveEntitySnapshot`, CSalvable filter, `PreSave` +
  native writer, snapshot allow-list, all diagnostics phases. Only the argument surface, the
  pre-generation param resolution, and the two enum setters changed.
- Verification: `make build` 0 warnings/0 errors; `make audit` clean. Options.Parse exercised by a
  throwaway net8 harness (17 cases: legacy/seed/save/diagnostics defaults + error paths) - ALL PASS.
  NOT verified live: engine-side save-mode read of a real `.zxsav` (tool not run per task), and the
  in-game effect of a non-default difficulty/theme. Extraction placement (after ProbePasswordMachinery)
  chosen so the read uses the already-verified password machinery; not before `ZombieInit`.

## save-mode crash: source-save read must happen after the start screen settles (FIXED/VERIFIED 2026-09-30)

`save` crashed the in-process engine on every attempt: ZXLog showed `ShowStartScreen -> fade ->
ShowLoadScreen -> 5x ZipSerializer.Deserialize "Zip File Corrupted" (blank filename) -> unhandled
NRE at DXVision.DXProjectImage.get_ImageArea()` on the render thread; the process died inside
`WaitForStartScreenSettled` and no save was written. `seed`/legacy `--phase full` crashed the same
way only when run back-to-back with a lingering engine (Windows WER showed TheyAreBillions.exe
AppHang + a system-wide crash burst at 10:55); a clean `seed` run succeeds.

Root cause: `ApplySaveParams()` ran in `RunFull` before `WaitForProjectContext`, so
`ZipSerializer.Read(source,"Data")` deserialized a foreign `ZXGameState` while the engine was
still loading the start screen. That races the scene machine and corrupts the loading-screen
render. `seed`/`full` never read a save before the start screen (their only such read is the
post-save read-back, with the engine paused).

Fix: moved `if (opts.Command == "save") ApplySaveParams();` into `CreateGameStateAndLevel()` (the
envelope's create delegate) directly before construction - i.e. after `WaitForStartScreenSettled`
and with the engine paused. Error handling unchanged: the delegate rethrows, the envelope signals
the main thread, and the run aborts fail-closed.

VERIFIED: `run-day0-save.bat` -> `STARTSCREEN GATE` then `SAVE EXTRACT` inside the envelope ->
`PHASE full COMPLETE`; `Read-back LevelEntities count=40, commandCenterPresent=True`; ZXLog clean
(no exception, no ZipSerializer errors). `seed` verified the same day. Deployed exe sha256
`eee96254f86578751ab5763dc62d760ffc36c28965c48794dc19c8e6ee384e3d` (previous exe kept as a
backup on the target host).

## Fix: empty wave schedule in generated saves (deployed; countdown live-verified)

`src/Day0Gen.cs` only. Symptom: the generated day-0 `.zxsav` loads but has NO zombie
waves (swarms) in-game. Root cause: the wave schedule lives in `ZXLevelState.LevelEvents`;
the map generator populates `ZXLevelExtension.LevelEvents`, and the engine copies them into
the serialized state only in its level-start lifecycle (game system `--zxRcpu...cs` ~:3113,
`_0023_003DzwK_pUIeRBpbp`: `ZXLevelState.Current.LevelEvents.AddRange(ZXLevelExtension.Current.LevelEvents)`).
Day0Gen's envelope saves before that lifecycle runs, so `LevelEvents` serialized empty.
Fix: new `TransferLevelEventsIntoLevelState(ls, level)` called in `SaveGeneratedState`
right after `ReAssertStateBeforeSave` - copies `DXLevel.Extension.LevelEvents` into
`ZXLevelState.LevelEvents` (idempotent by reference; aborts if the extension has none).
New fail-closed read-back `AssertReadBackHasLevelEvents(readBack)` (after the CC assertion)
logs `Read-back LevelEvents count=N` and aborts on 0. New log `LEVEL EVENTS: extension=N,
added=N, already-present=N, state total=N`.

LIVE (verified): `LEVEL EVENTS: extension=6, added=6, already-present=0, state total=6`;
`Read-back LevelEvents count=6`; the next-wave countdown appears in-game. The 6 events =
1 win (day 100) + 1 FinalSwarm + 4 periodic waves.

## Hardening from code-critic review of the wave fix (deployed)

- `TransferLevelEventsIntoLevelState` returns the state count; the read-back assertion now
  requires `count == transferred` (not just `> 0`), so a partially serialized schedule aborts.
- Spawn gates ARE saved in the snapshot's `LevelEntities` (the 4 `CUnitGenerator` gates
  render as `DXEntity=4`); `PreSave` strips CSalvable entities out of `ExtraEntities`, so an
  ExtraEntities-only probe reads 0 (the first live run's `Read-back SPAWN GATES: 0` was that
  probe artifact, not data loss). Read-back now counts gates in `LevelEntities` and
  fail-closes (`AssertReadBackHasSpawnGates`, expects the pre-save count, 4).
- Doom villages: the generator builds each as many `CInfectionNest` buildings added to
  `MapDrawer.ExtraEntities` (`--zyl_NPjj...cs:289-350`). They are non-CSalvable, so they do
  NOT appear in the LevelEntities histogram; they ride the serialized `CurrentGeneratedLevel
  .Extension.MapDrawer.ExtraEntities`. New probes `VILLAGE NESTS (pre-save)` and
  `Read-back VILLAGE NESTS` + fail-closed `AssertReadBackHasVillageNests` (read-back total =
  nests in LevelEntities + nests in ExtraEntities must equal the pre-save `level.Entities` count).
  NOT yet live-confirmed (one run pending).
- Histogram minors: snapshot histograms guarded; fast-template list capped at 30 groups;
  unparseable template keys labeled `key:<raw>`; comment notes the LOAD `AVyu=true` branch
  skips the engine AddRange (no duplicate events on reload).
