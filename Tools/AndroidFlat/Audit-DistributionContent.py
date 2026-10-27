"""Read-only APK/source/staging inspection; never print the external forbidden words.

Requires Python 3.10+, UnityPy, lz4, pyelftools, an audit DLL exposing the
three audit_smolv_* wrappers around upstream SMOL-V, and a DXC disassembly
adapter using the installed Unity dxcompiler.dll.
"""
import argparse
import collections
import ctypes
import hashlib
import io
import json
import re
import struct
import subprocess
import sys
import unicodedata
import zipfile
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CHUNK = 4 * 1024 * 1024


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(CHUNK), b''):
            digest.update(block)
    return digest.hexdigest()


def read_json(path):
    data = path.read_bytes()
    return json.loads(data.decode('utf-16' if data.startswith((b'\xff\xfe', b'\xfe\xff')) else 'utf-8-sig'))


class Audit:
    def __init__(self, words):
        self.words = words
        patterns = []
        longest = 0
        for word in words:
            for encoding in ('utf-8', 'utf-16le', 'utf-16be', 'utf-32le', 'utf-32be'):
                parts, length = [], 0
                for char in word:
                    forms = set()
                    for variant in (char, char.lower(), char.upper(), char.casefold()):
                        forms.add(variant.encode(encoding).lower())
                        utf16 = variant.encode('utf-16be')
                        escaped = ''.join('\\u%04x' % int.from_bytes(utf16[i:i+2], 'big') for i in range(0,len(utf16),2))
                        forms.add(escaped.encode(encoding).lower())
                    parts.append(b'(?:'+b'|'.join(re.escape(value) for value in sorted(forms,key=len,reverse=True))+b')')
                    length += max(map(len,forms))
                patterns.append(b''.join(parts))
                longest = max(longest,length)
        self.pattern = re.compile(b'|'.join(patterns), re.I)
        self.overlap = longest - 1
        self.mask_pattern = re.compile('|'.join(re.escape(word) for word in words), re.I)
        self.counts = collections.Counter()
        self.failures = []
        self.json_seen = set()
        self.decoder = None
        self.decoded_cache = {}
        self.dxil_command = None
        self.dxil_seen = set()

    def mask(self, value):
        return self.mask_pattern.sub('<forbidden>', str(value))

    def stream(self, chunks, scope, label):
        tail = b''
        total = 0
        hits = 0
        for chunk in chunks:
            combined = tail + chunk
            hits += sum(1 for match in self.pattern.finditer(combined) if match.end() > len(tail))
            total += len(chunk)
            tail = combined[-self.overlap:] if self.overlap else b''
        self.counts[scope + '_items'] += 1
        self.counts[scope + '_bytes'] += total
        self.counts['forbidden_matches'] += hits
        if hits:
            self.failures.append({'scope': scope, 'label': self.mask(label), 'matches': hits})
        return hits

    def data(self, data, scope, label):
        return self.stream((bytes(data),), scope, label)

    def text(self, value, scope, label):
        value = unicodedata.normalize('NFKC', value)
        return self.data(value.encode('utf-8', errors='surrogatepass'), scope, label)

    def json_escapes(self, text, label):
        if not text.lstrip('\ufeff \t\r\n').startswith(('{', '[')):
            return
        for match in re.finditer(r'"(?:[^"\\]|\\.)*"', text):
            token = match.group(0)
            if '\\' not in token or token in self.json_seen:
                continue
            self.json_seen.add(token)
            decoded = json.loads(token)
            self.text(decoded, 'decoded_json_strings', label)

    def png(self, data, label):
        if not data.startswith(b'\x89PNG\r\n\x1a\n'):
            return
        offset = 8
        while offset + 12 <= len(data):
            length = struct.unpack_from('>I', data, offset)[0]
            kind = data[offset + 4:offset + 8]
            payload = data[offset + 8:offset + 8 + length]
            if len(payload) != length or offset + length + 12 > len(data):
                raise ValueError('Invalid PNG chunk')
            decoded = None
            if kind in (b'zTXt', b'iCCP'):
                _, rest = payload.split(b'\0', 1)
                if rest[0] != 0:
                    raise ValueError('Unsupported PNG text/profile compression')
                decoded = zlib.decompress(rest[1:])
            elif kind == b'iTXt':
                _, rest = payload.split(b'\0', 1)
                compressed, method = rest[:2]
                _, rest = rest[2:].split(b'\0', 1)
                _, value = rest.split(b'\0', 1)
                if method != 0 or compressed not in (0, 1):
                    raise ValueError('Unsupported PNG international text compression')
                decoded = zlib.decompress(value) if compressed else value
            if decoded is not None:
                self.data(decoded, 'png_metadata', label)
            offset += length + 12
            if kind == b'IEND':
                break
        self.counts['png_files'] += 1

    def elf(self, data, label):
        from elftools.elf.elffile import ELFFile
        elf = ELFFile(io.BytesIO(data))
        self.counts['elf_files'] += 1
        for section in elf.iter_sections():
            self.counts['elf_sections'] += 1
            if section['sh_flags'] & 0x800:
                self.data(section.data(), 'elf_compressed_sections', label)
            elif section.name.startswith('.zdebug'):
                raw = section.data()
                if not raw.startswith(b'ZLIB'):
                    raise ValueError('Unknown compressed debug section')
                expected = struct.unpack('>Q', raw[4:12])[0]
                decoded = zlib.decompress(raw[12:])
                if len(decoded) != expected:
                    raise ValueError('Compressed debug section length differs')
                self.data(decoded, 'elf_compressed_sections', label)

    def dxbc(self, data, label):
        for match in re.finditer(b'DXBC', data):
            start = match.start()
            if start + 32 > len(data):
                continue
            total, count = struct.unpack_from('<II', data, start + 24)
            if count > 1024 or total < 32 + count * 4 or start + total > len(data):
                continue
            chunks = []
            for i in range(count):
                offset = struct.unpack_from('<I', data, start + 32 + 4 * i)[0]
                if offset + 8 > total:
                    break
                kind = data[start + offset:start + offset + 4]
                size = struct.unpack_from('<I', data, start + offset + 4)[0]
                if offset + 8 + size > total:
                    break
                chunks.append(kind)
            else:
                self.counts['dxbc_containers'] += 1
                if b'DXIL' in chunks or b'ILDB' in chunks:
                    self.counts['dxil_containers'] += 1
                    container = data[start:start + total]
                    digest = hashlib.sha256(container).digest()
                    if digest not in self.dxil_seen:
                        executable, library, directory = self.dxil_command
                        path = directory / '_dxil_input.bin'
                        try:
                            path.write_bytes(container)
                            process = subprocess.run([str(executable.resolve()), str(library.resolve()), str(path.resolve())],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
                        finally:
                            if path.exists():
                                path.unlink()
                        if process.returncode != 0 or not process.stdout:
                            raise ValueError('DXC disassembly failed with exit ' + str(process.returncode))
                        self.text(process.stdout.decode('utf-8'), 'dxil_disassembly', label)
                        self.dxil_seen.add(digest)

    @staticmethod
    def valid_spirv(data):
        if len(data) < 20 or len(data) % 4 or data[:4] != b'\x03\x02\x23\x07':
            return False
        word = 5
        while word * 4 < len(data):
            length = struct.unpack_from('<I', data, word * 4)[0] >> 16
            if length == 0:
                return False
            word += length
        return word * 4 == len(data)

    def smolv(self, data, label):
        for match in re.finditer(b'LOMS', data):
            tail = data[match.start():]
            size = self.decoder.size(tail)
            if size == 0:
                self.counts['non_header_smolv_magic'] += 1
                continue
            if size > 64 * 1024 * 1024:
                raise ValueError('SMOL-V decoded size exceeds the audit limit')
            _, target = self.decoder.decode(tail, size)
            if not self.valid_spirv(target):
                raise ValueError('SMOL-V output is not a complete SPIR-V instruction stream')
            key = hashlib.sha256(target).digest()
            length = self.decoded_cache.get(key)
            if length is None:
                # The upstream decoder bounds every output write. With a larger
                # container tail it finishes the module, then refuses another
                # output word. Find the first complete encoded prefix without
                # assuming a Unity container-header version. Complement bytes
                # ensure unwritten bytes cannot look like the expected output.
                initial = bytes(value ^ 0xff for value in target)
                low, high = 24, len(tail)
                while low < high:
                    middle = (low + high) // 2
                    _, output = self.decoder.decode(tail[:middle], size, initial)
                    if output == target:
                        high = middle
                    else:
                        low = middle + 1
                length = low
                self.decoded_cache[key] = length
            valid, decoded = self.decoder.decode(tail[:length], size)
            if not valid or decoded != target:
                raise ValueError('Upstream SMOL-V decoder did not validate the exact encoded prefix')
            self.data(decoded, 'smolv_decoded_spirv', label)
            self.counts['smolv_modules'] += 1

    def gpu(self, data, label):
        self.dxbc(data, label)
        self.smolv(data, label)

    def zip_archive(self, archive):
        names = archive.namelist()
        groups = collections.defaultdict(dict)
        for item in archive.infolist():
            self.text(item.filename, 'zip_names', 'APK entry name')
            self.data(item.comment, 'zip_comments', 'APK entry comment')
            if item.is_dir():
                continue
            with archive.open(item) as stream:
                self.stream(iter(lambda: stream.read(CHUNK), b''), 'zip_payload', item.filename)
            match = re.fullmatch(r'(.+)\.split(\d+)', item.filename)
            if match:
                groups[match.group(1)][int(match.group(2))] = item.filename
            with archive.open(item) as stream:
                head = stream.read(8)
            if head.startswith(b'\x7fELF'):
                self.elf(archive.read(item), item.filename)
            elif head == b'\x89PNG\r\n\x1a\n':
                self.png(archive.read(item), item.filename)
            elif head.startswith(b'PK\x03\x04'):
                nested = archive.read(item)
                if zipfile.is_zipfile(io.BytesIO(nested)):
                    with zipfile.ZipFile(io.BytesIO(nested)) as child:
                        self.zip_archive(child)
        for base, parts in groups.items():
            if base in names:
                if 0 in parts:
                    raise ValueError('Ambiguous split-file first segment')
                parts[0] = base
            if sorted(parts) != list(range(max(parts) + 1)):
                raise ValueError('Incomplete Unity split-file sequence')
            def chunks():
                for index in sorted(parts):
                    with archive.open(parts[index]) as stream:
                        yield from iter(lambda: stream.read(CHUNK), b'')
            self.stream(chunks(), 'unity_joined_split_files', base)
            self.counts['unity_split_segments'] += len(parts)

    def unity(self, apk):
        import UnityPy
        import lz4.block
        environment = UnityPy.load(str(apk))
        todo = list(environment.files.values()) + list(environment.cabs.values())
        seen = set()
        objects = []
        while todo:
            item = todo.pop()
            if id(item) in seen:
                continue
            seen.add(id(item))
            if isinstance(getattr(item, 'objects', None), dict):
                objects.extend(item.objects.values())
                self.counts['unity_serialized_files'] += 1
            todo.extend((getattr(item, 'files', None) or {}).values())
        kinds = collections.Counter(obj.type.name for obj in objects)
        self.counts['unity_objects'] = len(objects)

        def flat(value):
            if isinstance(value, (list, tuple)):
                for child in value:
                    yield from flat(child)
            else:
                yield value

        def walk(value, label, field=''):
            if isinstance(value, str):
                self.text(value, 'unity_decoded_strings', label)
                if field == 'm_Script':
                    try:
                        raw = value.encode('utf-8', errors='surrogateescape')
                    except UnicodeEncodeError:
                        raw = value.encode('utf-8', errors='surrogatepass')
                    self.gpu(raw, label)
                    self.json_escapes(value, label)
            elif isinstance(value, (bytes, bytearray)):
                self.data(value, 'unity_decoded_buffers', label)
                self.gpu(bytes(value), label)
                if field == 'm_Script':
                    self.json_escapes(bytes(value).decode('utf-8', errors='surrogateescape'), label)
            elif isinstance(value, dict):
                for key, child in value.items():
                    if key != 'compressedBlob':
                        walk(child, label, key)
            elif isinstance(value, list):
                if field in ('code', 'm_Script') and all(isinstance(x, int) and 0 <= x <= 255 for x in value):
                    walk(bytes(value), label, field)
                else:
                    for child in value:
                        walk(child, label, field)

        for index, obj in enumerate(objects):
            if obj.type.name not in ('Shader', 'ComputeShader', 'TextAsset', 'MonoScript', 'ResourceManager'):
                continue
            label = obj.type.name + ':' + str(index)
            tree = obj.read_typetree()
            walk(tree, label)
            if obj.type.name == 'Shader':
                blob = bytes(tree['compressedBlob'])
                covered = bytearray(len(blob))
                if len({len(tree[key]) for key in ('offsets','compressedLengths','decompressedLengths')}) != 1:
                    raise ValueError('Shader compression-table platform counts differ')
                for offsets, compressed, decoded in zip(tree['offsets'], tree['compressedLengths'], tree['decompressedLengths']):
                    blocks = []
                    triples = list(zip(flat(offsets), flat(compressed), flat(decoded)))
                    if not (len(triples) == len(list(flat(offsets))) == len(list(flat(compressed))) == len(list(flat(decoded)))):
                        raise ValueError('Shader compression-table shapes differ')
                    for offset, length, expected in triples:
                        if length == expected == 0:
                            continue
                        if offset < 0 or length <= 0 or offset + length > len(blob):
                            raise ValueError('Shader compressed range is invalid')
                        block = lz4.block.decompress(blob[offset:offset + length], uncompressed_size=expected)
                        if len(block) != expected:
                            raise ValueError('Shader decompressed size differs')
                        self.data(block, 'shader_lz4', label)
                        blocks.append(block)
                        covered[offset:offset + length] = b'\x01' * length
                    self.gpu(b''.join(blocks), label)
                if any(byte and not covered[i] for i, byte in enumerate(blob)):
                    raise ValueError('Unmapped nonzero shader compressed bytes')
            self.counts['unity_' + obj.type.name + '_decoded'] += 1
        return {'unitypy': UnityPy.__version__, 'object_types': dict(kinds)}


class Smolv:
    def __init__(self, path):
        self.library = ctypes.CDLL(str(path.resolve()))
        self.library.audit_smolv_size.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
        self.library.audit_smolv_size.restype = ctypes.c_size_t
        self.library.audit_smolv_decode.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t]
        self.library.audit_smolv_decode.restype = ctypes.c_int
        self.library.audit_smolv_encode.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t]
        self.library.audit_smolv_encode.restype = ctypes.c_size_t

    def size(self, data):
        return self.library.audit_smolv_size(data, len(data))

    def decode(self, data, size, initial=None):
        output = ctypes.create_string_buffer(size)
        if initial is not None:
            ctypes.memmove(output, initial, size)
        valid = self.library.audit_smolv_decode(data, len(data), output, size)
        return bool(valid), output.raw

    def encode(self, data):
        size = self.library.audit_smolv_encode(data, len(data), None, 0)
        if not size:
            raise ValueError('SMOL-V control encoding failed')
        output = ctypes.create_string_buffer(size)
        if self.library.audit_smolv_encode(data, len(data), output, size) != size:
            raise ValueError('SMOL-V control encoding length differs')
        return output.raw


