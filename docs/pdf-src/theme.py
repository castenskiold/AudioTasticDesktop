"""Shared AudioTastic PDF theme.
Run the doc scripts from anywhere: python docs/pdf-src/spec.py (output goes to the repo root unless AUDIOTASTIC_PDF_OUT is set).
Shared AudioTastic PDF theme: dark, monochrome, thin rounded white line work (based on the logo)."""
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.enums import TA_CENTER
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, PageTemplate, Frame, Paragraph, Spacer, Table, TableStyle,
                                KeepTogether, PageBreak, NextPageTemplate, Flowable, CondPageBreak)

import os
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT_DIR = os.environ.get("AUDIOTASTIC_PDF_OUT", REPO)
F = os.path.join(HERE, "fonts") + os.sep
for n, f in [("In", "Regular"), ("InL", "Light"), ("InM", "Medium"), ("InSB", "SemiBold"), ("InB", "Bold"), ("InI", "Italic")]:
    pdfmetrics.registerFont(TTFont(n, F + f"Inter-{f}.ttf"))
from reportlab.lib.fonts import addMapping
addMapping("In", 0, 0, "In"); addMapping("In", 1, 0, "InSB"); addMapping("In", 0, 1, "InI"); addMapping("In", 1, 1, "InSB")
addMapping("InL", 1, 0, "InSB"); addMapping("InL", 0, 1, "InI"); addMapping("InL", 1, 1, "InSB"); addMapping("InL", 0, 0, "InL")

LOGO = os.environ.get("AUDIOTASTIC_LOGO", os.path.join(REPO, "assets", "branding", "audiotastic-logo-transparent.png"))
LOGO_SMALL = LOGO

BG = colors.HexColor("#0B0B0C")
PANEL = colors.HexColor("#131315")
PANEL2 = colors.HexColor("#18181B")
LINE = colors.HexColor("#2A2A2E")
LINE2 = colors.HexColor("#202023")
WHITE = colors.HexColor("#F2F2F2")
TEXT = colors.HexColor("#D6D6D8")
MUTED = colors.HexColor("#8E8E93")
DIM = colors.HexColor("#5E5E63")

PAGE_W, PAGE_H = A4
LM = RM = 20 * mm
TM, BM = 24 * mm, 20 * mm
FRAME_W = PAGE_W - LM - RM

H1 = ParagraphStyle("H1", fontName="InSB", fontSize=17, leading=22, textColor=WHITE, spaceBefore=14, spaceAfter=8)
H2 = ParagraphStyle("H2", fontName="InM", fontSize=12, leading=16, textColor=WHITE, spaceBefore=10, spaceAfter=5)
H3 = ParagraphStyle("H3", fontName="InM", fontSize=10, leading=13, textColor=WHITE, spaceBefore=6, spaceAfter=3)
B = ParagraphStyle("B", fontName="InL", fontSize=9.4, leading=14, textColor=TEXT)
BM_ = ParagraphStyle("BMut", parent=B, textColor=MUTED, fontSize=8.6, leading=12.5)
C = ParagraphStyle("C", fontName="InL", fontSize=8.5, leading=12, textColor=TEXT)
CB = ParagraphStyle("CB", fontName="InM", fontSize=8.5, leading=12, textColor=WHITE)
HD = ParagraphStyle("HD", fontName="InSB", fontSize=7.2, leading=10, textColor=MUTED)
LBL = ParagraphStyle("LBL", fontName="InSB", fontSize=6.8, leading=10, textColor=MUTED)

GS = "<font name='InM' color='#A9A9AE' size='7.6'>[SETTINGS]</font>"
WO = "<font name='InM' color='#A9A9AE' size='7.6'>[WINDOWS ONLY]</font>"

STATUS = {"Required": "#F2F2F2", "High": "#F2F2F2", "Medium": "#B5B5BA", "Low": "#7C7C82"}


def status(v):
    col = STATUS.get(v)
    if not col:
        return Paragraph(v, C)
    return Paragraph(f"<font name='InSB' color='{col}' size='7.6'>{v.upper()}</font>", C)


def caps(t):
    return " ".join(t.upper())


