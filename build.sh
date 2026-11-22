#!/usr/bin/env bash
# Builds the SpecTuner desktop app from this checkout and prints the packaged app's absolute path on the last line.
#
#   ./build.sh [--skip-sidecar] [--skip-cli] [--installer]
#
#   --skip-sidecar  use the published sidecar as it is (or SPECTUNER_SIDECAR) instead of publishing it:
#                   artifacts/sidecar/spectuner-sidecar.exe on Windows, artifacts/sidecar/<rid>/spectuner-sidecar elsewhere
#   --skip-cli      reuse the server program already in app/packages/cli/dist (it must exist)
#   --installer     Windows: also build the NSIS installer (app/dist/desktop/spectuner-desktop-win-x64.exe).
#                   Linux and macOS always build their packages (AppImage and deb; dmg and zip), so it changes nothing
#
# Environment: SPECTUNER_VERSION overrides the version stamped into the server program and the app (default
# 2.3.0-spectuner.<UTC yyyymmddhhmm>, unique per build so a new build replaces the previous build's background service).
# Stops at the first failure. Runs from Git Bash on Windows x64, and on Linux x64 and macOS on Apple Silicon.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
code="$root/app"
desktop="$code/packages/desktop"
cli="$code/packages/cli"

# The system decides the server program's variant, the sidecar's runtime and the packaged app's layout. Windows keeps
# exactly the steps it always had.
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) system=windows ;;
  Linux) system=linux ;;
  Darwin) system=mac ;;
  *) echo "unsupported system: $(uname -s)" >&2; exit 2 ;;
esac
case "$system/$(uname -m)" in
  windows/*) ;;
  linux/x86_64) ;;
  mac/arm64) ;;
  *) echo "SpecTuner builds on Windows x64, Linux x64 and macOS on Apple Silicon, not $system/$(uname -m)" >&2; exit 2 ;;
esac
case "$system" in
  windows)
    cli_dist="$cli/dist/cli-windows-x64-baseline"
    cli_exe="$cli_dist/bin/spectuner.exe"
    ;;
  linux)
    cli_dist="$cli/dist/cli-linux-x64-baseline"
    cli_exe="$cli_dist/bin/spectuner"
    cli_target=linux-x64-baseline
    sidecar_rid=linux-x64
    ;;
  mac)
    cli_dist="$cli/dist/cli-darwin-arm64"
    cli_exe="$cli_dist/bin/spectuner"
    cli_target=darwin-arm64
    sidecar_rid=osx-arm64
    ;;
esac

skip_sidecar=0
skip_cli=0
installer=0
for arg in "$@"; do
  case "$arg" in
    --skip-sidecar) skip_sidecar=1 ;;
    --skip-cli) skip_cli=1 ;;
    --installer) installer=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

step() { printf '\n==> %s\n' "$*"; }

# The desktop build picks its server program by target; a leftover SPECTUNER_TARGET (for example windows-x64-baseline from a CLI
# build) would make it look for the wrong package. The channel is set explicitly for each step below.
# SPECTUNER_VERSION is not cleared: it is this script's own version override (read below), and every step is given
# the version explicitly.
unset SPECTUNER_TARGET SPECTUNER_CHANNEL

if [ "$skip_sidecar" = 0 ]; then
  step "Publishing the sidecar"
  if [ "$system" = windows ]; then
    "$root/sidecar/publish.sh"
  else
    "$root/sidecar/publish.sh" "$sidecar_rid"
  fi
fi

step "Staging SpecTuner resources"
bun "$desktop/script/stage-spectuner.ts"

if [ "$skip_cli" = 0 ]; then
  version="${SPECTUNER_VERSION:-2.3.0-spectuner.$(date -u +%Y%m%d%H%M)}"
  step "Building the server program $version"
  # Channel `latest`: the registration file is service.json and the database spectuner.db (under SpecTuner's own
  # folders), and experimental flags default off. Without an explicit channel the build would take the git branch name.
  rm -rf "$cli_dist"
  if [ "$system" = windows ]; then
    (cd "$cli" && SPECTUNER_CHANNEL=latest SPECTUNER_VERSION="$version" \
      bun run script/build.ts --single --baseline --skip-web-ui --skip-install)
  else
    # Only the variant the app bundles (on Linux the baseline one, for processors without AVX2).
    (cd "$cli" && SPECTUNER_CHANNEL=latest SPECTUNER_VERSION="$version" SPECTUNER_TARGET="$cli_target" \
      bun run script/build.ts --skip-web-ui --skip-install)
  fi
fi

[ -f "$cli_exe" ] || { echo "the server program is missing: $cli_exe" >&2; exit 1; }
version="$(bun -e 'console.log((await Bun.file(process.argv[1]).json()).version)' "$cli_dist/package.json")"
[ -n "$version" ] || { echo "the server program has no version: $cli_dist/package.json" >&2; exit 1; }
if [ "$skip_cli" = 0 ] && [ -n "${SPECTUNER_VERSION:-}" ] && [ "$version" != "$SPECTUNER_VERSION" ]; then
  echo "built server program version $version, expected $SPECTUNER_VERSION" >&2
  exit 1
fi

step "Building the desktop app $version"
(cd "$desktop" && SPECTUNER_CHANNEL=prod SPECTUNER_VERSION="$version" bun run build)

step "Packing the desktop app"
(cd "$desktop" && SPECTUNER_CHANNEL=prod SPECTUNER_VERSION="$version" bun run pack)

app="$code/dist/desktop/app"
case "$system" in
  windows)
    exe="$app/SpecTuner.exe"
    resources="$app/resources"
    ;;
  linux)
    # The prod app ID com.spectuner.desktop without its `.desktop` ending (electron-builder.config.ts).
    exe="$app/com.spectuner"
    resources="$app/resources"
    ;;
  mac)
    exe="$app/SpecTuner.app"
    resources="$exe/Contents/Resources"
    ;;
esac
[ -e "$exe" ] || { echo "packaged app missing: $exe" >&2; exit 1; }
[ "$(cat "$resources/spectuner-server.version")" = "$version" ] || { echo "packaged server program is not $version" >&2; exit 1; }

if [ "$system" != windows ]; then
  # The packages pack.ts copied next to the app; print them for the person or workflow running this.
  for file in "$code/dist/desktop"/*; do
    [ -f "$file" ] && echo "Package: $file"
  done
  echo "$exe"
  exit 0
fi

if [ "$installer" = 1 ]; then
  step "Building the installer"
  # pack.ts empties dist/desktop first, so the installer is built from a copy of the unpacked app.
  staged="$root/tmp/installer-app"
  rm -rf "$staged"
  mkdir -p "$root/tmp"
  cp -r "$app" "$staged"
  (cd "$desktop" && SPECTUNER_CHANNEL=prod SPECTUNER_VERSION="$version" SPECTUNER_DESKTOP_APP="$staged" bun run pack)
  cp -r "$staged" "$app"
  rm -rf "$staged"
  echo "Installer: $(cygpath -w "$code/dist/desktop/spectuner-desktop-win-x64.exe")"
fi

cygpath -w "$exe"
