#!/usr/bin/env python3
"""Summarize pulled Quest NPC Slash runs as JSON, including failed runs.

Usage: Summarize-NpcSlashDemo.py RUN [RUN ...] [--log PLAYER_LOG] [--output JSON]
CheckHits/CheckFrames JSONL files below RUN are optional. --log adds a pulled
player/logcat log when the development logger was unavailable on Android.
"""
import argparse
import collections
import csv
import json
import math
import re
import statistics
from pathlib import Path


def number(value):
    try:
        n = float(value)
        return n if math.isfinite(n) else None
    except (TypeError, ValueError):
        return None


def integer(value):
    n = number(value)
    return int(n) if n is not None and n.is_integer() else None


def distribution(values):
    values = sorted(v for v in values if v is not None and math.isfinite(v))
    if not values:
        return None
    return {
        "count": len(values), "mean": statistics.mean(values), "min": values[0],
        "p50": values[math.ceil(len(values) * .50) - 1],
        "p95": values[math.ceil(len(values) * .95) - 1],
        "p99": values[math.ceil(len(values) * .99) - 1], "max": values[-1],
    }


def read_text(path, warnings):
    if not path.is_file():
        return None
    try:
        return path.read_text(encoding="utf-8-sig", errors="replace")
    except OSError as e:
        warnings.append(str(path) + ": " + str(e))
        return None


def read_csv(path, warnings):
    if not path.is_file():
        return [], []
    try:
        with path.open(encoding="utf-8-sig", newline="") as stream:
            table = csv.DictReader(stream)
            rows = list(table)
            headers = table.fieldnames or []
        malformed = sum(None in row or any(v is None for v in row.values()) for row in rows)
        if malformed:
            warnings.append("{}: {} rows do not match the header; finite available values still included".format(path, malformed))
        return rows, headers
    except (OSError, csv.Error) as e:
        warnings.append(str(path) + ": " + str(e))
        return [], []


def metric(rows, column, scale=1.0, positive_only=False, unit="ms"):
    raw = [number(row.get(column)) for row in rows]
    usable = [v * scale for v in raw if v is not None and (v > 0 if positive_only else v >= 0)]
    return {
        "sourceColumn": column, "sourceToOutputMultiplier": scale, "unit": unit,
        "available": bool(usable), "validRows": len(usable),
        "zeroRows": sum(v == 0 for v in raw),
        "unavailableRows": len(rows) - len(usable), "distribution": distribution(usable),
    }


def counter_delta(rows, column):
    values = [integer(row.get(column)) for row in rows]
    known = [v for v in values if v is not None and v >= 0]
    decreases = sum(b < a for a, b in zip(known, known[1:]))
    return {"sourceColumn": column, "validRows": len(known), "first": known[0] if known else None,
            "last": known[-1] if known else None, "counterDecreases": decreases,
            "delta": known[-1] - known[0] if len(known) >= 2 and not decreases else None}


# Counts among the columns after roomCommittedBytes, not ProfilerRecorder durations:
# the plugin route's per-row counts (nBlocksInFlight a level, the rest differences)
# and the AppSW sections' calls of the row's frame (-1 = not known, counted unavailable).
COUNT_COLUMNS = frozenset((
    "nPointersAsked", "nPointersKept", "nBodyIssues", "nCasterIssues", "nNotDrawn", "nBuffersRetired", "nBlocksInFlight",
    "nMotionIssues", "nClipMotionTaken", "nClipMotionKept",
    "appSwCullCalls", "appSwRecordCalls", "appSwNativeCullCalls", "appSwMotionClipKeepCalls",
))


