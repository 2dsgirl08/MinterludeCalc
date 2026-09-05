#!/usr/bin/env bash
# Builds the standalone msd tool for Linux and drops it straight into
# Tools/linux-x64/msd, where MinaCalc.DefaultToolPath() expects it.
set -euo pipefail
cd "$(dirname "$0")"

rm -rf build-linux
cmake -S . -B build-linux -DCMAKE_BUILD_TYPE=Release
cmake --build build-linux -j"$(nproc)"

mkdir -p ../../Tools/linux-x64
cp build-linux/msd ../../Tools/linux-x64/msd
chmod +x ../../Tools/linux-x64/msd
strip ../../Tools/linux-x64/msd || true

echo "Built Tools/linux-x64/msd"
