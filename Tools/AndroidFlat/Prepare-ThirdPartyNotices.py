"""Collect the notices shipped with the Unity/Android inputs used by a recorded APK build."""
import argparse
import hashlib
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--inventory', required=True)
parser.add_argument('--unity-editor', default='C:/Program Files/Unity/Hub/Editor/6000.3.22f1/Editor')
parser.add_argument('--output', default='Assets/StreamingAssets/ThirdPartyNotices.txt')
parser.add_argument('--record', default='Logs/android-flat/github-third-party-notices.json')
args = parser.parse_args()
project = Path(__file__).resolve().parents[2]
inventory = json.loads(Path(args.inventory).read_text(encoding='utf-8-sig'))
paths = inventory['packedAssets']
if not paths or any('NonCommercialDemo' in p or 'StanfordBunny' in p for p in paths):
    raise SystemExit('A commercial Android inventory without noncommercial demo assets is required')
editor = Path(args.unity_editor)
sections = []
records = []

def add(title, source, label):
    text = source.read_text(encoding='utf-8-sig').replace('\r\n', '\n').strip()
    if not text:
        raise SystemExit('Empty upstream notice: ' + label)
    sections.append('\n' + '=' * 72 + '\n' + title + '\nSource: ' + label + '\n' + '=' * 72 + '\n\n' + text + '\n')
    records.append({'source': label, 'sha256': hashlib.sha256(source.read_bytes()).hexdigest()})

sections.append('''WORLD CUTTER for Android - Third-party notices

Publisher: hogehogeo
Support: https://github.com/piyohogeo/zantetsuken-vr/issues

This file preserves notices supplied with the Unity and Android components used
by this project. The complete upstream notice sets are retained conservatively;
some entries concern Editor tools or other platforms and need not be present
in the Android player. Each component keeps its own license. This file does not
relicense the game or its licensed models and animations.

Licensed art and animation acknowledgements
------------------------------------------
ITHappy - Megacity, Casual Characters, Professional Characters
Acquired through CGTrader; the models and their derived runtime data remain
subject to the applicable purchased CGTrader license.
https://www.cgtrader.com/3d-models/exterior/cityscape/megacity
https://www.cgtrader.com/3d-models/character/human-anatomy/characters-3-01
https://www.cgtrader.com/3d-models/character/other/cartoon-characters-3-06-professions
https://www.cgtrader.com/pages/terms-and-conditions

RBStudio - Inertial Locomotion Animations
Acquired through Fab; the animation and its derived runtime data remain subject
to the applicable purchased Fab license.
https://www.fab.com/listings/a7038492-23a2-4a35-91ae-8c4741a12d31
https://www.fab.com/eula

These licensed assets are incorporated into the game. They are not offered as
standalone assets or as part of the public source repository.

Unity software and Android native components
--------------------------------------------
Unity version: UNITY_VERSION
AndroidX GameActivity 4.4.0 and games-frame-pacing 2.1.2, AndroidX support
libraries: Android Open Source Project / Apache License 2.0.
Android libc++ runtime: LLVM Project / Apache License 2.0 with LLVM Exceptions.
The upstream license texts below include the applicable Apache and LLVM terms.
'''.replace('UNITY_VERSION', inventory['unityVersion']))
add('Unity supplied third-party notices', editor / 'Data/Resources/legal.txt', 'Unity Editor ' + inventory['unityVersion'] + '/Data/Resources/legal.txt')
add('Android NDK toolchain notices (including runtime libraries)', editor / 'Data/PlaybackEngines/AndroidPlayer/NDK/toolchains/llvm/prebuilt/windows-x86_64/NOTICE', 'Android NDK/NOTICE')
packages = {}
for root in (project / 'Library/PackageCache').iterdir():
    package_json = root / 'package.json'
    if package_json.is_file():
        meta = json.loads(package_json.read_text(encoding='utf-8-sig'))
        packages[meta['name']] = (root, meta['version'])
used = sorted({p.split('/')[1] for p in paths if p.startswith('Packages/')})
for name in used:
    if name not in packages:
        raise SystemExit('Missing registered package: ' + name)
    root, version = packages[name]
    license_files = sorted((p for p in root.rglob('*') if p.is_file() and not p.name.endswith('.meta')
                            and ('license' in p.name.lower() or 'notice' in p.name.lower())
                            and not any(part.lower() in ('tests', 'editor') or '~' in part for part in p.relative_to(root).parts[:-1])),
                           key=lambda p: str(p.relative_to(root)))
    if not license_files:
        raise SystemExit('No supplied license file: ' + name)
    for path in license_files:
        add(name + ' ' + version + ' - ' + path.name, path, name + '@' + version + '/' + path.relative_to(root).as_posix())
for header in ('ffx_fsr1.hlsl', 'ffx_a.hlsl'):
    fsr = packages['com.unity.render-pipelines.core'][0] / ('Runtime/PostProcessing/Shaders/ffx/' + header)
    lines = fsr.read_text(encoding='utf-8-sig').splitlines()
    start = next(i for i, s in enumerate(lines) if s.startswith('// Copyright (c) 2021 Advanced Micro Devices'))
    end = next(i for i in range(start, len(lines)) if lines[i] == '// ABOUT')
    sections.append('\n' + '=' * 72 + '\nAMD FidelityFX Super Resolution 1 - ' + header + '\n' + '=' * 72 + '\n\n'
                    + '\n'.join(s[3:] if s.startswith('// ') else s[2:] for s in lines[start:end]) + '\n')
    records.append({'source': 'com.unity.render-pipelines.core/' + header + ' (license header)', 'sha256': hashlib.sha256(fsr.read_bytes()).hexdigest()})
output = project / args.output
output.parent.mkdir(parents=True, exist_ok=True)
with output.open('w', encoding='utf-8', newline='\n') as stream:
    stream.write('\n'.join(line.rstrip() for line in '\n'.join(sections).splitlines()) + '\n')
record = {'unityVersion': inventory['unityVersion'], 'sourceInventory': str(Path(args.inventory).as_posix()),
          'packages': used, 'upstreamNotices': records, 'output': args.output,
          'sha256': hashlib.sha256(output.read_bytes()).hexdigest(), 'bytes': output.stat().st_size,
          'nonCommercialAssets': [], 'licensedAssetSourcesSuppliedByUser': True}
record_path = project / args.record
record_path.parent.mkdir(parents=True, exist_ok=True)
with record_path.open('w', encoding='utf-8', newline='\n') as stream:
    json.dump(record, stream, indent=2, ensure_ascii=True)
    stream.write('\n')
print(json.dumps({'packages': len(used), 'upstreamNoticeFiles': len(records), 'bytes': output.stat().st_size, 'sha256': record['sha256']}))
