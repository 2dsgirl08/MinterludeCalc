# MinterludeCalc

MinterludeCalc is an overlay and rating calculator for Interlude, built around three projects:

* **MinterludeCalc.Core** — Core logic, replay scoring, rating calculation, chart reading, profiles, caching, and memory-reading.
* **MinterludeCalc.Console** — Headless console application for testing and debugging.
* **MinterludeCalc.Overlay** — Avalonia-based always-on-top overlay for use alongside the game.

It includes GC-safe ClrMD process reading, replay parsing, and direct `charts.db` / `scores.db` support.

## Setup

1. Run `dotnet restore` and `dotnet build` from the solution root.
2. Run **MinterludeCalc.Console** first to verify everything works.
3. Run **MinterludeCalc.Overlay** once the console version is working.

The bundled `msd` MinaCalc binaries are located in `Tools/` and automatically copied to each project's output directory.

## Features

* SC J4 replay scoring and accuracy calculation
* Etterna-style player rating calculation
* MinaCalc difficulty calculation
* Live Interlude chart/rate reading
* Multiple profiles
* Persistent score/result caching
* Rating history and top-play tracking

Difficulty goals use MinaCalc's empirically tuned model; currently only **SC J4** scoring is implemented.

The overlay currently uses the standalone MinaCalc executable; a managed MinaCalc implementation can be added through the `IDifficultyCalculator` interface.
