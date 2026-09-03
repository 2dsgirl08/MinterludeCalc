# MinterludeCalc

MinterludeCalc is an Interlude overlay that calculates **SC J4 accuracy, chart difficulty, and Etterna-style player ratings**.
- **MinterludeCalc.Core** — Core scoring, rating, replay, chart, and memory-reading logic.
- **MinterludeCalc.Console** — Headless testing frontend using the same core services.
- **MinterludeCalc.Overlay** — Avalonia always-on-top overlay for displaying results in-game.
- Includes GC-safe ClrMD process reading, replay parsing, and direct `charts.db` / `scores.db` support.
- Difficulty goals use MinaCalc's empirically tuned model; currently only **SC J4** scoring is implemented.
