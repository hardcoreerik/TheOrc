#!/bin/bash
# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# macOS: builds TheOrc.app, a launcher for the app built from THIS checkout, so the Dock,
# Cmd-Tab and Finder show "TheOrc" and its icon. Started with `dotnet …dll` the app is the
# dotnet host and the Dock labels it "dotnet": macOS takes the name from the app bundle, and a
# bare dotnet process has none. This is not the signed, self-contained release bundle (that is
# its own roadmap item); it runs the Debug (or --release) build in this repo, so rebuild as
# usual and relaunch.
#
# Usage:  scripts/make-mac-dev-app.sh [--release] [--no-build] [path/to/TheOrc.app]
#         (default: ~/Applications/TheOrc.app; then open it, or drag it to the Dock)
set -euo pipefail

BUNDLE_ID="local.dev.theorc"
CONFIG="Debug"
BUILD=1
OUT="$HOME/Applications/TheOrc.app"
for arg in "$@"; do
  case "$arg" in
    --release)  CONFIG="Release" ;;
    --no-build) BUILD=0 ;;
    -h|--help)  sed -n '5,15p' "$0"; exit 0 ;;
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

if (( BUILD )); then
  DOTNET_ROOT="$DOTNET_HOME" "$DOTNET" build "$PROJECT" -c "$CONFIG" -v q --nologo
fi
[[ -f "$DLL" ]] || { echo "make-mac-dev-app: no build at $DLL (run without --no-build)." >&2; exit 1; }
[[ -f "$ICON" ]] || { echo "make-mac-dev-app: missing $ICON" >&2; exit 1; }

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

# exec keeps the bundle's process, so the Dock tile stays TheOrc's. The app writes its own log
# (~/Library/Logs/TheOrc); stdout/stderr aren't kept.
cat > "$OUT/Contents/MacOS/TheOrc" <<LAUNCHER
#!/bin/bash
export DOTNET_ROOT="$DOTNET_HOME"
cd "$REPO"
exec "$DOTNET" "$DLL" "\$@" >/dev/null 2>&1
LAUNCHER
chmod 755 "$OUT/Contents/MacOS/TheOrc"

# Register now so Finder, Spotlight and the Dock pick up the name and icon straight away.
# (THEORC_NO_REGISTER=1 skips it, for tests that build throwaway bundles.)
if [[ "${THEORC_NO_REGISTER:-0}" != "1" ]]; then
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$OUT" || true
fi

echo "Built $OUT (TheOrc $VERSION, $CONFIG build from $REPO)."
echo "Open it with:  open \"$OUT\""
