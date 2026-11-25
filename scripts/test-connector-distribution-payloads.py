"""Compare the four Windows package payloads against an audited CloudInlet publish directory."""
import argparse
import hashlib
import json
import lzma
import re
import zipfile
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

instruction = re.compile(rb"[\xe8\xe9]")


def encode_calls(data):
    # Inno's TransformCallInstructions v3 restarts its scan at 64 KiB boundaries.
    # Format source: jrsoftware/issrc, is-6_7_3, Projects/Src/Compression.Base.pas.
    encoded = bytearray(data)
    for start in range(0, len(data), 65536):
        stop = min(len(data), start + 65536)
        pos = start
        while hit := instruction.search(data, pos, max(start, stop - 4)):
            addr = hit.start() + 1
            pos = addr + 4
            if data[addr + 3] in (0, 255):
                rel = int.from_bytes(data[addr:addr + 3], "little")
                if rel & 0x800000:
                    encoded[addr + 3] ^= 255
                encoded[addr:addr + 3] = ((rel + pos) & 0xffffff).to_bytes(3, "little")
    return encoded


def verify_inno(app, installer, identity):
    files = {p.relative_to(app): p for p in app.rglob("*") if p.is_file()}
    files[Path("distribution.json")] = identity
    lengths = {name: path.stat().st_size for name, path in files.items()}
    expected_size = sum(lengths.values())
    compressed = installer.read_bytes()
    marker = compressed.index(b"zlb\x1a") + 4
    prop = compressed[marker]
    if prop > 32:
        raise ValueError("Unexpected LZMA2 dictionary size")
    decoder = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[
        {"id": lzma.FILTER_LZMA2, "dict_size": (2 | (prop & 1)) << (prop // 2 + 11)}])
    plain = decoder.decompress(compressed[marker + 1:], max_length=expected_size + 1)
    if not decoder.eof or len(plain) != expected_size:
        raise ValueError("Installer payload size differs from the reviewed file inventory")

    # The compiler can reorder its solid input. Match complete file digests and consume every byte.
    lookup = defaultdict(list)
    for name, path in files.items():
        raw = path.read_bytes()
        if not raw:
            continue
        variants = [raw]
        if raw.startswith(b"MZ"):
            variants.append(encode_calls(raw))
        for variant in variants:
            lookup[bytes(variant[:32])].append((name, hashlib.sha256(variant[:4096]).digest(),
                                                hashlib.sha256(variant).digest()))
    prefixes = sorted({len(k) for k in lookup}, reverse=True)
    payload = memoryview(plain)
    pos = 0
    pending = {name for name, size in lengths.items() if size}
    while pos < len(plain):
        for width in prefixes:
            for name, head, digest in lookup.get(plain[pos:pos + width], ()):
                if name not in pending:
                    continue
                segment = payload[pos:pos + lengths[name]]
                if hashlib.sha256(segment[:4096]).digest() == head and hashlib.sha256(segment).digest() == digest:
                    pos += lengths[name]
                    pending.remove(name)
                    break
            else:
                continue
            break
        else:
            raise ValueError(f"Unexpected data at byte {pos:,} ({len(pending)} files remaining)")
    if pending:
        raise ValueError("The installer is missing reviewed files")
    print(f"EXE: verified all {expected_size:,} decompressed bytes against {len(files)} source files.")


def sha(data):
    return hashlib.sha256(data).digest()


def verify_zip(path, hashes, msix=False):
    with zipfile.ZipFile(path) as archive:
        entries = [item.filename for item in archive.infolist() if not item.is_dir()]
        if len(entries) != len(set(entries)):
            raise ValueError(f"Duplicate paths in {path.name}")
        expected = set(hashes)
        if msix:
            expected.update({"AppxBlockMap.xml", "AppxManifest.xml", "[Content_Types].xml", "resources.pri",
                             "StoreAssets/Logo44.png", "StoreAssets/Logo50.png", "StoreAssets/Logo150.png"})
            if "AppxSignature.p7x" in entries:
                expected.add("AppxSignature.p7x")
            if json.loads(archive.read("distribution.json"))["installerKind"] != "Store":
                raise ValueError("Unexpected MSIX distribution identity")
        if set(entries) != expected:
            raise ValueError(f"Unexpected or missing entries in {path.name}: {set(entries) ^ expected}")
        for name, digest in hashes.items():
            if msix and name == "distribution.json":
                continue
            if sha(archive.read(name)) != digest:
                raise ValueError(f"Different bytes for {name} in {path.name}")
    print(f"{path.suffix}: verified {len(entries)} file entries.")


def verify_msi(app, staging, extracted, hashes):
    # The WiX build source maps the cabinet IDs back to the files in the audited publish directory.
    xml = ET.parse(staging / "CloudInlet.wxs")
    sources = {f.attrib["Id"]: Path(f.attrib["Source"]).resolve() for f in xml.findall(".//{*}File")}
    expected_sources = {app / name for name in hashes if name != "distribution.json"}
    expected_sources.add((staging / "distribution-msi.json").resolve())
    if set(sources.values()) != expected_sources or len(sources) != len(expected_sources):
        raise ValueError("The MSI source mapping differs from the reviewed application")
    if set(sources) != {p.name for p in extracted.iterdir()}:
        raise ValueError("Unexpected or missing files in the decoded MSI cabinet")
    for identifier, source in sources.items():
        if sha((extracted / identifier).read_bytes()) != sha(source.read_bytes()):
            raise ValueError(f"Different bytes for {source.name} in the MSI cabinet")
    print(f"MSI: verified {len(sources)} decoded cabinet files.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--portable", type=Path, required=True)
    parser.add_argument("--msix", type=Path, required=True)
    parser.add_argument("--installer-staging", type=Path, required=True, help="WiX source and installer distribution JSONs")
    parser.add_argument("--msi-files", type=Path, required=True, help="File directory from wix msi decompile -x")
    args = parser.parse_args()
    app = args.app.resolve()
    expected = {p.relative_to(app).as_posix(): sha(p.read_bytes()) for p in app.rglob("*") if p.is_file()}
    verify_inno(app, args.exe, args.installer_staging / "distribution-exe.json")
    verify_msi(app, args.installer_staging, args.msi_files, expected)
    verify_zip(args.portable, expected)
    verify_zip(args.msix, expected, msix=True)
