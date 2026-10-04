#!/usr/bin/env bash
# Runs RealPort5000_PingWorksAndIsRefusedAfterStop in two test processes at the same time, N times,
# the way `dotnet test` runs the net48 and net8.0 assemblies in parallel on Windows.
# Without the cross-process lock the two runs can share port 5000 and one of them fails
# (seen on windows-latest: TaskCanceledException instead of HttpRequestException after Stop()).
# Usage: experiments/parallel-real-port-test.sh [iterations]
set -u
cd "$(dirname "$0")/.."
N=${1:-10}
dotnet build tests/AutoCADHttp.Tests -c Release -v q >/dev/null || exit 1
fail=0
for i in $(seq 1 "$N"); do
  for p in 1 2; do
    dotnet test tests/AutoCADHttp.Tests -c Release --no-build \
      --filter "FullyQualifiedName~RealPort5000" --logger "console;verbosity=normal" > "/tmp/real-port-$p.log" 2>&1 &
  done
  for job in $(jobs -p); do wait "$job" || fail=$((fail + 1)); done
  grep -h "RealPort5000" /tmp/real-port-1.log /tmp/real-port-2.log | grep -E "Passed|Failed" | sed "s/^/[$i] /"
done
echo "failed test processes: $fail"
[ "$fail" -eq 0 ]
