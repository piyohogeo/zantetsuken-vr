#!/usr/bin/env python3
"""Summarize measured Quest CSV rows; keep capture-start hitches in the totals."""
import argparse
import csv
import json
import math
import statistics
from pathlib import Path


def distribution(values):
    values = sorted(v for v in values if math.isfinite(v))
    if not values:
        return None
    return {
        "count": len(values), "mean": statistics.mean(values),
        "p50": values[math.ceil(len(values) * .5) - 1],
        "p95": values[math.ceil(len(values) * .95) - 1],
        "p99": values[math.ceil(len(values) * .99) - 1], "max": values[-1],
    }


def summarize(directory):
    result = (directory / "comparison/draw-compare-result.txt").read_text(encoding="utf-8-sig")
    if "run: completed" not in result or "comparison conditions: held" not in result:
        raise ValueError("Run is incomplete or comparison conditions failed: " + str(directory))
    with (directory / "comparison/frames.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if not rows:
        raise ValueError("Empty frame table: " + str(directory))
    def values(column):
        return [float(row[column]) for row in rows]
    metrics = {column: distribution(values(column)) for column in (
        "deltaMs", "ftCpuMs", "ftMainMs", "ftMainPresentWaitMs", "xrAppGpuMs",
        "xrCompositorGpuMs", "collectMs", "Draw Calls Count", "SetPass Calls Count")}
    # GPU frame time and render thread time are all zero on this Unity/Quest build; never call zero a measurement.
    for column in ("ftGpuMs", "ftRenderMs"):
        metrics[column] = distribution([v for v in values(column) if v > 0])
    platform = (directory / "platform.txt").read_text(encoding="utf-8-sig")
    mode = json.loads((directory / "quest-config.json").read_text(encoding="utf-8-sig"))["mode"]
    return {
        "directory": str(directory.resolve()), "mode": mode, "platform": platform.splitlines(),
        "rows": len(rows), "metrics": metrics,
        "fpsFromMeanDelta": 1000 / metrics["deltaMs"]["mean"],
        "gcCollections": int(rows[-1]["gcCount"]) - int(rows[0]["gcCount"]),
        "xrDroppedDelta": int(rows[-1]["xrDropped"]) - int(rows[0]["xrDropped"]),
        "hitchesOver33_33Ms": [{"frame": int(row["frame"]), "pathSeconds": float(row["t"]),
                               "deltaMs": float(row["deltaMs"])}
                              for row in rows if float(row["deltaMs"]) > 1000 / 30],
        "rawProfilerBytes": (directory / "profile.raw").stat().st_size if (directory / "profile.raw").exists() else None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runs", type=Path, nargs="+")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    runs = [summarize(path) for path in args.runs]
    payload = {"percentile": "nearest rank", "allRowsIncluded": True, "runs": runs}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for run in runs:
        gpu = run["metrics"]["xrAppGpuMs"]
        main_thread = run["metrics"]["ftMainMs"]
        print("{}: {} rows, {:.2f} fps, XR App GPU mean/p95 {:.3f}/{:.3f} ms, CPU main mean/p95 {:.3f}/{:.3f} ms, {} hitches".format(
            run["mode"], run["rows"], run["fpsFromMeanDelta"], gpu["mean"], gpu["p95"],
            main_thread["mean"], main_thread["p95"], len(run["hitchesOver33_33Ms"])))


if __name__ == "__main__":
    main()
