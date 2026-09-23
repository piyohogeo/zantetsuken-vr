"""Summarize two completed paired mass runs; retain every sample without outlier exclusion.

Usage: python summarize_paired_mass.py PATH/TO/paired-mass1 --output PATH/TO/summary
Input: run*-paired-mass/{paired-mass.csv,paired-mass-environment.txt}.
Output: paired-summary.json, paired-summary.csv, paired-summary.md.
"""
import argparse
import csv
import hashlib
import io
import json
from pathlib import Path
from statistics import mean, median


ORDER = ("baseline", "candidate", "candidate", "baseline")
NOTES = [
    "Unity Editor MonoのMain上のStopwatch経過時間。CPU実行時間、IL2CPP Player性能、切断・公開全体の時間ではない。",
    "箱・Characterごとにbaseline/candidate各1 batchのwarmup後、ABBAを8 round。各batchは1024回のTryDivide。setup、出力一致assert、teardown、保存は計測外。",
    "各roundのbaseline値はorder 0/3の2 sample平均、candidate値はorder 1/2の2 sample平均。paired差はそのcandidate平均−baseline平均。負が短縮。",
    "runの主指標は8個のround paired差の中央値。百分率はroundごとにpaired差/baseline平均を計算した8値の中央値で、中央値同士の比率ではない。",
    "baseline/candidateのrun中央値はそれぞれ16 batch平均値の中央値。その2中央値の差は別指標であり、paired差の中央値とは同一視しない。",
    "2 process代表値は各run集約値の中央値、rangeはrun集約値のmin–max。process間でsampleをpoolしない。2 processだけで有意差・実Player効果を確定しない。",
    "全128 sampleを保持し、外れ値除外・失敗runの自動除外を行わない。GC測定・強制GCなし。反復中の割込みやGC等による経過時間変動を含み得る。",
    "baselineは8bae0b1eの型名だけを置換した実装、candidateは現行ProvisionalBoxMass。同じshape入力で全出力のbit一致assertを測定前後に行うハーネスの局所比較。",
]


def read_inputs(root):
    folders = sorted(path for path in root.glob("run*-paired-mass") if path.is_dir())
    if len(folders) != 2:
        raise ValueError(f"Expected exactly two run*-paired-mass directories, found {len(folders)}")
    groups, raw_rows, sources = {}, [], []
    for folder in folders:
        path = folder / "paired-mass.csv"
        data = path.read_bytes()
        rows = list(csv.DictReader(io.StringIO(data.decode("utf-8-sig"))))
        if len(rows) != 64:
            raise ValueError(f"{path}: expected 64 rows, found {len(rows)}")
        run_labels = set()
        by_round = {}
        for line, row in enumerate(rows, 2):
            if None in row or any(value is None for value in row.values()):
                raise ValueError(f"{path}:{line}: malformed CSV")
            run_labels.add(row["run"])
            case = row["case"]
            round_number, order = int(row["round"]), int(row["order"])
            ticks, frequency, iterations = int(row["ticks"]), int(row["freq"]), int(row["iterations"])
            if case not in ("box", "character") or not 0 <= round_number < 8 or not 0 <= order < 4:
                raise ValueError(f"{path}:{line}: unexpected case/round/order")
            if row["version"] != ORDER[order]:
                raise ValueError(f"{path}:{line}: expected {ORDER[order]} at ABBA order {order}")
            if frequency <= 0 or ticks < 0 or iterations != 1024:
                raise ValueError(f"{path}:{line}: invalid ticks/frequency/iterations")
            slot = by_round.setdefault((case, round_number), {})
            if order in slot:
                raise ValueError(f"{path}:{line}: duplicate order in a round")
            us = ticks * 1e6 / frequency / iterations
            slot[order] = us
            raw_rows.append({"process_run": folder.name, "file": path.relative_to(root).as_posix(),
                             "line": line, "values": row, "us_per_call": us})
        if len(run_labels) != 1:
            raise ValueError(f"{path}: inconsistent run labels {run_labels}")
        for case in ("box", "character"):
            for round_number in range(8):
                slot = by_round.get((case, round_number), {})
                if sorted(slot) != [0, 1, 2, 3]:
                    raise ValueError(f"{path}: missing ABBA batch for {case} round {round_number}")
                groups[(folder.name, case, round_number)] = slot
        environment_path = folder / "paired-mass-environment.txt"
        environment = environment_path.read_text(encoding="utf-8-sig")
        sources.append({"process_run": folder.name, "csv_run_label": next(iter(run_labels)),
                        "csv": path.relative_to(root).as_posix(), "sha256": hashlib.sha256(data).hexdigest(),
                        "environment_file": environment_path.relative_to(root).as_posix(), "environment": environment})
    return groups, raw_rows, sources


