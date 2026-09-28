# TAB day-0 map replay — PLAN

Goal: replay the They Are Billions (TAB v1.0.14, Steam) weekly Community Challenge map
(seed **550040233**, week of 2026-09-28) from day 0, as a **regular survival save**,
without touching the Community Challenge (CC) save or leaderboards.

## Background / user situation

- User started this week's CC, made a mistake ~15 min in (half the base infected), quit.
- Game state now: CC save `COMMUNITY CHALLENGE.zxsav` exists (15-min-in state),
  leaderboard entry is "in progress" (see mechanics below), Play button = "Continue".
- User does not care about leaderboards. Wants: same map, fresh start.
- Chosen approach (user decision): **generate a new day-0 survival save with seed 550040233**,
  loadable from the normal Load Game menu. CC files remain untouched.

## Verified game mechanics (decompiled TheyAreBillions.exe v1.0.14 + live tests)

Sources: ILSpy decompile in `vendor/decompiled/`, TABSAT source in `vendor/TABSAT/`,
live ZXLog/session observation on target-host (Windows gaming PC), web research (scout report).

### Weekly challenge
- Challenge ID = string derived from current week start date (7-day cycle from fixed anchor).
- Weekly seed = `DXHelper_HashCode.From(ChallengeID)` → this week **550040233** (confirmed in ZXLog:
  "Random Map Creation with seed: 550040233"). 100% local, deterministic, same for all players.
- CC map = survival random map: `ZXRandomLevelParams { Seed=550040233, NCells=256,
  ThemeType=None, FactorGameDuration=1, FactorZombiePopulation=1, DifficultyType=None(default), Name=<challenge> }`.
  ThemeType None → generator picks theme deterministically from the seeded RNG.
- Starting a CC game uploads a Steam-leaderboard marker: score=0, ExtraData={-1,-1}.
- On finish (win or death): uploads `{NDaysSurviving>=0, Won?1:0}`.
- Challenge-screen Play button state machine:
  - `UserHasScore==NotPlayed` → enabled "Play" → NEW game.
  - `Played && ExtraData[0]<0 && save file exists` → enabled "Continue" → loads save.
  - `Played && ExtraData[0]<0 && save missing` → **greyed** (live-confirmed by user experiment).
  - `Played && ExtraData[0]>=0` (finished) → **greyed permanently for the week**.
  - Leaderboard object null (offline) → **greyed**.
- Consequences (dead ends, verified): deleting the CC save breaks the button; dying bricks the
  week; offline doesn't help; no seed input exists in any UI (survival/custom/editor);
  survival seed is always `DXRandom.Global.NextSignedInt()` at click time.

### Save file format
- Saves dir: `%USERPROFILE%\Documents\My Games\They Are Billions\Saves\`.
- `Account.zxuser` (unencrypted zip): entries `Data` (XML→Base64 of `ZX.ZXGameAccount`) + `Check` (hash).
- `<name>.zxsav` (password-encrypted zip): entries `Info` (`ZX.ZXGameStateInfo`) + `Data` (XML→Base64 of ZXGameState).
- `<name>.zxcheck`: ASCII `"2.<number>"` = signing method `(file, 2)`.
- Writer: `ZX.ZXFile<T>._0023_003DzxjEnyeUCASiC(path)` → `ZipSerializer.WriteCrypted(path, [Info, Data])`.
- Password: derived per-file from the game account via flag/generator methods (TABSAT reflector pattern).
- Serialization: SharpSerializer XML → Base64; integrity hash = `DXHelper_HashCode.From(xml)`.
- Steam Auto-Cloud syncs the whole `My Games\They Are Billions` folder on game start/exit.
- SteamID3 36442315; game install: `C:\Program Files (x86)\Steam\steamapps\common\They Are Billions\`.

### Zombie-reflector precedent (TABSAT)
- TABSAT runs a "zombie" TAB instance: loads `TheyAreBillions` assembly into its own process,
  invokes `ZX.Program.Main(args)` (engine+account init; shows harmless popup), then serves
  reflection requests: password generation + zxcheck signing. Proven to work for save I/O.

## Approach: `Day0Gen` tool

A single-file C# console tool (`.NET Framework 4.x`, C# 5 compatible) that:
1. Verifies the game is not running; refuses unsafe operations (see Safety).
2. Loads the TAB assembly (zombie mode, TABSAT pattern).
3. Discovers all reflection targets (by public name where possible, by signature otherwise).
4. Constructs a fresh survival game state mirroring the game's own survival-start sequence,
   but with `Seed=550040233` and CC-equal params (theme None).
5. Generates the level with the game's own generator, wires it like the game does.
6. Writes `Saves/<name>.zxsav` (+ `.zxcheck`) using the game's own writer/signer.
7. Verifies by re-reading the written save through the game's own reader.

Then (manual, by main agent): user launches game → Load Game → plays the day-0 map.

## Phases

- **P0 — dry run:** discovery only; prints reflection map; no writes. Run on target-host via SSH.
- **P1 — zombie init:** engine init, account read, password derivation for a dummy path; no writes.
- **P2 — full run:** generate + write + verify new save files only.
- **P3 — acceptance:** CC files byte-identical before/after (SHA256); user loads new save in game,
  confirms terrain matches CC map, day counter at start, save/reload works.

## Safety constraints (non-negotiable)

- NEVER read-modify-write or delete: `COMMUNITY CHALLENGE.*`, `Account*.zxuser`, any other save.
- Only new files: `<newname>.zxsav`, `<newname>.zxcheck`. Refuse if target exists.
- Refuse to run while `TheyAreBillions.exe` is running.
- Full logging (console + `Day0Gen.log`).
- All CC-file SHA256 hashes recorded before/after every run.
- No leaderboard/challenge calls in the constructed state (ChallengeType stays Default).
- User-facing risk disclosure: patch 0.6.1 mentions CC-cheater bans; we do not touch CC data,
  but running the game binary in zombie mode is inherently detectable; accepted by user (leaderboards not cared about).

## Risks / unknowns (iterate empirically)

- Zombie init depth: tables ("Tables Excel Read") may not load under zombie args → generator may
  need a manual table-load invocation first.
- `DXSystem.Load<T>(false)` + `SetLevel` headless behavior unknown; save-state validity before
  mayor selection unproven until P2/P3.
- Program Files writability for the tool exe (likely user-writable via Steam; else run from
  another cwd with AssemblyResolve hook).
- Steam client interaction while zombie runs.

## Layout

- `PLAN.md` — this file. `SPEC.md` — implementation contract for the worker.
- `src/Day0Gen.cs` — the tool. `notes/` — research notes.
- `vendor/decompiled/` — ILSpy decompile of the exact installed game version (name-map source of truth).
- `vendor/TABSAT/` — upstream TABSAT source (reflector pattern reference).
- `vendor/samples/` — copies of Account.zxuser (unencrypted reference), the CC save pair, decoded account XML.
