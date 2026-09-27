"""Summarize opt-in IL2CPP measurements (stdlib only, no licensed data).

Usage: python Tools/MobPlan/analyze_profile.py <run directory> [<run directory> ...]
The 10-second warmup is excluded by request time, including boundary-spanning work.
CPU duty is normalized to one logical core, not the entire machine.
"""
import csv
import json
import math
from pathlib import Path
import sys


def rows(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        return [{key: float(value) for key, value in row.items()} for row in csv.DictReader(stream)]


def stats(values):
    values = sorted(values)
    if not values:
        return None
    def percentile(p):
        return values[math.ceil((len(values) - 1) * p)]
    return dict(mean=sum(values) / len(values), p50=percentile(.5), p95=percentile(.95),
                p99=percentile(.99), max=values[-1])


def analyze(directory):
    summary = dict(line.split("=", 1) for line in (directory / "summary.txt").read_text().splitlines() if "=" in line)
    warmup = float(summary["warmupEndPlanTime"])
    cycles = [row for row in rows(directory / "cycles.csv") if row["requested"] >= warmup]
    frames = rows(directory / "frames.csv")
    seconds = sum(row["frame_ms"] for row in frames) / 1000
    accepted = [row for row in cycles if row["accepted"]]
    result = dict(run=directory.name, summary=summary, cycles=len(cycles), accepted=len(accepted),
                  measured_wall_seconds=seconds,
                  cpu_one_core_percent=sum(row["cpu_ms"] for row in cycles) / (seconds * 10),
                  worker_wall_duty_percent=sum(row["compute_ms"] for row in cycles) / (seconds * 10),
                  cycles_per_second=len(cycles) / seconds, accepted_per_second=len(accepted) / seconds,
                  timeout_cycles=sum(row["timeout"] for row in cycles),
                  search_timeouts=sum(row["search_timeouts"] for row in cycles),
                  over_800_ms=sum(row["compute_ms"] > 800 for row in cycles),
                  over_1_second=sum(row["compute_ms"] > 1000 for row in cycles),
                  changes=sum(row["changes"] for row in accepted))
    for column in ("queue_ms", "compute_ms", "collect_ms", "cpu_ms", "planner_ms", "candidate_ms", "compatibility_ms", "assignment_ms", "expansions", "blocked", "deadlocks", "lod_near", "lod_mid", "lod_far"):
        result[column] = stats([row[column] for row in cycles])
    result["latency_ms"] = stats([row["queue_ms"] + row["compute_ms"] + row["collect_ms"] for row in cycles])
    result["prepare_publish_ms"] = stats([row["compute_ms"] - row["planner_ms"] for row in cycles])
    result["accepted_gap_seconds"] = stats([b["wall_elapsed"] - a["wall_elapsed"] for a, b in zip(accepted, accepted[1:])])
    result["frame_ms"] = stats([row["frame_ms"] for row in frames])
    result["frames_over_22ms"] = sum(row["frame_ms"] > 22.2222 for row in frames)
    result["frames"] = len(frames)
    result["cpu_accounting_finite"] = all(math.isfinite(row["cpu_ms"]) for row in cycles)
    return result


if __name__ == "__main__":
    for argument in sys.argv[1:]:
        folder = Path(argument)
        report = analyze(folder)
        encoded = json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False)
        (folder / "analysis.json").write_text(encoded + "\n", encoding="utf-8")
        print(encoded)
