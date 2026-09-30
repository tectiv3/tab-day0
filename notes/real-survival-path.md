# Real survival-start path vs. the tool's out-of-band SetLevel NRE

Research only. Static reads of `vendor/decompiled/` + `src/Day0Gen.cs`, plus ZXLog
observations recorded in `.serena/memories/day0gen.md`. **VERIFY** = needs a live run.
DXVision is embedded/undecrypted — its scene machine is **not statically inspectable**.

## BLUF

The tool's construct→generate→SetLevel *ordering* is a verbatim copy of the real
survival/CC handler, but it is *timed wrong*: it creates `ZXGameState`/`ZXLevelState` and
loads the game system during engine startup, **before** the engine settles on the start
screen. The engine's own startup transition to `ZXSystem_StartScreen` then completes and
its teardown clears those statics. `SetLevel` finds `ZXLevelState.Current == null`, tries
to create one, and NREs because `ZXGameState.Current` is *also* null — exact dereference
`ZXLevelState.cs:1167` (`ZXGameState.Current.LevelState = <new>`). The per-entity
`CreateSceneObject()` NRE was never reached because this earlier NRE fires first.

Fix (approach **b**): move the construction block (`src/Day0Gen.cs:4264-4290`) to *after*
`WaitForStartScreenSettled` (`:4317`). Then `SetLevel`'s null-check at
`--zxRcpu6e7NYzT7tGWqPjpOkc-.cs:1787` is false and `:1789`/`ZXLevelState.cs:1167` never run.

## 1. The real path (file:line)

Handler system type `_0023_003DzLs_0024VX1eoAapVflmW_w_003D_003D`
(`vendor/decompiled/--zLs-VX1eoAapVflmW_w--.cs:20`), the survival/CC **menu game system**.
The start-new sequence exists as an Action and inlined in button handlers:

- Action form: `_0023_003Dz_0024fv2hsIRVRVkrEcesSKNL4VSJ4UZNsWJZgfZ8vs_003D()`
  (`--zLs-VX1eoAapVflmW_w--.cs:158-181`); sets `ChallengeType=CommunityChallenge`.
  Not referenced from C# (name-based UI wiring). **VERIFY.**
- Plain "new survival" handler (no CC; closest to what the tool emulates):
  a `ZXButtonSteam.Activated` lambda at `:2796-2813`.
- CC button handlers: `_0023_003DzNdwJlVGRAorbOmF2eBy0C3A_003D(DXButton)` (`:1865`), body
  `:1912-1934`; and `_0023_003DzUVQvOPKsWJ8hoP9u...` (`:750`), body `:771-791`.

All route through the manager loading dialog
`_0023_003Dz9DPDdq9qP9lZ(int delay, Action create, Action saveAfter, List<string>, bool)`
(`--z4RevDP3eECXqXS6JRA--.cs:2914`): pauses (`:2919`), disposes the current game system
(`:2998-3002`), runs the **create** delegate on a `Task.Factory.StartNew` thread
(`:2955-2992`), then the **save-after** delegate on the engine frame (`PostMethods_OnFinishFrame`).

Exact ordering in the create delegate (`:160-177` / `:2798-2812` / `:1914-1931`):

1. `ZXGameState.Set(new ZXGameState(name))` — :160 / :2798 / :1914
2. `GameMode = Survival` — :161 / :2799; (CC) `ChallengeType`/`ChallengeID` — :171-172
3. `new ZXLevelState()` + `ZXLevelState.Set(...)` — :173 / :2801
4. `SurvivalModeParams = …` — :174 / :2800
5. `ZXLevelState.Current.Init()` (`_0023_003DzF1YHTRUZSNRa`, `ZXLevelState.cs:1677`) — :175 / :2802
6. `manager.CurrentGameSystem = DXSystem.Load<gamesystem>(false)` — :176 / :2803
7. `gameSystem.SetLevel(generator(params))` — :177 / :2812

**UI/Form dependence:** the sequence is a plain method/action (no Form arg); what makes it
real is that it is *only ever invoked from a button handler on the already-settled
start-screen menu*. The dialog runs it on a worker Task with the engine frame pumping, so it
is **not** strictly UI-thread-bound.

## 2. Divergence from the tool

`RunConstructGenerateSave` (`src/Day0Gen.cs:4260`):
- construction block (`:4264-4290`): `new ZXGameState`+Set, params, `new ZXLevelState`+Set+Init,
  `DXSystem.Load<gamesystem>(false)`, `manager.CurrentGameSystem=sys`
- `… WaitForEntityDefaultParamsGate()` `:4312`, `WaitForStartScreenSettled()` `:4317`,
  generator `:4321`, `SetLevel` `:4331`.

