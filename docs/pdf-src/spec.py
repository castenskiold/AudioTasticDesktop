import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import theme as T
from theme import *
from reportlab.lib.units import mm
from reportlab.platypus import Flowable
import spec_data as D

story = []
P = lambda t, s=B: Paragraph(t, s)

# ---------------------------------------------------------------- wireframe
class Wireframe(Flowable):
    H = 405
    def wrap(self, aw, ah):
        self.aw = aw; return aw, self.H
    # helpers (top-left coordinates)
    def Y(self, y): return self.H - y
    def rr(self, x, y, w, h, r=4, lw=0.8, col=WHITE, fill=None):
        c = self.canv; c.setStrokeColor(col); c.setLineWidth(lw)
        if fill is not None: c.setFillColor(fill)
        c.roundRect(x, self.Y(y + h), w, h, r, stroke=1, fill=1 if fill is not None else 0)
    def txt(self, x, y, s, size=5.5, font="InM", col=MUTED, align="l"):
        c = self.canv; c.setFillColor(col); c.setFont(font, size)
        f = {"l": c.drawString, "c": c.drawCentredString, "r": c.drawRightString}[align]
        f(x, self.Y(y), s)
    def tag(self, x, y, s):
        c = self.canv; w = c.stringWidth(s, "InSB", 5) + 6
        c.setFillColor(WHITE); c.roundRect(x, self.Y(y + 7), w, 7, 3.5, stroke=0, fill=1)
        c.setFillColor(BG); c.setFont("InSB", 5); c.drawString(x + 3, self.Y(y + 5.2), s)
    def pill(self, x, y, s, on=False, w=None):
        c = self.canv; w = w or c.stringWidth(s, "InSB", 4.6) + 7
        c.setStrokeColor(WHITE if on else DIM); c.setLineWidth(0.6)
        if on: c.setFillColor(WHITE)
        c.roundRect(x, self.Y(y + 8), w, 8, 4, stroke=1, fill=1 if on else 0)
        c.setFillColor(BG if on else TEXT); c.setFont("InSB", 4.6); c.drawCentredString(x + w / 2, self.Y(y + 5.6), s)
        return w
    def titlebar(self, x, y, w, title):
        c = self.canv
        c.drawImage(T.LOGO_SMALL, x + 4, self.Y(y + 13), 10, 10, mask="auto")
        self.txt(x + 17, y + 10, " ".join(title), 4.8, "InSB", MUTED)
        for i in range(3):
            c.setStrokeColor(DIM); c.setLineWidth(0.6); c.circle(x + w - 8 - i * 9, self.Y(y + 8), 2.6, stroke=1, fill=0)
        c.setStrokeColor(LINE); c.line(x + 1, self.Y(y + 16), x + w - 1, self.Y(y + 16))

    def draw(self):
        c = self.canv; c.setLineCap(1); c.setLineJoin(1)
        import math, random
        random.seed(4)
        # ---------------- main window
        X, Yo, W, Hh = 0, 0, 330, 128
        self.rr(X, Yo, W, Hh, 6, 0.9, WHITE, PANEL)
        self.titlebar(X, Yo, W, "AUDIOTASTIC")
        self.tag(X + 70, Yo + 4, "MW1")
        self.rr(X + 8, Yo + 22, W - 16, 54, 5, 0.6, LINE, BG)
        # art
        self.rr(X + 14, Yo + 28, 42, 42, 4, 0.6, DIM)
        c.drawImage(T.LOGO, X + 17, self.Y(Yo + 67), 36, 36, mask="auto")
        self.tag(X + 14, Yo + 28, "MW2")
        # time
        self.txt(X + 64, Yo + 50, "-3:41", 20, "InL", WHITE)
        self.tag(X + 112, Yo + 28, "MW3")
        # vis bars
        for i in range(16):
            h = 2 + abs(math.sin(i * 0.7)) * 9 * (1 - i / 24)
            c.setStrokeColor(TEXT); c.setLineWidth(1.6); c.line(X + 66 + i * 4.2, self.Y(Yo + 71), X + 66 + i * 4.2, self.Y(Yo + 71 - h))
        self.tag(X + 136, Yo + 60, "MW6")
        # title + indicators
        self.txt(X + 160, Yo + 41, "Artist Name – Track Title (5:12)  ·  Artist Name – Tr", 6.2, "In", WHITE)
        self.tag(X + 160, Yo + 24, "MW4")
        ix = X + 160
        for s in ["320 KBPS", "44 KHZ", "STEREO", "RG", "XF·BM"]:
            ix += self.pill(ix, Yo + 47, s) + 3
        self.tag(X + 160, Yo + 58, "MW5")
        # waveform
        self.rr(X + 8, Yo + 80, W - 16, 20, 4, 0.6, LINE, BG)
        mid = Yo + 90
        for i in range(0, W - 24, 2):
            a = (abs(math.sin(i * 0.09)) * 0.6 + random.random() * 0.4) * 7.5
            col = WHITE if i < 120 else DIM
            c.setStrokeColor(col); c.setLineWidth(1)
            c.line(X + 12 + i, self.Y(mid - a), X + 12 + i, self.Y(mid + a))
        self.tag(X + W - 30, Yo + 81, "MW7")
        # transport
        bx = X + 10
        for k in range(6):
            self.rr(bx, Yo + 106, 15, 12, 3, 0.6, DIM)
            cx, cy = bx + 7.5, Yo + 112
            c.setFillColor(WHITE); c.setStrokeColor(WHITE); c.setLineWidth(0.8)
            p = c.beginPath()
            if k == 1:
                p.moveTo(cx - 2.5, self.Y(cy - 3)); p.lineTo(cx + 3, self.Y(cy)); p.lineTo(cx - 2.5, self.Y(cy + 3)); p.close(); c.drawPath(p, fill=1, stroke=0)
            elif k == 2:
                c.rect(cx - 2.5, self.Y(cy + 3), 1.6, 6, fill=1, stroke=0); c.rect(cx + 0.9, self.Y(cy + 3), 1.6, 6, fill=1, stroke=0)
            elif k == 3:
                c.rect(cx - 2.5, self.Y(cy + 2.5), 5, 5, fill=1, stroke=0)
            elif k in (0, 4):
                d = -1 if k == 0 else 1
                p.moveTo(cx - 2 * d, self.Y(cy - 3)); p.lineTo(cx + 2 * d, self.Y(cy)); p.lineTo(cx - 2 * d, self.Y(cy + 3)); p.close(); c.drawPath(p, fill=1, stroke=0)
                c.rect(cx + 2.4 * d - 0.6, self.Y(cy + 3), 1.2, 6, fill=1, stroke=0)
            else:
                p.moveTo(cx - 3, self.Y(cy + 0.5)); p.lineTo(cx, self.Y(cy - 3)); p.lineTo(cx + 3, self.Y(cy + 0.5)); p.close(); c.drawPath(p, fill=1, stroke=0)
                c.rect(cx - 3, self.Y(cy + 3), 6, 1.2, fill=1, stroke=0)
            bx += 18
        self.tag(X + 10, Yo + 120, "MW8")
        # volume
        c.setStrokeColor(DIM); c.setLineWidth(1.2); c.line(X + 124, self.Y(Yo + 112), X + 176, self.Y(Yo + 112))
        c.setStrokeColor(WHITE); c.line(X + 124, self.Y(Yo + 112), X + 160, self.Y(Yo + 112))
        c.setFillColor(WHITE); c.circle(X + 160, self.Y(Yo + 112), 2.6, stroke=0, fill=1)
        self.tag(X + 124, Yo + 116, "MW9")
        tx = X + 186
        for s, on in [("SHUF", False), ("REP", True), ("XF", True), ("EQ", True), ("PL", True), ("CAST", False)]:
            tx += self.pill(tx, Yo + 108, s, on) + 3
        self.tag(X + 186, Yo + 118, "MW10")

        # ---------------- equalizer
        Y2 = 132
        self.rr(X, Y2, W, 104, 6, 0.9, WHITE, PANEL)
        self.titlebar(X, Y2, W, "EQUALIZER"); self.tag(X + 64, Y2 + 4, "EQ1")
        ex = X + 10
        for s, on in [("ON", True), ("AUTO", False), ("PARAM", False), ("PRESETS", False), ("DSP", False)]:
            ex += self.pill(ex, Y2 + 22, s, on) + 3
        self.tag(X + 10, Y2 + 32, "EQ2")
        self.rr(X + 160, Y2 + 21, W - 170, 18, 3, 0.6, LINE, BG)
        p = c.beginPath(); c.setStrokeColor(WHITE); c.setLineWidth(0.8)
        vals = [4, 5, 3, 1, 0, -1, 1, 3, 4, 4]
        for i, v in enumerate(vals):
            xx = X + 166 + i * ((W - 182) / 9); yy = self.Y(Y2 + 30 - v)
            p.moveTo(xx, yy) if i == 0 else p.lineTo(xx, yy)
        c.drawPath(p, stroke=1, fill=0)
        self.tag(X + 166, Y2 + 41, "EQ3")
        labels = ["PRE", "60", "170", "310", "600", "1K", "3K", "6K", "12K", "14K", "16K"]
        for i, lab in enumerate(labels):
            sx = X + 22 + i * 27 + (8 if i else 0)
            c.setStrokeColor(DIM); c.setLineWidth(1.2); c.line(sx, self.Y(Y2 + 50), sx, self.Y(Y2 + 88))
            v = 0 if i == 0 else vals[i - 1]
            ky = Y2 + 69 - v * 3
            c.setStrokeColor(WHITE); c.line(sx, self.Y(Y2 + 69), sx, self.Y(ky))
            c.setFillColor(BG); c.setStrokeColor(WHITE); c.setLineWidth(0.8); c.roundRect(sx - 4, self.Y(ky + 2), 8, 4, 2, stroke=1, fill=1)
            self.txt(sx, Y2 + 97, lab, 4.4, "InSB", MUTED, "c")
        self.tag(X + W - 28, Y2 + 52, "EQ4")

        # ---------------- playlist
        Y3 = 240
        self.rr(X, Y3, W, 162, 6, 0.9, WHITE, PANEL)
        self.titlebar(X, Y3, W, "PLAYLIST"); self.tag(X + 58, Y3 + 4, "PW1")
        px = X + 10
        for s, on in [("Main", True), ("Chill", False), ("Party", False), ("+", False)]:
            px += self.pill(px, Y3 + 20, s, on) + 3
        self.tag(px + 6, Y3 + 20, "PW2")
        self.rr(X + 10, Y3 + 32, W - 20, 10, 5, 0.6, DIM, BG)
        self.txt(X + 16, Y3 + 39, "J   Search playlist…", 5, "In", MUTED)
        self.tag(X + W - 34, Y3 + 33.5, "PW3")
        rows = [("1.", "Artist One – Opening Track", "4:02"), ("2.", "Artist Two – Now Playing", "5:12"),
                ("3.", "Artist Three – Queued Next", "3:47"), ("4.", "Artist Four – Another Track", "6:20"),
                ("5.", "Artist Five – Long Mix", "1:02:13"), ("6.", "Artist Six – Closing Track", "3:58")]
        for i, (n, t, d) in enumerate(rows):
            ry = Y3 + 48 + i * 13
            if i == 1:
                c.setFillColor(PANEL2); c.roundRect(X + 8, self.Y(ry + 11), W - 16, 11, 3, stroke=0, fill=1)
                c.setStrokeColor(WHITE); c.setLineWidth(1.2); c.line(X + 9, self.Y(ry + 2), X + 9, self.Y(ry + 9))
            self.txt(X + 14, ry + 8, n, 5.5, "In", MUTED)
            self.txt(X + 28, ry + 8, t, 5.8, "InM" if i == 1 else "In", WHITE if i == 1 else TEXT)
            self.txt(X + W - 14, ry + 8, d, 5.5, "In", MUTED, "r")
            if i == 2: self.pill(X + 140, ry + 1.5, "Q1")
        self.tag(X + W - 70, Y3 + 63, "PW4")
        bx = X + 10
        for s in ["ADD", "REM", "SEL", "SORT", "LIST"]:
            bx += self.pill(bx, Y3 + 146, s) + 3
        self.tag(bx + 4, Y3 + 146, "PW5")
        self.txt(X + W - 12, Y3 + 152, "5:12 / 1:25:22", 5.5, "InM", TEXT, "r")
        self.tag(X + W - 90, Y3 + 146, "PW6")

        # ---------------- art & lyrics
        AX, AW = 340, self.aw - 340
        self.rr(AX, 0, AW, 236, 6, 0.9, WHITE, PANEL)
        self.titlebar(AX, 0, AW, "ART & LYRICS")
        self.rr(AX + 8, 22, AW - 16, AW - 16, 5, 0.6, LINE, BG)
        s = AW - 36
        c.drawImage(T.LOGO, AX + 18, self.Y(32 + s), s, s, mask="auto")
        self.tag(AX + 10, 24, "AL1")
        ly = 22 + AW - 16 + 12
        lines = [("Previous lyric line here", DIM), ("Current line is highlighted", WHITE), ("Next lyric line follows", MUTED),
                 ("And another one after that", DIM), ("Click a line to seek", DIM)]
        for i, (t, col) in enumerate(lines):
            self.txt(AX + AW / 2, ly + i * 10, t, 5.8 if col == WHITE else 5.4, "InM" if col == WHITE else "In", col, "c")
        self.tag(AX + 10, ly + 46, "AL2")

        # ---------------- mini player (two rows, UI-05)
        MY = 256
        self.txt(AX, MY - 6, "M I N I   P L A Y E R", 4.8, "InSB", MUTED)
        self.rr(AX, MY, AW, 38, 7, 0.9, WHITE, PANEL)
        c.drawImage(T.LOGO_SMALL, AX + 6, self.Y(MY + 15), 10, 10, mask="auto")
        self.txt(AX + 20, MY + 12, "Artist Two – Now Playing", 6.2, "InSB", WHITE)
        self.txt(AX + 6, MY + 29, "1:31", 5.4, "InL", WHITE)
        c.setStrokeColor(DIM); c.setLineWidth(1); c.line(AX + 22, self.Y(MY + 27), AX + AW * 0.45, self.Y(MY + 27))
        c.setStrokeColor(WHITE); c.line(AX + 22, self.Y(MY + 27), AX + 40, self.Y(MY + 27))
        self.txt(AX + AW * 0.45 + 4, MY + 29, "5:12", 5.4, "InL", MUTED)
        cx, cy = AX + AW * 0.66, MY + 27
        c.setStrokeColor(WHITE); c.setLineWidth(0.8); c.circle(cx, self.Y(cy), 5, stroke=1, fill=0)
        c.setFillColor(WHITE)
        p = c.beginPath(); p.moveTo(cx - 1.5, self.Y(cy - 2.2)); p.lineTo(cx + 2.4, self.Y(cy)); p.lineTo(cx - 1.5, self.Y(cy + 2.2)); p.close(); c.drawPath(p, fill=1, stroke=0)
        self.pill(AX + AW - 34, MY + 23, "SHF")
        self.tag(AX + AW - 22, MY + 40, "MP")

        # ---------------- legend
        self.txt(AX, 310, "M A I N   M E N U", 4.8, "InSB", MUTED)
        self.txt(AX, 320, "Click the logo in MW1, or right-click", 5.4, "In", TEXT)
        self.txt(AX, 329, "anywhere in the main window.", 5.4, "In", TEXT)
        self.txt(AX, 345, "D O C K I N G", 4.8, "InSB", MUTED)
        self.txt(AX, 355, "Windows snap together within 10 px", 5.4, "In", TEXT)
        self.txt(AX, 364, "and move as one group (UI-01).", 5.4, "In", TEXT)


