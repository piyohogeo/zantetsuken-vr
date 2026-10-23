"""Read-only ELF and APK packaging checks for 16 KB Android devices."""
import argparse
import json
import struct
import zipfile

p = argparse.ArgumentParser()
p.add_argument("package")
a = p.parse_args()
results = []
with zipfile.ZipFile(a.package) as z, open(a.package, "rb") as archive:
    for entry in z.infolist():
        if not entry.filename.endswith(".so") or not entry.filename.startswith(("lib/", "base/lib/")):
            continue
        data = z.read(entry)
        if data[:6] != b"\x7fELF\x02\x01":
            raise SystemExit("Unsupported ELF: " + entry.filename)
        offset = struct.unpack_from("<Q", data, 32)[0]
        stride, count = struct.unpack_from("<HH", data, 54)
        loads = [struct.unpack_from("<IIQQQQQQ", data, offset + i * stride) for i in range(count)]
        loads = [segment for segment in loads if segment[0] == 1]
        aligned = bool(loads) and all(s[7] >= 16384 and s[2] % 16384 == s[3] % 16384 for s in loads)
        # APK entries mapped directly from an uncompressed ZIP must also start on a 16 KB boundary.
        archive.seek(entry.header_offset)
        header = archive.read(30)
        name_len, extra_len = struct.unpack_from("<HH", header, 26)
        data_offset = entry.header_offset + 30 + name_len + extra_len
        apk_zip_ok = entry.compress_type != zipfile.ZIP_STORED or data_offset % 16384 == 0
        if entry.filename.startswith("base/"):
            apk_zip_ok = True  # AAB is not loaded on device; its generated APK is checked separately.
        results.append({"library": entry.filename, "elf16k": aligned, "apkZip16k": apk_zip_ok})
print(json.dumps({"libraries": results, "passed": bool(results) and all(x["elf16k"] and x["apkZip16k"] for x in results)}))
raise SystemExit(0 if results and all(x["elf16k"] and x["apkZip16k"] for x in results) else 1)
