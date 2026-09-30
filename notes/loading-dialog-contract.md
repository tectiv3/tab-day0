# Manager loading dialog `#=z9DPDdq9qP9lZ` — invocation contract (for Day0Gen)

Research only. Static reads of `vendor/decompiled/` + `src/Day0Gen.cs`. **VERIFY** = needs a live run.
DXVision (`DXGame`, `PostMethods_*`, scene machine) is embedded/undecrypted — internals **STATIC-UNVERIFIABLE**.

## BLUF

Drive construct→generate→SetLevel through the engine's own manager loading dialog
`#=z9DPDdq9qP9lZ(int delay, Action create, Action saveAfter, List<string> messages, bool showLoading)`
(`--z4RevDP3eECXqXS6JRA--.cs:2914`, public instance void on the manager singleton) instead of poking
`CurrentGameSystem`/`SetLevel` out of band. The dialog pauses the game, disposes+nulls the current game
system, runs `create` on a `Task` thread (frame loop keeps pumping → SetLevel's `InvokeOnStartFrame`+`WaitOne`
signals), then runs `saveAfter` on the engine finish frame *after* `create` has returned. Pass `true` for
`showLoading` (the only mode that uses the Task, i.e. the only deadlock-safe mode). Split the tool: `create`
= construct→generate→SetLevel→adopt; `saveAfter` = ReAssert + SaveState wrapper. Because the dialog returns
before its work runs, the tool must join on a `ManualResetEvent` set by `saveAfter` before post-save checks.

## 1. Signature + parameter roles

```csharp
// --z4RevDP3eECXqXS6JRA--.cs:2914  (public, instance, void)
public void _0023_003Dz9DPDdq9qP9lZ(int delay, Action create, Action saveAfter,
                                    List<string> messages, bool showLoading)
```

| # | Type | Decompiled name | CC handler value (`--zLs-VX1eoAapVflmW_w--.cs:1912`) | Role |
|---|------|-----------------|--------------------------------------------------------|------|
| 1 | `int` | `zimSK46CYBDD7KB7_jQ` | `20` | OFakeMeter nominal duration (seconds). Passed to `_0023_003DzTTFkxREfqpc3YAc5KBAfJB0_003D`/`_0023_003DzxALXqkuCCSMY` → `new OFakeMeter(sec,…)`; `DurationSeconds = sec` (`OFakeMeter.cs:30-40`). Display pacing only. |
| 2 | `Action` | `mn5IQU7F6_6r` | the create lambda (`:1914-1930`) | Runs after pause+game-system dispose. See §2. |
| 3 | `Action` | `_zyB5t6kAg7k` | `<>c` cached save delegate | Runs on the finish frame after `create`. See §3. |
| 4 | `List<string>` | `ws1HsB4` | `TableManagerStrings[key].Rows.Select(...).ToList()` | Only used by the `zxALXqkuCCSMY` progress screen as a rotating subtitle (`:3063-3092`, guarded `!= null && Count > 0`). The `EtqIAUHlGT8d==true` screen (`:3152`) ignores it. Empty/null is safe. |
| 5 | `bool` | `NT0YOq6iVda2` | `true` | Gates async loading mode. `true`: loading screen + `Task.Factory.StartNew(create)` + finish-frame handoff. `false`: dispose game system, then run `create`/`saveAfter` **synchronously** on the caller's thread (`:2994-3020`), and `IsLoading`/restore via fades only. **Must pass `true`** (false puts SetLevel on the caller thread → pump deadlock). |

## 2. `create` (2nd Action) contract

- Runs at `--z4RevDP3eECXqXS6JRA--.cs:2954`, **inside `Task.Factory.StartNew`** opened at `:2949` — thread-pool
  thread, NOT the UI thread and NOT the frame thread.
- Preconditions the dialog already established: `Cursor=Wait`+lock (`:2916`), `Paused=true` (`:2919`),
  `IsLoading=true` (`:2931`), previous `CurrentGameSystem.Enabled=false; Dispose(); = null` (`:2943-2945`),
  loading scene added by `_0023_003DzXAMaTPI_003D(true,…)`.
