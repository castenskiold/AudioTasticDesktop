#!/usr/bin/env python3
"""Make the user manual's screenshots (docs/manual/images) from the real app, without touching the screen.

Copies the Debug build of the app to artifacts/manual-app as a portable copy (its own settings, no effect on yours),
writes settings for the pictures (equalizer and Art & Lyrics shown, beat-matching crossfades, dark dialogs, global
hotkeys and media controls off), copies the demo tracks next to it and starts it with AUDIOTASTIC_MANUAL_SHOTS set.
The app (Debug builds only) then plays silently on the null audio device, opens every window, menu, dialog and settings
page, renders each one's own content to a PNG at 2x and exits. Nothing is captured from the screen.

Usage: python scripts/make_manual_screenshots.py [--music tracks/demo] [--vst3 artifacts/vst3-test-plugins] [--no-build]
Then:  python docs/manual/make_pdf.py
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP = ROOT / "artifacts" / "manual-app"
SHOTS = ROOT / "artifacts" / "manual-shots"
IMAGES = ROOT / "docs" / "manual" / "images"


def find_build():
    base = ROOT / "src" / "AudioTastic.App" / "bin" / "Debug"
    exe = "AudioTastic.exe" if os.name == "nt" else "AudioTastic"
    found = sorted(base.glob(f"*/{exe}"))
    if not found:
        sys.exit(f"No Debug build under {base}: run without --no-build")
    return found[-1].parent, exe


def settings(vst3):
    return {
        "schemaVersion": 2,
        "playback": {"resumeSession": "Off", "volume": 70},
        "crossfading": {"enabled": True, "mode": "BeatMatch"},
        "interface": {"equalizerVisible": True, "artVisible": True, "playlistVisible": True, "dialogTheme": "Dark"},
        "equalizerDsp": {
            "vst3Folders": [str(vst3)] if vst3 else [],
            "equalizer": {"bands": [4.5, 3, 1.5, 0, -1.5, -1, 0.5, 2, 3, 3.5], "preampDb": -2},
            "chain": [{"id": i, "enabled": i in ("equalizer", "limiter")}
                      for i in ("equalizer", "compressor", "widener", "reverb", "limiter")],
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--music", default=str(ROOT / "tracks" / "demo"))
    parser.add_argument("--vst3", default=str(ROOT / "artifacts" / "vst3-test-plugins"))
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()

    if not args.no_build:
        subprocess.run(["dotnet", "build", str(ROOT / "src" / "AudioTastic.App"), "-v", "q", "-nologo"], check=True)
    build, exe = find_build()

    shutil.rmtree(APP, ignore_errors=True)
    shutil.copytree(build, APP)
    (APP / "portable").touch()
    data = APP / "Data"
    data.mkdir(exist_ok=True)
    vst3 = Path(args.vst3) if Path(args.vst3).is_dir() else None
    (data / "settings.json").write_text(json.dumps(settings(vst3), indent=2), encoding="utf-8")
    music = APP / "Music"
    music.mkdir()
    for f in sorted(Path(args.music).iterdir()):
        if f.is_file():
            shutil.copy2(f, music / f.name)
    tracks = [str(f) for f in sorted(music.iterdir()) if f.suffix.lower() not in (".lrc", ".cue", ".jpg", ".png")]
    if not tracks:
        sys.exit(f"No tracks in {args.music}")

    shutil.rmtree(SHOTS, ignore_errors=True)
    env = dict(os.environ, AUDIOTASTIC_MANUAL_SHOTS=str(SHOTS))
    subprocess.run([str(APP / exe), *tracks], env=env, timeout=900, check=False)
    shots = sorted(SHOTS.glob("*.png"))
    if not shots:
        sys.exit(f"No screenshots were written; see the log in {data / 'logs'}")
    shutil.rmtree(IMAGES, ignore_errors=True)
    IMAGES.mkdir(parents=True)
    for shot in shots:
        shutil.copy2(shot, IMAGES / shot.name)
    compose()
    print(f"{len(list(IMAGES.glob('*.png')))} screenshots -> {IMAGES}")
    return 0


def compose():
    """Settings pages get the dialog background and a margin; the player picture is built from the docked windows."""
    from PIL import Image  # pip install pillow

    window = Image.open(IMAGES / "30-settings-window.png").convert("RGBA")
    background = window.getpixel((window.width - 12, window.height // 2))  # the page area, right of the content
    margin = 32
    for page in IMAGES.glob("3[1-9]-settings-*.png"):
        _pad(page, background, margin)
    for page in IMAGES.glob("40-settings-*.png"):
        _pad(page, background, margin)

    # The default docked layout: equalizer and playlist under the main window, Art & Lyrics to its right.
    main, eq, playlist, art = (Image.open(IMAGES / f).convert("RGBA") for f in
                               ("02-main-window.png", "14-equalizer.png", "05-playlist.png", "19-art-and-lyrics.png"))
    width = main.width + art.width
    height = max(main.height + eq.height + playlist.height, art.height)
    player = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    player.alpha_composite(main, (0, 0))
    player.alpha_composite(eq, (0, main.height))
    player.alpha_composite(playlist, (0, main.height + eq.height))
    player.alpha_composite(art, (main.width, 0))
    player.save(IMAGES / "01-player.png")


def _pad(path, background, margin):
    from PIL import Image

    content = Image.open(path).convert("RGBA")
    page = Image.new("RGBA", (content.width + 2 * margin, content.height + 2 * margin), background)
    page.alpha_composite(content, (margin, margin))
    page.save(path)


if __name__ == "__main__":
    sys.exit(main())
