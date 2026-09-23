"""Summarize completed Unity Editor comparisons without pooling samples across processes.

Usage: python summarize.py PATH/TO/comparison --output PATH/TO/summary
Only the three named summary files are written. Input CSVs are read unchanged, and every
original row is retained in summary.json.raw_rows alongside its source file and line.
"""
import argparse
import csv
import hashlib
import io
import json
import math
from collections import defaultdict
from pathlib import Path
from statistics import median


PHYSICS_COUNTS = {
    "shape_pair_create_dispose": 8,
    "box_mass_divide": 8,
    "provisional_build": 64,
    "prepare_colliders": 64,
    "adopt_colliders": 64,
}
VARIANTS = ("baseline", "o1", "view", "colliders", "mass", "all")
REGISTRATION_CASES = ("live16", "live32", "live512", "highwater2048_live16")
REGISTRATION_SCOPES = ("unregistered", "late_duplicate")
NOTES = [
    "時刻はUnity Editor MonoのMain上のStopwatch経過時間。CPU実行時間、IL2CPP Player性能、切断全体の時間ではない。Ready・割込み・GC等を含み得る。",
    "登録はprivate IsRegisteredのdelegate呼出しだけ。storage入力検証、空slot探索、登録・retire更新、Geometry Commit全体を含まない。setup/teardownは計測外。",
    "登録live16/32は代表規模、live512とhighwater2048_live16はstress。各scopeは1 batch warmup後8 sample、1 sample=4096 lookupの平均。",
    "shape_pair_create_disposeは正負shape生成とDisposeを含む256反復平均、box_mass_divideは質量分割1024反復平均。各1 batch warmup後8 sample。",
    "provisional_build/prepare_colliders/adopt_collidersは各64回の単発経過時間。先行4 pairはwarmup。Actorは非活性、Destroy/Withdrawおよびcook準備・完了は対象区間外。公開やFinal handoff全体は測っていない。",
    "全時間を1 iterationあたりusへ換算。p95は各run内のsample値をtype 7線形補間で算出。8 batch平均のp95は個々のcallの95パーセンタイルではない。",
    "variant代表値は2 processのrun中央値の中央値。rangeはrun中央値のmin–max。異なるprocessのsampleを混ぜず、子区間の中央値も足さない。",
    "差分はcandidate代表値−baseline代表値。負が短縮。独立2 processの記述比較であり、有意差・Player改善・因果効果を確定するものではない。baseline自身のrun間変動も併記する。",
    "4096 B配列allocation probeへ応答しないcounterのbytesはunavailable。0を無割当の証明に使わず、ソース上の割当削減とも区別する。",
    "個別修正の差分は対象metricだけ: o1=登録、view=shape生成/破棄とprovisional build、colliders=prepare/adopt、mass=mass divideとprovisional build。allのみ全metricを比較。対象外の行も原文とrun集約を保持する。",
]


def percentile(values, probability):
    ordered = sorted(values)
    position = (len(ordered) - 1) * probability
    lower = math.floor(position)
    upper = math.ceil(position)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def target_metric(variant, source, region):
    if variant in ("baseline", "all"):
        return True
    if variant == "o1":
        return source == "registration"
    if source != "physics":
        return False
    return region in {
        "view": {"shape_pair_create_dispose", "provisional_build"},
        "colliders": {"prepare_colliders", "adopt_colliders"},
        "mass": {"box_mass_divide", "provisional_build"},
    }[variant]


def integer(row, name, path, line):
    try:
        return int(row[name])
    except (KeyError, ValueError, TypeError) as error:
        raise ValueError(f"{path}:{line}: invalid {name}") from error