def aggregate(groups):
    rounds, runs, combined = [], [], []
    for (run, case, round_number), samples in sorted(groups.items()):
        baseline, candidate = mean((samples[0], samples[3])), mean((samples[1], samples[2]))
        delta = candidate - baseline
        rounds.append({"level": "round", "run": run, "case": case, "round": round_number,
                       "baseline_mean_us": baseline, "candidate_mean_us": candidate,
                       "paired_delta_us": delta, "paired_delta_percent": delta / baseline * 100 if baseline else None})
    for run, case in sorted({(key[0], key[1]) for key in groups}):
        selected = [row for row in rounds if row["run"] == run and row["case"] == case]
        baseline = [groups[(run, case, round_number)][order] for round_number in range(8) for order in (0, 3)]
        candidate = [groups[(run, case, round_number)][order] for round_number in range(8) for order in (1, 2)]
        percentages = [row["paired_delta_percent"] for row in selected]
        baseline_median, candidate_median = median(baseline), median(candidate)
        runs.append({"level": "run", "run": run, "case": case, "round_count": 8, "samples_per_version": 16,
                     "baseline_batch_median_us": baseline_median, "candidate_batch_median_us": candidate_median,
                     "difference_of_batch_medians_us": candidate_median - baseline_median,
                     "paired_delta_us_median": median(row["paired_delta_us"] for row in selected),
                     "paired_delta_percent_median": median(percentages) if all(value is not None for value in percentages) else None})
    metrics = ("baseline_batch_median_us", "candidate_batch_median_us", "difference_of_batch_medians_us",
               "paired_delta_us_median", "paired_delta_percent_median")
    for case in ("box", "character"):
        selected = [row for row in runs if row["case"] == case]
        row = {"level": "two_process", "run": "", "case": case, "process_count": 2,
               "process_runs": [value["run"] for value in selected]}
        for metric in metrics:
            values = [value[metric] for value in selected]
            available = all(value is not None for value in values)
            row[metric] = median(values) if available else None
            row[metric + "_min_run"] = min(values) if available else None
            row[metric + "_max_run"] = max(values) if available else None
        # This extra difference uses the two process representatives; it is not the primary paired result.
        row["difference_of_two_process_version_representatives_us"] = row["candidate_batch_median_us"] - row["baseline_batch_median_us"]
        combined.append(row)
    return rounds, runs, combined


def fmt(value):
    return "unavailable" if value is None else f"{value:.6f}"


def report(rounds, runs, combined):
    text = ["# Paired mass — 同一process ABBA比較", ""]
    text.extend("- " + note for note in NOTES)
    text += ["", "## 2 process代表値", "", "主指標は各runのround paired差中央値を2 runで集約した値。", "",
             "| case | paired差中央値 us | run範囲 us | paired差 % | baseline batch中央値 us | candidate batch中央値 us |", "|---|---:|---:|---:|---:|---:|"]
    for row in combined:
        text.append(f"| {row['case']} | {fmt(row['paired_delta_us_median'])} | {fmt(row['paired_delta_us_median_min_run'])}–{fmt(row['paired_delta_us_median_max_run'])} | {fmt(row['paired_delta_percent_median'])} | {fmt(row['baseline_batch_median_us'])} | {fmt(row['candidate_batch_median_us'])} |")
    text += ["", "各version代表値のprocess間範囲。paired差とは独立の記述統計。", "",
             "| case | baseline run中央値 min–max us | candidate run中央値 min–max us | version代表値の差 us |", "|---|---:|---:|---:|"]
    for row in combined:
        text.append(f"| {row['case']} | {fmt(row['baseline_batch_median_us_min_run'])}–{fmt(row['baseline_batch_median_us_max_run'])} | {fmt(row['candidate_batch_median_us_min_run'])}–{fmt(row['candidate_batch_median_us_max_run'])} | {fmt(row['difference_of_two_process_version_representatives_us'])} |")
    text += ["", "## Process別", "", "| run | case | paired差中央値 us | paired差 % | baseline batch中央値 us | candidate batch中央値 us | 2中央値の差 us |", "|---|---|---:|---:|---:|---:|---:|"]
    for row in runs:
        text.append(f"| {row['run']} | {row['case']} | {fmt(row['paired_delta_us_median'])} | {fmt(row['paired_delta_percent_median'])} | {fmt(row['baseline_batch_median_us'])} | {fmt(row['candidate_batch_median_us'])} | {fmt(row['difference_of_batch_medians_us'])} |")
    text += ["", "## Round別paired差", "", "| run | case | round | baseline 2sample平均 us | candidate 2sample平均 us | paired差 us | paired差 % |", "|---|---|---:|---:|---:|---:|---:|"]
    for row in rounds:
        text.append(f"| {row['run']} | {row['case']} | {row['round']} | {fmt(row['baseline_mean_us'])} | {fmt(row['candidate_mean_us'])} | {fmt(row['paired_delta_us'])} | {fmt(row['paired_delta_percent'])} |")
    text += ["", "全128測定行をpaired-summary.jsonのraw_rowsへ保持。元CSVは変更していない。"]
    return "\n".join(text) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="Completed paired-mass directory with two run*-paired-mass children")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.input.resolve()
    groups, raw_rows, sources = read_inputs(root)
    rounds, runs, combined = aggregate(groups)
    args.output.mkdir(parents=True, exist_ok=True)
    payload = {"input_directory": str(root), "notes": NOTES, "sources": sources,
               "round_summaries": rounds, "run_summaries": runs, "two_process_summaries": combined, "raw_rows": raw_rows}
    (args.output / "paired-summary.json").write_text(json.dumps(payload, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    (args.output / "paired-summary.md").write_text(report(rounds, runs, combined), encoding="utf-8")
    all_rows = rounds + runs + combined
    fields = list(dict.fromkeys(key for row in all_rows for key in row if key != "process_runs"))
    with (args.output / "paired-summary.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(all_rows)
    print(f"Summarized {len(raw_rows)} samples / {len(rounds)} paired rounds into {args.output.resolve()}")


if __name__ == "__main__":
    main()
