#!/usr/bin/env python3
"""Download prebuilt LGPL FFmpeg shared libraries for AudioTastic (phase P0, native library pipeline).

Source: BtbN/FFmpeg-Builds (GitHub). Only the "lgpl-shared" variants are used: they are configured
without --enable-gpl / --enable-nonfree, so they meet the licence policy (LGPL, dynamically linked).
macOS is not provided by BtbN; build it with build_ffmpeg_macos.sh instead.

Result: native/out/<rid>/native/<libraries> plus FFMPEG-BUILDINFO.txt and the FFmpeg licence file.

Usage: python native/fetch_ffmpeg.py [--rid win-x64 ...] [--version 8.1]
"""
import argparse
import hashlib
import re
import shutil
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASE_URL = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest"

# rid -> BtbN platform name, archive type, folder with the shared libraries inside the archive
PLATFORMS = {
    "win-x64": ("win64", "zip", "bin", (".dll",)),
    "win-arm64": ("winarm64", "zip", "bin", (".dll",)),
    "linux-x64": ("linux64", "tar.xz", "lib", (".so",)),
    "linux-arm64": ("linuxarm64", "tar.xz", "lib", (".so",)),
}


# Only the libraries AudioTastic uses (decoding, demuxing, resampling, encoding for casting/conversion).
WANTED = ("avcodec", "avformat", "avutil", "swresample")
# Windows: avcodec-62.dll; Linux: libavcodec.so.62 (the SONAME the loader looks for; other symlink copies are skipped)
LIB_PATTERN = re.compile(r"^(?:lib)?(" + "|".join(WANTED) + r")(?:-\d+\.dll|\.so\.\d+)$")


def asset_name(platform, version, ext):
    return f"ffmpeg-n{version}-latest-{platform}-lgpl-shared-{version}.{ext}"


def download(url, target: Path):
    target.parent.mkdir(parents=True, exist_ok=True)
    if not target.exists():
        print(f"Downloading {url}")
        with urllib.request.urlopen(url) as r, open(target, "wb") as f:  # noqa: S310 (fixed https URL)
            shutil.copyfileobj(r, f)
    return hashlib.sha256(target.read_bytes()).hexdigest()


def extract_libs(archive: Path, kind, lib_folder, suffixes, dest: Path):
    dest.mkdir(parents=True, exist_ok=True)
    copied = []
    if kind == "zip":
        with zipfile.ZipFile(archive) as z:
            for name in z.namelist():
                parts = Path(name).parts
                if len(parts) >= 2 and parts[-2] == lib_folder and LIB_PATTERN.match(parts[-1]):
                    (dest / parts[-1]).write_bytes(z.read(name))
                    copied.append(parts[-1])
                elif parts[-1] in ("LICENSE.txt", "LICENSE"):
                    (dest / "FFMPEG-LICENSE.txt").write_bytes(z.read(name))
    else:
        with tarfile.open(archive) as t:
            for m in t.getmembers():
                parts = Path(m.name).parts
                is_lib = len(parts) >= 2 and parts[-2] == lib_folder and LIB_PATTERN.match(parts[-1]) is not None
                if is_lib and (m.isfile() or m.issym()):
                    if m.issym():
                        # keep versioned symlinks as real copies so the folder works on every file system
                        target = m
                        while target.issym():
                            target = t.getmember(str(Path(target.name).parent / target.linkname))
                        data = t.extractfile(target).read()
                    else:
                        data = t.extractfile(m).read()
                    (dest / parts[-1]).write_bytes(data)
                    copied.append(parts[-1])
                elif parts[-1] in ("LICENSE.txt", "LICENSE") and m.isfile():
                    (dest / "FFMPEG-LICENSE.txt").write_bytes(t.extractfile(m).read())
    return copied


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", nargs="*", default=list(PLATFORMS), choices=list(PLATFORMS))
    parser.add_argument("--version", default="8.1", help="FFmpeg release branch (BtbN builds n<version>-latest)")
    args = parser.parse_args()

    for rid in args.rid:
        platform, kind, lib_folder, suffixes = PLATFORMS[rid]
        name = asset_name(platform, args.version, kind)
        archive = HERE / "downloads" / name
        sha = download(f"{BASE_URL}/{name}", archive)
        dest = HERE / "out" / rid / "native"
        libs = extract_libs(archive, kind, lib_folder, suffixes, dest)
        if not libs:
            print(f"No libraries found in {name}", file=sys.stderr)
            return 1
        (dest / "FFMPEG-BUILDINFO.txt").write_text(
            f"source: {BASE_URL}/{name}\nsha256: {sha}\nvariant: lgpl-shared\nlibraries: {', '.join(sorted(libs))}\n")
        print(f"{rid}: {len(libs)} libraries -> {dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
