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
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASE_URL = "http://losslessaudio.org/Downloads/A1C0/5100/"
PACKAGES = {
    # rid: (folder on the site, package, SHA-1, library files inside SDK/Library). The site moved the packages into
    # per-OS folders (seen 19 Nov 2026); the files are unchanged (same SHA-1).
    "win-x64": ("Win", "OptimFROG_Win_x64_5100.zip", "abf21227c5417c984ee51ba391e2bcb5546d033b", ("OptimFROG.dll",)),
    "linux-x64": ("Linux", "OptimFROG_Linux_x64_5100.zip", "e3dd9fd56ee11c544b1faadee24ae6adef661b6a", ("libOptimFROG.so", "libOptimFROG.so.0")),
    "osx-x64": ("OSX", "OptimFROG_OSX_x64_5100.zip", "c07eb7a6b877bd4900f4a1f3b950c5ed7f730e26", ("libOptimFROG.dylib", "libOptimFROG.0.dylib")),
}


def sha1(path):
    digest = hashlib.sha1()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def fetch(site_folder, package, expected):
    folder = HERE / "downloads" / "optimfrog"
    folder.mkdir(parents=True, exist_ok=True)
    archive = folder / package
    if archive.exists() and sha1(archive) == expected:
        return archive
    url = f"{BASE_URL}{site_folder}/{package}"
    # The site is small and sometimes times out or sends a broken file to CI runners: try three times.
    for attempt, pause in enumerate((10, 30, None), start=1):
        print(f"Downloading {url} (attempt {attempt})")
        request = urllib.request.Request(url, headers={"User-Agent": "AudioTastic-build"})
        try:
            with urllib.request.urlopen(request, timeout=120) as response, open(archive, "wb") as out:
                shutil.copyfileobj(response, out)
            actual = sha1(archive)
            if actual == expected:
                return archive
            print(f"  SHA-1 mismatch: expected {expected}, got {actual}")
        except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
            print(f"  failed: {error}")
        if archive.exists():
            archive.unlink()
        if pause is not None:
            time.sleep(pause)
    sys.exit(f"Could not download a verified {package}.")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", action="append", choices=sorted(PACKAGES), help="runtime identifier (default: win-x64)")
    for rid in parser.parse_args().rid or ["win-x64"]:
        site_folder, package, expected, libraries = PACKAGES[rid]
        archive = fetch(site_folder, package, expected)
        target = HERE / "out" / "optimfrog" / "runtimes" / rid / "native"
        if target.exists():
            shutil.rmtree(target)
        target.mkdir(parents=True)
        root = package.removesuffix(".zip")
        with zipfile.ZipFile(archive) as zf:
            for library in libraries:
                info = zf.getinfo(f"{root}/SDK/Library/{library}")
                data = zf.read(info)
                if (info.external_attr >> 16) & 0o170000 == 0o120000:
                    # A symlink (libOptimFROG.so -> libOptimFROG.so.0): store the library itself under the link's
                    # name, since plugin packages and Windows checkouts cannot carry symlinks.
                    data = zf.read(f"{root}/SDK/Library/{data.decode().strip()}")
                (target / library).write_bytes(data)
            (target / "OptimFROG-license.html").write_bytes(zf.read(f"{root}/license.html"))
        print(f"{rid}: {', '.join(libraries)} -> {target}")


if __name__ == "__main__":
    main()