def read_inputs(root):
    groups = defaultdict(list)
    raw_rows, source_files = [], []
    runs = sorted(path for path in root.glob("run*") if path.is_dir())
    if not runs:
        raise ValueError(f"No run* directories: {root}")
    run_variants = {}
    for run in runs:
        seen_variants = set()
        for source in ("registration", "physics"):
            path = run / (source + ".csv")
            data = path.read_bytes()  # A missing run file is an error, never silently excluded.
            rows = list(csv.DictReader(io.StringIO(data.decode("utf-8-sig"))))
            if not rows:
                raise ValueError(f"Empty CSV: {path}")
            relative = path.relative_to(root).as_posix()
            source_files.append({"file": relative, "sha256": hashlib.sha256(data).hexdigest(), "rows": len(rows)})
            for line, row in enumerate(rows, 2):
                if None in row or any(value is None for value in row.values()):
                    raise ValueError(f"{path}:{line}: malformed CSV")
                variant = row["variant"]
                if variant not in VARIANTS:
                    raise ValueError(f"{path}:{line}: unknown variant {variant!r}")
                seen_variants.add(variant)
                iterations = integer(row, "iterations", path, line)
                sample = integer(row, "sample", path, line)
                if source == "registration":
                    case, region = row["scenario"], row["scope"]
                    role = row["role"]
                    ticks = integer(row, "ticks", path, line)
                    frequency = integer(row, "stopwatch_frequency", path, line)
                    responded = row["allocation_counter_responded"].lower() == "true"
                    responded &= integer(row, "allocation_probe_delta", path, line) >= 4096
                    if integer(row, "expected_true", path, line) != integer(row, "observed_true", path, line):
                        raise ValueError(f"{path}:{line}: lookup result mismatch")
                    if case not in REGISTRATION_CASES or region not in REGISTRATION_SCOPES or iterations != 4096:
                        raise ValueError(f"{path}:{line}: unexpected registration case/scope/iterations")
                else:
                    case, region, role = row["case"], row["region"], "representative"
                    ticks = integer(row, "elapsed_ticks", path, line)
                    frequency = integer(row, "frequency", path, line)
                    responded = row["counter_responded"].lower() == "true"
                    expected_iterations = 256 if region == "shape_pair_create_dispose" else 1024 if region == "box_mass_divide" else 1
                    if case not in ("box", "character") or region not in PHYSICS_COUNTS or iterations != expected_iterations:
                        raise ValueError(f"{path}:{line}: unexpected physics case/region/iterations")
                allocated = integer(row, "allocated_bytes", path, line)
                if iterations <= 0 or frequency <= 0 or ticks < 0 or sample < 0:
                    raise ValueError(f"{path}:{line}: invalid timing/sample")
                value = {"sample": sample, "us": ticks * 1e6 / frequency / iterations,
                         "bytes": allocated / iterations if responded and allocated >= 0 else None}
                key = (run.name, variant, source, case, region, role)
                groups[key].append(value)
                raw_rows.append({"run": run.name, "file": relative, "line": line, "values": row,
                                 "us_per_iteration": value["us"], "bytes_per_iteration": value["bytes"]})
        if len(seen_variants) != 1:
            raise ValueError(f"{run}: inconsistent variant values: {seen_variants}")
        run_variants[run.name] = next(iter(seen_variants))

    for key, samples in groups.items():
        expected = 8 if key[2] == "registration" else PHYSICS_COUNTS[key[4]]
        if len(samples) != expected or sorted(value["sample"] for value in samples) != list(range(expected)):
            raise ValueError(f"{key}: expected samples 0..{expected - 1}, got {[value['sample'] for value in samples]}")
    for run, variant in run_variants.items():
        count = sum(key[0] == run for key in groups)
        if count != 18:  # Eight registration groups plus two cases times five physics regions.
            raise ValueError(f"{run}: expected 18 metric groups, got {count}")
    counts = {variant: sum(value == variant for value in run_variants.values()) for variant in VARIANTS}
    if any(count != 2 for count in counts.values()):
        raise ValueError(f"Expected two independent process runs for each variant, got {counts}")
    return groups, raw_rows, source_files, run_variants


def aggregate(groups):
    runs = []
    by_variant = defaultdict(list)
    for key, values in sorted(groups.items()):
        run, variant, source, case, region, role = key
        times = [value["us"] for value in values]
        allocations = [value["bytes"] for value in values]
        row = {"level": "run", "run": run, "variant": variant, "source": source, "case": case,
               "region": region, "role": role, "samples": len(values), "run_count": 1,
               "median_us": median(times), "p95_us": percentile(times, .95),
               "target_metric": target_metric(variant, source, region),
               "allocation_status": "available" if all(value is not None for value in allocations) else "unavailable",
               "median_bytes_per_iteration": median(allocations) if all(value is not None for value in allocations) else None}
        runs.append(row)
        by_variant[(variant, source, case, region, role)].append(row)
    variants = []
    for key, values in sorted(by_variant.items()):
        variant, source, case, region, role = key
        times = [value["median_us"] for value in values]
        center = median(times)
        spread = max(times) - min(times)
        allocations = [value["median_bytes_per_iteration"] for value in values]
        variants.append({"level": "variant", "run": "", "variant": variant, "source": source, "case": case,
                         "region": region, "role": role, "samples": sum(value["samples"] for value in values),
                         "run_count": len(values), "median_us": center, "p95_us": None,
                         "min_run_median_us": min(times), "max_run_median_us": max(times),
                         "run_median_spread_us": spread, "run_median_spread_percent": spread / center * 100 if center else None,
                         "target_metric": target_metric(variant, source, region),
                         "allocation_status": "available" if all(value is not None for value in allocations) else "unavailable",
                         "median_bytes_per_iteration": median(allocations) if all(value is not None for value in allocations) else None,
                         "process_runs": [value["run"] for value in values]})
    baseline = {(row["source"], row["case"], row["region"]): row for row in variants if row["variant"] == "baseline"}
    for row in variants:
        reference = baseline[(row["source"], row["case"], row["region"])]
        row["baseline_median_us"] = reference["median_us"]
        if row["target_metric"]:
            row["delta_us"] = row["median_us"] - reference["median_us"]
            row["delta_percent"] = row["delta_us"] / reference["median_us"] * 100 if reference["median_us"] else None
        else:
            row["delta_us"] = row["delta_percent"] = None
    return runs, variants


