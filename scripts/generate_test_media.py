#!/usr/bin/env python3
"""Generate licence-free test media for AudioTastic (Implementation Plan, phase P0).

Creates synthetic signals (sine, sweep, silence, clicks and gapless pairs) in every format FFmpeg
can encode, plus a small MIDI file written directly. Output: tests/media/generated/ (git-ignored)
and a manifest.json describing each file.

OptimFROG files (FM-06) are encoded with the encoder from the verified OptimFROG package in
native/downloads/optimfrog (python native/fetch_optimfrog.py), used locally only and never shipped.
Other formats FFmpeg cannot encode (APE, Musepack, Shorten, DSF/DFF, XM/IT/S3M) need hand-made fixtures;
they are listed in the manifest under "missing". The MOD tests write their own module.

Usage: python scripts/generate_test_media.py [--ffmpeg PATH] [--out DIR]
"""
import argparse
import json
import shutil
import struct
import platform
import subprocess
import sys
import tempfile
import zipfile
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
    "alac": ("m4a", ["-c:a", "alac", "-sample_fmt", "s16p"]),
    "vorbis": ("ogg", ["-c:a", "libvorbis", "-q:a", "5"]),
    "opus": ("opus", ["-c:a", "libopus", "-b:a", "128k"]),
    "wavpack": ("wv", ["-c:a", "wavpack", "-sample_fmt", "s16p"]),
    "wma": ("wma", ["-c:a", "wmav2", "-b:a", "192k"]),
    "tta": ("tta", ["-c:a", "tta", "-sample_fmt", "s16"]),
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

MISSING = ["ape", "musepack", "shorten", "xm", "it", "s3m"]

# FM-06: host OS -> (OptimFROG package, encoder inside it)
OPTIMFROG = {
    "Windows": ("OptimFROG_Win_x64_5100", "ofr.exe"),
    "Linux": ("OptimFROG_Linux_x64_5100", "ofr"),
    "Darwin": ("OptimFROG_OSX_x64_5100", "ofr"),
}


def write_optimfrog(out: Path, manifest):
    """Encodes 16- and 24-bit WAVs to .ofr with the OptimFROG encoder (local test data only)."""
    package, encoder = OPTIMFROG.get(platform.system(), (None, None))
    archive = REPO / "native" / "downloads" / "optimfrog" / f"{package}.zip"
    if package is None or platform.machine().lower() not in ("amd64", "x86_64") or not archive.exists():
        print("OptimFROG encoder not available (run native/fetch_optimfrog.py); no .ofr test files")
        manifest["missing"].append("optimfrog")
        return
    with tempfile.TemporaryDirectory() as tmp, zipfile.ZipFile(archive) as zf:
        exe = Path(tmp) / encoder
        exe.write_bytes(zf.read(f"{package}/{encoder}"))
        exe.chmod(0o755)
        for source in ("sine1k.wav16.wav", "sweep.wav16.wav", "sine1k.wav24.wav"):
            signal, fmt, _ = source.split(".")
            name = f"{signal}.ofr{fmt.removeprefix('wav')}.ofr"
            (out / name).unlink(missing_ok=True)  # the encoder refuses to overwrite
            subprocess.run([str(exe), "--encode", str(out / source), "--output", str(out / name), "--preset", "2", "--silent"],
                           check=True)
            manifest["files"].append({"file": name, "signal": signal, "format": "optimfrog", "reference": source})


def run(ffmpeg, args):
    cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-y", *args]
    subprocess.run(cmd, check=True)


# PB-03 exit criterion: our R128 must be within 0.1 LU of FFmpeg's ebur128 filter on these files.
LOUDNESS_FILES = ["sine1k.wav16.wav", "sweep.wav16.wav", "clicks.wav16.wav", "sine1k.mp3.mp3", "sine1k.aac.m4a",
                  "sweep.opus.opus", "gapless.reference.wav", "long5min.flac", "surround51.flac", "sine1k.hires-24-96.flac"]


def ffmpeg_loudness(ffmpeg, path: Path):
    """Integrated loudness (LUFS) measured by FFmpeg's ebur128 filter, to three decimals."""
    result = subprocess.run([ffmpeg, "-hide_banner", "-nostats", "-i", str(path),
                             "-af", "ebur128=metadata=1,ametadata=print:key=lavfi.r128.I", "-f", "null", "-"],
                            capture_output=True, text=True, check=True)
    values = [line.split("=")[-1] for line in result.stderr.splitlines() if "lavfi.r128.I=" in line]
    return float(values[-1])


DSD_RATE = 2_822_400  # DSD64
DSD_SECONDS = 1.0


