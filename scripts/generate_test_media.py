#!/usr/bin/env python3
"""Generate licence-free test media for AudioTastic (Implementation Plan, phase P0).

Creates synthetic signals (sine, sweep, silence, clicks and gapless pairs) in every format FFmpeg
can encode, plus a small MIDI file written directly. Output: tests/media/generated/ (git-ignored)
and a manifest.json describing each file.

Formats FFmpeg cannot encode (APE, Musepack, Shorten, OptimFROG, DSF/DFF, tracker modules) need
hand-made fixtures; they are listed in the manifest under "missing".

Usage: python scripts/generate_test_media.py [--ffmpeg PATH] [--out DIR]
"""
import argparse
import json
import shutil
import struct
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

# name -> (extension, ffmpeg encoder arguments). Requires an FFmpeg with libmp3lame, libvorbis and libopus.
# Lossless encoders are pinned to 16-bit so their decoded output is bit-identical to the wav16 file (FM-01 tests);
# otherwise FFmpeg keeps float sources (sweep, clicks) at 24-bit or float.
FORMATS = {
    "wav16": ("wav", ["-c:a", "pcm_s16le"]),
    "wav24": ("wav", ["-c:a", "pcm_s24le"]),
    "aiff": ("aiff", ["-c:a", "pcm_s16be"]),
    "flac": ("flac", ["-c:a", "flac", "-sample_fmt", "s16"]),
    "mp3": ("mp3", ["-c:a", "libmp3lame", "-b:a", "192k"]),
    "aac": ("m4a", ["-c:a", "aac", "-b:a", "192k"]),
    "alac": ("m4a", ["-c:a", "alac"]),
    "vorbis": ("ogg", ["-c:a", "libvorbis", "-q:a", "5"]),
    "opus": ("opus", ["-c:a", "libopus", "-b:a", "128k"]),
    "wavpack": ("wv", ["-c:a", "wavpack", "-sample_fmt", "s16p"]),
    "wma": ("wma", ["-c:a", "wmav2", "-b:a", "192k"]),
    "tta": ("tta", ["-c:a", "tta"]),
}

# name -> (lavfi source, duration seconds)
SIGNALS = {
    "sine1k": ("sine=frequency=1000:sample_rate=44100", 5),
    "sweep": ("aevalsrc='sin(2*PI*(20*pow(1000,t/10)/(log(1000)/10)))':s=44100", 10),
    "silence": ("anullsrc=r=44100:cl=stereo", 3),
    "clicks": ("aevalsrc='if(lt(mod(t,1),0.001),0.9,0)':s=44100", 5),
}

GAPLESS_FORMATS = ["wav16", "flac", "mp3", "aac", "opus", "vorbis"]
GAPLESS_SPLIT_SECONDS = 4.321  # deliberately not on a codec frame boundary
GAPLESS_TOTAL_SECONDS = 10

MISSING = ["ape", "musepack", "shorten", "optimfrog", "dsf", "dff", "mod", "xm", "it", "s3m"]


def run(ffmpeg, args):
    cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-y", *args]
    subprocess.run(cmd, check=True)


def write_midi(path: Path):
    """A one-bar C major arpeggio on piano, format 0, 480 ticks per quarter note."""
    events = bytearray()
    events += b"\x00\xff\x51\x03\x07\xa1\x20"  # tempo 120 bpm
    events += b"\x00\xc0\x00"  # program change: acoustic grand piano
    for note in (60, 64, 67, 72):
        events += bytes([0x00, 0x90, note, 0x64])  # note on
        events += bytes([0x83, 0x60, 0x80, note, 0x00])  # 480 ticks later: note off
    events += b"\x00\xff\x2f\x00"  # end of track
    header = b"MThd" + struct.pack(">IHHH", 6, 0, 1, 480)
    track = b"MTrk" + struct.pack(">I", len(events)) + bytes(events)
    path.write_bytes(header + track)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    parser.add_argument("--out", default=str(REPO / "tests" / "media" / "generated"))
    args = parser.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    manifest = {"files": [], "missing": MISSING}

    for sig, (source, seconds) in SIGNALS.items():
        for fmt, (ext, enc) in FORMATS.items():
            name = f"{sig}.{fmt}.{ext}"
            channels = ["-ac", "2"]
            run(args.ffmpeg, ["-f", "lavfi", "-i", source, "-t", str(seconds), *channels, *enc, str(out / name)])
            manifest["files"].append({"file": name, "signal": sig, "format": fmt, "seconds": seconds})

    # 24-bit / 96 kHz high-resolution file
    run(args.ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=96000", "-t", "5", "-ac", "2",
                      "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", str(out / "sine1k.hires-24-96.flac")])
    manifest["files"].append({"file": "sine1k.hires-24-96.flac", "signal": "sine1k", "format": "flac", "seconds": 5,
                              "sampleRate": 96000, "bitDepth": 24})

    # Gapless pairs: one continuous sine split into two files; joined playback must equal the reference.
    reference = out / "gapless.reference.wav"
    run(args.ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", str(GAPLESS_TOTAL_SECONDS),
                      "-ac", "2", "-c:a", "pcm_s16le", str(reference)])
    for fmt in GAPLESS_FORMATS:
        ext, enc = FORMATS[fmt]
        a, b = out / f"gapless.{fmt}.part1.{ext}", out / f"gapless.{fmt}.part2.{ext}"
        run(args.ffmpeg, ["-i", str(reference), "-t", str(GAPLESS_SPLIT_SECONDS), *enc, str(a)])
        run(args.ffmpeg, ["-ss", str(GAPLESS_SPLIT_SECONDS), "-i", str(reference), *enc, str(b)])
        manifest["files"].append({"file": a.name, "gaplessPair": b.name, "reference": reference.name, "format": fmt,
                                  "splitSeconds": GAPLESS_SPLIT_SECONDS})

    write_midi(out / "arpeggio.mid")
    manifest["files"].append({"file": "arpeggio.mid", "format": "midi", "seconds": 2})

    (out / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(f"Wrote {len(manifest['files'])} test files to {out}")


if __name__ == "__main__":
    sys.exit(main())
