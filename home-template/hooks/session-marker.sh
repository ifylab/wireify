#!/bin/sh
# Wireify lesson gate: stamp the session start. A fresh session gets a fresh
# mutation record, so the Stop-time check below only ever sees this session.
# Before that, print the warmup Wireify wrote at Connect (the previous
# session's facts); SessionStart output reaches the session as context.
dir="$(cd "$(dirname "$0")/.." && pwd)"
[ -f "$dir/warmup.txt" ] && cat "$dir/warmup.txt"
: > "$dir/session-start-marker"
rm -f "$dir/mutations.log" "$dir/review-done"
exit 0
