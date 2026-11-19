# AudioTastic skin format (format 1)

A skin changes the look of the player windows (UI-03). The default skin uses this same format; its source is in `src/AudioTastic.App/Skins/Default/` and is a good starting point.

Classic Winamp `.wsz` skins are not supported.

## Package

- A skin is a **`.atskin` file**: a zip archive with `skin.json` at its root, plus the images and fonts it refers to.
- During development a skin can also be a **folder** containing `skin.json`.
- **Installing:** use Settings › Interface › Skin › Install skin…, or copy the `.atskin` file (or the folder) into the `skins` folder of the data folder.
- **Paths** inside `skin.json` are relative to the package root and use forward slashes (`images/play.svg`).
- **Images:**
  - SVG is preferred, because it is sharp at every scale (100–300 %, double size).
  - PNG also works. Add an `@2x` file next to it (`play.png` and `play@2x.png`); the larger one is used when the window is drawn at 150 % or more.
- **Fonts:** TrueType or OpenType files in the package, declared under `fonts`.

## Validation

`skin.json` is validated against [`skin.schema.json`](skin.schema.json) (JSON Schema draft-07) and then checked further:
- referenced files must exist;
- colour names must be defined;
- `bind` values must be valid for the element type.

A skin that fails is not used. The player keeps the default skin and shows the errors.

Add `"$schema": "https://audiotastic.app/schemas/skin-1.json"` (or a relative path to the schema file) to get completion in editors such as VS Code.

## skin.json

```json
{
  "format": 1,
  "name": "My Skin",
  "author": "Me",
  "version": "1.0.0",
  "colors": { "bg": "#0B0B0C", "text": "#F2F2F2" },
  "fonts": { "ui": { "family": "Inter", "files": ["fonts/Inter-Regular.ttf"], "size": 7 } },
  "windows": {
    "main": { "width": 330, "height": 128, "background": "@bg", "elements": [ ... ] },
    "mini": { "width": 330, "height": 24, "elements": [ ... ] }
  }
}
```

| Property | Meaning |
|---|---|
| `format` | Always `1` for this version. |
| `name`, `author`, `version`, `description` | Shown in the skin list. |
| `preview` | Optional preview image for the skin list. |
| `colors` | Named colours (`#RRGGBB` or `#AARRGGBB`). Anywhere a colour is expected you may use a hex value, `@name` or `transparent`. |
| `fonts` | Named fonts: `family` (the family name inside the files, or an installed system font), `files`, `weight` (`light` … `bold`) and default `size`. |
| `windows.main` | The main window. Required. |
| `windows.mini` | The mini player (UI-05). If it is missing, the default skin's mini player is used. |
| `windows.playlist` | The playlist window (PL-01, regions PW1–PW6). Resizable. If it is missing, the default skin's is used. |
| `windows.equalizer` | The equalizer window (EQ-01, regions EQ1–EQ4). If it is missing, the default skin's is used. |
| `windows.art` | The Art & Lyrics window (MD-02, MD-04, regions AL1–AL2). Resizable. If it is missing, the default skin's is used. |

### Windows

Each window has `width` and `height` in layout units. The player draws them at the player size from Settings › Interface (default 150 %), and double size (UI-08) doubles that. Use SVG so the skin stays sharp at every size. It also has an optional `background`, `border`, `borderWidth` and `cornerRadius`, and a list of `elements`. Elements are drawn in order, so later elements appear on top.

### Resizable windows and anchors

A window with `"resizable": true` (the playlist) can be resized by the user, down to `minWidth` × `minHeight`. Its `width` and `height` are the design size. Each element's `anchor` says which window edges it keeps its distance to when the window is larger or smaller than that:

- `left top` (default): stays where it is.
- `right top`: moves with the right edge (a close button).
- `left right top`: stretches horizontally (title bar, tabs, search).
- `left right top bottom`: stretches both ways (the track list).
- `left bottom` / `right bottom`: moves with the bottom edge (bottom bar buttons, info, resize grip).

### Elements

Every element has a `type`, a position (`x`, `y`) and a size (`width`, `height`). Most types also take `bind`, which says what the element does or shows.

