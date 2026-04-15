#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
dotnet build "$SCRIPT_DIR/recorder/STS2CombatRecorder.csproj" -c Debug

MOD_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/STS2CombatRecorder"
mkdir -p "$MOD_DIR"
cp "$SCRIPT_DIR/recorder/bin/Debug/net9.0/STS2CombatRecorder.dll" "$MOD_DIR/"
cp "$SCRIPT_DIR/recorder/STS2CombatRecorder.json" "$MOD_DIR/"
echo "✓ Deployed to $MOD_DIR"
