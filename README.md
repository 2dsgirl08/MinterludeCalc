# MinterludeCalc Overlay

Three projects, one solution:

- **MinterludeCalc.Core** - all the logic. No UI. This is where everything new lives.
- **MinterludeCalc.Console** - the original console app, rebuilt on top of `OverlayService`. Useful as a quick sanity check / headless test harness.
- **MinterludeCalc.Overlay** - the new Avalonia UI. A small always-on-top window meant to sit beside/over the game.

## Setup

1. Build the standalone `msd` tool from the earlier `minacalc-standalone` project and drop it at `Tools/msd.exe` (or `Tools/msd` on Linux/Mac) next to whichever app you're running - `MinaCalc.cs` now passes `--goal` and `--keys` through to it, so make sure you're using the updated `main.cpp` that reads those flags.
2. `dotnet restore` then `dotnet build` at the solution root, or open `MinterludeCalc.sln`.
3. Run `MinterludeCalc.Console` first - it's much easier to debug from a terminal than a floating window, and exercises the exact same `OverlayService`/`PlayerRatingService` code the overlay uses.
4. Once that's behaving, run `MinterludeCalc.Overlay`.

## What's actually new here

### `Scoring/ScoringEngine.cs` - SC J4 replay scorer

Interlude's `scores.db` only stores raw input (gzip'd keypress frames), not an accuracy percentage - see `Replay.fs`. To get a real "% under SC J4" for a play, something has to replay it against the chart and Interlude's own hit-judging logic. This is a from-scratch C# port of:

- `GameplayEventProcessor.fs` (raw input -> hit/miss/hold events)
- `HitMechanics.fs` -> `HitMechanics.interlude` (the note-priority/candidate-matching algorithm SC uses)
- `ScoreProcessor.fs` (events -> judgements -> points, for SC's specific `PointsPerJudgement` + `CombineHeadAndTail(HeadJudgementOr)` configuration)
- `SC.fs` (the actual judge-window/point constants)

**This was ported by hand, line-by-line against the F# source, and could not be compiled or tested in the environment I built it in (no .NET SDK available there).** Before trusting its numbers:

- Run it against a handful of your own real scores and sanity-check the resulting accuracy % against what Interlude itself shows for that same play (its score screen / replay viewer). They should match closely.
- The known, deliberate scope cut: **combo/max-combo/combo-breaks/lamps are not tracked at all** - they don't affect the accuracy percentage (confirmed from `ScoreProcessor.fs`: `Accuracy = points_scored / max_possible_points`, entirely separate from combo bookkeeping), and accuracy is the only thing this pipeline needs. If you later want lamps ("Full Combo", etc.) or a proper score-screen replica, that combo logic would need adding back in - it's a much smaller amount of code than the judging engine itself.
- Only SC J4 is implemented (hardcoded, not a generic ruleset interpreter). `ScJ4Ruleset` takes a `judge` parameter and the judge-scaling formulas are general, so other judges (J1-J9) should work, but only SC has been ported - not Wife3/osu!mania/Quaver rulesets.

### `PlayerRating.cs` - Etterna's actual rating formula

Pulled directly from `ScoreManager.cpp` (`AggregateSSRs`/`AggregateSkillsets`/`CalcPlayerRating`) - **not** a "top N scores, decayed weight" formula (that was my first guess, and it was wrong). It's an iterative equilibrium search using `erfc`. `.NET` has no built-in `erf`/`erfc`, so this uses the standard Abramowitz & Stegun approximation (~1.5e-7 max error - plenty accurate here).

One real design decision worth double-checking against your own preferences: **Etterna keeps a personal-best entry per chart, and all 7 skillset SSRs for that chart's contribution come from that single best play** (confirmed via `GetAllPBPtrs()`/`TopSSRs` in the source) - it does **not** mix-and-match your best Stream play on a chart with your best Jack play on the same chart if they were different attempts. `PlayerRatingService` picks "best play per chart" by raw accuracy %, which is the natural analogue since Interlude doesn't have StepMania's "chord cohesion" concept that Etterna uses to justify keeping two separate PBs (nocc/cc) per chart.

### The "95% SC J4" difficulty number

Important nuance, explained in `OverlayService.DifficultyGoal`'s doc comment: MinaCalc's `--goal` parameter is **not** a literal implementation of any scoring system's formula (checked directly in `Calc::Chisel` in the C++ source - it searches an abstract, empirically-tuned point-loss model, calibrated against real Wife3 J4 player data, with no knowledge of Wife3 or SC's actual math). So "95%" here means "a stricter threshold in that same abstract model," not "the skill needed to reach 95% under SC's real judgement bands." This is fine as a practical convention (Etterna itself computes per-play SSR the exact same way, just with the *player's actual* percentage as the goal - see `PlayerRatingService`), just don't read more precision into the number than it has.

### `Replay.cs`, `ScoresDatabaseReader.cs`, `ChartNoteData.cs`

Straightforward binary-format ports (gzip'd frame stream; SQLite row reads; the chart Notes/BPM/SV blob) - lower risk than the scoring engine, but still worth a first-run sanity check since none of it could be compiled here either.

### `ChartReader.cs` - why nothing it caches is trusted

Every address the memory reader resolves (the rate getter, the selected-chart
ref cell, each `ChartMeta`) is a raw heap address inside a *running* .NET
process, and Interlude's GC compacts. A gen2 collection moves all of them at
once - most likely while the game is idle in the background, since that's when
it has time for a full blocking collection.

So each read re-checks that the object still at the cached address is of the
type it was resolved as, and re-resolves (rate-limited, since that means a heap
walk) when it isn't. Anything cached without that check turns one background GC
into a permanently wedged reader: it goes on reading whatever now occupies the
old address and never recovers, which shows up as the overlay freezing and no
longer following song select - typically noticed just after tabbing back in.
`ClrType`/`ClrInstanceField` handles have the same problem for a different
reason: `FlushCachedData()` invalidates them, so only plain field *offsets*
(fixed per type) are kept between calls.

The overlay layers its own recovery on top: a run of failed reads drops the
resolved addresses, and a longer one (or Interlude exiting) rebuilds the reader
from scratch, so closing and reopening the game doesn't need an overlay restart.

## Known rough edges to expect

- **`OverlayService.ComputePlayerRating` recomputes from every score in your library, every time.** Fine for a first pass / small libraries, but it rescoring every replay you've ever set is real work. The natural next step is caching each chart's best-play SSR (`PlayScoreResult`) keyed by chart hash + score id, and only recomputing entries touched by a new score - `PlayerRatingService.AggregateFromBestPlays` is already split out separately for exactly this reason.
- **New-score detection polls `MAX(Id)` on `scores.db` every 500ms.** Works, but if you'd rather this be event-driven, a `FileSystemWatcher` on `scores.db` would be more responsive than polling.
- **Difficulty numbers arrive a moment after the song title.** MinaCalc runs out-of-process and can take seconds, so the overlay shows the new chart immediately and fills in the skillset values when `msd` returns (the console app still does it inline). A chart+rate that fails is not retried for 30s, to keep one bad chart from stalling the loop.
- **The selected-chart ref is identified by value**, i.e. the first `FSharpRef<string>` on the heap whose contents is a hash we know. If Interlude ever holds two such refs, this can latch onto the wrong one; the type-validation above catches a *stale* ref but not a wrong-but-live one.
- The overlay window's chrome/positioning (`MainWindow.axaml`) is intentionally plain - it's meant to be a starting point for you to reskin, not a finished design.
- Live process reading (`ChartReader`, via ClrMD) works best on Windows; attaching to a live .NET process from another process on Linux/macOS can hit permission/ptrace quirks that don't come up on Windows. The rest of the app (UI, `charts.db`, `scores.db`) is genuinely cross-platform.