def dsd_bitstream(seconds=DSD_SECONDS, hz=1000.0, amplitude=0.5, phase=0.0):
    """1-bit DSD of a sine (2nd-order sigma-delta modulator). Returns a list of bytes, MSB = earliest bit."""
    import math
    n = int(DSD_RATE * seconds)
    out = bytearray((n + 7) // 8)
    i1 = i2 = 0.0
    y = 0.0
    step = 2 * math.pi * hz / DSD_RATE
    for k in range(n):
        x = amplitude * math.sin(step * k + phase)
        i1 += x - y
        i2 += i1 - y
        bit = 1 if i2 >= 0 else 0
        y = 1.0 if bit else -1.0
        if bit:
            out[k >> 3] |= 0x80 >> (k & 7)
    return bytes(out)


def reverse_bits(b):
    return int(f"{b:08b}"[::-1], 2)


def write_dsf(path: Path, channels):
    """DSF (Sony): 4096-byte blocks per channel, LSB-first bit order."""
    block = 4096
    per_channel = len(channels[0])
    blocks = (per_channel + block - 1) // block
    data = bytearray()
    lsb = [bytes(reverse_bits(b) for b in ch) for ch in channels]
    for i in range(blocks):
        for ch in lsb:
            chunk = ch[i * block:(i + 1) * block]
            data += chunk + bytes(block - len(chunk))
    fmt = struct.pack("<4sQIIIIIIQII", b"fmt ", 52, 1, 0, 2, len(channels), DSD_RATE, 1, per_channel * 8, block, 0)
    data_chunk = struct.pack("<4sQ", b"data", 12 + len(data)) + data
    total = 28 + len(fmt) + len(data_chunk)
    header = struct.pack("<4sQQQ", b"DSD ", 28, total, 0)
    path.write_bytes(header + fmt + data_chunk)


def write_dff(path: Path, channels):
    """DSDIFF (Philips): byte-interleaved channels, MSB-first bit order, big-endian chunk sizes."""
    def chunk(cid, body):
        pad = b"\0" if len(body) % 2 else b""
        return struct.pack(">4sQ", cid, len(body)) + body + pad
    ids = [b"SLFT", b"SRGT"][:len(channels)]
    prop = b"SND " + chunk(b"FS  ", struct.pack(">I", DSD_RATE)) \
        + chunk(b"CHNL", struct.pack(">H", len(channels)) + b"".join(ids)) \
        + chunk(b"CMPR", b"DSD " + bytes([14]) + b"not compressed" + b"\0")
    interleaved = bytes(b for frame in zip(*channels) for b in frame)
    body = b"DSD " + chunk(b"FVER", struct.pack(">I", 0x01050000)) + chunk(b"PROP", prop) + chunk(b"DSD ", interleaved)
    path.write_bytes(struct.pack(">4sQ", b"FRM8", len(body)) + body)


def write_dsd(out: Path, manifest):
    """FM-05 fixtures: a 1 kHz sine (left) and 1 kHz sine 90 degrees later (right) as DSD64, in DSF and DFF, plus
    the raw bitstreams (MSB first) for the DoP tests."""
    import math
    left = dsd_bitstream(phase=0.0)
    right = dsd_bitstream(phase=math.pi / 2)
    write_dsf(out / "sine1k.dsd64.dsf", [left, right])
    write_dff(out / "sine1k.dsd64.dff", [left, right])
    (out / "sine1k.dsd64.raw-left").write_bytes(left)
    (out / "sine1k.dsd64.raw-right").write_bytes(right)
    for name in ("sine1k.dsd64.dsf", "sine1k.dsd64.dff"):
        manifest["files"].append({"file": name, "signal": "sine1k", "format": "dsd64", "seconds": DSD_SECONDS})


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

    write_optimfrog(out, manifest)
    write_dsd(out, manifest)

    # CO-07 exit criterion: a 5-minute FLAC (music-like: chords with a slow envelope and a beat).
    run(args.ffmpeg, ["-f", "lavfi", "-i",
                      "aevalsrc='0.3*sin(2*PI*220*t)*(0.6+0.4*sin(2*PI*0.1*t))+0.2*sin(2*PI*330*t)+0.15*sin(2*PI*440*t)*lt(mod(t,0.5),0.1)"
                      "|0.3*sin(2*PI*277*t)*(0.6+0.4*cos(2*PI*0.07*t))+0.2*sin(2*PI*415*t)+0.15*sin(2*PI*440*t)*lt(mod(t,0.5),0.1)':s=44100",
                      "-t", "300", "-c:a", "flac", "-sample_fmt", "s16", str(out / "long5min.flac")])
    manifest["files"].append({"file": "long5min.flac", "format": "flac", "seconds": 300})
    # PB-03: a 5.1 file, to check the surround channel weights and LFE exclusion.
    run(args.ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000", "-t", "5", "-af",
                      "pan=5.1|FL=c0|FR=0.8*c0|FC=0.5*c0|LFE=c0|BL=0.3*c0|BR=0.3*c0",
                      "-c:a", "flac", "-sample_fmt", "s16", str(out / "surround51.flac")])
    manifest["files"].append({"file": "surround51.flac", "format": "flac", "seconds": 5, "channels": 6})
    loudness = {name: ffmpeg_loudness(args.ffmpeg, out / name) for name in LOUDNESS_FILES}
    (out / "loudness.json").write_text(json.dumps(loudness, indent=2))

    write_midi(out / "arpeggio.mid")
    manifest["files"].append({"file": "arpeggio.mid", "format": "midi", "seconds": 2})

    (out / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(f"Wrote {len(manifest['files'])} test files to {out}")


if __name__ == "__main__":
    sys.exit(main())
