"""Bounded experiments in the dedicated order worktree. Original repository files are read only.

Install diagnostic-only source patches, then run Editor tests / build once / launch that same Player.
Uninstall restores exactly the saved product and project-setting bytes. Never run alongside an Editor.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TEMPLATES = Path(__file__).resolve().parent
BASE = 'b19ce67a'
LOGS = ROOT / 'Logs/MainThreadOrder20260924'
STATE = LOGS / 'installed'
UNITY = Path(r'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe')
BUILD = 'Assets/Zantetsu/Runtime/PhysicsCut/ProvisionalOwnerBuild.cs'
PUBLICATION = 'Assets/Zantetsu/Runtime/PhysicsCut/ProvisionalPhysicsPublication.cs'
SETTINGS = 'ProjectSettings/ProjectSettings.asset'
PROBE = 'Assets/Zantetsu/Runtime/PhysicsCut/CutOrderProbeTemporary.cs'
TEMP = ROOT / 'Assets/Zantetsu/Tests/PlayMode/MainThreadOrderTemporary'
FILTER = ('Zantetsu.PhysicsCut.PlayModeTests.OrderBenchmark;'
          'Zantetsu.MainThreadAudit.Tests.AxisMassBenchmark;'
          'Zantetsu.PhysicsCut.PlayModeTests.NativeBookkeepingBenchmark;'
          'Zantetsu.PhysicsCut.PlayModeTests.FinalHandoffPlayModeTests')


def save(path, value):
    path.write_bytes((json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))


def git(*args):
    return subprocess.check_output(['git', '-c', 'safe.directory=' + ROOT.as_posix(), *args], cwd=ROOT)


def replace_once(text, before, after):
    if text.count(before) != 1:
        raise RuntimeError('Diagnostic anchor no longer unique: ' + before[:120])
    return text.replace(before, after, 1)


def install():
    if STATE.exists() or TEMP.exists() or (ROOT / PROBE).exists():
        raise RuntimeError('Diagnostic state already exists; inspect it before reinstalling')
    STATE.mkdir(parents=True)
    for path in (BUILD, PUBLICATION, SETTINGS):
        target = STATE / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes((ROOT / path).read_bytes())
    save(STATE / 'manifest.json', {'base': git('rev-parse', BASE).decode().strip(),
                                 'restore': [BUILD, PUBLICATION, SETTINGS]})
    try:
        code = (ROOT / BUILD).read_text(encoding='utf-8')
        positive = 'positive = BuildSide(in input, true, positiveShape, in positiveMass, localRotation, localOffset);'
        negative = 'negative = BuildSide(in input, false, negativeShape, in negativeMass, localRotation, localOffset);'
        order = ('if ((CutOrderProbe.Mode & 1) == 0)\n                {\n'
                 '                    using (CutOrderProbe.Span(8)) ' + positive + '\n'
                 '                    using (CutOrderProbe.Span(9)) ' + negative + '\n'
                 '                }\n                else\n                {\n'
                 '                    using (CutOrderProbe.Span(9)) ' + negative + '\n'
                 '                    using (CutOrderProbe.Span(8)) ' + positive + '\n'
                 '                }')
        code = replace_once(code, positive + '\n                ' + negative, order)
        code = replace_once(code, 'var root = new GameObject(positive ? name + " +" : name + " -");',
                            'var objectsTimer = CutOrderProbe.Span(positive ? 1 : 2);\n'
                            '            GameObject root;\n'
                            '            using (CutOrderProbe.Span(positive ? 10 : 11))\n'
                            '                root = new GameObject(positive ? name + " +" : name + " -");')
        code = replace_once(code, 'var side = new PhysicsOwnerSide(positive, root, shapeFrame, body);',
                            'var side = new PhysicsOwnerSide(positive, root, shapeFrame, body);\n'
                            '                objectsTimer.Dispose();\n'
                            '                using (CutOrderProbe.Span(positive ? 3 : 4))\n                {')
        code = replace_once(code, '                side.ProducedColliderCount = 0;',
                            '                }\n                side.ProducedColliderCount = 0;')
        (ROOT / BUILD).write_bytes(code.encode('utf-8'))
        code = (ROOT / PUBLICATION).read_text(encoding='utf-8')
        code = replace_once(code, 'if (!Establish(negative, false) || !Establish(positive, true))',
                            'if ((CutOrderProbe.Mode & 2) == 0\n'
                            '                    ? (!Establish(negative, false) || !Establish(positive, true))\n'
                            '                    : (!Establish(positive, true) || !Establish(negative, false)))')
        code = replace_once(code, 'side.Root.SetActive(true);',
                            'using (CutOrderProbe.Span(side.positive ? 6 : 7)) side.Root.SetActive(true);')
        (ROOT / PUBLICATION).write_bytes(code.encode('utf-8'))
        shutil.copyfile(TEMPLATES / 'CutOrderProbe.cs', ROOT / PROBE)
        TEMP.mkdir()
        for name in ('OrderBenchmark.cs', 'SandboxCharacterBody.cs', 'NativeBookkeepingBenchmark.cs', 'StandaloneResults.cs'):
            shutil.copyfile(TEMPLATES / name, TEMP / name)
        old = git('show', BASE + ':Assets/Zantetsu/Runtime/PhysicsCut/ProvisionalBoxMass.cs').decode('utf-8')
        (TEMP / 'BaselineProvisionalBoxMass.cs').write_bytes(old.replace('ProvisionalBoxMass', 'BaselineProvisionalBoxMass').encode('utf-8'))
        paired = (ROOT / 'Tools/MainThreadOptimization20260924/PairedMassBenchmark.cs').read_text(encoding='utf-8')
        paired = paired.replace('PairedMassBenchmark', 'AxisMassBenchmark').replace('paired-mass', 'axis-mass')
        paired = paired.replace('8bae0b1e', BASE).replace('Backend=Editor Mono;', 'Backend=' + '{BACKEND};')
        paired = paired.replace('environment.AppendLine("Backend={BACKEND};',
                                'environment.AppendLine("Backend=" + (Application.isEditor ? "Editor Mono" : "IL2CPP") + ";')
        paired = paired.replace('bool baseline = order == 0 || order == 3;',
                                'bool baseline = (order == 0 || order == 3) != (Environment.GetEnvironmentVariable("ORDER_REVERSE") == "1");')
        paired = paired.replace('foreach (bool character in new[] { false, true })',
                                'foreach (bool character in (Environment.GetEnvironmentVariable("ORDER_CHARACTER_FIRST") == "1" ? new[] { true, false } : new[] { false, true }))')
        paired = paired.replace('Assert.That(BitConverter.DoubleToInt64Bits(actual.mass), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected.mass)), label + " mass bits");',
                                'Assert.That(actual.mass, Is.EqualTo(expected.mass).Within(Math.Max(1e-12, Math.Abs(expected.mass) * 2e-6)), label + " mass");')
        paired = paired.replace('Assert.That(math.all(math.asuint(actual) == math.asuint(expected)), Is.True, label + " bits");',
                                'Assert.That(math.all(math.abs(actual - expected) <= math.max(new float3(1e-6f), math.abs(expected) * 2e-6f)), Is.True, label);')
        # Existing helper spells its operands in the opposite order; replace that exact anchor too.
        paired = paired.replace('Assert.That(math.all(math.asuint(expected) == math.asuint(actual)), Is.True, label + " bits");',
                                'Assert.That(math.all(math.abs(actual - expected) <= math.max(new float3(1e-6f), math.abs(expected) * 2e-6f)), Is.True, label);')
        paired = paired.replace('AssertIdentical', 'AssertEquivalent')
        paired = paired.replace('// The optimization reuses identical corner-distance expressions; it changes neither the arithmetic\n            // order of each expression nor the integration order. Require exact bits, with no tolerance relaxation.',
                                '// The slab fast path preserves the mathematical result, with a different floating-point evaluation order.')
        paired = paired.replace('Order=ABBA (A baseline, B candidate), 8 rounds, 1024 calls per batch.',
                                'Order=ABBA or BAAB by ORDER_REVERSE; 8 rounds, 1024 calls per batch.')
        (TEMP / 'AxisMassBenchmark.cs').write_bytes(paired.encode('utf-8'))
        code = (ROOT / SETTINGS).read_text(encoding='utf-8')
        code = replace_once(code, '  runInBackground: 0', '  runInBackground: 1')
        code = replace_once(code, '  fullscreenMode: 1', '  fullscreenMode: 3')
        (ROOT / SETTINGS).write_bytes(code.encode('utf-8'))
    except BaseException:
        uninstall()
        raise
    print('Installed diagnostic patches; product snapshots:', STATE, flush=True)


def uninstall():
    data = json.loads((STATE / 'manifest.json').read_text())
    for path in data['restore']:
        (ROOT / path).write_bytes((STATE / path).read_bytes())
    for path in (ROOT / PROBE, Path(str(ROOT / PROBE) + '.meta'), Path(str(TEMP) + '.meta')):
        if path.exists(): path.unlink()
    if TEMP.exists():
        assert TEMP.resolve() == ROOT / 'Assets/Zantetsu/Tests/PlayMode/MainThreadOrderTemporary'
        TEMP.resolve().relative_to(ROOT)
        shutil.rmtree(TEMP)
    # Keep restoration evidence, with a new unique name, instead of deleting it.
    STATE.rename(STATE.with_name('restored-' + str(time.time_ns())))
    print('Restored product files and settings', flush=True)


def execute(command, folder, reverse, character_first, timeout):
    folder.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(ORDER_OUTPUT=str(folder), ORDER_REVERSE=str(reverse), ORDER_CHARACTER_FIRST=str(character_first),
               ZANTETSU_AUDIT_OUTPUT=str(folder), ZANTETSU_AUDIT_VARIANT='axis')
    standalone = Path(command[0]).resolve() != UNITY.resolve()
    env['ORDER_STANDALONE'] = '1' if standalone else '0'
    sources = {p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
               for base in (ROOT/'Assets/Zantetsu/Runtime/PhysicsCut', TEMP) if base.exists() for p in base.rglob('*.cs')}
    save(folder / 'command.json', {'argv': command, 'base':BASE,'reverse': reverse,'character_first': character_first,'source_sha256': sources})
    if standalone:
        binary = Path(command[0])
        files = [binary, binary.parent/'GameAssembly.dll', binary.parent/'UnityPlayer.dll',
                 binary.parent/(binary.stem+'_Data')/'il2cpp_data/Metadata/global-metadata.dat']
        save(folder / 'binary.json', {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in files})
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = subprocess.SW_HIDE
    started = time.time()
    process = subprocess.Popen(command, cwd=ROOT, env=env, startupinfo=startup)
    save(folder / 'process.json', {'pid':process.pid,'started':started})
    print('Started', folder.name, 'PID', process.pid, flush=True)
    try:
        result = process.wait(timeout=timeout)
    except BaseException:
        if process.poll() is None:
            process.kill()
            process.wait()
        save(folder / 'interrupted.json', {'pid':process.pid})
        raise
    save(folder / 'exit.json', {'exit_code':result,'seconds':time.time()-started})
    xml = ET.parse(folder / 'results.xml').getroot()
    save(folder / 'summary.json', dict(xml.attrib))
    if result != 0 or xml.get('result') != 'Passed' or any(int(xml.get(k,'0')) for k in ('failed','inconclusive','skipped')):
        raise RuntimeError('Run failed: ' + str(folder))
    if int(xml.get('passed', '0')) <= 0: raise RuntimeError('No tests ran')
    after = {p:hashlib.sha256((ROOT/p).read_bytes()).hexdigest() for p in sources}
    if after != sources: raise RuntimeError('Sources changed during Unity execution')
    print('Passed', folder.name, xml.get('passed'), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['install','uninstall','editor','build','player','regression'])
    parser.add_argument('--output', default='run1')
    parser.add_argument('--reverse', type=int, choices=[0,1], default=0)
    parser.add_argument('--character-first', type=int, choices=[0,1], default=0)
    parser.add_argument('--player', help='Previously built executable, below this worktree Logs')
    args = parser.parse_args()
    if ROOT.name != 'zantetsuken-vr-main-thread-order-20260924' or not (ROOT/'.git').is_file():
        raise RuntimeError('Use only the dedicated linked worktree')
    LOGS.mkdir(exist_ok=True)
    if args.action == 'install': return install()
    if args.action == 'uninstall': return uninstall()
    folder = (LOGS / args.output).resolve()
    folder.relative_to(LOGS.resolve())
    if args.action == 'regression':
        if STATE.exists(): raise RuntimeError('Restore product code before regression')
        spec = importlib.util.spec_from_file_location('previous', ROOT/'Tools/MainThreadOptimization20260924/run_experiments.py')
        previous = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(previous)
        cases = [('editmode', 'EditMode', 'Zantetsu.PhysicsCut.Tests;Zantetsu.Rendering.Tests.VpGeometryReferenceTableTests;Zantetsu.Rendering.Tests.VpGeometryDescriptorRegistrationTests;Zantetsu.MeshCut.Tests.LogicalCutLedgerTests')]
        cases += [('playmode','PlayMode', previous.ordinary_play_filter())]
        cases += [('termination'+str(i), 'PlayMode', name) for i,name in enumerate(previous.TERMINATION_TESTS,1)]
        for name, platform, test_filter in cases:
            child = folder / name
            cmd = [str(UNITY),'-batchmode','-projectPath',str(ROOT),'-runTests','-testPlatform',platform,'-testFilter',test_filter,'-testResults',str(child/'results.xml'),'-logFile',str(child/'editor.log')]
            if platform == 'EditMode': cmd.append('-nographics')
            execute(cmd, child, 0, 0, 900)
        return
    if args.action != 'player' and not STATE.exists(): raise RuntimeError('Install diagnostic templates first')
    if args.action == 'player':
        player = Path(args.player).resolve()
        player.relative_to(LOGS.resolve())
        command = [str(player),'-batchmode','-runTests','-testResults',str(folder/'results.xml'),'-logFile',str(folder/'player.log'),'-screen-fullscreen','0','-screen-width','640','-screen-height','400']
        timeout = 180
    else:
        command = [str(UNITY),'-batchmode','-projectPath',str(ROOT),'-runTests','-testPlatform',
                   'StandaloneWindows64' if args.action == 'build' else 'PlayMode',
                   '-testFilter',FILTER,'-testResults',str(folder/'results.xml'),'-logFile',str(folder/'editor.log')]
        if args.action == 'build': command += ['-buildPlayerPath',str(folder/'player')]
        timeout = 2400 if args.action == 'build' else 900
    execute(command, folder, args.reverse, args.character_first, timeout)


if __name__ == '__main__': main()
