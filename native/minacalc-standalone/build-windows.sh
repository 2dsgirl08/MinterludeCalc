#!/usr/bin/env bash
# Cross-compiles the standalone msd tool for Windows (x86_64) using
# mingw-w64, and drops it into Tools/win-x64/msd.exe, where
# MinaCalc.DefaultToolPath() expects it.
#
# Requires: cmake, x86_64-w64-mingw32-g++ (e.g. `apt install mingw-w64`
# on Debian/Ubuntu, or `pacman -S mingw-w64-gcc` on Arch).
set -euo pipefail
cd "$(dirname "$0")"

rm -rf build-windows
cmake -S . -B build-windows \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE=toolchains/mingw-w64-x86_64.cmake
cmake --build build-windows -j"$(nproc)"

mkdir -p ../../Tools/win-x64
cp build-windows/msd.exe ../../Tools/win-x64/msd.exe
x86_64-w64-mingw32-strip ../../Tools/win-x64/msd.exe || true

echo "Built Tools/win-x64/msd.exe"