def summarize_frames(rows, headers):
    # Prop Slash writes FrameTimingManager values in ms but XR API values in
    # seconds, WITHOUT converting them despite the xr*Ms column names.
    specs = {
        "deltaMs": ("delta", 1000, True), "ftCpuMs": ("ftCpuMs", 1, True),
        "ftMainMs": ("ftMainMs", 1, True), "ftMainPresentWaitMs": ("ftMainPresentWaitMs", 1, False),
        "ftRenderMs": ("ftRenderMs", 1, True), "ftGpuMs": ("ftGpuMs", 1, True),
        "xrAppGpuMs": ("xrAppGpuMs", 1000, True), "xrCompositorGpuMs": ("xrCompositorGpuMs", 1000, True),
        "realMinusRecordedMs": ("realMinusRecorded", 1000, False),
    }
    metrics = {name: metric(rows, col, scale, positive) for name, (col, scale, positive) in specs.items()}
    for name in ("xrAppGpuMs", "xrCompositorGpuMs"):
        values = [number(r.get(name)) for r in rows]
        values = [v for v in values if v is not None and v > 0]
        metrics[name]["distinctReportedValues"] = len(set(values))
        metrics[name]["reportedValueChanges"] = sum(a != b for a, b in zip(values, values[1:]))
        metrics[name]["samplingNote"] = "Repeated latest XR timing values are retained; these are not synchronized per-frame GPU measurements."
    # A signed clock offset is meaningful; retain small negative offsets too.
    offset = [number(r.get("realMinusRecorded")) for r in rows]
    metrics["realMinusRecordedMs"]["distribution"] = distribution([v * 1000 for v in offset if v is not None])
    metrics["realMinusRecordedMs"]["validRows"] = sum(v is not None for v in offset)
    metrics["realMinusRecordedMs"]["unavailableRows"] = sum(v is None for v in offset)
    metrics["realMinusRecordedMs"]["available"] = any(v is not None for v in offset)
    # Existing instrumentation's *Ms columns already convert seconds to ms.
    for column in headers:
        if column.endswith("Ms") and column not in specs:
            metrics[column] = metric(rows, column)
    # The columns after roomCommittedBytes: the plugin route's counts and the AppSW
    # sections' calls (counts, written before the markers), then the ProfilerRecorder
    # values. GC allocation is bytes, duration markers are ns; counters are not durations.
    marker_columns = headers[headers.index("roomCommittedBytes") + 1:] if "roomCommittedBytes" in headers else []
    time_markers, counters = {}, {}
    for column in marker_columns:
        if column in COUNT_COLUMNS:
            counters[column] = metric(rows, column, 1, unit="count")
        elif column == "GC Allocated In Frame" or column.endswith(" Count") or column.endswith(" Bytes"):
            counters[column] = metric(rows, column, unit="bytes" if "Bytes" in column or column == "GC Allocated In Frame" else "count")
        else:
            time_markers[column] = metric(rows, column, 1e-6)
    delta = metrics["deltaMs"]["distribution"]
    gc_column = "gcCountAtRow" if "gcCountAtRow" in headers else "gcCount"
    hitches = {}
    for hz in (45, 30, 22.5, 15):
        threshold = 1000 / hz
        hitches["over{:.5g}Ms".format(threshold)] = {
            "thresholdMs": threshold,
            "count": sum((number(r.get("delta")) or 0) * 1000 > threshold for r in rows),
        }
    slow = sorted((r for r in rows if number(r.get("delta")) is not None),
                  key=lambda r: number(r["delta"]), reverse=True)[:20]
    slow_rows = []
    for r in slow:
        def scaled(column, factor=1):
            v = number(r.get(column))
            return v * factor if v is not None else None
        slow_rows.append({"frame": integer(r.get("frame")), "phase": r.get("phase"),
                          "realSeconds": scaled("real"), "fed": integer(r.get("fed")),
                          "deltaMs": scaled("delta", 1000), "ftMainMs": scaled("ftMainMs"),
                          "ftMainPresentWaitMs": scaled("ftMainPresentWaitMs"),
                          "xrAppGpuMs": scaled("xrAppGpuMs", 1000), "hits": integer(r.get("hits")),
                          "gcCount": integer(r.get(gc_column))})
    gc_alloc = counters.get("GC Allocated In Frame")
    return {
        "rows": len(rows), "allRowsIncluded": True, "phaseRows": dict(collections.Counter(r.get("phase", "") for r in rows)),
        "metrics": metrics, "profilerMarkersMs": time_markers, "profilerCounters": counters,
        "fpsFromMeanDelta": 1000 / delta["mean"] if delta and delta["mean"] > 0 else None,
        "summedDeltaSeconds": sum(number(r.get("delta")) or 0 for r in rows),
        "gcCollectionsBetweenFirstAndLastMeasuredRow": counter_delta(rows, gc_column),
        "gcAllocatedBytes": {"total": sum(number(r.get("GC Allocated In Frame")) or 0 for r in rows
                                             if (number(r.get("GC Allocated In Frame")) or 0) >= 0),
                             "available": bool(gc_alloc and gc_alloc["available"]),
                             "framesWithAllocation": sum((number(r.get("GC Allocated In Frame")) or 0) > 0 for r in rows)},
        "xrDroppedBetweenFirstAndLastMeasuredRow": counter_delta(rows, "xrDropped"),
        "hitches": hitches, "slowestFrames": slow_rows,
        "notes": ["ftMainMs includes waiting; it is not active CPU work.",
                  "ftRenderMs and ftGpuMs zero values are unavailable, not measured zero cost.",
                  "XR GPU source columns contain seconds despite their Ms suffix; converted by 1000.",
                  "Profiler duration markers include their children; do not add parent and child markers.",
                  "FrameTimingManager/XR latest timings can refer to earlier frames; same-row delta and GPU/FT values are not a synchronized trace.",
                  "No startup, capture, GC or ending rows are removed; missing/negative/nonfinite metrics are counted unavailable."],
    }


