#!/usr/bin/env bash
# Publishes the sidecar as one self-contained program for one runtime identifier:
#   ./sidecar/publish.sh                 win-x64  -> <repo>/artifacts/sidecar/spectuner-sidecar.exe
#   ./sidecar/publish.sh linux-x64       linux    -> <repo>/artifacts/sidecar/linux-x64/spectuner-sidecar
#   ./sidecar/publish.sh osx-arm64       macOS    -> <repo>/artifacts/sidecar/osx-arm64/spectuner-sidecar
#   ./sidecar/publish.sh osx-x64         macOS    -> <repo>/artifacts/sidecar/osx-x64/spectuner-sidecar
# Works from any directory. The Windows output stays where other scripts read it.
set -euo pipefail

rid="${1:-win-x64}"
case "$rid" in
  win-x64|linux-x64|osx-arm64|osx-x64) ;;
  *) echo "Unsupported runtime identifier: $rid (use win-x64, linux-x64, osx-arm64 or osx-x64)" >&2; exit 2 ;;
esac

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ "$rid" = "win-x64" ]; then
  out="$here/../artifacts/sidecar"
  name="spectuner-sidecar.exe"
else
  out="$here/../artifacts/sidecar/$rid"
  name="spectuner-sidecar"
fi

dotnet publish "$here/src/SpecTuner.Sidecar/SpecTuner.Sidecar.csproj" \
  -c Release \
  -r "$rid" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false \
  -p:DebugType=none \
  -o "$out"

echo "Published: $(cd "$out" && pwd)/$name"
