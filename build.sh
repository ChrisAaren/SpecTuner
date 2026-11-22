#!/usr/bin/env bash
# Builds the SpecTuner desktop app from this checkout and prints the packaged exe's absolute path on the last line.
#
#   ./build.sh [--skip-sidecar] [--skip-cli] [--installer]
#
#   --skip-sidecar  use artifacts/sidecar/spectuner-sidecar.exe as it is (or SPECTUNER_SIDECAR) instead of publishing it
#   --skip-cli      reuse the server program already in app/packages/cli/dist (it must exist)
#   --installer     also build the NSIS installer (app/dist/desktop/spectuner-desktop-win-x64.exe)
#
# Environment: SPECTUNER_VERSION overrides the version stamped into the server program and the app (default
# 2.3.0-spectuner.<UTC yyyymmddhhmm>, unique per build so a new build replaces the previous build's background service).
# Stops at the first failure. Run from Git Bash on Windows x64.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
code="$root/app"
desktop="$code/packages/desktop"
cli="$code/packages/cli"
cli_dist="$cli/dist/cli-windows-x64-baseline"

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
  "$root/sidecar/publish.sh"
fi

step "Staging SpecTuner resources"
bun "$desktop/script/stage-spectuner.ts"

if [ "$skip_cli" = 0 ]; then
  version="${SPECTUNER_VERSION:-2.3.0-spectuner.$(date -u +%Y%m%d%H%M)}"
  step "Building the server program $version"
  # Channel `latest`: the registration file is service.json and the database spectuner.db (under SpecTuner's own
  # folders), and experimental flags default off. Without an explicit channel the build would take the git branch name.
  rm -rf "$cli_dist"
  (cd "$cli" && SPECTUNER_CHANNEL=latest SPECTUNER_VERSION="$version" \
    bun run script/build.ts --single --baseline --skip-web-ui --skip-install)
fi

[ -f "$cli_dist/bin/spectuner.exe" ] || { echo "the server program is missing: $cli_dist/bin/spectuner.exe" >&2; exit 1; }
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
exe="$app/SpecTuner.exe"
[ -f "$exe" ] || { echo "packaged app missing: $exe" >&2; exit 1; }
[ "$(cat "$app/resources/spectuner-server.version")" = "$version" ] || { echo "packaged server program is not $version" >&2; exit 1; }

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
