# minacalc-standalone

A standalone, dependency-free build of Etterna's MinaCalc difficulty
calculator (`src/Etterna/MinaCalc` from the
[etterna](https://github.com/etternagame/etterna) repo), exposed as a
small command-line tool (`msd` / `msd.exe`) instead of a library linked
into the game engine.

It reads a chart as JSON on stdin and prints the resulting MSD skillsets
as JSON on stdout — see `src/main.cpp` for the exact contract. This is
what `MinterludeCalc.Core/MinaCalc.cs` shells out to.

## What's in here

- `src/Etterna/MinaCalc/` — the calc engine itself, copied verbatim from
  upstream except for one small patch (see below).
- `src/Etterna/Models/NoteData/NoteDataStructures.h` — the one header the
  calc depends on from outside its own folder (defines `NoteInfo`).
- `src/main.cpp` — the CLI wrapper: argument parsing, a minimal built-in
  JSON reader/writer (no external JSON library needed), and the call into
  `MinaSDCalc`.
- `CMakeLists.txt` — builds everything with `-DSTANDALONE_CALC`, which is
  a define upstream already supports for stripping out the parts of the
  calc that talk to the rest of the game engine (debug XML param dumping,
  Lua bindings, etc).

## The one upstream patch

`Calc::InitializeHands` in `MinaCalc.cpp` had a debug-only branch
(`if (debugmode || loadparams)`) that calls `load_calc_params_from_disk`,
a method that's `#if !defined(STANDALONE_CALC)`-guarded out of existence
everywhere else in the same file. That's a gap in `STANDALONE_CALC`
support, not something this project changes on purpose: the branch is
dead code here either way (`debugmode`/`loadparams` are never set to
`true`), so it's just wrapped in the same guard so it compiles. No output
is affected — verified by comparing Linux and Windows output on identical
input (see below).

## Building

**Linux** (needs `cmake` + a C++ compiler):
```
./build-linux.sh
```

**Windows, cross-compiled from Linux/WSL** (needs `cmake` +
`mingw-w64`, e.g. `apt install cmake mingw-w64`):
```
./build-windows.sh
```

**Windows, natively** (needs `cmake` + Visual Studio's C++ build tools,
run from a "Developer Command Prompt" or with them on PATH):
```
build-windows.bat
```

Each script drops the result straight into `Tools/win-x64/msd.exe` or
`Tools/linux-x64/msd` at the solution root, where
`MinaCalc.DefaultToolPath()` expects it.

## Verifying a build

```
echo '[{"notes":1,"time":0.5},{"notes":2,"time":0.75}]' | ./msd --goal 0.93 --keys 4
# {"Overall":...,"Stream":...,"Jumpstream":...,"Handstream":...,"Stamina":...,"JackSpeed":...,"Chordjack":...,"Technical":...}
```

The Windows and Linux builds in this repo were checked against each
other (Windows binary run under Wine) on the same generated chart and
produce byte-identical MSD output, so both are exercising the same calc
logic — not just "it compiles."

## Not included: macOS

There's no bundled macOS build. Cross-compiling a real, signed Mach-O
binary needs Apple's SDK (extracted from Xcode), which isn't something
that can be fetched or redistributed from a Linux build environment. The
source here is portable, ordinary C++17/20 with no Linux/Windows-specific
code, so `cmake -S . -B build && cmake --build build` on an actual Mac
with Xcode command line tools installed should produce a working `msd`
with no changes needed — just no pre-built binary is shipped for it.
