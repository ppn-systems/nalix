#!/usr/bin/env bash
# End-to-end network comparison: Nalix vs SignalR / gRPC / MagicOnion / raw baselines.
# Each library runs as a separate server process (spawned by the driver) + in-process clients,
# over 127.0.0.1. Results: JSON lines + aggregated markdown tables.
#
# Usage: benchmarks/run-comparison.sh [results-dir] [extra harness args...]
#   e.g. benchmarks/run-comparison.sh /tmp/cmp --runs 5 --duration 10
#   LIBS="nalix-tcp,grpc-unary" MO_LIBS="" benchmarks/run-comparison.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/build/comparison-results}"
shift || true
EXTRA=("$@")

LIBS="${LIBS-raw-tcp,kestrel-ws,nalix-tcp,nalix-ws,nalix-tcp-aead,signalr-ws-msgpack,grpc-unary,grpc-duplex,grpc-unary-tls,grpc-duplex-tls}"
MO_LIBS="${MO_LIBS-magiconion-unary,magiconion-hub}"
ARGS=(--sizes 32,1024 --clients 1,16,64 --runs 3 --lat-warmup 20000 --lat-iters 100000 --warmup 2 --duration 8)

export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build "$ROOT/benchmarks/Nalix.Comparison.Benchmarks/Nalix.Comparison.Benchmarks.csproj" -c Release -nologo -v q
dotnet build "$ROOT/benchmarks/Nalix.Comparison.MagicOnion/Nalix.Comparison.MagicOnion.csproj" -c Release -nologo -v q

mkdir -p "$OUT"
RESULTS="$OUT/results.jsonl"
: > "$RESULTS"
BIN="$ROOT/build/bin/Release"

IFS=',' read -ra L <<< "$LIBS"
for lib in "${L[@]}"; do
  [ -z "$lib" ] && continue
  "$BIN/Nalix.Comparison.Benchmarks/net10.0/Nalix.Comparison.Benchmarks" bench "$lib" "${ARGS[@]}" "${EXTRA[@]}" --out "$RESULTS" | tee -a "$OUT/console.log"
done
IFS=',' read -ra M <<< "$MO_LIBS"
for lib in "${M[@]}"; do
  [ -z "$lib" ] && continue
  "$BIN/Nalix.Comparison.MagicOnion/net10.0/Nalix.Comparison.MagicOnion" bench "$lib" "${ARGS[@]}" "${EXTRA[@]}" --out "$RESULTS" | tee -a "$OUT/console.log"
done

python3 "$ROOT/benchmarks/Nalix.Comparison.Benchmarks/aggregate.py" "$RESULTS" > "$OUT/tables.md"
echo "Tables written to $OUT/tables.md"