def parse_result(run_text, demo_text):
    text = run_text or ""
    line = lambda prefix: next((s for s in text.splitlines() if s.startswith(prefix)), None)
    run_line, checks_line = line("run: "), line("checks: ")
    completed = True if run_line and run_line.startswith("run: completed") else False if run_line else None
    checks = "passed" if checks_line and checks_line.startswith("checks: passed") else "failed" if checks_line and checks_line.startswith("checks: FAILED") else "not_run" if checks_line else "unavailable"
    match = re.search(r"^code:\s*(-?\d+)", text, re.M)
    code = int(match.group(1)) if match else None
    demo_match = re.search(r"^run code=(-?\d+)", demo_text or "", re.M)
    demo_code = int(demo_match.group(1)) if demo_match else None
    not_started = (demo_text or "").startswith("NOT started:")
    if not_started or completed is False or checks == "failed" or any(v is not None and v != 0 for v in (code, demo_code)):
        status = "failed"
    elif completed is True and checks == "passed" and code == 0:
        status = "passed"
    elif completed is True:
        status = "completed_checks_not_confirmed"
    else:
        status = "unavailable"
    return {"status": status, "code": code, "demoCode": demo_code, "completed": completed,
            "checks": checks, "runLine": run_line, "checksLine": checks_line,
            "notStarted": not_started, "demoResult": (demo_text or "").splitlines(),
            "runResult": text.splitlines(), "codesAgree": code == demo_code if code is not None and demo_code is not None else None}


def fields(record):
    value = record.get("value")
    if isinstance(value, dict):
        return value
    if isinstance(value, list) and len(value) % 2 == 0:
        return dict(zip(value[::2], value[1::2]))
    return {}


