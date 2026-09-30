# Why the generated day-0 save has no Command Center

Research only. All claims are static reads of `src/Day0Gen.cs` and the decompiled
vendor tree; nothing was executed. Items that need a runtime check are marked **VERIFY**.

## BLUF

The Command Center is serialized by exactly one code path: `PreSave`
(`vendor/decompiled/ZX/ZXLevelState.cs:1633-1657`) rebuilds
`LevelEntities = DXGame.Current.ComponentsOfType<CSalvable>()...` after first
**clearing** `CurrentGeneratedLevel.Entities` (1637) and **removing every
CSalvable-bearing ExtraEntity** (1638). The CC is only ever created into
`MapDrawer.ExtraEntities` (`--zyl_NPjjlA7DRfVtsRJCX1kN4BxSr.cs:201`), so it is
saved iff its `CSalvable` is present in the live game component registry at
PreSave time. In the tool's flow it is not, so it is dropped.

The `ZXMapDrawer.cs:910/1420-1423` `flag` skip is **not** the cause: with the
tool's fresh `ZXLevelState` (`CurrentGeneratedLevel == null`) `flag` is `false`,
identical to the real survival flow, so `UpdateLevel` DOES move the CC into
`level.Entities`. The problem is downstream of that.

## Traced path (file:line)

1. Generator creates the CC in `ExtraEntities` only:
   `--zyl_NPjjlA7DRfVtsRJCX1kN4BxSr.cs:193` (`CreateInstance`),
   `:201` (`zXMapDrawer.ExtraEntities.Add(val13)`). Template id
   `3153977018683405164` = `--zYLpZK4K9zSHp.cs:3605-3607` (`pMFG1rRDzbXO`).
2. `CSalvable` for the CC can only come from its template / `DefaultComponents`
   (`ZX.Entities/ZXEntity.cs:272-276` — `{CSalvable, CSelectable}`;
   `CommandCenter.cs:34-40` adds only `CRotatingEngine`). `SetLevel`'s explicit loop
   `--zxRcpu6e7NYzT7tGWqPjpOkc-.cs:1826-1829` runs BEFORE `UpdateLevel` (`:1831`),
   so it cannot give the CC its CSalvable (the CC is still in ExtraEntities at
   that point). => In a clean run the CC must already carry CSalvable from
   instantiation. **VERIFY** (game-data templates not in tree).
3. `SetLevel` -> `UpdateLevel` (`:1831`) -> `ZXLevelExtension.cs:518-521` ->
   `ZXMapDrawer._0023_003Dzl2XxwLg_003D` (`ZXMapDrawer.cs:1466`):
   `_0023_003Dz0Pkwp_k_003D` builds the temp list; `flag =
   ZXLevelState.Current?.CurrentGeneratedLevel != null` (`:910`); loop
   `:1420-1423` adds an ExtraEntity only if `!flag || !HasComponent<CSalvable>()`;
   `_0023_003Dz07l0PNliuIrM` (`:1457`) then `level.AddEntity(item)`.
   Tool flow: `flag == false` (fresh `ls`) => CC is added to `level.Entities`.
4. `SetLevel` then NREs at `level.CreateSceneObject()` (`:1846`) and the engine
   adopt `ZXLevelState.#=zf9PbDap0F6OC` (`:1847`) is skipped. The tool re-invokes
   it itself (`src/Day0Gen.cs:4196`). That method is pure ZXLevelState
   bookkeeping (`ZXLevelState.cs:1597-1621`): sets `IDCurrentMission`,
   `CurrentGeneratedLevel = DXLevel.Current`, `LevelEntities = null`, `LayerFog`,
   `LayerActivity`, gold/wood. It does NOT register entities/components.
5. Save -> manager SaveState wrapper -> `PreSave` (`ZXLevelState.cs:1633-1657`):
   re-sets `CurrentGeneratedLevel = DXLevel.Current` (1636), **clears
   `CurrentGeneratedLevel.Entities`** (1637), **removes CSalvable ExtraEntities**
   (1638), then rebuilds `LevelEntities` from the live
   `DXGame.Current.ComponentsOfType<CSalvable>()` (1644-1657). The `where` filter
   keeps a dead/under-construction `Structure` only when `IsBeingBuilt` (CLife
   `IsAlive` defaults true with `Life = 1`, `ZX.Components/CLife.cs:22,126`, so
   this filter is probably not the trigger).

## Most likely root cause

