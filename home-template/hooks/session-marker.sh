#!/bin/sh
# Wireify lesson gate: stamp the session start. A fresh session gets a fresh
# mutation record, so the Stop-time check below only ever sees this session.
dir="$(cd "$(dirname "$0")/.." && pwd)"
: > "$dir/session-start-marker"
rm -f "$dir/mutations.log" "$dir/review-done"
exit 0
