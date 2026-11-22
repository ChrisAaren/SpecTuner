#!/usr/bin/env bash
# Checks the app ./build.sh packaged in app/dist/desktop, on the system it was built on, and prints what it finds.
#
#   scripts/check-build.sh [--version VERSION] [--start-app]
#
#   --version    the version the build must carry (default: whatever the packaged server program says)
#   --start-app  also start the packaged app and wait until its server answers on 127.0.0.1. Only for a machine with no
#                SpecTuner installed (a CI runner): the app uses the installed app's profile, port and background
#                service. On Linux it runs the app under xvfb-run; it may ask sudo to set up Electron's sandbox helper.
#
# Every check runs even when an earlier one failed. Exits 1 if any required check failed. Runs from Git Bash on Windows,
# and on Linux and macOS.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dist="$root/app/dist/desktop"
app="$dist/app"

expected=""
start_app=0
while [ $# -gt 0 ]; do
  case "$1" in
    --version) expected="$2"; shift 2 ;;
    --start-app) start_app=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) system=windows ;;
  Linux) system=linux ;;
  Darwin) system=mac ;;
  *) echo "unsupported system: $(uname -s)" >&2; exit 2 ;;
esac

case "$system" in
  windows)
    resources="$app/resources"
    server="$resources/spectuner-server.exe"
    sidecar="$resources/spectuner/spectuner-sidecar.exe"
    launch="$app/SpecTuner.exe"
    ;;
  linux)
    resources="$app/resources"
    server="$resources/spectuner-server"
    sidecar="$resources/spectuner/spectuner-sidecar"
    launch="$app/com.spectuner"
    ;;
  mac)
    resources="$app/SpecTuner.app/Contents/Resources"
    server="$resources/spectuner-server"
    sidecar="$resources/spectuner/spectuner-sidecar"
    launch="$app/SpecTuner.app/Contents/MacOS/SpecTuner"
    ;;
esac
plugin="$resources/spectuner/plugin"

# Paths handed to Windows programs (Bun, the server, the sidecar) must be Windows paths.
native() {
  if [ "$system" = windows ]; then cygpath -w "$1"; else printf '%s\n' "$1"; fi
}

failed=()
passed=()
section() { printf '\n==> %s\n' "$*"; }
result() {
  if [ "$2" = 0 ]; then
    passed+=("$1")
    echo "PASS: $1"
  else
    failed+=("$1")
    echo "FAIL: $1"
  fi
}

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