The CC is not present in `DXGame.Current.ComponentsOfType<CSalvable>()` when
PreSave rebuilds `LevelEntities`. The CC reached `level.Entities` (step 3) but its
live registration with the game world is what got lost: `PreSave` clears
`level.Entities` and then relies entirely on the live registry, and the engine
path that normally establishes that registry (the level/scene setup around
`CreateSceneObject`, `:1846-1904`) was aborted by the tolerated NRE; the tool's
substitute only restores ZXLevelState fields. (`DXLevel.Current` is also not
re-asserted by `ReAssertStateBeforeSave`, `src/Day0Gen.cs:4428`; if the engine's
post-SetLevel start-screen teardown nulls it, `PreSave` early-returns at
`ZXLevelState.cs:1629-1631` and `LevelEntities` stays at the adopt's `null` —
no entities at all.) **VERIFY with a decoded read-back of `LevelEntities`.**

## Ranked alternatives

1. **Live-registry loss across the tolerated NRE / scene teardown** (above):
   `CreateSceneObject()` aborts before the game world is registered; the tool
   re-asserts only `ZXGameState`/`ZXLevelState`/`CurrentGameSystem`, not
   `DXLevel.Current` or the entity/component registry.
2. **`DXLevel.Current != level` at PreSave** -> `PreSave` early-return
   (`ZXLevelState.cs:1629-1631`) -> empty `LevelEntities`. The adopt precondition
   sets it (`src/Day0Gen.cs:4206-4228`) but `ReAssertStateBeforeSave` does not.
3. **`flag` skip** (`ZXMapDrawer.cs:910,1420-1423`) — falls away: `flag == false`
   in the fresh-state tool flow, same as the game; and either way `PreSave` wipes
   both `level.Entities` and CSalvable ExtraEntities.
4. **CC lacks CSalvable** (template/`DefaultComponents` not applied) — would also
   break the real game, so unlikely, but not excluded (templates not inspectable).
5. **Crash save** (`CC 550040233_Crash.zxsav`): no crash-save handler exists in
   the decompiled tree (grep "Crash" = 0 hits); it lives in the non-decompilable
   DXVision/manager, so a pre-save mutation of `DXGame.Current`/entities cannot be
   ruled out statically. **VERIFY** (not likely to be CC-specific).
6. **Seed mutation** (Q5): the generator re-seeds in place —
   `--zyl_NPjjlA7DRfVtsRJCX1kN4BxSr.cs:198-199,205` set
   `params.Seed = val.NextSignedInt()` on the SAME params object the tool passed,
   then recurse. It does not remove the CC, but it can make the map differ from
   seed 550040233. Detect from ZXLog: count `Random Map Creation with seed:` lines
   and their seeds (one line = the requested seed; >1 = reseeded). Plausible but
   unrelated to the missing CC.

## Minimal fix point

Make the CC a live, registered `CSalvable` before the wrapper's `PreSave` runs,
then prove it on read-back.

- Set `DXLevel.Current = level` BEFORE the `gamesystem.SetLevel(level)` invoke
  (currently only done inside `AdoptLevelIntoLevelState`, i.e. after SetLevel), so
  the `level.AddEntity` calls made by `ZXMapDrawer._0023_003Dz07l0PNliuIrM`
  (`:1457`) happen while our level is the game's current level.
- Extend `ReAssertStateBeforeSave` (`src/Day0Gen.cs:4428`) to re-assert
  `DXLevel.Current == level` against the same getter/setter used by the adopt
  precondition, together with the existing three statics, before the save.
- If the CC is still absent, explicitly ensure registration after the adopt: find
  the `CommandCenter` in `level.Extension.MapDrawer.ExtraEntities` /
  `level.Entities`, ensure `HasComponent<CSalvable>()`
  (`AddComponent<CSalvable>()`), and add it to the live game the same way the
  engine does (`level.AddEntity` / `AddToScene`), logging before/after.
- Regardless of the fix, add a hard post-save assertion (the tool already decodes
  the save via `ReadBackState`, `src/Day0Gen.cs`): after read-back, require
  `LevelEntities` (and/or `CurrentGeneratedLevel.Entities`) to contain a
  `CommandCenter`; abort otherwise. That converts this silent drop into a
  fail-closed error.

## Not verifiable statically

Whether `DXLevel.AddEntity`/`DXGame.ComponentsOfType<T>` register components with
the live game or only the level list; whether the CC template carries `CSalvable`
(templates absent); whether the engine start-screen teardown nulls
`DXLevel.Current`/clears the registry between SetLevel and the save; the
crash-save handler (DXVision, not decompilable).
