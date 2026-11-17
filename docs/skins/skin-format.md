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

### Windows

Each window has `width` and `height` in layout units. The player draws them at the player size from Settings › Interface (default 150 %), and double size (UI-08) doubles that. Use SVG so the skin stays sharp at every size. It also has an optional `background`, `border`, `borderWidth` and `cornerRadius`, and a list of `elements`. Elements are drawn in order, so later elements appear on top.

### Elements

Every element has a `type`, a position (`x`, `y`) and a size (`width`, `height`). Most types also take `bind`, which says what the element does or shows.

| Type | `bind` | What it is |
|---|---|---|
| `rect` | — | A rectangle: `fill`, `stroke`, `strokeWidth`, `cornerRadius`. Decoration and panels. |
| `image` | — | A picture: `image`, and `color` for `currentColor` in SVG. |
| `text` | — | Static `text` with `font`, `fontSize`, `color`, `align`, `letterSpacing`. |
| `button` | `previous`, `play`, `pause`, `playPause`, `stop`, `next`, `open`, `menu`, `minimize`, `close`, `miniPlayer`, `expand` | A push button. `playPause` is in the `active` state while playing, so `images.active` can show a pause icon. |
| `toggle` | `shuffle`, `repeat`, `mute`, `equalizer`, `playlist`, `crossfade`, `cast` | An on/off button. `repeat` uses the `alternate` state for repeat-track. Toggles for features not built yet are drawn `disabled`. |
| `slider` | `volume`, `seek` | A horizontal slider: `trackColor`, `fillColor`, `thumbColor`, `thumbSize`, `trackThickness`. |
| `time` | none, or `duration` | Without bind: elapsed/remaining time (CO-06); click to toggle; it blinks while paused. With `duration`: the track length. `font`, `fontSize`, `color`, `align`. |
| `marquee` | — | Scrolling "Artist – Title (duration)" (UI-06). `font`, `fontSize`, `color`, `speed` (px/s). |
| `indicators` | — | Format indicators and badges (UI-06): bitrate, kHz, channels. `font`, `fontSize`, `color`, `stroke`, `spacing`. |
| `dragArea` | — | Dragging moves the window, together with the windows docked to it. Double-click toggles the mini player. |
| `art` | — | Album art thumbnail (MW2). Shows the logo until album art (MD-02) arrives. |
| `visualisation` | — | Area reserved for the spectrum and oscilloscope (UI-07, phase P7). |

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
