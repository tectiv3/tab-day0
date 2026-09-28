# Day0Gen — implementation notes / findings

Scope: `src/Day0Gen.cs` (+ `src/Day0Gen.csproj`), per SPEC.md. All findings below verified against
`vendor/decompiled/` (ILSpy of TheyAreBillions v1.0.14) and `vendor/TABSAT/TABReflector/TABReflector.cs`.

## 1. SPEC reflection-map corrections (SPEC said X, decompile says Y)

| SPEC claim | Verified reality | Consequence in Day0Gen |
|---|---|---|
| `get_GameAccount` is a **static** method on the manager | It is the getter of a **public instance property** `GameAccount` on `_0023_003Dz4RevDP3eECXqXS6JRA_003D_003D` (manager file line ~1224). TABSAT never invokes it — it only uses it to *locate* the class (`type.GetMethod("get_GameAccount")` with default flags finds instance getters). | Readiness poll = static singleton `_0023_003Dzuartwoo_003D()` → non-null manager; then instance `GameAccount` → non-null. |
| Saves-folder `_0023_003DzND5ul2zfzAnWdSnC0A_003D_003D()` is an instance method | It is **static** (`public static string`, manager file line 1435) | Invoked statically; instance lookup kept as defensive fallback. |
| Save writer = `_0023_003DzxjEnyeUCASiC(path)` **on ZXGameState** (from `ZXFile<T>`) | **Wrong.** `ZXGameState` does NOT inherit `ZXFile<T>` (`ZX/ZXGameState.cs` — plain class). `_0023_003DzxjEnyeUCASiC` exists only on `ZXFile<T>` and is used exclusively by mod-file types (`ZXModFile`, `ZXModFileProject`). | Day0Gen uses the game's actual save writer: manager **private instance** `_0023_003DzMtGuEM2lBSlZ5BGWvg_003D_003D(path)` (manager file line ~2263), which does the whole native sequence (see §2). A manual-composition fallback replicates it piecewise if the name ever drifts. |
| `_0023_003DzyuVTytDlXbMA(path)`-equivalent for verify | `_0023_003DzyuVTytDlXbMA` is `ZXFile<T>`'s static reader (uses `ZipSerializer.ReadCrypted`). The game's own save-load path (`_0023_003Dz3YFOTVtBw_rT7oSQRA_003D_003D`, manager line ~2300) instead does: `flag = _0023_003Dz4Qt7c_0024gkgLZHT7ayvA_003D_003D(path)` → `_0023_003DzpKDARrtdO1GK7shdnQ_003D_003D(path, flag, true)` → `ZipSerializer.Read(path, "Data")` → clear. | Verify replicates the game's load path (flag + set-password + read + clear). `ZXFile<ZXGameState>._0023_003DzyuVTytDlXbMA` is discovered and available as an alternate (works because the method lives on the generic type and only casts), but the load-path replica is primary. |

## 2. Save write path — exact native sequence (from `_0023_003DzMtGuEM2lBSlZ5BGWvg_003D_003D` + `_0023_003Dzv3_eRJTVn6_00245`)

```
lock (static lock object)
  _0023_003DzpKDARrtdO1GK7shdnQ_003D_003D(path, 2, false)          // set ZipSerializer.Current.Password (flag hardcoded 2!)
  ZipSerializer.Write(path, "Data", ZXGameState.Current,
                      "Info", ZXGameState.Current._0023_003DzHhDw0V62_0024fqG(filename))
  _0023_003DzvgSfu3ouG_TLllPQAA_003D_003D(path, 2, false)          // clear password
  File.WriteAllText(ChangeExtension(path, ".zxcheck"),
                    _0023_003DzX5dOGo9W_0024Dop(path, 2))          // signer
```

Notes:
- The write path hardcodes flag **2**; the *read* path derives the flag via `_0023_003Dz4Qt7c_0024gkgLZHT7ayvA_003D_003D(path)`.
- Entry names: `"Data"` = ZXGameState, `"Info"` = ZXGameStateInfo (string-id ↔ name mapping cross-checked
  between `ZX/ZXFile.cs` and the manager's Write call; also matches PLAN.md's sample-file inspection).
- `_0023_003DzpKDARrtdO1GK7shdnQ_003D_003D` (set) and `_0023_003DzvgSfu3ouG_TLllPQAA_003D_003D` (clear)
  **both** match TABSAT's `(String,Int32,Boolean)→Void` generator signature. TABSAT picks the first
  candidate that yields a non-empty `ZipSerializer.Current.Password` (i.e. the setter). Day0Gen prefers
  the exact names, falls back to the TABSAT probe.
- Signing `_0023_003DzX5dOGo9W_0024Dop(path, 2)`: mode 2 = byte-sum hash `Σ(b + b²/111)`, then delegated to
  `_0023_003Dz9QdntbHPY7Me(path, hash, mode)` — an Eazfuscator proxy stub that mixes in the path
  (consistent with the already-disproven pure-file-hash reproduction; do NOT try to reimplement).