def load_recordings(directory, warnings, logger_directories=()):
    groups = collections.defaultdict(list)
    run_markers = collections.defaultdict(list)
    paths = set(directory.rglob("*.jsonl"))
    for root in logger_directories:
        if root.is_dir():
            paths.update(root.rglob("*.jsonl"))
        elif root.is_file():
            paths.add(root)
        else:
            warnings.append("Logger directory/file does not exist: " + str(root))
    for path in sorted(paths):
        bad = 0
        try:
            with path.open(encoding="utf-8-sig", errors="replace") as stream:
                for line_no, line in enumerate(stream, 1):
                    if not line.strip():
                        continue
                    try:
                        r = json.loads(line)
                        if not isinstance(r, dict):
                            continue
                        if r.get("writer_id") == "QuestNpcSlashDemo" and r.get("tag") in ("npc_run_begin", "npc_run_end") and directory.name in str(r.get("value", "")):
                            run_markers[str(path)].append(r)
                        if r.get("writer_id") not in ("CheckHits", "CheckFrames"):
                            continue
                        d = fields(r)
                        groups[(str(path), r["writer_id"], d.get("recording"))].append((r, d))
                    except (ValueError, TypeError):
                        bad += 1
            if bad:
                warnings.append("{}: {} malformed JSONL lines ignored".format(path, bad))
        except OSError as e:
            warnings.append(str(path) + ": " + str(e))
    hits = [(key, recs) for key, recs in groups.items() if key[1] == "CheckHits"]
    # Device paths differ from local pull paths, but the generated run directory
    # (timestamp/mode/GUID) is stable. Never silently combine multiple sessions.
    matching = [(k, r) for k, r in hits if any(directory.name in str(d.get("directory", "")) for rec, d in r if rec.get("tag") == "start")]
    candidates = matching or hits
    if len(candidates) != 1:
        if candidates:
            warnings.append("Multiple CheckHits recordings do not uniquely identify this run; not aggregated")
        return None, [], [], []
    key, recs = candidates[0]
    sequences = [integer(d.get("seq")) for _, d in recs]
    known = [s for s in sequences if s is not None]
    summaries = [d for r, d in recs if r.get("tag") == "summary"]
    summary = summaries[0] if len(summaries) == 1 else None
    last = integer(summary.get("seq")) if summary else None
    seq_complete = last is not None and sorted(known) == list(range(1, last + 1))
    refused = sum(integer(summary.get(k)) or 0 for k in ("queueFull", "unavailable", "invalidValue", "disabled")) if summary else None
    ops = [dict(d) for r, d in recs if r.get("tag") == "op"]
    hit_rows = [dict(d) for r, d in recs if r.get("tag") == "hit"]
    tally_complete = bool(summary and integer(summary.get("opsPlanned")) == len(ops)
                          and integer(summary.get("opsAttempted")) == len(ops)
                          and integer(summary.get("opsAccepted")) == len(ops))
    info = {"source": key[0], "recording": key[2], "records": len(recs), "summary": summary,
            "sequenceComplete": seq_complete, "refusedBeforeSummary": refused,
            "opsTallyMatchesSaved": tally_complete,
            "opCoverage": "complete_in_saved_recording" if seq_complete and refused == 0 and tally_complete else "not_confirmed",
            "npcRunMarkers": run_markers.get(key[0], []),
            "npcRunEndPersisted": any(r.get("tag") == "npc_run_end" for r in run_markers.get(key[0], []))}
    check_frames = []
    for frame_key, frame_recs in groups.items():
        if frame_key[0] == key[0] and frame_key[1] == "CheckFrames" and any(d.get("hitRecording") == key[2] for r, d in frame_recs if r.get("tag") == "start"):
            check_frames.extend(d for r, d in frame_recs if r.get("tag") == "frame")
    return info, ops, hit_rows, check_frames