Steps 1-6 are exactly the real order — but they run **during engine startup**, not from a
click handler on the settled menu. The engine's startup fade (`ZXGame - ShowStartScreen` →
`ChangeScene - Init/Paused/Fade` → `ZXSystem_StartScreen - ShowScene/ShowSceneSuccess`;
`day0gen.md:108-116,457-461`) completes **between** tool step 6 and `SetLevel`, and its
teardown clears `ZXGameState.Current`/`ZXLevelState.Current` (same teardown previously seen
clearing them before the SaveState wrapper, `day0gen.md:255-262`). **VERIFY** live.

## 3. `ZXLevelState.Set` NRE cause

`ZXLevelState.Set` = `_0023_003Dz2SXmL2Q_003D`, `vendor/decompiled/ZX/ZXLevelState.cs:1163-1169`:

    if (ls != null || ZXGameState.Current != null)      // :1165
        ZXGameState.Current.LevelState = ls;            // :1167  <-- exact NRE deref

The engine passes a freshly-constructed non-null `ZXLevelState`, so the body **always** runs
and `ZXGameState.Current` is dereferenced. Reached from `SetLevel`
(`_0023_003DzmTU4kueQctVr`, declared `--zxRcpu6e7NYzT7tGWqPjpOkc-.cs:1685`), `:1787-1790`:

    if (ZXLevelState.Current == null)                   // :1787
        ZXLevelState.Set(new ZXLevelState());           // :1789

`ZXLevelState.Current == ZXGameState.Current?.LevelState` (`ZXLevelState.cs:1158-1161`), so
once the teardown nulls `ZXGameState.Current`, both are null → `:1787` true → `:1789` throws
at `ZXLevelState.cs:1167`. The downstream `CreateSceneObject()` NRE at
`--zxRcpu6e7NYzT7tGWqPjpOkc-.cs:1846` is never reached.

## 4. Scene-machine trigger

`ShowStartScreen` / `ChangeScene` / `ZXSystem_StartScreen` / `ZXSystem_GameLevel` appear
**nowhere** in `vendor/decompiled/` (`grep -rin` = 0 hits; `DXVision/` holds one unrelated
file) — the scene machine is in the embedded, non-decompilable DXVision assembly, so its
internals are **STATIC-UNVERIFIABLE**.

ZXLog evidence (`day0gen.md:108-116,457-461`): the engine reaches the main menu and runs
`ZXGame - ShowStartScreen` / `ChangeScene … Fade` **as part of its own startup**, independent
of any ZX state the tool set — the tool had already set `ZXGameState.Current` non-null, yet
the engine still went to the start screen. So it is the unconditional startup transition, not
a reaction to `ZXGameState.Current == null`. (Consistent: `ZXGameState.Set`
`ZXGameState.cs:203-206` is a plain field write and cannot synchronously trigger a scene
change.) Creating state **after** the settle parks the scene machine on the start screen just
like the click handler; `SetLevel` then drives the change to the game level.

## 5. Ranked recommendation (approach #3)

**(b) FIRST — reorder, do not re-plumb.** Move the construction block
(`src/Day0Gen.cs:4264-4290`) to *after* `WaitForStartScreenSettled` (`:4317`) and
`WaitForEntityDefaultParamsGate` (`:4312`). New order: settle → entity-params gate → theme
re-verify → construct state/system → generator → SetLevel. Matches the real handler exactly
and removes the window in which the startup teardown can null the statics. Minimal change, no
engine internals touched; `ReAssertStateBeforeSave`/`AdoptLevelIntoLevelState` stay as
backstops. **Most likely to work.**

**(a) SECOND — invoke the real handler.** Real start method
`_0023_003Dz_0024fv2hsIRVRVkrEcesSKNL4VSJ4UZNsWJZgfZ8vs_003D()` (instance, 0 args, `void`) on
the survival/CC menu system (`--zLs...cs:158`), reachable as `manager.CurrentGameSystem`
while the menu system is up. Feasibility **poor**: it hardcodes the live CC `ChallengeID`/`Seed`
(not `550040233`), and calls `DXLinq.AddIfNotPresent` on
`GameAccount._0023_003DzzOIFKv19HsLQljPKlg_003D_003D` + `manager._0023_003DzmQetCvifY0eqPqP1AQ_003D_003D(false, true)`
(`:179-180`) which **writes the account/CC state** — violating the no-touch-CC constraint. It
also never calls the SaveState wrapper, so a save step is still required. Keep only as fallback.

## Unverifiable / inferred

- Whether the startup teardown (vs. something else) nulls `ZXGameState.Current` — inferred
  from `day0gen.md:255-262`; confirm with the reordered live run. **VERIFY.**
- Scene-machine internals (why `ZXSystem_StartScreen` is entered; whether `CreateSceneObject`
  needs a settled scene) — **STATIC-UNVERIFIABLE** (DXVision).
- `_0024fv2hs…` being the wired "Survival"/CC button handler — **VERIFY**.