def number(value):
    return "unavailable" if value is None else f"{value:.6f}"


def metric(row):
    return f"{row['source']}/{row['case']}/{row['region']}"


def report(runs, variants, raw_count):
    lines = ["# Main thread optimization — Editor Mono局所比較", "", f"入力測定行: {raw_count}（全行をsummary.jsonのraw_rowsに保持）。", ""]
    lines += [f"- {note}" for note in NOTES]
    lines += ["", "## Baselineのprocess間変動", "", "| metric | run中央値の中央値 us | run中央値 min–max us | spread us | spread % |", "|---|---:|---:|---:|---:|"]
    for row in variants:
        if row["variant"] == "baseline":
            lines.append(f"| {metric(row)} | {number(row['median_us'])} | {number(row['min_run_median_us'])}–{number(row['max_run_median_us'])} | {number(row['run_median_spread_us'])} | {number(row['run_median_spread_percent'])} |")
    lines += ["", "## 対象metricの比較", "", "差分の分母・基準は同じmetricのbaseline process中央値の中央値。時間は1 iterationあたり。", "", "| variant | metric | role | median us | run中央値 min–max us | delta us | delta % | bytes/iteration |", "|---|---|---|---:|---:|---:|---:|---:|"]
    for row in variants:
        if row["variant"] != "baseline" and row["target_metric"]:
            lines.append(f"| {row['variant']} | {metric(row)} | {row['role']} | {number(row['median_us'])} | {number(row['min_run_median_us'])}–{number(row['max_run_median_us'])} | {number(row['delta_us'])} | {number(row['delta_percent'])} | {number(row['median_bytes_per_iteration'])} |")
    lines += ["", "## 各processの集計", "", "個別変更の対象外metricも観測値として保持し、改善差分には使用しない。", "", "| run | metric | samples | median us | p95 us | 比較対象 | allocation |", "|---|---|---:|---:|---:|---|---|"]
    for row in runs:
        lines.append(f"| {row['run']} | {metric(row)} | {row['samples']} | {number(row['median_us'])} | {number(row['p95_us'])} | {'yes' if row['target_metric'] else 'no'} | {row['allocation_status']} |")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("comparison", type=Path, help="Completed comparison directory containing run*/registration.csv and physics.csv")
    parser.add_argument("--output", required=True, type=Path, help="Directory for the three summary files")
    args = parser.parse_args()
    root = args.comparison.resolve()
    groups, raw_rows, source_files, run_variants = read_inputs(root)
    runs, variants = aggregate(groups)
    result = {"comparison_directory": str(root), "notes": NOTES, "run_variants": run_variants,
              "source_files": source_files, "run_summaries": runs, "variant_summaries": variants, "raw_rows": raw_rows}
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "summary.json").write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    (args.output / "report-summary.md").write_text(report(runs, variants, len(raw_rows)), encoding="utf-8")
    fields = ["level", "run", "variant", "source", "case", "region", "role", "samples", "run_count",
              "median_us", "p95_us", "min_run_median_us", "max_run_median_us", "run_median_spread_us",
              "run_median_spread_percent", "baseline_median_us", "delta_us", "delta_percent", "target_metric",
              "allocation_status", "median_bytes_per_iteration"]
    with (args.output / "flat-summary.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(runs + variants)
    print(f"Summarized {len(run_variants)} runs / {len(raw_rows)} rows into {args.output.resolve()}")


if __name__ == "__main__":
    main()