section "What was built"
echo "system: $system $(uname -m)"
for file in "$dist"/*; do
  [ -f "$file" ] || continue
  size=$(wc -c <"$file" | tr -d ' ')
  if command -v sha256sum >/dev/null; then sum=$(sha256sum "$file" | cut -d' ' -f1); else sum=$(shasum -a 256 "$file" | cut -d' ' -f1); fi
  echo "package: $(basename "$file")  $size bytes  sha256 $sum"
done
for file in "$launch" "$server" "$sidecar" "$plugin/index.ts" "$resources/spectuner/modder.txt" "$resources/spectuner-server.version"; do
  if [ -e "$file" ]; then echo "present: ${file#"$root"/}"; else echo "MISSING: ${file#"$root"/}"; fi
done
[ -e "$launch" ] && [ -f "$server" ] && [ -f "$sidecar" ] && [ -f "$plugin/index.ts" ]
result "the packaged app holds the app, the server program, the sidecar and the tool plugin" $?

if [ "$system" != windows ]; then
  [ -x "$server" ] && [ -x "$sidecar" ]
  result "the server program and the sidecar are executable" $?
fi

section "Server program --version"
packaged_version="$(cat "$resources/spectuner-server.version" 2>/dev/null)"
version_out="$("$server" --version 2>&1)"
code=$?
echo "spectuner-server --version: $version_out (exit $code)"
echo "spectuner-server.version:   $packaged_version"
# It prints `spectuner v<version>`.
reported="$(printf '%s' "$version_out" | tail -n 1 | tr -d '\r' | awk '{print $NF}' | sed 's/^v//')"
[ "$code" = 0 ] && [ "$reported" = "$packaged_version" ] && { [ -z "$expected" ] || [ "$packaged_version" = "$expected" ]; }
result "the server program runs and reports the packaged version${expected:+ $expected}" $?

section "Sidecar detect on an empty folder"
game="$scratch/Empty Game"
mkdir -p "$game"
game_json="$game"
[ "$system" = windows ] && game_json="$(cygpath -m "$game")"
detect_out="$(printf '{"gamePath":"%s"}' "$game_json" | "$sidecar" detect 2>&1)"
code=$?
echo "$detect_out"
echo "(exit $code)"
printf '%s' "$detect_out" | grep -Eq '"command"[[:space:]]*:[[:space:]]*"detect"' && printf '%s' "$detect_out" | grep -Eq '"ok"[[:space:]]*:'
result "the sidecar starts and answers detect with an envelope" $?

section "Server, plugin and sidecar together (modder/script/tools.ts --check)"
(cd "$root/app/packages/modder" && bun run script/tools.ts --check \
  --game "$(native "$game")" --home "$(native "$scratch/tools-home")" \
  --exe "$(native "$server")" --sidecar "$(native "$sidecar")" --plugin "$(native "$plugin")")
result "the packaged server offers the game tools and the packaged sidecar answers through it" $?

section "Server service lifecycle (cli/script/service-smoke.ts, informative)"
# The starting code base's smoke test. It also expects the embedded web UI, which build.sh leaves out, so its result
# is reported but does not fail this script.
if (cd "$root/app/packages/cli" && bun run script/service-smoke.ts "$(native "$server")"); then
  echo "INFO: service smoke test passed"
else
  echo "INFO: service smoke test failed (informative only)"
fi

if [ "$system" = mac ]; then
  section "Signatures (ad hoc; SpecTuner has no signing identity)"
  codesign --verify --deep --strict --verbose=2 "$app/SpecTuner.app"
  result "the app bundle's ad hoc signature and seal are valid" $?
  codesign --verify --verbose=2 "$server" && codesign --verify --verbose=2 "$sidecar"
  result "the server program and the sidecar are signed" $?
  codesign -dv "$app/SpecTuner.app" 2>&1 | sed 's/^/  /'
  echo "Gatekeeper's view (expected to reject an unsigned app; testers use Open Anyway):"
  spctl --assess --type execute --verbose "$app/SpecTuner.app" 2>&1 | sed 's/^/  /'
fi

if [ "$system" = linux ]; then
  section "Linux packages"
  appimage="$(ls "$dist"/*.AppImage 2>/dev/null | head -n 1)"
  deb="$(ls "$dist"/*.deb 2>/dev/null | head -n 1)"
  [ -n "$appimage" ] && [ -x "$appimage" ] && [ -n "$deb" ]
  result "an executable AppImage and a deb were built" $?
  if [ -n "$deb" ]; then
    dpkg-deb --info "$deb" | sed 's/^/  /'
    dpkg-deb --contents "$deb" | grep -E 'spectuner-server|spectuner-sidecar|\.desktop$|/icons/hicolor/' | sed 's/^/  /'
  fi
fi

if [ "$start_app" = 1 ]; then
  section "Start the packaged app and wait for its server"
  port=29524
  if [ "$system" != windows ]; then
    # The app's folders under a scratch home, so nothing of the runner's profile is used.
    export HOME="$scratch/home"
    mkdir -p "$HOME"
    unset XDG_DATA_HOME XDG_CONFIG_HOME XDG_CACHE_HOME XDG_STATE_HOME
  fi
  app_log="$scratch/app.log"
  case "$system" in
    linux)
      # As the deb's install script does: Chromium's sandbox helper must belong to root and carry the setuid bit.
      if [ -f "$app/chrome-sandbox" ] && sudo -n true 2>/dev/null; then
        sudo chown root:root "$app/chrome-sandbox" && sudo chmod 4755 "$app/chrome-sandbox"
      fi
      xvfb-run -a "$launch" >"$app_log" 2>&1 &
      ;;
    *) "$launch" >"$app_log" 2>&1 & ;;
  esac
  pid=$!
  answer=000
  for _ in $(seq 1 120); do
    answer="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/" 2>/dev/null)"
    [ "$answer" != 000 ] && [ -n "$answer" ] && break
    sleep 1
  done
  # The packaged server has no web page of its own, so "/" is an unknown address there. Its API asks for the
  # password: 401 without one shows that it is SpecTuner's server that answers, not something else on the port.
  api="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/api/session" 2>/dev/null)"
  echo "HTTP status from 127.0.0.1:$port: / $answer, /api/session without the password $api (expected 401)"
  [ "$api" = 401 ]
  ok=$?
  result "the packaged app starts and its server asks for the password on port $port" $ok
  if [ "$ok" != 0 ]; then
    echo "--- app output ---"
    tail -n 80 "$app_log" 2>/dev/null
    case "$system" in
      linux) logs="$HOME/.config/com.spectuner.desktop/logs"; folder="$HOME/.local/share/SpecTuner" ;;
      mac) logs="$HOME/Library/Application Support/com.spectuner.desktop/logs"; folder="$HOME/Library/Application Support/SpecTuner" ;;
      windows) logs="$APPDATA/com.spectuner.desktop/logs"; folder="$LOCALAPPDATA/SpecTuner" ;;
    esac
    for file in $(find "$logs" "$folder/app/data/spectuner/log" -type f 2>/dev/null | head -n 5); do
      echo "--- $file ---"
      tail -n 60 "$file"
    done
  fi
  # Stop the window and the background service it started (it outlives the window).
  kill "$pid" 2>/dev/null
  if [ "$system" = windows ]; then
    taskkill //F //IM SpecTuner.exe >/dev/null 2>&1
    taskkill //F //IM spectuner-server.exe >/dev/null 2>&1
  else
    pkill -f "$launch" 2>/dev/null
    pkill -f "$server" 2>/dev/null
  fi
fi

section "Summary"
for name in "${passed[@]+"${passed[@]}"}"; do echo "PASS: $name"; done
for name in "${failed[@]+"${failed[@]}"}"; do echo "FAIL: $name"; done
[ "${#failed[@]}" = 0 ]
