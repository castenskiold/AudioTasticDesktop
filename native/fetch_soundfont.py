#!/usr/bin/env python3
"""Download the bundled General MIDI SoundFont for the MIDI plugin (FM-08).

GeneralUser GS v2.0.3 by S. Christian Collins (https://github.com/mrbumpy409/GeneralUser-GS).
Licence: GeneralUser GS License v2.0 (free use in software projects, private or commercial; see LICENSE.txt).
The author asks that his download links are not hot-linked from apps, so the player ships a local copy:
this script fetches it once at build time and verifies the SHA-256 recorded when it was chosen (17 Nov 2026).

Result: native/downloads/soundfont/GeneralUser-GS.sf2 and LICENSE.txt (copied into the MIDI plugin on build).

Usage: python native/fetch_soundfont.py
"""
import hashlib
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
TARGET = HERE / "downloads" / "soundfont"
SF2_URL = "https://github.com/mrbumpy409/GeneralUser-GS/raw/refs/heads/main/GeneralUser-GS.sf2"
LICENSE_URL = "https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/main/documentation/LICENSE.txt"
SF2_SHA256 = "9575028c7a1f589f5770fccc8cff2734566af40cd26ed836944e9a5152688cfe"
SF2_SIZE = 32_319_396


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def download(url, path):
    print(f"Downloading {url}")
    request = urllib.request.Request(url, headers={"User-Agent": "AudioTastic-build"})
    with urllib.request.urlopen(request, timeout=120) as response, open(path, "wb") as out:
        while block := response.read(1 << 20):
            out.write(block)


def main():
    TARGET.mkdir(parents=True, exist_ok=True)
    sf2 = TARGET / "GeneralUser-GS.sf2"
    if sf2.exists() and sf2.stat().st_size == SF2_SIZE and sha256(sf2) == SF2_SHA256:
        print(f"{sf2.name} already present and verified.")
    else:
        partial = sf2.with_suffix(".part")
        download(SF2_URL, partial)
        actual = sha256(partial)
        if actual != SF2_SHA256:
            partial.unlink()
            sys.exit(f"SHA-256 mismatch for GeneralUser-GS.sf2: expected {SF2_SHA256}, got {actual}. "
                     "The upstream file changed; check the new version and its licence before updating the hash.")
        partial.replace(sf2)
        print(f"Verified {sf2.name} ({SF2_SIZE:,} bytes).")
    licence = TARGET / "LICENSE.txt"
    if not licence.exists():
        download(LICENSE_URL, licence)
    print(f"SoundFont ready in {TARGET}")


if __name__ == "__main__":
    main()
