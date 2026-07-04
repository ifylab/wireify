#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Assembles the Yak package folder at dist/wireify/ from Release builds.
# Layout per Rhino's multi-target packaging: manifest.yml at the package root,
# framework folder(s) beside it, each holding that build's .rhp + .gha + deps +
# home-template. The yak CLI itself ships inside Rhino, so the final two steps
# run on a machine with Rhino installed:
#
#   cd dist/wireify
#   "C:\Program Files\Rhino 8\System\yak.exe" build
#   # inspect the produced wireify-<version>-rh8_0-*.yak, then:
#   "C:\Program Files\Rhino 8\System\yak.exe" login
#   "C:\Program Files\Rhino 8\System\yak.exe" push wireify-<version>-rh8_0-*.yak
#
# NB: only Windows is live-verified today. If yak infers an "-any" distribution
# tag, rename the file to ...-rh8_0-win.yak before pushing (the tag is the filename).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$ROOT/dist/wireify"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"

echo "== build (Release) =="
"$DOTNET" build "$ROOT/Wireify.sln" -c Release -v quiet --nologo

echo "== assemble $DIST =="
rm -rf "$DIST"
mkdir -p "$DIST/net7.0"

cp -R "$ROOT/src/WireifyGh/bin/Release/net7.0-windows/." "$DIST/net7.0/"
cp "$ROOT/src/Wireify/bin/Release/net7.0/Wireify.rhp" "$DIST/net7.0/"
cp "$ROOT/src/Wireify/bin/Release/net7.0/Wireify.deps.json" "$DIST/net7.0/" 2>/dev/null || true
find "$DIST" -name "*.pdb" -delete

cp "$ROOT/manifest.yml" "$DIST/manifest.yml"

if [ -f "$ROOT/assets/brand/wireify-avatar-512.png" ]; then
  cp "$ROOT/assets/brand/wireify-avatar-512.png" "$DIST/icon.png"
else
  echo "WARNING: assets/brand/wireify-avatar-512.png missing - pull the avatar from the"
  echo "         .ify design-system canvas before shipping (manifest.yml expects icon.png)."
fi

echo
echo "== package contents =="
(cd "$DIST" && find . -type f | sort)
echo
echo "Done. Copy dist/wireify/ to the Rhino machine and run yak build there (see header)."