## 3. Obfuscated identifiers — ILSpy escaping (important for runtime lookup)

The decompile shows identifiers like `_0023_003Dz4RevDP3eECXqXS6JRA_003D_003D`. These are ILSpy escapes:
`_XXXX` (4 hex digits) → char 0xXXXX. Real metadata names contain `#`, `=`, `$`. Examples:

- `_0023_003Dz4RevDP3eECXqXS6JRA_003D_003D` → `#=#z4RevDP3eECXqXS6JRA==`
- `zX5dOGo9W_0024Dop` → `zX5dOGo9W$Dop`
- `z4k5FO_0024EclQhr` → `z4k5FO$EclQhr`

Day0Gen stores the escaped literals (as they appear in vendor/decompiled) and `Unescape()`s them at
runtime before `GetType`/`GetMethod`. Escaping validated against `--zLs-VX1eoAapVflmW_w--.cs` whose class
is `_0023_003DzLs_0024VX1eoAapVflmW_w_003D_003D` (`$`, literal `_w`, `==`). Caveat: a literal `_XXXX`-looking
sequence in a name would be ambiguous, but Eazfuscator's charset makes that a non-issue for these names.

## 4. Construction-sequence verification (CC handler, `--zLs-VX1eoAapVflmW_w--.cs` ~750–790)

The SPEC sequence matches the CC handler **minus** the following lines, which Day0Gen deliberately skips:

- `gs.ChallengeType = CommunityChallenge` / `gs.ChallengeID = <id>` — never set (leaderboard safety).
- `DXLinq.AddIfNotPresent(GameAccount.<challenge-list>, id)` — account mutation, skipped.
- `mgr._0023_003DzmQetCvifY0eqPqP1AQ_003D_003D(save: false/true, ...)` — **account save**, skipped
  (writes `Account.zxuser` + a `-backup` copy; would violate the write allow-list).

Extra engine details discovered while wiring:

- `ZXLevelState.Set(ls)` writes **through** `ZXGameState.Current.LevelState` → `ZXGameState.Set(gs)`
  MUST happen first (Day0Gen order: GameState.Set → params → LevelState.Set → Init → Load → SetLevel).
- `ZXLevelState`'s ctor already calls `_0023_003DzF1YHTRUZSNRa()` (Init); the handlers call it **again**
  after Set — Day0Gen replicates the handler order exactly (double init is game-native).
- `ZXRandomLevelParams.FactorZombiePopulation` **defaults to 0.5f**, not 1f — CLI default 1.0 must be
  applied explicitly (it is).
- `ScoreFactor` getter has a side effect (mutates `ThemeType` None→BR). Day0Gen never touches it.
- `DifficultyType` left at default `None` — CC-handler parity.
- Enums: `ZXGameModeType.Survival=0`, `ZXMapThemeType.None=0`, `ZXGameChallengeType.Default=0`.

## 5. GameAccount safety

The `GameAccount` getter, when `Account.zxuser` is missing or unreadable, **creates a new account and
saves it** (`_0023_003DzmQetCvifY0eqPqP1AQ_003D_003D(save:true, ...)`). Day0Gen therefore verifies
`<root>\Account.zxuser` exists on disk BEFORE ever reading the property; if missing → abort
(no account file is ever created by us).

## 6. Tables / theme table

- Manager ctor runs `_0023_003Dz3Zxcp6RwVCZHa9xpeg_003D_003D._0023_003DzUoK3qsRYSJTT()` ("Tables Excel Read")
  and throws if `TableManagerDefinitions`/`TableManagerStrings` end up null → tables are loaded during
  zombie init; manager-singleton readiness implies tables (TABSAT precedent: reflector worked right after
  popup).