class Pipeline(Flowable):
    H = 120
    def wrap(self, aw, ah): self.aw = aw; return aw, self.H
    def box(self, x, y, w, h, t1, t2=None, hl=False):
        c = self.canv
        c.setStrokeColor(WHITE if hl else DIM); c.setLineWidth(0.8); c.setFillColor(PANEL)
        c.roundRect(x, self.H - y - h, w, h, 5, stroke=1, fill=1)
        c.setFillColor(WHITE); c.setFont("InM", 6.6); c.drawCentredString(x + w / 2, self.H - y - h / 2 + (2 if t2 else -2), t1)
        if t2: c.setFillColor(MUTED); c.setFont("In", 5.3); c.drawCentredString(x + w / 2, self.H - y - h / 2 - 6, t2)
    def arrow(self, x1, y1, x2, y2):
        c = self.canv; c.setStrokeColor(WHITE); c.setLineWidth(0.8); c.setLineCap(1)
        c.line(x1, self.H - y1, x2, self.H - y2)
        import math
        a = math.atan2((self.H - y2) - (self.H - y1), x2 - x1)
        for d in (2.6, -2.6):
            c.line(x2, self.H - y2, x2 - 4 * math.cos(a + d / 6), self.H - y2 - 4 * math.sin(a + d / 6))
    def draw(self):
        bw, bh = 64, 30
        self.box(0, 8, bw, bh, "Deck A", "decoder + time-stretch")
        self.box(0, 50, bw, bh, "Deck B", "next track (preload)")
        self.box(84, 29, bw, bh, "ReplayGain", "per deck")
        self.box(168, 29, bw, bh, "Crossfade mixer", "XF-01 / XF-02", True)
        self.box(252, 29, bw, bh, "DSP chain", "EQ · effects · VST3")
        self.box(336, 29, bw, bh, "Volume", "gain stage")
        self.box(self.aw - 64, 8, 64, bh, "Output", "miniaudio")
        self.box(self.aw - 64, 50, 64, bh, "Cast", "stream server")
        self.box(252, 84, bw, 26, "Visualisation", "FFT tap")
        self.arrow(64, 23, 84, 40); self.arrow(64, 65, 84, 50)
        self.arrow(148, 44, 168, 44); self.arrow(232, 44, 252, 44); self.arrow(316, 44, 336, 44)
        self.arrow(400, 40, self.aw - 64, 25); self.arrow(400, 50, self.aw - 64, 63)
        self.arrow(284, 59, 284, 84)