| Type | `bind` | What it is |
|---|---|---|
| `rect` | — | A rectangle: `fill`, `stroke`, `strokeWidth`, `cornerRadius`. Decoration and panels. |
| `image` | — | A picture: `image`, and `color` for `currentColor` in SVG. |
| `text` | — | Static `text` with `font`, `fontSize`, `color`, `align`, `letterSpacing`. |
| `button` | `previous`, `play`, `pause`, `playPause`, `stop`, `next`, `open`, `menu`, `minimize`, `close`, `miniPlayer`, `expand`; in the playlist window also `hide` (close the playlist), `plAdd`, `plRemove`, `plSelect`, `plSort`, `plList` (PW5 menus); in the equalizer window `hide`, `eqPresets` (presets menu, EQ-02) and `eqDsp` (DSP chain window, EQ-05) | A push button. `playPause` is in the `active` state while playing, so `images.active` can show a pause icon. |
| `toggle` | `shuffle`, `repeat`, `mute`, `equalizer`, `playlist`, `crossfade`, `cast`; in the equalizer window `eqOn`, `eqAuto` (EQ-03), `eqParam` (EQ-04) | An on/off button. `repeat` uses the `alternate` state for repeat-track. Toggles for features not built yet are drawn `disabled`. |
| `slider` | `volume`, `seek` | A horizontal slider: `trackColor`, `fillColor`, `thumbColor`, `thumbSize`, `trackThickness`. |
| `time` | none, or `duration` | Without bind: elapsed/remaining time (CO-06); click to toggle; it blinks while paused. With `duration`: the track length. `font`, `fontSize`, `color`, `align`. |
| `marquee` | — | Scrolling "Artist – Title (duration)" (UI-06). `font`, `fontSize`, `color`, `speed` (px/s). |
| `waveform` | — | The waveform seek bar (CO-07, MW7): the track's peaks, played part in `fillColor`, the rest in `trackColor`, position line in `thumbColor`. Click or drag to seek. A thin centre line shows progress while the waveform is made; streams show a scrolling live waveform. |
| `indicators` | `state` (optional) | Format indicators and badges (UI-06): bitrate, kHz, channels, and `RG` when ReplayGain is applied (PB-03). With `"bind": "state"` the element shows the player-state badges instead: `OUT` (output rate differs from the file, PB-05), speed such as `1.25×` (PB-06), `A-B` (PB-08), `STOP AFTER` (PB-09), `SLEEP 30 MIN` (PB-10). A skin without a state element shows those badges at the start of its format row. `font`, `fontSize`, `color`, `stroke`, `spacing`. |
| `dragArea` | — | Dragging moves the window, together with the windows docked to it. Double-click toggles the mini player. |
| `art` | — | Album art (MD-02): the cover of the playing track (embedded, else cover/folder/front/albumart .jpg/.png in its folder), drawn uniformly scaled and centred; the logo when there is none or album art is off. `cornerRadius` rounds the image. In the main window (MW2) a click opens the Art & Lyrics window; in that window (AL1) it is the large art. |
| `visualisation` | — | Spectrum analyser or oscilloscope of the audio after the DSP chain (UI-07, MW6); clicking cycles spectrum → oscilloscope → off. Bars and the scope line use `fillColor`, peak caps `thumbColor`, the idle baseline `trackColor`. |
| `eqSlider` | `preamp`, `band0` … `band9` | A vertical ±12 dB slider for the preamp or one of the ten bands (60 Hz … 16 kHz). Drag, wheel (0.5 dB), double-click resets. Hidden in parametric mode. `trackColor`, `fillColor` (from 0 dB to the value), `thumbColor`, `thumbSize`. |
| `eqCurve` | — | The equalizer's response curve (EQ3): `fillColor` for the curve (dimmed while the EQ is off), `trackColor` for the 0 dB line, `color`/`font`/`fontSize` for the preset name. |
| `eqParametric` | — | The parametric editor (EQ-04), shown instead of the sliders in parametric mode; place it over them, after them in the element list. `fill` (its opaque background), `trackColor` (grid), `fillColor` (curve), `thumbColor` (band points), `color`/`font`/`fontSize` (labels). |
| `lyrics` | — | Lyrics of the playing track (MD-04, AL2): a .lrc file next to it or embedded lyrics. Synced lyrics highlight the line being sung in `color` (other lines `mutedColor`) and keep it in the middle; clicking a line seeks there. Plain lyrics use `color`. `font`/`fontSize`. The mouse wheel scrolls; no scroll bar is drawn. Hidden when lyrics are off in Settings › Metadata. |
| `playlistTabs` | — | Playlist tabs (PW2) as pills. Takes `font`, `spacing`, and per-state `strokes`/`fills`/`colors` (`active` = the tab shown). |
| `playlistSearch` | — | Search field (PW3). Takes `font`, `fontSize`, `color`, `stroke`, `fill`, `cornerRadius`; `text` is the placeholder. |
| `playlistList` | — | Track list (PW4). `font`, `fontSize` and `rowHeight` set the text and rows. `color` is for rows, `mutedColor` for numbers, durations and the scroll bar, `playingColor` for the playing row and its bar, `selectionColor` for selected rows. |
| `playlistInfo` | — | Selection / total duration (PW6). Takes `font`, `fontSize`, `color`, `align`. |
| `resizeGrip` | — | Drag to resize a resizable window from its bottom-right corner; the right and bottom edges snap to other player windows and the screen edge within 10 px. |

### States (buttons and toggles)

A button or toggle is drawn from, in this order:
1. **Background:** `fills` and `strokes` (per state), or `fill` and `stroke`.
2. **Image:** `images` (per state) or `image`, inset by `padding`. For SVG, `colors` (per state) or `color` sets `currentColor`, so one SVG drawn with `currentColor` serves every state.
3. **Text:** `text` with `font`, `fontSize` and the per-state `colors`, centred. The default skin's SHUF/REP/XF/EQ/PL/CAST pills use this.

The `repeat` toggle shows a small `1` after its text in repeat-track mode (the `alternate` state).

The states are:

| State | When |
|---|---|
| `normal` | Default. |
| `hover` | Pointer over the element. |
| `pressed` | Mouse button down. |
| `active` | Toggle on. |
| `alternate` | Second "on" state (repeat-track). |
| `disabled` | Feature not available. |

A state that is not given falls back to `normal`; `alternate` falls back to `active`.

## Making a package

Zip the contents of the skin folder, so that `skin.json` sits at the root of the zip, and rename the file to `.atskin`.
