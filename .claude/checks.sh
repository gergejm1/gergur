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

# The YouTube ad pruning is JavaScript, so dotnet test cannot reach it, and it is the
# whole of the blocking on that site: the ads come from the hosts the video comes from.
# Run it here rather than leave it to inspection.
echo "checks: running the ad-pruning tests"
if ! command -v node >/dev/null 2>&1; then
  echo "checks: node is needed for the ad-pruning tests and was not found"
  exit 1
fi
if ! command -v timeout >/dev/null 2>&1; then
  echo "checks: timeout is needed to bound the ad-pruning tests and was not found"
  exit 1
fi
# Under a timeout: the cycle-guard case fails by hanging rather than by failing, so
# without one a regression there would stall the gate instead of reporting.
# Capture the status directly. Inside "if ! cmd", $? is the status of the negation,
# which is 0 whenever the command ran at all, so the timeout case never showed.
timeout 60 node "$ROOT/src/Gergur/Assets/adblock.test.js"
status=$?
if [ "$status" -eq 124 ]; then
  echo "checks: ad-pruning tests timed out, which is what a lost cycle guard looks like"
  exit 1
fi
if [ "$status" -ne 0 ]; then
  echo "checks: ad-pruning tests failed"
  exit 1
fi

echo "checks: OK"