class Rule(Flowable):
    """Thin white line with rounded caps (logo stroke style)."""
    def __init__(self, width=None, w=1.2, color=WHITE, space=4, length=None):
        super().__init__(); self.w = w; self.color = color; self.space = space; self.length = length
        self.width = width or FRAME_W
    def wrap(self, aw, ah):
        return aw, self.space * 2 + self.w
    def draw(self):
        c = self.canv; c.setStrokeColor(self.color); c.setLineWidth(self.w); c.setLineCap(1)
        L = self.length or self.width
        c.line(self.w, self.space, L, self.space)


class Heading(Flowable):
    """Section heading: small number + title + short rounded rule."""
    def __init__(self, num, title, level=1):
        super().__init__(); self.num = num; self.title = title; self.level = level
    def wrap(self, aw, ah):
        self.aw = aw
        return aw, (38 if self.level == 1 else 22)
    def draw(self):
        c = self.canv
        if self.level == 1:
            c.setFillColor(MUTED); c.setFont("InSB", 7.5); c.drawString(0, 28, caps(self.num) if self.num else "")
            c.setFillColor(WHITE); c.setFont("InSB", 16.5); c.drawString(0, 9, self.title)
            c.setStrokeColor(WHITE); c.setLineWidth(1.3); c.setLineCap(1); c.line(0.8, 0, 26, 0)
        else:
            c.setFillColor(WHITE); c.setFont("InM", 11.5)
            x = 0
            if self.num:
                c.setFillColor(MUTED); c.drawString(0, 6, self.num); x = c.stringWidth(self.num + "  ", "InM", 11.5)
                c.setFillColor(WHITE)
            c.drawString(x, 6, self.title)


def h1(text):
    """'5. DJ-style crossfading' -> numbered heading."""
    num, _, title = text.partition(". ")
    if title and num.replace(".", "").isdigit():
        return [CondPageBreak(60 * mm), Spacer(1, 8), Heading(f"Section {num}", title, 1), Spacer(1, 8)]
    return [CondPageBreak(60 * mm), Spacer(1, 8), Heading("", text, 1), Spacer(1, 8)]


def h2(text):
    num, _, title = text.partition(" ")
    if num.replace(".", "").isdigit() and "." in num:
        return [CondPageBreak(35 * mm), Spacer(1, 4), Heading(num, title, 2), Spacer(1, 2)]
    return [CondPageBreak(35 * mm), Spacer(1, 4), Heading("", text, 2), Spacer(1, 2)]


def table(rows, widths=None, head=None, first_bold=True, status_col=None):
    ncol = len(rows[0]) if rows else len(head)
    if widths is None:
        if ncol == 2: widths = (44 * mm, FRAME_W - 44 * mm)
        elif ncol == 3: widths = (42 * mm, FRAME_W - 42 * mm - 24 * mm, 24 * mm)
        else: widths = [FRAME_W / ncol] * ncol
    else:
        tot = sum(widths); widths = [w * FRAME_W / tot for w in widths]
    if status_col is None and ncol == 3 and all(r[-1] in STATUS for r in rows):
        status_col = 2
    data = []
    if head:
        data.append([Paragraph(caps(h) if (len(h) < 14 and widths[i] > 22 * mm) else h.upper(), HD) for i, h in enumerate(head)])
    for r in rows:
        row = []
        for i, x in enumerate(r):
            if i == status_col: row.append(status(x))
            elif i == 0 and first_bold: row.append(Paragraph(x, CB))
            else: row.append(Paragraph(x, C))
        data.append(row)
    t = Table(data, colWidths=widths, repeatRows=1 if head else 0)
    st = [("VALIGN", (0, 0), (-1, -1), "TOP"),
          ("BACKGROUND", (0, 0), (-1, -1), PANEL),
          ("ROUNDEDCORNERS", [6, 6, 6, 6]),
          ("BOX", (0, 0), (-1, -1), 0.6, LINE),
          ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
          ("LEFTPADDING", (0, 0), (-1, -1), 8), ("RIGHTPADDING", (0, 0), (-1, -1), 8)]
    start = 1 if head else 0
    if head:
        st += [("LINEBELOW", (0, 0), (-1, 0), 0.9, WHITE), ("TOPPADDING", (0, 0), (-1, 0), 7)]
    for i in range(start + 1, len(data)):
        st.append(("LINEABOVE", (0, i), (-1, i), 0.4, LINE2))
    t.setStyle(TableStyle(st))
    return t


