#!/bin/bash
# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# macOS: builds TheOrc.app from THIS checkout, so the Dock, Cmd-Tab and Finder show "TheOrc"
# and its icon (started as `dotnet …dll` the app is the dotnet host and shows as "dotnet").
#
# Two kinds of bundle:
#   default       A launcher for the Debug (or --release) build in this repo: rebuild as usual
#                 and relaunch. It runs Microsoft's dotnet, so macOS privacy permissions
#                 (Screen Recording, folders, …) are asked for and granted to "dotnet".
#   --standalone  The app itself, self-contained (.NET runtime included, no SDK needed) and
#                 signed ad-hoc as its own program, so permissions are asked for TheOrc and
#                 cover only TheOrc. Re-run the script after code changes; an ad-hoc signature
#                 changes with every build, so macOS may ask for permissions again.
#                 Not notarized (that needs an Apple Developer ID): fine on this Mac, but
#                 Gatekeeper would block a copy downloaded on another one.
#
# Usage:  scripts/make-mac-dev-app.sh [--standalone | --release] [--no-build] [path/to/TheOrc.app]
#         (default: ~/Applications/TheOrc.app; then open it, or drag it to the Dock)
set -euo pipefail

BUNDLE_ID="local.dev.theorc"
CONFIG="Debug"
BUILD=1
STANDALONE=0
OUT="$HOME/Applications/TheOrc.app"
for arg in "$@"; do
  case "$arg" in
    --release)    CONFIG="Release" ;;
    --standalone) STANDALONE=1; CONFIG="Release" ;;
    --no-build)   BUILD=0 ;;
    -h|--help)    sed -n '5,23p' "$0"; exit 0 ;;
    -*)         echo "make-mac-dev-app: unknown option $arg" >&2; exit 2 ;;
    *)          OUT="$arg" ;;
  esac
done

[[ "$(uname -s)" == "Darwin" ]] || { echo "make-mac-dev-app: macOS only." >&2; exit 2; }
[[ "$OUT" == *.app ]] || { echo "make-mac-dev-app: output must end in .app: $OUT" >&2; exit 2; }

REPO="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO/OrchestratorIDE.Avalonia/OrchestratorIDE.Avalonia.csproj"
DLL="$REPO/OrchestratorIDE.Avalonia/bin/$CONFIG/net10.0/OrchestratorIDE.Avalonia.dll"
ICON="$REPO/Assets/macos/TheOrc.icns"

