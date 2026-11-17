#!/usr/bin/env python3
"""Download prebuilt libopenmpt for the tracker-module plugin (FM-07).

Windows: the official "dev.windows.vs2022" package from lib.openmpt.org (libopenmpt.dll plus its helper DLLs
openmpt-mpg123 (LGPL-2.1, dynamically linked), openmpt-ogg, openmpt-vorbis (BSD) and openmpt-zlib (zlib)).
The package's SHA-256 was recorded when it was first downloaded over HTTPS (17 Nov 2026); a different file fails.
Linux/macOS: use build_libopenmpt_unix.sh (system packages also work: libopenmpt0 / brew install libopenmpt).

Result: native/out/libopenmpt/runtimes/<rid>/native/<libraries> plus the licence files (copied into the plugin on build).

Usage: python native/fetch_libopenmpt.py [--rid win-x64 --rid win-arm64]
"""
import argparse
import hashlib
import shutil
import sys
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
VERSION = "0.8.10"
URL = f"https://lib.openmpt.org/files/libopenmpt/dev/libopenmpt-{VERSION}+release.dev.windows.vs2022.zip"
SHA256 = "52481ca47d5386616adabd8048d0e7161f8abef9063afc9b241daca1e00251db"
ARCH = {"win-x64": "amd64", "win-arm64": "arm64"}


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", action="append", choices=sorted(ARCH), help="runtime identifier (default: win-x64)")
    rids = parser.parse_args().rid or ["win-x64"]

    downloads = HERE / "downloads"
    downloads.mkdir(exist_ok=True)
    archive = downloads / Path(URL).name
    if not archive.exists() or sha256(archive) != SHA256:
        print(f"Downloading {URL}")
        request = urllib.request.Request(URL, headers={"User-Agent": "AudioTastic-build"})
        with urllib.request.urlopen(request, timeout=120) as response, open(archive, "wb") as out:
            shutil.copyfileobj(response, out)
        actual = sha256(archive)
        if actual != SHA256:
            archive.unlink()
            sys.exit(f"SHA-256 mismatch: expected {SHA256}, got {actual}. Check the new release before updating the hash.")

    with zipfile.ZipFile(archive) as zf:
        for rid in rids:
            target = HERE / "out" / "libopenmpt" / "runtimes" / rid / "native"
            if target.exists():
                shutil.rmtree(target)
            target.mkdir(parents=True)
            prefix = f"bin/{ARCH[rid]}/"
            for name in zf.namelist():
                if name.startswith(prefix) and name.endswith(".dll"):
                    (target / Path(name).name).write_bytes(zf.read(name))
                elif name == "LICENSE.txt" or (name.startswith("Licenses/") and name.endswith(".txt")):
                    (target / ("libopenmpt-" + Path(name).name)).write_bytes(zf.read(name))
            print(f"{rid}: {', '.join(sorted(p.name for p in target.glob('*.dll')))} -> {target}")


if __name__ == "__main__":
    main()