def controls(words, decoder, dxil_command):
    import lz4.block
    tested = 0
    word = words[0]
    for encoding in ('utf-8', 'utf-16le', 'utf-16be', 'utf-32le', 'utf-32be'):
        data = word.swapcase().encode(encoding)
        scanner = Audit(words)
        if scanner.stream((data[:len(data)//2], data[len(data)//2:]), 'control', 'split-encoding') == 0:
            raise ValueError('Positive encoding/boundary control failed')
        tested += 1
        escaped = ''.join(('\\u%04x' % ord(char)) if index % 2 else char for index,char in enumerate(word))
        encoded = escaped.encode(encoding)
        scanner = Audit(words)
        if scanner.stream((encoded[:len(encoded)//2],encoded[len(encoded)//2:]), 'control', 'mixed Unicode escape boundary') == 0:
            raise ValueError('Mixed Unicode escape boundary control failed')
        tested += 1
    escaped = json.dumps({'value': ''.join('\\u%04x' % ord(c) for c in word)})
    # Two layers model escaped JSON text inside a serialized TextAsset.
    scanner = Audit(words)
    inner = json.loads(escaped)['value']
    scanner.json_escapes('{"value":"' + inner + '"}', 'JSON control')
    if not scanner.failures:
        raise ValueError('Escaped JSON positive control failed')
    tested += 1
    raw = word.encode('utf-8')
    scanner = Audit(words)
    scanner.data(lz4.block.decompress(lz4.block.compress(raw, store_size=False), uncompressed_size=len(raw)), 'control', 'LZ4')
    if not scanner.failures:
        raise ValueError('LZ4 positive control failed')
    tested += 1
    padded = raw + b'\0'
    padded += b'\0' * (-len(padded) % 4)
    spirv = struct.pack('<5I', 0x07230203, 0x10000, 0, 2, 0) + struct.pack('<2I', ((2+len(padded)//4)<<16)|7, 1) + padded
    encoded = decoder.encode(spirv)
    scanner = Audit(words)
    scanner.decoder = decoder
    scanner.smolv(encoded + b'\xaa\xbb\xcc\xdd', 'SMOL-V positive control')
    if not scanner.failures or scanner.counts['smolv_modules'] != 1:
        raise ValueError('SMOL-V positive control failed')
    tested += 1
    executable, library, _ = dxil_command
    process = subprocess.run([str(executable.resolve()), str(library.resolve()), '--control'], input=raw,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
    scanner = Audit(words)
    if process.returncode != 0 or not process.stdout or scanner.data(process.stdout, 'control', 'DXIL') == 0:
        raise ValueError('DXIL disassembly positive control failed')
    tested += 1
    scanner = Audit(words)
    if scanner.data(b'\x00\x01\x02\x03', 'control', 'negative') != 0:
        raise ValueError('Negative control failed')
    return {'positive_controls_passed': tested, 'negative_control_passed': True, 'control_contents_recorded': False}


def inventory(args):
    text = args.build_log.read_text(encoding='utf-8-sig')
    if args.apk.name not in text or 'ANDROID FLAT ARTIFACT:' not in text:
        raise ValueError('The build log does not identify the inspected APK')
    block = text.split('Used Assets and files from the Resources folder, sorted by uncompressed size:', 1)[1]
    paths = []
    for line in block.splitlines()[1:]:
        match = re.match(r'^\s*[\d.,]+\s*(?:kb|mb|gb|b)\s+[\d.,]+\s*%\s+(.+?)\s*$', line, re.I)
        if match:
            paths.append(match.group(1).strip())
        elif paths and not line.strip():
            break
    if not paths:
        raise ValueError('Build log has no packed asset list')
    old = read_json(args.previous_inventory)
    current, previous = set(paths), {value.strip() for value in old['packedAssets']}
    added, removed = sorted(current-previous), sorted(previous-current)
    if removed or any(not value.startswith('Assets/Zantetsu/') or not value.endswith('.cs') for value in added):
        raise ValueError('Packed art/package changes require a fresh source/license review')
    if any('NonCommercialDemo' in value or 'StanfordBunny' in value for value in current):
        raise ValueError('Noncommercial demo source is packed')
    return {'inventory_source': 'Original APK build log used-assets list; not a later build report',
            'build_log_sha256': sha256(args.build_log), 'previous_inventory_sha256': sha256(args.previous_inventory),
            'unityVersion': old['unityVersion'], 'packedAssets': sorted(current),
            'previous_asset_count': len(previous), 'asset_count': len(current), 'added': added, 'removed': removed,
            'packages': sorted({value.split('/')[1] for value in current if value.startswith('Packages/')}),
            'new_licensed_art_or_packages': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apk', required=True, type=Path)
    parser.add_argument('--build-log', required=True, type=Path)
    parser.add_argument('--previous-inventory', required=True, type=Path)
    parser.add_argument('--record-directory', required=True, type=Path)
    parser.add_argument('--modules', type=Path)
    parser.add_argument('--smolv-library', required=True, type=Path)
    parser.add_argument('--dxil-adapter', required=True, type=Path)
    parser.add_argument('--dxc-library', required=True, type=Path)
    parser.add_argument('--staging', required=True, type=Path)
    parser.add_argument('--forbidden-words-file', type=Path)
    args = parser.parse_args()
    if args.modules:
        sys.path.insert(0, str(args.modules.resolve()))
    words_path = args.forbidden_words_file or Path(subprocess.check_output(['git','-C',str(ROOT),'config','--get','zantetsu.forbiddenWordsFile']).decode().strip())
    if ROOT == words_path.resolve() or ROOT in words_path.resolve().parents:
        raise ValueError('The forbidden-word list must be outside the repository')
    words = [line.strip() for line in words_path.read_text(encoding='utf-8-sig').splitlines() if line.strip() and not line.lstrip().startswith('#')]
    if not words:
        raise ValueError('The external forbidden-word list is empty')
    audit = Audit(words)
    args.record_directory.mkdir(parents=True, exist_ok=True)
    report_path = args.record_directory / 'deep-content-inspection.json'
    result = {'status': 'running', 'forbidden_word_count': len(words),
              'forbidden_rule_file_sha256': sha256(words_path),
              'audit_tool_sha256': sha256(Path(__file__)), 'published': False}
    try:
        expected = read_json(Path(str(args.apk) + '.record/inspection.json'))
        before = sha256(args.apk)
        if before != expected['Sha256'].lower():
            raise ValueError('APK differs from the inspected artifact')
        audit.decoder = Smolv(args.smolv_library)
        audit.dxil_command = (args.dxil_adapter, args.dxc_library, args.record_directory)
        result['scanner_controls'] = controls(words, audit.decoder, audit.dxil_command)
        print('Positive controls passed; inspecting original APK and ZIP payloads', flush=True)
        with args.apk.open('rb') as stream:
            audit.stream(iter(lambda: stream.read(CHUNK), b''), 'apk_file', args.apk.name)
        with zipfile.ZipFile(args.apk) as archive:
            audit.zip_archive(archive)
            loose = [item.filename for item in archive.infolist() if Path(item.filename).suffix.lower() in ('.blend','.fbx','.uasset','.jks','.keystore','.pdb','.mdb')]
            if loose:
                raise ValueError('Loose art, signing or debug source files are packaged')
        print('ZIP, split-file boundaries, ELF and PNG metadata inspected; decoding Unity shaders', flush=True)
        result['unity'] = audit.unity(args.apk)
        sources = inventory(args)
        for path in sources['packedAssets']:
            audit.text(path, 'packed_source_names', 'Source inventory path')
        whitelist = {args.apk.name, args.apk.name+'.sha256', 'signing-certificate-sha256.txt', 'ThirdPartyNotices.txt'}
        assets = args.staging/'assets'
        files = list(assets.iterdir())
        if any(not path.is_file() for path in files) or {path.name for path in files} != whitelist:
            raise ValueError('Staged asset whitelist differs')
        if sha256(assets/args.apk.name) != before or sha256(assets/'ThirdPartyNotices.txt') != expected['ThirdPartyNoticesSha256'].lower():
            raise ValueError('Staged APK or notices differ')
        if (assets/(args.apk.name+'.sha256')).read_text(encoding='utf-8-sig').strip() != before+'  '+args.apk.name:
            raise ValueError('Staged checksum differs')
        if (assets/'signing-certificate-sha256.txt').read_text(encoding='utf-8-sig').strip().upper() != expected['CertificateSha256'].upper():
            raise ValueError('Staged signing-certificate fingerprint differs')
        validation = read_json(Path(str(args.apk)+'.record/device-validation.json'))
        plan = read_json(args.staging/'publication-plan.json')
        if plan['SourceCommit'] != validation['SourceCommit'] or plan['VersionCode'] != expected['VersionCode'] or plan['VersionName'] != expected['VersionName']:
            raise ValueError('Staged publication plan does not match the APK source/version record')
        if any(plan.get(key) is not False for key in ('Published','TagCreated','GitHubDraftCreated')):
            raise ValueError('Staging must remain local and unpublished')
        draft = read_json(ROOT/'Tools/AndroidFlat/DistributionDraft.json')
        tag = plan['ReleaseTag']
        if (tag['Name'] != draft['tagDraft'] or tag['Kind'] != draft['tagKind'] or tag['Kind'] != 'annotated'
                or tag['TargetCommit'] != validation['SourceCommit'] or tag['TargetCommit'] != draft['tagTargetCommit']
                or tag['GitHubTargetCommitish'] != tag['TargetCommit'] or tag['ApkSha256'].lower() != before
                or tag['Created'] is not False or validation['SourceClean'] is not True):
            raise ValueError('Staged release tag does not match the exact clean APK build-source record')
        tag_tree = subprocess.check_output(['git','-C',str(ROOT),'rev-parse',tag['TargetCommit']+'^{tree}']).decode().strip()
        if tag['TargetTree'] != tag_tree or tag_tree != draft['tagTargetTree']:
            raise ValueError('Staged release tag tree differs from the fixed source record')
        notes = (args.staging/'release-notes.draft.md').read_text(encoding='utf-8-sig')
        if re.search(r'@[A-Z][A-Z0-9_]*@',notes) or any(value not in notes for value in (before,expected['CertificateSha256'],validation['SourceCommit'],args.apk.name,tag['Name'])):
            raise ValueError('Staged release notes contain placeholders or omit an artifact identity')
        staged_hashes = {}
        for path in args.staging.rglob('*'):
            if path.is_symlink():
                raise ValueError('Staging must not include symbolic links')
            if path.is_file():
                audit.text(path.relative_to(args.staging).as_posix(), 'staging_names', 'Distribution filename')
                with path.open('rb') as stream:
                    audit.stream(iter(lambda: stream.read(CHUNK), b''), 'staging_files', path.name)
                staged_hashes[path.relative_to(args.staging).as_posix()] = sha256(path)
        if sha256(args.apk) != before:
            raise ValueError('APK changed during inspection')
        result.update({'status': 'passed' if not audit.failures else 'failed', 'apk_sha256': before,
            'apk_unchanged': True, 'counts': dict(audit.counts), 'forbidden_matches': audit.counts['forbidden_matches'],
            'failures': audit.failures, 'asset_inventory': {key:value for key,value in sources.items() if key!='packedAssets'},
            'smolv_decoder_sha256': sha256(args.smolv_library), 'dxil_adapter_sha256': sha256(args.dxil_adapter),
            'dxc_library_sha256': sha256(args.dxc_library), 'asset_whitelist': sorted(whitelist),
            'staged_sha256': staged_hashes, 'staged_metadata_consistent': True,
            'release_tag_binding': tag,
            'scope': ['case-insensitive UTF-8, UTF-16LE/BE and UTF-32LE/BE byte strings, including mixed JSON Unicode escapes',
                'ZIP names/comments and all decompressed entries', 'reconstructed Unity split-file boundaries',
                'ELF compressed debug sections, PNG compressed textual/profile metadata',
                'Unity native text/shader metadata, JSON string escapes, all shader LZ4 segments',
                'SMOL-V shader modules decoded to SPIR-V using the upstream decoder',
                'DXIL bitcode disassembled through the installed Unity DXC library', 'all staged files and source-inventory paths'],
            'limits': ['Image-pixel OCR and arbitrary encrypted/steganographic data are outside the text/metadata scan.']})
        (args.record_directory/'asset-inventory.json').write_text(audit.mask(json.dumps(sources,ensure_ascii=False,indent=2))+'\n',encoding='utf-8')
    except Exception as error:
        result.update({'status':'failed','error_type':type(error).__name__,'error':audit.mask(error),
                       'counts':dict(audit.counts),'failures':audit.failures})
    report_path.write_text(audit.mask(json.dumps(result,ensure_ascii=False,indent=2))+'\n',encoding='utf-8')
    print(json.dumps({'status':result['status'],'forbidden_matches':audit.counts['forbidden_matches'],
                      'report':audit.mask(report_path.relative_to(ROOT) if report_path.is_relative_to(ROOT) else report_path.name)}),flush=True)
    return 0 if result['status']=='passed' else 1


if __name__=='__main__':
    sys.exit(main())