# An absolute dotnet path: an app opened from Finder or the Dock doesn't get the shell's PATH.
DOTNET=""
for candidate in "${DOTNET_ROOT:-}/dotnet" "$(command -v dotnet 2>/dev/null || true)" \
                 "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet /opt/homebrew/bin/dotnet; do
  if [[ -n "$candidate" && "$candidate" == /* && -x "$candidate" ]]; then DOTNET="$candidate"; break; fi
done
[[ -n "$DOTNET" ]] || { echo "make-mac-dev-app: dotnet not found (install the .NET 10 SDK)." >&2; exit 1; }
DOTNET="$(cd "$(dirname "$DOTNET")" && pwd -P)/$(basename "$DOTNET")"   # resolve symlinks
DOTNET_HOME="$(dirname "$DOTNET")"

[[ -f "$ICON" ]] || { echo "make-mac-dev-app: missing $ICON" >&2; exit 1; }
if (( STANDALONE )); then
  (( BUILD )) || { echo "make-mac-dev-app: --standalone always publishes; drop --no-build." >&2; exit 2; }
  PUB="$(mktemp -d)"
  trap 'rm -rf "$PUB"' EXIT
  DOTNET_ROOT="$DOTNET_HOME" "$DOTNET" publish "$PROJECT" -c Release -r osx-arm64 --self-contained \
    -p:DebugType=none -p:DebugSymbols=false -o "$PUB" -v q --nologo
  [[ -x "$PUB/OrchestratorIDE.Avalonia" ]] || { echo "make-mac-dev-app: publish made no app executable." >&2; exit 1; }
else
  if (( BUILD )); then
    DOTNET_ROOT="$DOTNET_HOME" "$DOTNET" build "$PROJECT" -c "$CONFIG" -v q --nologo
  fi
  [[ -f "$DLL" ]] || { echo "make-mac-dev-app: no build at $DLL (run without --no-build)." >&2; exit 1; }
fi

# Only ever replace a bundle this script made.
if [[ -e "$OUT" ]]; then
  existing="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$OUT/Contents/Info.plist" 2>/dev/null || true)"
  if [[ "$existing" != "$BUNDLE_ID" ]]; then
    echo "make-mac-dev-app: $OUT exists and isn't a TheOrc dev launcher; not replacing it." >&2
    exit 1
  fi
  rm -rf "$OUT"
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -1)"
VERSION="${VERSION:-0.0.0}"

mkdir -p "$OUT/Contents/MacOS" "$OUT/Contents/Resources"
cp "$ICON" "$OUT/Contents/Resources/TheOrc.icns"

cat > "$OUT/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>         <string>$BUNDLE_ID</string>
  <key>CFBundleName</key>               <string>TheOrc</string>
  <key>CFBundleDisplayName</key>        <string>TheOrc</string>
  <key>CFBundleExecutable</key>         <string>TheOrc</string>
  <key>CFBundleIconFile</key>           <string>TheOrc</string>
  <key>CFBundlePackageType</key>        <string>APPL</string>
  <key>CFBundleShortVersionString</key> <string>$VERSION</string>
  <key>CFBundleVersion</key>            <string>$VERSION</string>
  <key>LSMinimumSystemVersion</key>     <string>13.0</string>
  <key>LSArchitecturePriority</key>     <array><string>arm64</string></array>
  <key>NSHighResolutionCapable</key>    <true/>
</dict>
</plist>
PLIST
plutil -lint -s "$OUT/Contents/Info.plist"

if (( STANDALONE )); then
  # Layout codesign accepts: Contents/MacOS may hold only code, so the published app lives in
  # Contents/Resources/app. The .NET apphost (renamed TheOrc) stays in MacOS and finds the app
  # through the DLL symlink; it treats the symlink's real folder as the app folder, so
  # everything (runtime included) must sit together in Resources/app.
  APP="$OUT/Contents/Resources/app"
  mkdir -p "$APP"
  cp -R "$PUB/." "$APP/"
  # Native libraries for other OSes (Linux, Windows, Intel Mac) are never loaded here (~90 MB).
  if [[ -d "$APP/runtimes" ]]; then
    find "$APP/runtimes" -mindepth 1 -maxdepth 1 ! -name osx-arm64 -exec rm -rf {} +
  fi
  mv "$APP/OrchestratorIDE.Avalonia" "$OUT/Contents/MacOS/TheOrc"
  ln -s ../Resources/app/OrchestratorIDE.Avalonia.dll "$OUT/Contents/MacOS/OrchestratorIDE.Avalonia.dll"

  # Sign every native file, then the bundle as TheOrc (ad-hoc: no certificate needed).
  while IFS= read -r -d '' f; do
    if file -b "$f" | grep -q 'Mach-O'; then codesign --force --sign - "$f" 2>/dev/null; fi
  done < <(find "$APP" -type f -print0)
  codesign --force --sign - --identifier "$BUNDLE_ID" "$OUT" 2>/dev/null
  codesign --verify --deep --strict "$OUT"
else
  # exec keeps the bundle's process, so the Dock tile stays TheOrc's. The app writes its own log
  # (~/Library/Logs/TheOrc); stdout/stderr aren't kept.
  cat > "$OUT/Contents/MacOS/TheOrc" <<LAUNCHER
#!/bin/bash
export DOTNET_ROOT="$DOTNET_HOME"
cd "$REPO"
exec "$DOTNET" "$DLL" "\$@" >/dev/null 2>&1
LAUNCHER
  chmod 755 "$OUT/Contents/MacOS/TheOrc"
fi

# Register now so Finder, Spotlight and the Dock pick up the name and icon straight away.
# (THEORC_NO_REGISTER=1 skips it, for tests that build throwaway bundles.)
if [[ "${THEORC_NO_REGISTER:-0}" != "1" ]]; then
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$OUT" || true
fi

if (( STANDALONE )); then
  echo "Built $OUT (TheOrc $VERSION, standalone, signed ad-hoc as $BUNDLE_ID, $(du -sh "$OUT" | cut -f1))."
else
  echo "Built $OUT (TheOrc $VERSION, launcher for the $CONFIG build in $REPO)."
fi
echo "Open it with:  open \"$OUT\""
