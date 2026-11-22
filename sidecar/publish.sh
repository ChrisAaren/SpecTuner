#!/usr/bin/env bash
# Publishes the sidecar as one self-contained exe:
#   <repo>/artifacts/sidecar/spectuner-sidecar.exe
# Run from Git Bash: ./sidecar/publish.sh   (works from any directory)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$here/../artifacts/sidecar"

dotnet publish "$here/src/SpecTuner.Sidecar/SpecTuner.Sidecar.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false \
  -p:DebugType=none \
  -o "$out"

echo "Published: $(cd "$out" && pwd)/spectuner-sidecar.exe"