def log_observations(paths, warnings):
    lines, sources = [], []
    for path in paths:
        text = read_text(path, warnings)
        if text is not None:
            sources.append(str(path))
            lines.extend(text.splitlines())
    # Logcat buffers can repeat the same saved text; identical observation lines
    # are read once. Different observations are never collapsed by operation ID.
    lines = list(dict.fromkeys(lines))
    summaries = [s for s in lines if "summary: fed=" in s]
    if len(summaries) > 1:
        warnings.append("Player logs contain multiple run summaries; optional per-operation log fallback not used")
        return {"sources": sources, "coverage": "ambiguous_multiple_runs"}, [], []
    info = {"sources": sources, "coverage": "observed_log_lines_only", "summaryLine": summaries[0] if summaries else None}
    if summaries:
        for key in ("fed", "hits", "rootCuts", "childCuts", "geometryFaults"):
            m = re.search(r"\b" + key + r"=(\d+)", summaries[0])
            info[key] = int(m.group(1)) if m else None
        m = re.search(r"fed=\d+/(\d+)", summaries[0])
        info["loadedRows"] = int(m.group(1)) if m else None
    first_check = next((s for s in lines if "the first Slash (" in s and "accepted on all ten" in s), None)
    if first_check:
        m = re.search(r"the first Slash \((\d+)\).*lineages \((\d+)\)", first_check)
        info["firstSlashCheck"] = {"slash": int(m.group(1)), "rootLineagesAccepted": int(m.group(2)),
                                   "passed": "ok:" in first_check, "line": first_check} if m else {"line": first_check}
    hits = []
    for line in lines:
        m = re.search(r"multi hit: slashId=(\d+) of=(\S+) (child|root) fragment=(\d+) acceptance=(\w+) admission=(\w+) operation=(\d+) frame=(\d+)", line)
        if m:
            hits.append(dict(zip(("slashId", "of", "child", "fragment", "acceptance", "admission", "operation", "eventFrame"),
                                 (int(m[1]), m[2], m[3] == "child", int(m[4]), m[5], m[6], int(m[7]), int(m[8])))))
    ops = []
    for line in lines:
        m = re.search(r"(op(\d+)-\S+): slash=(\d+) fragment=(\d+) accepted@(-?\d+) final@(-?\d+) geometry@(-?\d+)", line)
        if not m:
            continue
        op = {"name": m[1], "operation": int(m[2]), "slash": int(m[3]), "source": int(m[4]),
              "acceptedFrame": int(m[5]), "finalFrame": int(m[6]), "committedFrame": int(m[7]),
              "child": "-child" in m[1], "provisionalFrame": None, "acceptance": None}
        hit = next((h for h in hits if h["operation"] == op["operation"] and h["slashId"] == op["slash"] and h["fragment"] == op["source"]), None)
        if hit:
            op.update({"of": hit["of"], "child": hit["child"], "acceptance": hit["acceptance"]})
            # Published is a directly observed provisional acceptance, not a
            # prediction from final/commit. Pending needs its publication log.
            if hit["acceptance"] == "Published":
                op["provisionalFrame"] = op["acceptedFrame"]
        pending = next((s for s in lines if re.search(r"multi op" + str(op["operation"]) + r" Provisional published after Pending:", s)), None)
        if pending:
            f = re.search(r"frame=(\d+)", pending)
            op["provisionalFrame"] = int(f[1]) if f else None
            op["acceptance"] = "Pending"
        ops.append(op)
    return info, ops, hits


def stage_count(ops, stage):
    frames = [integer(op.get(stage)) for op in ops]
    return {"observed": sum(f is not None and f >= 0 for f in frames),
            "notObserved": sum(f is not None and f < 0 for f in frames),
            "unknown": sum(f is None for f in frames)}


def latency(ops, end):
    diffs, missing, reverse = [], 0, 0
    for op in ops:
        start, finish = integer(op.get("acceptedFrame")), integer(op.get(end))
        if start is None or finish is None or start < 0 or finish < 0:
            missing += 1
        elif finish < start:
            reverse += 1
        else:
            diffs.append(finish - start)
    return {"unit": "observed Unity frames", "distribution": distribution(diffs),
            "histogram": dict(sorted(collections.Counter(diffs).items())),
            "missingStagePairs": missing, "reversedStagePairs": reverse}