# ---------------------------------------------------------------- 1. intro
story += T.h1("1. About this document")
story.append(P("This document specifies every required feature of AudioTastic Desktop: <b>what it does</b>, <b>where it is in the user interface</b> "
               "and <b>exactly what is needed to build it</b> (components, libraries, platform APIs and dependencies on other features). "
               "It is the detailed companion to the Required Features document; where the two differ, the Required Features document defines <i>what</i> is in scope "
               "and this document defines <i>how</i> it behaves and is built."))
story.append(Spacer(1, 8))
story.append(T.table([
    ("Feature IDs", "Each feature has a short ID (e.g. XF-02) used in code, issues and commits. Prefixes: CO core, FM formats, PB playback engine, XF crossfading, AN analysis, PL playlists, MD metadata, UI interface, SI system integration, EQ equalizer & DSP, CN casting & network, PG plugins, TL tools."),
    ("UI references", "Codes like MW3 or PW5 point to the regions in the UI map (section 2)."),
    ("Settings", "'Settings › Group: option' means the option lives in that group of the General settings window (UI-10)."),
    ("Platforms", "Windows is the primary platform; macOS and Linux are supported. Platform-specific parts are named per OS; features marked Windows only are hidden elsewhere."),
    ("Licences", "Every library named here must be free and open source (MIT, BSD, Apache, zlib, public domain; LGPL only when dynamically linked). Exception: closed-source freeware is allowed when no open-source option exists, it needs no licence key and the end user never pays (e.g. OptimFROG SDK). Paid, commercial-only or licence-key components are never allowed; GPL/AGPL only with Anders's approval."),
    ("Proposals", "Exact window layouts, keyboard shortcuts and default values are a first proposal and may be adjusted during development; behaviour descriptions are binding."),
], head=("Convention", "Meaning")))

