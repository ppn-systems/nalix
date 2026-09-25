#!/usr/bin/env python3
"""Aggregate results.jsonl from the comparison harness into markdown tables.

Each cell = median across runs; the +/- column is (max-min)/median across runs in %.
"""
import json
import statistics
import sys
from collections import defaultdict

rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
order = []
for r in rows:
    if r["lib"] not in order:
        order.append(r["lib"])

groups = defaultdict(list)
for r in rows:
    groups[(r["kind"], r["lib"], r["size"], r["clients"])].append(r)


def med(rs, k):
    return statistics.median(x[k] for x in rs)


def spread(rs, k):
    v = [x[k] for x in rs]
    m = statistics.median(v)
    return 0.0 if m == 0 else (max(v) - min(v)) / m * 100


sizes = sorted({r["size"] for r in rows})
clients = sorted({r["clients"] for r in rows if r["kind"] == "throughput"})

for size in sizes:
    print(f"\n### Latency — {size} B payload, 1 client, sequential request/response\n")
    print("| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |")
    print("|:--|--:|--:|--:|--:|--:|--:|--:|")
    for lib in order:
        rs = groups.get(("latency", lib, size, 1))
        if not rs:
            continue
        print(f"| {lib} | {med(rs,'mean_us'):.1f} | {med(rs,'p50_us'):.1f} | {med(rs,'p90_us'):.1f} | {med(rs,'p99_us'):.1f} | {med(rs,'p999_us'):.1f} | ±{spread(rs,'p50_us')/2:.1f}% | {med(rs,'server_alloc_bytes_per_op'):.0f} |")

for size in sizes:
    print(f"\n### Throughput — {size} B payload, N clients closed-loop (ops/s, median of runs)\n")
    hdr = "| Library | " + " | ".join(f"{n} client{'s' if n > 1 else ''}" for n in clients) + " |"
    print(hdr)
    print("|:--|" + "--:|" * len(clients))
    for lib in order:
        cells = []
        for n in clients:
            rs = groups.get(("throughput", lib, size, n))
            cells.append("—" if not rs else f"{med(rs,'ops_per_sec'):,.0f} (±{spread(rs,'ops_per_sec')/2:.0f}%)")
        if any(c != "—" for c in cells):
            print(f"| {lib} | " + " | ".join(cells) + " |")

    n = max(clients)
    print(f"\n#### Cost per message at {n} clients — {size} B\n")
    print("| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |")
    print("|:--|--:|--:|--:|--:|--:|")
    for lib in order:
        rs = groups.get(("throughput", lib, size, n))
        if not rs:
            continue
        g = f"{med(rs,'gen0'):.0f}/{med(rs,'gen1'):.0f}/{med(rs,'gen2'):.0f}"
        print(f"| {lib} | {med(rs,'server_alloc_bytes_per_op'):.0f} | {med(rs,'server_cpu_us_per_op'):.1f} | {med(rs,'client_cpu_us_per_op'):.1f} | {med(rs,'client_alloc_bytes_per_op'):.0f} | {g} |")