def summarize_cuts(ops, hits, coverage):
    slash_ids = [integer(h.get("slashId")) for h in hits] + [integer(op.get("slash")) for op in ops]
    first = min((n for n in slash_ids if n is not None and n > 0), default=None)
    roots = [op for op in ops if integer(op.get("slash")) == first and op.get("child") in (False, 0)] if first else []
    root_names = sorted(set(str(op["of"]) for op in roots if op.get("of") is not None))
    counts = {name: stage_count(ops, field) for name, field in (
        ("accepted", "acceptedFrame"), ("provisional", "provisionalFrame"),
        ("final", "finalFrame"), ("committed", "committedFrame"))}
    return {
        "available": bool(ops or hits), "coverage": coverage, "acceptedCutRecords": len(ops),
        "hitRecords": len(hits), "stages": counts,
        "acceptanceCounts": dict(collections.Counter(op.get("acceptance") or "unknown" for op in ops)),
        "hitAcceptanceCounts": dict(collections.Counter(h.get("acceptance") or "unknown" for h in hits)),
        "acceptedAsPending": sum(op.get("acceptance") == "Pending" for op in ops),
        "pendingAtEnd": counts["provisional"]["notObserved"],
        "uncommittedAtEnd": counts["committed"]["notObserved"],
        "unknownPendingAtEnd": counts["provisional"]["unknown"],
        "ledgerStates": dict(collections.Counter(op.get("ledgerState") or "unknown" for op in ops)),
        "pendingEndedWithoutPublication": sum(bool(op.get("pendingEnd")) for op in ops),
        "unknownPendingEndRecords": sum("pendingEnd" not in op for op in ops),
        "firstSlash": {"slashId": first, "expectedRootLineages": 10, "acceptedRootCutRecords": len(roots),
                       "distinctRootLineages": len(root_names), "rootLineages": root_names,
                       "tenLineagesObserved": len(root_names) == 10 if first is not None else None,
                       "provisional": stage_count(roots, "provisionalFrame"), "final": stage_count(roots, "finalFrame"),
                       "committed": stage_count(roots, "committedFrame")},
        "acceptedToProvisionalFrames": latency(ops, "provisionalFrame"),
        "acceptedToFinalFrames": latency(ops, "finalFrame"), "acceptedToCommitFrames": latency(ops, "committedFrame"),
        "operations": ops,
    }


def summarize_prewarm(directory, warnings):
    before_path = directory / "prewarm.txt"
    if not before_path.is_file():
        before_path = directory / "warm.txt"
    after_path = directory / "prewarm-after-replay.txt"
    before, after = read_text(before_path, warnings), read_text(after_path, warnings)
    result = {"available": before is not None or after is not None,
              "beforeReplay": before.splitlines() if before is not None else None,
              "afterReplay": after.splitlines() if after is not None else None}
    for prefix, text in (("before", before), ("after", after)):
        if text is None:
            continue
        status = re.search(r"native prewarm: complete=(True|False).*?pending=(\d+).*?consumed=(\d+).*?failure=([^\r\n]+)", text)
        result[prefix + "NativeStatus"] = ({"complete": status[1] == "True", "pending": int(status[2]),
                                            "consumed": int(status[3]), "failure": status[4]}
                                           if status else None)
        pipelines = []
        for line in text.splitlines():
            counters = dict((key, int(value)) for key, value in
                            re.findall(r"\b(variants|warmCalls|warmCreates|drawVariantHits|drawMissesAfterWarm)=(\d+)", line))
            if "warmCalls" in counters:
                pipelines.append({"description": line.split(" SPIR-V descriptors=")[0], **counters})
        result[prefix + "Pipelines"] = pipelines
        if prefix == "after":
            drawn = [v for v in pipelines if v.get("drawVariantHits", 0) + v.get("drawMissesAfterWarm", 0) > 0]
            result["replayObservedPipelineCount"] = len(drawn)
            result["replayObservedDrawMissesAfterWarm"] = sum(v.get("drawMissesAfterWarm", 0) for v in pipelines) if pipelines else None
            result["allReplayObservedPipelinesHitWarmCache"] = (all(v.get("drawMissesAfterWarm") == 0 for v in drawn) if drawn else None)
    match = re.search(r"Unity rendered warm frames=(\d+)", before or "")
    result["unityRenderedWarmFrames"] = int(match[1]) if match else None
    result["note"] = "Native pipeline counters describe cache lookup, not GPU duration. Unused prepared pipelines do not prove replay coverage. Startup prewarm is outside comparison/frames.csv."
    return result