# ---------------------------------------------------------------- 2. UI map
story.append(PageBreak())
story += T.h1("2. User interface map")
story.append(P("The player follows the classic Winamp layout: separate windows that dock together. Below is the default layout in the default skin (UI-02), with the region codes used throughout this document.", B))
story.append(Spacer(1, 10))
story.append(Wireframe())
story.append(Spacer(1, 10))
story += T.h2("2.1 Window regions")
story.append(T.table([
    ("MW1", "Main window title bar", "Logo (opens main menu), title, mini player, minimise and close buttons. Drag to move the docked group; double-click toggles the mini player."),
    ("MW2", "Album art thumbnail", "Cover of the current track (MD-02); click opens the Art & Lyrics window."),
    ("MW3", "Time display", "Elapsed / remaining time; click toggles (CO-06)."),
    ("MW4", "Scrolling title", "Artist – Title (duration), scrolling when too long (UI-06)."),
    ("MW5", "Indicator row", "Bitrate, sample rate, channels and status badges: RG, A-B, speed, STOP, sleep timer, cast device, DoP, crossfade mode and BPM."),
    ("MW6", "Visualisation", "Spectrum analyser or oscilloscope; click cycles (UI-07)."),
    ("MW7", "Waveform / seek bar", "Whole-track waveform beneath the display panel; click or drag to seek; shows A-B loop and crossfade overlap (CO-07)."),
    ("MW8", "Transport buttons", "Previous, Play, Pause, Stop, Next, Open (CO-02)."),
    ("MW9", "Volume", "Volume slider and mute; right-click for output device menu (CO-05, PB-04)."),
    ("MW10", "Toggle buttons", "SHUF (CO-03), REP (CO-04), XF crossfading (XF-01/02), EQ and PL window toggles (UI-01), CAST device picker (CN-05)."),
    ("EQ1", "Equalizer title bar", "Title and close button."),
    ("EQ2", "Equalizer buttons", "ON, AUTO (EQ-03), PARAM (EQ-04), PRESETS (EQ-02), DSP chain (EQ-05)."),
    ("EQ3", "Response curve", "Live frequency-response curve of the current EQ settings."),
    ("EQ4", "Sliders", "Preamp and ten band sliders (EQ-01)."),
    ("PW1", "Playlist title bar", "Title and close button; the playlist window is resizable."),
    ("PW2", "Playlist tabs", "One tab per playlist, + adds a tab (PL-02)."),
    ("PW3", "Search field", "Opens with J; filters the list as you type (PL-05)."),
    ("PW4", "Track list", "Entries with number, artist – title and duration; now playing highlighted with a white bar; queue numbers (Q1…) for queued tracks."),
    ("PW5", "Playlist buttons", "ADD, REM, SEL, SORT, LIST menus (PL-01, PL-03, PL-06)."),
    ("PW6", "Duration info", "Selection / total duration and background analysis status (PL-07, AN-01)."),
    ("AL1", "Album art", "Full-size cover; right-click for online lookup (MD-02, MD-03)."),
    ("AL2", "Lyrics", "Synced or plain lyrics (MD-04)."),
    ("MP", "Mini player", "Compact two-row player replacing all windows: title row, then time, seek, transport, shuffle, playlist and mute (UI-05)."),
], head=("Code", "Region", "Contents"), widths=(17, 40, 113), status_col=-1))