def callout(text, label=None):
    """Rounded panel with a white left accent line."""
    inner = [Paragraph(f"<font name='InSB' size='7' color='#8E8E93'>{caps(label)}</font>", LBL)] if label else []
    inner.append(Paragraph(text, B))
    t = Table([[inner]], colWidths=[FRAME_W])
    t.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, -1), PANEL2), ("ROUNDEDCORNERS", [6, 6, 6, 6]),
                           ("BOX", (0, 0), (-1, -1), 0.6, LINE), ("LINEBEFORE", (0, 0), (0, -1), 2, WHITE),
                           ("LEFTPADDING", (0, 0), (-1, -1), 12), ("RIGHTPADDING", (0, 0), (-1, -1), 12),
                           ("TOPPADDING", (0, 0), (-1, -1), 8), ("BOTTOMPADDING", (0, 0), (-1, -1), 9)]))
    return t


def _bg(c):
    c.saveState(); c.setFillColor(BG); c.rect(0, 0, PAGE_W, PAGE_H, stroke=0, fill=1); c.restoreState()


def build(path, doc_title, subtitle, version_line, story, short_title=None):
    short_title = short_title or doc_title

    def cover(c, d):
        _bg(c); c.saveState()
        s = 112 * mm
        c.drawImage(LOGO, (PAGE_W - s) / 2, PAGE_H - 40 * mm - s, s, s, mask="auto")
        y = PAGE_H - 40 * mm - s - 22 * mm
        c.setFillColor(MUTED); c.setFont("InSB", 8.5)
        c.drawCentredString(PAGE_W / 2, y + 16 * mm, caps("AudioTastic Desktop"))
        c.setFillColor(WHITE); c.setFont("InL", 30); c.drawCentredString(PAGE_W / 2, y, doc_title)
        c.setStrokeColor(WHITE); c.setLineWidth(1.4); c.setLineCap(1)
        c.line(PAGE_W / 2 - 14 * mm, y - 9 * mm, PAGE_W / 2 + 14 * mm, y - 9 * mm)
        c.setFillColor(TEXT); c.setFont("InL", 11); c.drawCentredString(PAGE_W / 2, y - 19 * mm, subtitle)
        c.setFillColor(MUTED); c.setFont("In", 8.5); c.drawCentredString(PAGE_W / 2, y - 26 * mm, version_line)
        c.setFillColor(DIM); c.setFont("InSB", 7); c.drawCentredString(PAGE_W / 2, 16 * mm, caps("Winamp-style desktop audio player  ·  .NET  ·  Windows · macOS · Linux"))
        c.restoreState()

    def page(c, d):
        _bg(c); c.saveState()
        c.drawImage(LOGO_SMALL, LM, PAGE_H - 15.5 * mm, 7 * mm, 7 * mm, mask="auto")
        c.setFillColor(MUTED); c.setFont("InSB", 7)
        c.drawString(LM + 9.5 * mm, PAGE_H - 12.8 * mm, caps("AudioTastic"))
        c.setFont("In", 7.5); c.drawRightString(PAGE_W - RM, PAGE_H - 12.8 * mm, short_title)
        c.setStrokeColor(LINE); c.setLineWidth(0.5); c.line(LM, PAGE_H - 18 * mm, PAGE_W - RM, PAGE_H - 18 * mm)
        c.setFillColor(WHITE); c.setFont("InM", 8); c.drawRightString(PAGE_W - RM, 11 * mm, f"{d.page - 1:02d}")
        c.setStrokeColor(WHITE); c.setLineWidth(1); c.setLineCap(1)
        c.line(PAGE_W - RM - 14 * mm, 12.2 * mm, PAGE_W - RM - 9 * mm, 12.2 * mm)
        c.setFillColor(DIM); c.setFont("In", 7); c.drawString(LM, 11 * mm, version_line)
        c.restoreState()

    path = os.path.join(OUT_DIR, path)
    doc = BaseDocTemplate(path, pagesize=A4, leftMargin=LM, rightMargin=RM, topMargin=TM, bottomMargin=BM,
                          title=f"AudioTastic Desktop - {doc_title}", author="Anders")
    fr = Frame(LM, BM, FRAME_W, PAGE_H - TM - BM, id="f", leftPadding=0, rightPadding=0, topPadding=0, bottomPadding=0)
    doc.addPageTemplates([PageTemplate("cover", [Frame(0, 0, PAGE_W, PAGE_H, id="c")], onPage=cover),
                          PageTemplate("page", [fr], onPage=page)])
    doc.build([NextPageTemplate("page"), PageBreak()] + story)
