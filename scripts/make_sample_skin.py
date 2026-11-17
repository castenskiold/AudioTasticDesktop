#!/usr/bin/env python3
"""Build a sample skin package (UI-03): a light variant of the default skin, as an example for skin authors and
for the P2 exit criterion "a second skin can be installed and switched to while playing".

The default skin's skin.json and images are taken from src/AudioTastic.App/Skins/Default, the Inter fonts from
docs/pdf-src/fonts and the logo from assets/branding (the same files the app embeds). Only the colour palette changes.

Usage: python scripts/make_sample_skin.py [--out DIR]   (default: tests/media/generated/skins, git-ignored)
Install the result with Main menu > Skins > Install skin...
"""
import argparse
import json
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEFAULT = REPO / "src" / "AudioTastic.App" / "Skins" / "Default"
FONTS = REPO / "docs" / "pdf-src" / "fonts"
LOGO = REPO / "assets" / "branding" / "png"

LIGHT = {"bg": "#FFFFFF", "panel": "#F2F2F4", "line": "#D0D0D6", "dim": "#B4B4BC", "muted": "#6E6E76", "text": "#16161A"}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default=str(REPO / "tests" / "media" / "generated" / "skins"))
    args = parser.parse_args()

    skin = json.loads((DEFAULT / "skin.json").read_text(encoding="utf-8"))
    skin.pop("$schema", None)
    skin["name"] = "AudioTastic Light"
    skin["description"] = "Sample skin: the default layout with a light palette (scripts/make_sample_skin.py)."
    skin["colors"] = LIGHT

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    package = out / "AudioTastic-Light.atskin"
    with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("skin.json", json.dumps(skin, indent=2))
        for image in sorted((DEFAULT / "images").glob("*.svg")):
            z.write(image, f"images/{image.name}")
        z.write(LOGO / "audiotastic-32.png", "images/logo.png")
        z.write(LOGO / "audiotastic-64.png", "images/logo@2x.png")
        for font in sorted({f for spec in skin["fonts"].values() for f in spec.get("files", [])}):
            z.write(FONTS / Path(font).name, font)
        z.write(FONTS / "LICENSE-Inter.txt", "fonts/LICENSE-Inter.txt")
    print(f"Wrote {package}")


if __name__ == "__main__":
    sys.exit(main())