story += T.h2("2.2 Menus")
story.append(T.table([
    ("Main menu", "Logo in MW1, or right-click the main window",
     "Open file… · Open folder… · Open URL… · Play CD · Playback (Shuffle, Repeat, Speed…, A-B repeat, Stop after current) · Sleep timer… · Cast to… · Visualisation · Windows (Equalizer, Playlist, Art & Lyrics, Mini player) · Skins · Options (Double size, Always on top) · Settings… · About AudioTastic · Exit"),
    ("Playlist context menu", "Right-click an entry in PW4",
     "Play · Play next (queue) · Stop after this track · File info… · Edit tags… · Look up online… · Convert… · Rip… (CD tracks) · Reset resume position · Show in folder · Remove · Crop"),
    ("Tab context menu", "Right-click a tab in PW2", "Rename · Duplicate · Save as… · Move left · Move right · Close"),
    ("Tray menu", "Tray / menu bar icon", "Play/Pause · Stop · Previous · Next · Sleep timer… · Show/Hide AudioTastic · Exit"),
    ("Device picker", "CAST button in MW10", "This computer · discovered Chromecast, DLNA and AirPlay devices · Refresh"),
], head=("Menu", "Opened from", "Items"), widths=(30, 40, 100), status_col=-1))

story += T.h2("2.3 Dialogs and other windows")
story.append(T.table([
    ("Settings window", "Main menu › Settings… (Ctrl+P). Groups: Playback, Output, Crossfading, Metadata, Casting & network, Interface, System integration, Equalizer & DSP, Plugins, General (UI-10)."),
    ("Tag editor", "Playlist context menu › Edit tags… (MD-01, MD-03)."),
    ("File info", "Playlist context menu › File info… (MD-05)."),
    ("DSP chain", "EQ2 › DSP (EQ-05, EQ-06, EQ-07)."),
    ("Convert", "Playlist context menu › Convert… (TL-01, FM-11)."),
    ("Open URL", "Main menu › Open URL… (FM-10)."),
    ("Speed", "Main menu › Playback › Speed… popover (PB-06)."),
    ("About", "Main menu › About AudioTastic (UI-04)."),
], head=("Window", "Opened from / features")))

