#!/usr/bin/env python3
"""Download AudioTastic's own FFmpeg build (P14) from CI, for development on any OS.

The FFmpeg libraries are built from source by native/build_ffmpeg.sh (audio-focused, LGPL-2.1-or-later); CI runs it for
every runtime identifier and keeps the result as the artifact "ffmpeg-<rid>". This script fetches that artifact from the
latest successful CI run on main with the GitHub CLI (gh, signed in), so a Windows developer needs no Linux toolchain.
On Linux or macOS you can also build it yourself: native/build_ffmpeg.sh <rid>.

Result: native/out/<rid>/native/<libraries> plus FFMPEG-BUILDINFO.txt and FFMPEG-LICENSES.txt.

Usage: python native/fetch_ffmpeg.py [--rid win-x64 ...] [--run <id>] [--repo owner/name]
"""
import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64"]
DEFAULT_REPO = "castenskiold/AudioTasticDesktop"
# What a previous FFmpeg in the folder may have left (BtbN builds before P14 used FFMPEG-LICENSE.txt).
OLD_FILES = ("avcodec", "avformat", "avutil", "swresample", "FFMPEG-")


def gh(*args):
    if shutil.which("gh") is None:
        sys.exit("The GitHub CLI (gh) is needed: https://cli.github.com (or build with native/build_ffmpeg.sh)")
    return subprocess.run(["gh", *args], check=True, capture_output=True, text=True).stdout


def latest_run(repo):
    runs = json.loads(gh("run", "list", "-R", repo, "-w", "CI", "-b", "main", "-s", "success", "-L", "1",
                         "--json", "databaseId"))
    if not runs:
        sys.exit(f"No successful CI run on main in {repo}")
    return str(runs[0]["databaseId"])


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", nargs="*", default=["win-x64"], choices=RIDS)
    parser.add_argument("--run", help="CI run id (default: the latest successful run on main)")
    parser.add_argument("--repo", default=DEFAULT_REPO)
    args = parser.parse_args()

    run = args.run or latest_run(args.repo)
    for rid in args.rid:
        dest = HERE / "out" / rid / "native"
        dest.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory() as tmp:
            gh("run", "download", run, "-R", args.repo, "-n", f"ffmpeg-{rid}", "-D", tmp)
            files = [f for f in Path(tmp).iterdir() if f.is_file()]
            if not any(f.name.startswith(("avcodec", "libavcodec")) for f in files):
                sys.exit(f"The artifact ffmpeg-{rid} of run {run} has no avcodec")
            for old in dest.iterdir():
                if old.is_file() and (old.name.startswith(OLD_FILES) or old.name.startswith(tuple("lib" + o for o in OLD_FILES))):
                    old.unlink()
            for f in files:
                shutil.copy2(f, dest / f.name)
        print(f"{rid}: {len(files)} files from CI run {run} -> {dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
