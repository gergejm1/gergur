#!/usr/bin/env bash
# Definition of "done" for Gergur, enforced by ~/.claude/hooks/stop-gate.sh.
# Build must be warning-free (the project holds a zero-warning standard) and every
# test must pass. Exit non-zero to block "done".
set -uo pipefail

# Locate the solution from this script rather than the caller's cwd: a GitHub ZIP
# download nests the project one folder deeper than the directory you opened.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SLN="$(find "$HERE/.." "$HERE/../.." -maxdepth 2 -name 'Gergur.slnx' 2>/dev/null | head -1)"
if [ -z "$SLN" ]; then
  echo "checks: could not find Gergur.slnx"
  exit 1
fi
ROOT="$(dirname "$SLN")"
cd "$ROOT" || exit 1

echo "checks: building (warnings are errors)"
if ! dotnet build "$SLN" -c Debug --nologo -v q -warnaserror; then
  echo "checks: build failed or produced warnings"
  exit 1
fi

echo "checks: running tests"
if ! dotnet test "$SLN" -c Debug --nologo -v q; then
  echo "checks: tests failed"
  exit 1
fi

echo "checks: OK"