def summarize(directory, extra_logs=(), logger_directories=()):
    directory = directory.resolve()
    warnings = []
    if not directory.is_dir():
        warnings.append("Run directory does not exist")
    platform_text = read_text(directory / "platform.txt", warnings)
    config_text = read_text(directory / "quest-config.json", warnings)
    config = None
    if config_text is not None:
        try:
            config = json.loads(config_text)
        except ValueError as e:
            warnings.append("quest-config.json: " + str(e))
    run_path, demo_path = directory / "comparison/run-result.txt", directory / "demo-result.txt"
    functional = parse_result(read_text(run_path, warnings), read_text(demo_path, warnings))
    frame_path = directory / "comparison/frames.csv"
    rows, headers = read_csv(frame_path, warnings)
    if not rows:
        warnings.append("Final comparison/frames.csv missing or empty")
    logger, ops, hits, check_frames = load_recordings(directory, warnings, logger_directories)
    log_paths = set(extra_logs)
    for path in directory.rglob("*"):
        if path.is_file() and (path.suffix.lower() == ".log" or
                               (path.suffix.lower() == ".txt" and any(s in path.name.lower() for s in ("logcat", "player-log", "playerlog")))):
            log_paths.add(path)
    log_info, log_ops, log_hits = log_observations(sorted(log_paths), warnings)
    if logger is None:
        ops, hits = log_ops, log_hits
        coverage = log_info["coverage"] if ops or hits else "unavailable"
        warnings.append("CheckHits JSONL unavailable; per-cut stages may be incomplete in player logs")
    else:
        coverage = logger["opCoverage"]
    if not ops:
        warnings.append("No per-cut operation records; total cut stages and latency are unavailable")
    cuts = summarize_cuts(ops, hits, coverage)
    cuts["reportedFirstSlashCheck"] = log_info.get("firstSlashCheck")
    cuts["logger"] = logger
    cuts["playerLog"] = log_info
    if check_frames:
        cuts["lastObservedCheckFrame"] = check_frames[-1]
    expected = integer(config.get("inputRows")) if isinstance(config, dict) else None
    expected = expected if expected is not None else 512
    fed_values = [integer(r.get("fed")) for r in rows]
    fed = max((v for v in fed_values if v is not None), default=log_info.get("fed"))
    replay = {"expectedRows": expected, "fedRows": fed, "allExpectedRowsFed": fed == expected if fed is not None else None,
              "512RowRun": expected == 512, "configuredInputStart": config.get("inputStart") if isinstance(config, dict) else None,
              "mode": "recorded timestamps in real time" if platform_text and "replay=recorded timestamps in real time" in platform_text else None}
    if rows:
        advances = [b - a for a, b in zip(fed_values, fed_values[1:]) if a is not None and b is not None]
        replay["rowsFedPerMeasuredFrame"] = dict(sorted(collections.Counter(advances).items()))
        replay["maxRowsFedInMeasuredFrame"] = max(advances, default=None)
    status = "analyzed_functional_failure" if functional["status"] == "failed" else "analyzed" if rows else "incomplete_data"
    return {"directory": str(directory), "analysisStatus": status, "warnings": warnings,
            "platform": platform_text.splitlines() if platform_text is not None else None, "config": config,
            "functional": functional, "replay": replay, "cuts": cuts,
            "prewarm": summarize_prewarm(directory, warnings),
            "frames": summarize_frames(rows, headers),
            "files": {"frames": str(frame_path) if frame_path.is_file() else None,
                      "runResult": str(run_path) if run_path.is_file() else None,
                      "demoResult": str(demo_path) if demo_path.is_file() else None,
                      "rawProfilerBytes": (directory / "profile.raw").stat().st_size if (directory / "profile.raw").is_file() else None}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runs", type=Path, nargs="+")
    parser.add_argument("--log", "--logcat", type=Path, action="append", default=[], help="Optional pulled player/logcat log; repeatable")
    parser.add_argument("--logger-directory", "--loggerDirectory", type=Path, action="append", default=[],
                        help="Optional pulled logger directory or JSONL file; repeatable")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    payload = {"schema": "quest-npc-slash-summary-v1", "percentile": "nearest rank", "allRowsIncluded": True,
               "runs": [summarize(path, args.log, args.logger_directory) for path in args.runs]}
    encoded = (json.dumps(payload, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(encoded)
    # Functional failure is a measured result, not a parser failure. JSON remains
    # available on stdout and in --output; no success-only gate discards the data.
    print(encoded.decode("utf-8"), end="")


if __name__ == "__main__":
    main()