story += T.h2("2.4 Default keyboard shortcuts")
story.append(T.table([
    ("Z / X / C / V / B", "Previous / Play / Pause / Stop / Next"),
    ("L · Ctrl+L", "Open file · Open URL"),
    ("S · R", "Shuffle · Repeat"),
    ("J · Q", "Search playlist · Play next (queue)"),
    ("Shift+V · [", "Stop after current · A-B repeat"),
    ("Up / Down · Left / Right", "Volume · Seek ±5 s"),
    ("Alt+G · Alt+E · Alt+L", "Equalizer · Playlist · Art & Lyrics windows"),
    ("Ctrl+W · Ctrl+D", "Mini player · Double size"),
    ("Ctrl+E · Alt+3 · Ctrl+P", "Edit tags · File info · Settings"),
], head=("Keys", "Action")))

# ---------------------------------------------------------------- 3. architecture
story.append(PageBreak())
story += T.h1("3. Architecture overview")
story.append(P("The build requirements in section 5 refer to these projects and shared components.", B))
story.append(Spacer(1, 6))
story.append(T.table([
    ("AudioTastic.App", "Avalonia UI: windows, skin engine, controls (waveform, spectrum, time display), window snapping, tray, settings window."),
    ("AudioTastic.Core", "PlaybackEngine, MixerGraph (decks A/B, gain stages, time-stretch), CrossfadeController, BeatMatcher, DspChain, OutputService, PlaylistService, PlayOrder, QueueService."),
    ("AudioTastic.Sdk", "Public plugin contracts (NuGet). No dependency on Core or App."),
    ("AudioTastic.Plugins.Host", "Plugin discovery, manifest validation, one AssemblyLoadContext per plugin, fault handling."),
    ("AudioTastic.Analysis", "AnalysisService queue; waveform, loudness and BPM/beat/break analysers."),
    ("AudioTastic.Metadata", "TagService (TagLibSharp, FFmpeg fallback, cached), TagLoader (background tag reading), ArtService, LyricsService, OnlineLookupService."),
    ("AudioTastic.Casting", "CastService, Chromecast/DLNA/AirPlay targets, discovery, StreamServer, Transcoder, remote control API."),
    ("AudioTastic.Data", "SettingsStore (JSON), CacheStore (SQLite), AppPaths (normal / portable)."),
    ("AudioTastic.Platform (+ .Windows / .MacOS / .Linux)", "Interfaces and per-OS implementations: media controls, hotkeys, taskbar, shell integration, notifications, CD drive."),
    ("AudioTastic.NativeBridge", "32-bit helper process for native Winamp plugins (Windows only)."),
    ("native/ (C/C++, CMake)", "miniaudio with a small flat C shim used by the .NET output code; SoundTouch; libopenmpt and later the VST3 host shim, built per platform. FFmpeg is fetched as LGPL builds."),
    ("plugins/", "Built-in plugins written against the SDK: FFmpeg decoder, libopenmpt, MIDI, visualisations, DSP effects, cast targets."),
], head=("Project", "Responsibility"), widths=(52, 118)))
story += T.h2("3.1 Audio pipeline")
story.append(P("All audio is 32-bit float inside the pipeline. The audio thread never allocates or locks; parameters change through lock-free updates.", B))
story.append(Spacer(1, 6))
story.append(Pipeline())
story += T.h2("3.2 Stored data")
story.append(T.table([
    ("settings.json", "All settings (SettingsStore), with schema version for migrations."),
    ("playlists/", "One M3U8 file per playlist tab (titles in #EXTINF, CUE ranges in an #AT-RANGE line) plus playlists.json (tab order, names, active tab, scroll position)."),
    ("cache.db (SQLite)", "Waveform peaks, loudness, BPM/beat/break data, durations, resume positions, auto-EQ file rules, tags read for the playlist. Keyed by path + size + modified time."),
    ("presets/, skins/, plugins/", "EQ presets, installed skins (.atskin), installed plugins."),
    ("logs/, crash/", "Serilog rolling logs and crash reports."),
], head=("Location (data folder)", "Contents"), widths=(52, 118)))