- Must: create the game state + game system and call `SetLevel` (exactly the CC lambda `:1914-1930`) — i.e. the
  tool's `src/Day0Gen.cs` construction block (~`:4285-4320`), generator, `SetLevel` (~`:4323-4345`), plus
  `AdoptLevelIntoLevelState` (the tool's SetLevel NRE workaround). Must NOT call the SaveState wrapper.
- On throw: the `catch` at `:2997-2999` logs and posts an error handler via `DXGame.Current.InvokeOnStartFrame`
  (`<>c__DisplayClass227_1.zwelJkavgJRkhGkLkWw`). It does **not** rethrow, does **not** call
  `_0023_003Dz_0024myF5ADMxD6Y`, and does **not** run `saveAfter`. So a failed create → saveAfter never runs.

## 3. `saveAfter` (3rd Action) contract

- Runs on the engine finish frame, reached only via the handoff in §4, *after* `create` returns.
  Order in the success branch: `Task(create)` (`:2949`) → create returns → `_0023_003Dz_0024myF5ADMxD6Y(cb)`
  (`:2955`) → meter completes → `cb` on start frame → `base.PostMethods_OnFinishFrame.Invoke(...)` (`:2958`)
  → `_0023_003DzXAMaTPI_003D(true, inner)` (`:2961`) → fade-in finished → `IsLoading=false` (`:2963`) →
  `if (saveAfter != null) saveAfter()` (`:2966-2968`).
- Nullable: yes, guarded `!= null`. `DXLevel`/state is settled because `create` returned before the meter even
  starts completing. **This is the right place for the tool's SaveState wrapper**; matches the CC handler.
- Tool must add a completion signal (set in a `finally` inside saveAfter) + capture any exception into a field,
  because the dialog itself returns immediately and cannot surface saveAfter/create failures.

## 4. Finish-frame handoff (`_0023_003Dz_0024myF5ADMxD6Y`, `:2955-2995`)

- `_0023_003Dz_0024myF5ADMxD6Y(cb)` = `OWftHnU1JFCqYc4fiA.OFakeMeter.spZONZ0(cb)` (`:3186-3189`,
  `ZX.GUI/OFakeMeter.cs:54`) — registers `cb` as the meter-finish callback and starts the fast fill.
- `OFakeMeter.OnRender` (render/frame thread) ramps `FactorValue` to 1 then calls
  `DXGame.Current.InvokeOnStartFrame(cb)` (`OFakeMeter.cs:78-82`).
- `cb` runs on a **start frame**, then queues on `PostMethods_OnFinishFrame` (**finish frame**) → fade-in →
  `IsLoading=false` → `saveAfter`. So: create fully returns before saveAfter; the frame loop has been pumping
  throughout, so any `SetLevel` `InvokeOnStartFrame` action has already executed.
- Restore order after `saveAfter` (`:2969-2990`): `_0023_003DzTkvm_0024fc5hNvM=false`; `_0023_003DzIB5ucfBCe7G8()`
  (decrement cursor refcount, restore Normal cursor if 0/1 — `:3155-3164`); if `EtqIAUHlGT8d` →
  `owftHnU1JFCqYc4fiA=null` (drop meter) and clear the flag. `IsLoading` is set false at `:2963`. **`Paused` is
  not reset by the dialog** (only level/scene code would). The `false` branch mirrors this synchronously.

## 5. Reflective invocation feasibility (C#5 / net48)

- Public instance void method on the manager type; the tool already holds `managerInstance`. `MethodInfo.Invoke`
  with a `new object[] { 20, createAction, saveAfterAction, new List<string>(), true }`.
- `System.Action` wraps the tool's existing **static** parameterless methods: `new Action(MethodName)` or
  `new Action(delegate { … })` (C#3+ closures OK). `new List<string>()` (empty) is sufficient — the active
  progress screen ignores it; the alternate screen null-guards it. Type names match exactly (`typeof(Action)`,
  `typeof(List<string>)`).
- **UI/Form dependency:** the dialog's synchronous part (`_0023_003DzXAMaTPI_003D`) manipulates
  `DXGame.Current.Scene`; the real call comes from a WinForms click handler on the UI thread. Calling it from
  the tool's foreign thread races the render thread (**STATIC-UNVERIFIABLE**). Safest: invoke the *dialog* on the
  engine UI thread (existing `RunOnEngineUiThread`) — this does NOT reintroduce the SetLevel deadlock, because
  the dialog returns immediately and `create` runs on a Task.

## 6. Deadlock analysis (SetLevel `WaitOne` vs dialog)

- `create` runs on a Task thread; `SetLevel` posts `InvokeOnStartFrame(action)` then `_0023_003D…WaitOne()`
  (`--zxRcpu6e7NYzT7tGWqPjpOkc-.cs:1837-1840`). The dialog's own finish-frame work happens only *after* create
  returns, so it never blocks the frame loop create waits on. The frame/render loop is independent and busy
  animating the meter → the action runs, `WaitOne` signals. **Safe — provided `showLoading==true`.** With
  `false`, create runs synchronously on the dialog caller's thread; if that is the pump thread → deadlock.
- No deadlock risk between `WaitOne` and the finish-frame handoff. Do NOT invoke the dialog on the frame/pump
  thread while also expecting create to run inline (only relevant if `false` is passed).

## 7. Concrete recommendation

1. Discover the dialog on `refl.ManagerType`: exact escaped name `#=z9DPDdq9qP9lZ` (no `_2CDE` ambiguity),
   signature-validate `(int, Action, Action, List<string>, bool) -> void`, else abort.
2. Build in C#5:
   - `Action create = delegate { <construct → generate → SetLevel → AdoptLevelIntoLevelState> };`
   - `Action saveAfter = delegate { try { ReAssertStateBeforeSave(...); invoke SaveStateWrapperMethod; }
     catch (Exception ex) { saveError = ex; } finally { saveDone.Set(); } };`
3. Invoke on `managerInstance` with `(20, create, saveAfter, new List<string>(), true)` — optionally from the
   engine UI thread.
4. `saveDone.WaitOne(timeout)` under the existing watchdog; if create threw, saveAfter never ran → timeout →
   abort (fail closed). Only then run the existing `File.Exists`/zxcheck/read-back/CC assertions.
5. Keep `WaitForEntityDefaultParamsGate` before generation; the start-screen gate is obviated by the dialog.
6. Runtime checks: abort on null `managerInstance`/missing-or-mismatched dialog method; treat a timeout as an
   abort; do not rely on the dialog's `int`/`List` contents.

**Unverifiable / DXVision-dependent:** `PostMethods_OnFinishFrame.Invoke(Action)` queueing semantics, exact
fade/start-frame threading, whether foreign-thread scene mutation is tolerated, `Paused` left true, and the
`catch` error-handler's UI behavior (may block on a modal). All require a live run.
