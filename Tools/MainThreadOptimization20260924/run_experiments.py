"""Run bounded Unity Editor comparisons in this worktree, restoring candidate files on every exit.

The source revisions change between processes, never in an open Editor. No original-repository
working file, global setting, old evidence, or unrelated Unity process is changed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
BASE = "8bae0b1e1c7f530a19a91f00603404d291170980"
UNITY = Path(r"C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe")
GROUPS = {
    "o1": ["Assets/Zantetsu/Runtime/Rendering/VpGeometryReferenceTable.cs"],
    "view": ["Assets/Zantetsu/Runtime/PhysicsCut/PhysicsOwnerShape.cs",
             "Assets/Zantetsu/Runtime/PhysicsCut/ProvisionalOwnerBuild.cs"],
    "colliders": ["Assets/Zantetsu/Runtime/PhysicsCut/PhysicsOwnerBuild.cs"],
    "mass": ["Assets/Zantetsu/Runtime/PhysicsCut/ProvisionalBoxMass.cs"],
}
FILES = [p for group in GROUPS.values() for p in group]
PLAY_NAMESPACE = "Zantetsu.PhysicsCut.PlayModeTests."
ROOT_FIXTURE = PLAY_NAMESPACE + "CutWorldRootPlayModeTests."
TERMINATION_TESTS = [
    ROOT_FIXTURE + "AfterTheTerminationRequest_AFinishedCutIsNotPublished_EvenLaterInThatUpdate",
    ROOT_FIXTURE + "AGeometryThatCannotBeCut_RequestsThePlayersTermination_Once",
]


def ordinary_play_filter():
    # A termination deliberately retains resources and blocks this fixture's later cases.
    # This inventory is pinned to BASE; the expected pass count detects inventory drift.
    fixtures = ["CutWorldRootEndingGuardTests", "CutWorldSandboxScenePlayModeTests",
                "FinalColliderReuseEndingGuardTests", "FinalColliderReusePlayModeTests",
                "FinalHandoffPlayModeTests", "ProvisionalMassFlagActivationPlayModeTests",
                "ProvisionalPairPlayModeTests", "ProvisionalPublishedContractPlayModeTests"]
    methods = ["ACaseThatNeverEndsItsWorld_IsEndedAndCollectedByTheTeardown",
               "AnAskTakenUpByTheUpdateLoop_IsPublishedAndDrawnInThatFrame",
               "EndingTheWorldWithAWorkHeld_FreesNothingEarly_AndFinishesOnTheOrdinaryFrames",
               "LettingOneThrough_KeepsWhatThatWorkEndedAs",
               "TheHoldGivesBackTheCompletionItWasGiven_AndEachWorkOnlyOnce",
               "TheUpdateLoopCarriesACutToItsCommit_AndAChildCanBeCutAgain",
               "TwoOwnersCutAtOnce_BothReachTheirDestination_AndNeitherWaitsForTheOther"]
    return ";".join([PLAY_NAMESPACE + name for name in fixtures]
                    + [ROOT_FIXTURE + name for name in methods])


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, data):
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")


def run_unity(folder, variant, test_filter, platform="EditMode", timeout=900,
              expected_inconclusive=0, expected_passed=None):
    folder.mkdir()
    environment = os.environ.copy()
    environment["ZANTETSU_AUDIT_OUTPUT"] = str(folder)
    environment["ZANTETSU_AUDIT_VARIANT"] = variant
    command = [str(UNITY), "-batchmode", "-projectPath", str(ROOT), "-runTests",
               "-testPlatform", platform, "-testFilter", test_filter,
               "-testResults", str(folder / "results.xml"), "-logFile", str(folder / "editor.log")]
    if platform == "EditMode":
        command.append("-nographics")
    hashes = {p: digest((ROOT / p).read_bytes()) for p in FILES}
    write_json(folder / "command.json", {"argv": command, "variant": variant,
               "source_sha256": hashes, "timeout_seconds": timeout})
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = subprocess.SW_HIDE
    started = time.time()
    process = subprocess.Popen(command, cwd=ROOT, env=environment, startupinfo=startup)
    write_json(folder / "process.json", {"pid": process.pid, "started_unix": started})
    print("Started", folder.name, "PID", process.pid, flush=True)
    try:
        result = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        process.kill()  # Only the Unity process created above.
        process.wait()
        write_json(folder / "termination.json", {"external_timeout": True, "pid": process.pid})
        raise
    except BaseException:
        # Do not let main() restore sources underneath a Unity process after Ctrl+C or an error.
        if process.poll() is None:
            process.kill()
            process.wait()
        write_json(folder / "termination.json", {"runner_interrupted": True, "pid": process.pid})
        raise
    write_json(folder / "exit.json", {"exit_code": result, "seconds": time.time() - started,
                                     "pid": process.pid, "normal_exit": True})
    xml = ET.parse(folder / "results.xml").getroot()
    summary = dict(xml.attrib)
    write_json(folder / "summary.json", summary)
    expected_exit = 2 if expected_inconclusive else 0
    if (result != expected_exit or xml.get("result") != "Passed"
            or int(xml.get("failed", "0")) or int(xml.get("skipped", "0"))
            or int(xml.get("inconclusive", "0")) != expected_inconclusive
            or (expected_passed is not None and int(xml.get("passed", "0")) != expected_passed)):
        raise RuntimeError("Unity run failed: " + str(folder))
    if int(xml.get("total", "0")) <= 0:
        raise RuntimeError("No tests ran: " + str(folder))
    after = {p: digest((ROOT / p).read_bytes()) for p in FILES}
    if hashes != after:
        raise RuntimeError("Runtime sources changed while Unity ran")
    print("Checked", folder.name, xml.get("passed"), "passed;", expected_inconclusive,
          "expected inconclusive", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, help="New directory below this worktree's Logs")
    parser.add_argument("--sequence", choices=["smoke", "comparison", "paired-mass", "regression", "playmode", "playmode-baseline"], required=True)
    options = parser.parse_args()
    output = (ROOT / options.output).resolve()
    if ROOT.name != "zantetsuken-vr-main-thread-20260924" or not (ROOT / ".git").is_file():
        raise RuntimeError("This runner is limited to the dedicated experiment worktree")
    output.relative_to((ROOT / "Logs").resolve())
    output.mkdir(parents=True, exist_ok=False)
    baseline = {}
    candidate = {}
    for path in FILES:
        baseline[path] = subprocess.check_output(["git", "-c", "safe.directory=" + ROOT.as_posix(),
                                                  "show", BASE + ":" + path], cwd=ROOT)
        candidate[path] = (ROOT / path).read_bytes()
    for label, snapshot in [("baseline", baseline), ("candidate", candidate)]:
        for path, data in snapshot.items():
            target = output / label / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    write_json(output / "plan.json", {"base": BASE, "sequence": options.sequence,
                                      "groups": GROUPS, "root": str(ROOT)})
    temporary = ROOT / "Assets/Zantetsu/Tests/EditMode/MainThreadAuditTemporary"
    if temporary.exists():
        raise RuntimeError("A temporary harness already exists; inspect before retrying")
    temporary.mkdir()
    template = Path(__file__).resolve().parent
    try:
        for name in ("PhysicsMainBenchmark.cs", "RegistrationBenchmark.cs", "SandboxCharacterBody.cs"):
            shutil.copyfile(template / name, temporary / name)
        if options.sequence == "paired-mass":
            shutil.copyfile(template / "PairedMassBenchmark.cs", temporary / "PairedMassBenchmark.cs")
            old_mass = baseline[GROUPS["mass"][0]].decode("utf-8")
            (temporary / "BaselineProvisionalBoxMass.cs").write_text(
                old_mass.replace("ProvisionalBoxMass", "BaselineProvisionalBoxMass"), encoding="utf-8")
        if options.sequence == "paired-mass":
            for index in (1, 2):
                run_unity(output / ("run%d-paired-mass" % index), "all",
                          "Zantetsu.MainThreadAudit.Tests.PairedMassBenchmark", expected_passed=1)
        elif options.sequence == "playmode-baseline":
            for path in FILES:
                (ROOT / path).write_bytes(baseline[path])
            run_unity(output / "playmode-baseline", "baseline", PLAY_NAMESPACE[:-1], "PlayMode",
                      expected_inconclusive=7, expected_passed=34)
        elif options.sequence in ("regression", "playmode"):
            if options.sequence == "regression":
                run_unity(output / "editmode", "all", "Zantetsu.PhysicsCut.Tests;Zantetsu.Rendering.Tests.VpGeometryReferenceTableTests;Zantetsu.Rendering.Tests.VpGeometryDescriptorRegistrationTests;Zantetsu.MeshCut.Tests.LogicalCutLedgerTests")
            run_unity(output / "playmode-ordinary", "all", ordinary_play_filter(), "PlayMode", expected_passed=39)
            for index, test in enumerate(TERMINATION_TESTS, 1):
                run_unity(output / ("playmode-termination%d" % index), "all", test, "PlayMode", expected_passed=1)
        else:
            sequence = (["all"] if options.sequence == "smoke" else
                        ["baseline", "o1", "view", "colliders", "mass", "all",
                         "all", "mass", "colliders", "view", "o1", "baseline"])
            for i, variant in enumerate(sequence):
                selected = set(FILES if variant == "all" else GROUPS.get(variant, []))
                for path in FILES:
                    (ROOT / path).write_bytes(candidate[path] if path in selected else baseline[path])
                run_unity(output / ("run%02d-" % (i + 1) + variant), variant, "Zantetsu.MainThreadAudit.Tests")
        write_json(output / "completed.json", {"completed": True, "at_unix": time.time()})
    finally:
        for path, data in candidate.items():
            (ROOT / path).write_bytes(data)
        # This exact new folder was created above; resolve and constrain it before recursive removal.
        resolved = temporary.resolve()
        if resolved != ROOT / "Assets/Zantetsu/Tests/EditMode/MainThreadAuditTemporary":
            raise RuntimeError("Unexpected temporary harness path")
        resolved.relative_to(ROOT)
        shutil.rmtree(resolved)
        meta = Path(str(resolved) + ".meta")
        if meta.exists():
            meta.unlink()


if __name__ == "__main__":
    main()