# ---------------------------------------------------------------- 4. open points
story += T.h1("4. Open points")
if D.OPEN_POINTS:
    story.append(P("Points found while writing this specification that need a decision from Anders before the feature is built.", B))
    story.append(Spacer(1, 6))
    story.append(T.table(D.OPEN_POINTS, head=("Point", "Details and options")))
else:
    story.append(P("There are no open points at the moment. Points resolved so far: OptimFROG uses the closed-source freeware SDK under the licence-policy exception, and its libraries may be bundled (FM-06); the bundled MIDI SoundFont is GeneralUser GS (FM-08); Avalonia 12; FFmpeg.AutoGen bindings; miniaudio through an own C shim; NJsonSchema for skin validation; the player is shown at 150 % by default and the mini player has two rows (UI-05, UI-08).", B))

# ---------------------------------------------------------------- feature index
story.append(PageBreak())
story += T.h1("Feature index")
allf = [(f[0], f[1]) for _, _, fs in D.SECTIONS for f in fs]
half = (len(allf) + 1) // 2
L, R = allf[:half], allf[half:]
rows = []
for i in range(half):
    a = L[i]; b = R[i] if i < len(R) else ("", "")
    rows.append((a[0], a[1], b[0], b[1]))
idx = Table([[Paragraph(x, CB if j in (0, 2) else C) for j, x in enumerate(r)] for r in rows],
            colWidths=[17 * mm, 68 * mm, 17 * mm, 68 * mm])