- `ZXMapTheme._0023_003Dz4k5FO_0024EclQhr()` is **lazy**: builds the Dictionary, then pulls each theme's
  properties from `TableManagerDefinitions` and cliff-piece templates from `DXProject.Current.EntityTemplates`.
  If it throws/empty, Day0Gen re-invokes the loader and re-checks (SPEC's fallback), then aborts if still bad.
- Nearly all bodies in this build are Eazfuscator **proxy stubs** (`_003CEazfuscator…CI_003E.c` runtime
  dispatcher with string keys) — nothing can be reimplemented out-of-process; everything must be invoked
  in the zombie. Confirms the TABSAT architecture is the only viable one.

## 7. Zombie init

- `ZX.Program.Main` is `internal static void Main(string[] args)` — invoked on a background **STA**
  thread with our original CLI args (TABSAT passed `{""}`; SPEC says pass ours). Error popup expected;
  minimized via `ShowWindow(SW_MINIMIZE)` (user32) whenever a main-window handle appears during the poll.
- Poll: `_0023_003Dzuartwoo_003D()` non-null within 60 s → then account checks. Timeout → dump ZXLog.txt
  tail (read-only) and abort.
- Process guard `TheyAreBillions`, write allow-list (`<name>.zxsav`, `<name>.zxcheck`, `Day0Gen.log`)
  and before/after SHA256 over saves dir + parent implemented per SPEC. **ZXLog.txt is allow-listed too**:
  the engine appends its own log during zombie init — outside our control (documented deviation; CC
  files/Account are NOT allow-listed and any touch fails the run).
- Assembly loading: run-from-TAB-dir assumed; if `AppDomain.BaseDirectory != --tab-dir`, an
  `AssemblyResolve` hook is installed instead of failing (log warning) — covers PLAN's writability risk.

## 8. Uncertainties / open risks (for P1/P2 on the target box)

1. **Headless `DXSystem.Load<gamesystem>(false)` + SetLevel while the engine sits in its error-popup
   message loop** is unproven (TABSAT only exercised signing/password machinery). If SetLevel stalls or
   corrupts state, options are: run construction on a thread pool work item via the engine, or accept
   popup → this is exactly what the phased rollout is for.
2. Zip entry names `"Info"`/`"Data"` in the manual fallback are hardcoded from call-site analysis, not
   runtime capture (the strings are runtime-decoded). High confidence; primary path (native writer)
   doesn't depend on them.
3. `--validate-signer` against `vendor/samples/COMMUNITY CHALLENGE.zxsav` must print `2.227699125761`
   (expected value from the sample's `.zxcheck`). First real P1 checkpoint.
4. If Steam client interaction under zombie init fails on the target (TABSAT suggests it works), the
   manager may not reach readiness → ZXLog tail will say so.
5. Build drift: if the installed game != v1.0.14, exact obfuscated names may differ. Discovery phase
   prints every match + strategy; signature-scan fallbacks exist for the critical ones; a manager-name
   mismatch prints a NOTE but does not abort (scan-based discovery still works).

## 9. Build verification on this Mac

`nix shell nixpkgs#dotnet-sdk` initially timed out (300 s) mid-download; a first interrupted attempt
left an incomplete store path whose host then failed SDK resolution (`No .NET SDKs found`). A detached
`nix build nixpkgs#dotnet-sdk` (182 MiB from cache.nixos.org) was started to complete the store paths.
Status: **PENDING — see below for final outcome.**

Fallback executed meanwhile: strict mechanical C#5 audit of `src/Day0Gen.cs`
(comments/strings stripped, then token scan + brace-balance check):

- No `?.`, `?[`, `$"`, `nameof`, `async`/`await`, `using static`, `??=`, `=>` (zero occurrences —
  anonymous methods used throughout), no inline `out` declarations, no pattern matching, no
  target-typed `new`.
- Braces/parens/brackets balanced.
- APIs used are all net48 `System.*` (no extra compile-time deps).

(Outcome: the dotnet-sdk download completed; `make build` compiles clean under `LangVersion 5` /
net48, and `make audit` reports no C#6+ constructs.)

## 10. DXVision assembly resolution — fixed after the first on-target run

First real `--phase discovery` on the target failed: `Assembly.Load("TheyAreBillions")` +
`GetTypes()` threw `ReflectionTypeLoadException` (1677 types total, **507 failed**), and the manager
class (owner of `get_GameAccount`) was among the failures → abort.

Root cause: `TheyAreBillions.exe` (v1.0.14.29) references a **separate** assembly
`DXVision, Version=1.0.0.0` that has **no file on disk**. It is embedded as a manifest resource and
decrypted/loaded by an Eazfuscator `AssemblyResolve` handler installed by the TheyAreBillions **module
initializer**. Discovery never executes any game code, so the resolver was never installed and every
DXVision-derived type failed to load. (`TabAssembly.GetType("DXVision.*")` cannot find them either —
they live in the separate assembly's metadata, not in `TheyAreBillions.exe`.)

Fix (in `GameReflector.LoadAssemblies()`): load TheyAreBillions, then
`RuntimeHelpers.RunModuleConstructor(TabAssembly.ManifestModule.ModuleHandle)` to install the resolver,
then `Assembly.Load("DXVision")`. Verified live: all 1677 types load; `DXVision.DXLevel`,
`DXVision.DXSystem`, `DXVision.Serialization.ZipSerializer` all resolve; the module initializer does
**not** start the game.

Resolver source in the decompile:
`vendor/decompiled/--qfkZ-KkimwzG_5GjOFAkJH7Cbg6LrXJCCJODXq7ULSO4-.cs`
(`AppDomain.CurrentDomain.AssemblyResolve += …`, registered by `_0023_003DzX6exa18_003D()`).

With this fix, `--phase discovery` completes on the target: every SPEC reflection target found
(manager, signer, password flag/setter/clearer, native save writer, saves-folder, save list,
ZXGameState/ZXLevelState Set/Current/Init, ZXRandomLevelParams, generator, theme table + loader,
game system + SetLevel, DXSystem.Load<T>, ZipSerializer.Read/Write, ZXFile<T> read).
