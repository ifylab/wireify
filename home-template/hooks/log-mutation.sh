#!/bin/sh
# Wireify lesson gate: record that a structural mutation ran this session.
# Content is a debugging aid; the Stop-time check only needs existence + mtime.
dir="$(cd "$(dirname "$0")/.." && pwd)"
cat > /dev/null
printf '%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$dir/mutations.log"
exit 0