idx.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, -1), PANEL), ("ROUNDEDCORNERS", [6, 6, 6, 6]), ("BOX", (0, 0), (-1, -1), 0.6, LINE),
                         ("LINEBEFORE", (2, 0), (2, -1), 0.4, LINE), ("VALIGN", (0, 0), (-1, -1), "TOP"),
                         ("TOPPADDING", (0, 0), (-1, -1), 3), ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
                         ("LEFTPADDING", (0, 0), (-1, -1), 7)] + [("LINEABOVE", (0, i), (-1, i), 0.3, LINE2) for i in range(1, len(rows))]))
story.append(idx)

# ---------------------------------------------------------------- 5. features
LW = 27 * mm
def card(f):
    fid, name, what, ui, build, settings = f
    rows = [[Paragraph(f"<font name='InSB' size='7.5' color='#8E8E93'>{fid}</font>&nbsp;&nbsp;&nbsp;<font name='InSB' size='10.5' color='#F2F2F2'>{name}</font>", B), ""]]
    for lab, val in [("What it does", what), ("UI location", ui), ("To build", build), ("Settings", settings)]:
        if val:
            rows.append([Paragraph(lab.upper(), LBL), Paragraph(val.replace("[Windows only]", T.WO), C)])
    t = Table(rows, colWidths=[LW, T.FRAME_W - LW])
    st = [("SPAN", (0, 0), (1, 0)), ("BACKGROUND", (0, 0), (-1, -1), PANEL), ("ROUNDEDCORNERS", [7, 7, 7, 7]),
          ("BOX", (0, 0), (-1, -1), 0.6, LINE), ("VALIGN", (0, 0), (-1, -1), "TOP"),
          ("LINEBELOW", (0, 0), (-1, 0), 0.9, WHITE),
          ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
          ("TOPPADDING", (0, 0), (-1, 0), 8), ("BOTTOMPADDING", (0, 0), (-1, 0), 7),
          ("LEFTPADDING", (0, 0), (-1, -1), 9), ("RIGHTPADDING", (0, 0), (-1, -1), 9)]
    for i in range(2, len(rows)):
        st.append(("LINEABOVE", (0, i), (-1, i), 0.35, LINE2))
    t.setStyle(TableStyle(st))
    return t

for n, (title, intro, feats) in enumerate(D.SECTIONS, start=5):
    story.append(PageBreak())
    story += T.h1(f"{n}. {title}")
    story.append(P(intro, B)); story.append(Spacer(1, 8))
    for f in feats:
        story.append(KeepTogether([card(f), Spacer(1, 9)]))

T.build("AudioTastic_Feature_Specification.pdf", "Feature Specification",
        "What each feature does, where it lives in the UI and what it takes to build",
        "Version 1.16  ·  19 November 2026", story, short_title="Feature Specification  ·  v1.16")
print("built")
