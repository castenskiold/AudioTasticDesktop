#!/usr/bin/env python3
"""Download the OptimFROG decoder libraries for the OptimFROG plugin (FM-06).

OptimFROG 5.100 / SDK 1.600 by Florin Ghido is closed-source freeware, allowed under the licence-policy exception
(no open-source decoder exists, no licence key, free for the end user). Its licence (license.html, shipped beside the
library) allows redistributing the unmodified binary Libraries under their original names with software that uses
them via the SDK, for free and commercial software, without notification (checked 17 Nov 2026). The command-line
encoders are NOT redistributed (they need permission); generate_test_media.py only uses them locally.

Builds exist for win-x64, linux-x64 and osx-x64 only (no ARM64), so ARM64 builds of AudioTastic do not list .ofr/.ofs.

losslessaudio.org's HTTPS certificate is broken, so the packages are fetched over HTTP and verified against SHA-1
values published over HTTPS by the Internet Archive (web.archive.org/web/2024id_/http://losslessaudio.org/...).

Result: native/out/optimfrog/runtimes/<rid>/native/<library> plus license.html (copied into the plugin on build).

Usage: python native/fetch_optimfrog.py [--rid win-x64 --rid linux-x64 --rid osx-x64]
"""
import argparse
import hashlib
import shutil
import sys
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASE_URL = "http://losslessaudio.org/Downloads/A1C0/5100/"
PACKAGES = {
    # rid: (package, SHA-1, library files inside SDK/Library)
    "win-x64": ("OptimFROG_Win_x64_5100.zip", "abf21227c5417c984ee51ba391e2bcb5546d033b", ("OptimFROG.dll",)),
    "linux-x64": ("OptimFROG_Linux_x64_5100.zip", "e3dd9fd56ee11c544b1faadee24ae6adef661b6a", ("libOptimFROG.so", "libOptimFROG.so.0")),
    "osx-x64": ("OptimFROG_OSX_x64_5100.zip", "c07eb7a6b877bd4900f4a1f3b950c5ed7f730e26", ("libOptimFROG.dylib", "libOptimFROG.0.dylib")),
}


def sha1(path):
    digest = hashlib.sha1()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def fetch(package, expected):
    folder = HERE / "downloads" / "optimfrog"
    folder.mkdir(parents=True, exist_ok=True)
    archive = folder / package
    if archive.exists() and sha1(archive) == expected:
        return archive
    url = BASE_URL + package
    print(f"Downloading {url}")
    request = urllib.request.Request(url, headers={"User-Agent": "AudioTastic-build"})
    with urllib.request.urlopen(request, timeout=120) as response, open(archive, "wb") as out:
        shutil.copyfileobj(response, out)
    actual = sha1(archive)
    if actual != expected:
        archive.unlink()
        sys.exit(f"SHA-1 mismatch for {package}: expected {expected}, got {actual}.")
    return archive


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", action="append", choices=sorted(PACKAGES), help="runtime identifier (default: win-x64)")
    for rid in parser.parse_args().rid or ["win-x64"]:
        package, expected, libraries = PACKAGES[rid]
        archive = fetch(package, expected)
        target = HERE / "out" / "optimfrog" / "runtimes" / rid / "native"
        if target.exists():
            shutil.rmtree(target)
        target.mkdir(parents=True)
        root = package.removesuffix(".zip")
        with zipfile.ZipFile(archive) as zf:
            for library in libraries:
                (target / library).write_bytes(zf.read(f"{root}/SDK/Library/{library}"))
            (target / "OptimFROG-license.html").write_bytes(zf.read(f"{root}/license.html"))
        print(f"{rid}: {', '.join(libraries)} -> {target}")


if __name__ == "__main__":
    main()
