"""AudioTastic User Manual as a PDF, generated from docs/manual/README.md (the Markdown is the source).

Run: python docs/pdf-src/manual.py  (needs reportlab and Pillow; writes AudioTastic_User_Manual.pdf to the repo root,
or to AUDIOTASTIC_PDF_OUT). The Windows installer ships the PDF and adds a Start menu entry for it.

Supported Markdown (keep the manual to it): # title, ## sections (each starts a page), ### subsections, paragraphs,
- bullets, 1. numbered lists, | tables | (cells may hold an <img>), ``` code blocks, and <img src width alt> lines;
inline **bold**, *italic*, `code` and [links](...). Relative links point to the repository on GitHub.
"""
import os
import re
import sys
import tempfile
import xml.sax.saxutils as sax

from PIL import Image as PILImage
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.platypus import (BaseDocTemplate, CondPageBreak, Frame, Image, KeepTogether, ListFlowable, ListItem,
                                NextPageTemplate, PageBreak, PageTemplate, Paragraph, Preformatted, Spacer, Table,
                                TableStyle)
from reportlab.platypus.tableofcontents import TableOfContents

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import theme as T  # noqa: E402  (registers the Inter fonts; shared paths)

MANUAL = os.path.join(T.REPO, "docs", "manual")
SOURCE = os.path.join(MANUAL, "README.md")
OUT = os.path.join(T.OUT_DIR, "AudioTastic_User_Manual.pdf")
GITHUB = "https://github.com/castenskiold/AudioTasticDesktop/blob/main/"
LOGO_DARK = os.path.join(T.REPO, "assets", "branding", "audiotastic-logo-original.png")  # white on black

PAGE_W, PAGE_H = A4
LM = RM = 18 * mm
TM, BM = 22 * mm, 18 * mm
FRAME_W = PAGE_W - LM - RM
FRAME_H = PAGE_H - TM - BM
PX = 0.75  # one CSS pixel (the screenshots' 1x size) in points

INK = colors.HexColor("#1B1B1E")
BODY = colors.HexColor("#2C2C30")
MUTED = colors.HexColor("#6E6E74")
RULE = colors.HexColor("#D9D9DE")
TINT = colors.HexColor("#F3F3F5")
LINK = "#2457A6"
DARK = colors.HexColor("#0B0B0C")

P = ParagraphStyle("P", fontName="In", fontSize=10, leading=15, textColor=BODY, spaceAfter=6)
H1 = ParagraphStyle("H1", fontName="InSB", fontSize=22, leading=28, textColor=INK, spaceAfter=10)
H2 = ParagraphStyle("H2", fontName="InSB", fontSize=13.5, leading=18, textColor=INK, spaceBefore=10, spaceAfter=5)
CELL = ParagraphStyle("Cell", parent=P, fontSize=9, leading=12.5, spaceAfter=0)
CELL_HEAD = ParagraphStyle("CellHead", parent=CELL, fontName="InSB", textColor=INK)
CAPTION = ParagraphStyle("Caption", parent=P, fontName="InI", fontSize=8.5, leading=11, textColor=MUTED, alignment=TA_CENTER,
                         spaceBefore=3, spaceAfter=10)
CODE = ParagraphStyle("Code", fontName="Courier", fontSize=8.6, leading=11.5, textColor=INK)
TOC1 = ParagraphStyle("TOC1", parent=P, fontSize=10.5, leading=17, leftIndent=0, spaceAfter=0)


def slug(text):
    """GitHub's heading anchor: lower case, punctuation dropped, spaces to hyphens."""
    text = re.sub(r"[^\w\- ]", "", text.lower(), flags=re.UNICODE)
    return text.replace(" ", "-")


def link_target(url):
    if url.startswith(("http://", "https://", "#")):
        return url
    path = os.path.normpath(os.path.join("docs", "manual", url)).replace(os.sep, "/")
    return GITHUB + path


FACE = pdfmetrics.getFont("In").face
MISSING = set()


def glyphs(text):
    """Characters Inter lacks are replaced (and reported once)."""
    out = []
    for ch in text:
        if ord(ch) > 127 and ord(ch) not in FACE.charToGlyph:
            MISSING.add(ch)
            out.append({"⏏": "Open", "○": "o", "▸": "›"}.get(ch, "?"))
        else:
            out.append(ch)
    return "".join(out)


def inline(text):
    """Markdown inline markup to ReportLab paragraph markup."""
    codes = []

    def keep_code(m):
        codes.append(m.group(1))
        return f"\x00{len(codes) - 1}\x00"

    text = re.sub(r"`([^`]+)`", keep_code, text)
    links = []

    def keep_link(m):
        links.append((m.group(1), m.group(2)))
        return f"\x01{len(links) - 1}\x01"

    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", keep_link, text)
    text = sax.escape(text)
    text = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", text)
    text = re.sub(r"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", r"<i>\1</i>", text)
    text = re.sub(r"\x01(\d+)\x01", lambda m: f'<link href="{sax.escape(link_target(links[int(m.group(1))][1]))}" color="{LINK}">'
                  f"{inline(links[int(m.group(1))][0])}</link>", text)
    text = re.sub(r"\x00(\d+)\x00", lambda m: f'<font name="InM" color="#3A3A40">{sax.escape(codes[int(m.group(1))])}</font>', text)
    return glyphs(text)


IMG = re.compile(r'<img\s+src="([^"]+)"\s+width="(\d+)"(?:\s+alt="([^"]*)")?\s*/?>')
_slices = tempfile.mkdtemp(prefix="manual-slices-")


def image(src, width_px, max_w=FRAME_W, max_h=FRAME_H - 30):
    """A screenshot at its 1x size (narrower when the page is), split into slices when taller than a page."""
    path = os.path.join(MANUAL, src)
    with PILImage.open(path) as im:
        w, h = im.size
        width = min(int(width_px) * PX, max_w)
        scale = width / w
        height = h * scale
        if height <= max_h:
            return [Image(path, width=width, height=height)]
        if max_h / height >= 0.6:  # still legible a little smaller: keep it whole on one page
            return [Image(path, width=width * max_h / height, height=max_h)]
        parts, top, n = [], 0, 0
        step = int(max_h / scale)
        rgb = im.convert("RGB")
        while top < h:
            bottom = min(h, top + step)
            if bottom < h:  # cut on a blank band of the background, never through a line of text
                for y in range(bottom, max(top + step // 2, bottom - step // 4), -1):
                    lo, hi = zip(*rgb.crop((0, y - 1, w, y + 1)).getextrema())
                    if lo == hi:
                        bottom = y
                        break
            piece = os.path.join(_slices, f"{os.path.basename(src)}-{n}.png")
            im.crop((0, top, w, bottom)).save(piece)
            parts.append(Image(piece, width=width, height=(bottom - top) * scale))
            top, n = bottom, n + 1
        return parts


def table(lines):
    rows = [[c.strip() for c in line.strip().strip("|").split("|")] for line in lines]
    head, body = rows[0], rows[2:]
    columns = len(head)
    # Column widths from the text length (images count as their width), at least 16 % each.
    weights = []
    for i in range(columns):
        longest = 0
        for row in [head] + body:
            cell = row[i] if i < len(row) else ""
            m = IMG.search(cell)
            longest = max(longest, int(m.group(2)) / 6 if m else len(re.sub(r"[*`]", "", cell)))
        weights.append(max(longest, 1))
    total = sum(weights)
    widths = [max(0.16, w / total) for w in weights]
    widths = [FRAME_W * w / sum(widths) for w in widths]

    def cell(text, style, width):
        m = IMG.search(text)
        if m:
            return image(m.group(1), m.group(2), max_w=width - 10)[0]
        return Paragraph(inline(text), style)

    data = [[cell(h, CELL_HEAD, widths[i]) for i, h in enumerate(head)]]
    data += [[cell(row[i] if i < len(row) else "", CELL, widths[i]) for i in range(columns)] for row in body]
    t = Table(data, colWidths=widths, repeatRows=1, hAlign="LEFT")
    t.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), TINT),
        ("LINEBELOW", (0, 0), (-1, 0), 0.8, RULE),
        ("LINEBELOW", (0, 1), (-1, -1), 0.4, RULE),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("TOPPADDING", (0, 0), (-1, -1), 5),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
        ("LEFTPADDING", (0, 0), (-1, -1), 6),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6),
    ]))
    return [t, Spacer(1, 8)]


def bullets(items, numbered):
    flow = [ListItem(Paragraph(inline(i), P), leftIndent=14, value=n + 1 if numbered else None) for n, i in enumerate(items)]
    return [ListFlowable(flow, bulletType="1" if numbered else "bullet", start="1" if numbered else "•",
                         bulletFontName="In", bulletFontSize=9 if numbered else 8, bulletColor=MUTED, leftIndent=14,
                         bulletOffsetY=0 if numbered else -1, spaceAfter=6)]


class Manual(BaseDocTemplate):
    def __init__(self, path, version):
        super().__init__(path, pagesize=A4, leftMargin=LM, rightMargin=RM, topMargin=TM, bottomMargin=BM,
                         title="AudioTastic User Manual", author="Big Apps ApS", subject=f"AudioTastic {version}")
        self.version = version
        frame = Frame(LM, BM, FRAME_W, FRAME_H, id="body", leftPadding=0, rightPadding=0, topPadding=0, bottomPadding=0)
        self.addPageTemplates([
            PageTemplate(id="cover", frames=[frame], onPage=self.cover),
            PageTemplate(id="body", frames=[frame], onPage=self.page),
        ])
        self.section = ""
        self.titles = {}  # page -> the first section starting on it (kept from the previous pass of multiBuild)

    def afterFlowable(self, flowable):
        if isinstance(flowable, Paragraph) and flowable.style.name in ("H1", "H2"):
            text = flowable.getPlainText()
            key = getattr(flowable, "_anchor", slug(text))
            level = 0 if flowable.style.name == "H1" else 1
            if level == 0:
                self.section = text
                self.titles.setdefault(self.page, text)
                self.notify("TOCEntry", (0, text, self.page, key))
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(text, key, level=level, closed=level == 0)

    def cover(self, c, doc):
        c.saveState()
        c.setFillColor(DARK)
        c.rect(0, 0, PAGE_W, PAGE_H, stroke=0, fill=1)
        s = 62 * mm
        c.drawImage(T.LOGO, (PAGE_W - s) / 2, PAGE_H - 38 * mm - s, s, s, mask="auto")
        c.setFillColor(colors.HexColor("#F2F2F2"))
        c.setFont("InSB", 34)
        c.drawCentredString(PAGE_W / 2, PAGE_H - 38 * mm - s - 22 * mm, "AudioTastic")
        c.setFont("InL", 17)
        c.drawCentredString(PAGE_W / 2, PAGE_H - 38 * mm - s - 33 * mm, "User Manual")
        shot = os.path.join(MANUAL, "images", "01-player.png")
        with PILImage.open(shot) as im:
            w, h = im.size
        width = 118 * mm
        c.drawImage(shot, (PAGE_W - width) / 2, 34 * mm, width, width * h / w, mask="auto")
        c.setFillColor(colors.HexColor("#8E8E93"))
        c.setFont("In", 9)
        c.drawCentredString(PAGE_W / 2, 18 * mm, f"Version {self.version}  ·  github.com/castenskiold/AudioTasticDesktop")
        c.restoreState()

    def page(self, c, doc):
        c.saveState()
        c.setFillColor(DARK)
        c.roundRect(LM, PAGE_H - 14 * mm, 7 * mm, 7 * mm, 1.5 * mm, stroke=0, fill=1)
        c.drawImage(LOGO_DARK, LM + 0.6 * mm, PAGE_H - 13.4 * mm, 5.8 * mm, 5.8 * mm, mask="auto")
        c.setFont("InM", 8.5)
        c.setFillColor(INK)
        c.drawString(LM + 10 * mm, PAGE_H - 11.6 * mm, "AudioTastic User Manual")
        c.setFont("In", 8.5)
        c.setFillColor(MUTED)
        # This page's section: the first one starting on it, else the one running on from the pages before.
        running = ""
        for page in sorted(p for p in self.titles if p <= doc.page):
            running = self.titles[page]
        c.drawRightString(PAGE_W - RM, PAGE_H - 11.6 * mm, glyphs(running))
        c.setStrokeColor(RULE)
        c.setLineWidth(0.5)
        c.line(LM, PAGE_H - 16 * mm, PAGE_W - RM, PAGE_H - 16 * mm)
        c.line(LM, 12 * mm, PAGE_W - RM, 12 * mm)
        c.setFillColor(MUTED)
        c.drawString(LM, 8 * mm, f"Version {self.version}")
        c.drawRightString(PAGE_W - RM, 8 * mm, str(doc.page - 1))
        c.restoreState()


def heading(text, style):
    para = Paragraph(f'<a name="{slug(text)}"/>{inline(text)}', style)
    para._anchor = slug(text)
    return para


def story_from(markdown):
    lines = markdown.splitlines()
    story = [NextPageTemplate("body"), PageBreak(), heading("Introduction", H1)]
    i, subsection = 0, None
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        if not stripped or stripped.startswith("# "):
            i += 1
            continue
        if stripped.startswith("## "):
            title = stripped[3:]
            if title == "Contents":  # replaced by the generated table of contents with page numbers
                toc = TableOfContents()
                toc.levelStyles = [TOC1]
                toc.dotsMinLevel = 0
                story += [PageBreak(), Paragraph("Contents", H2), toc]
                i += 1
                while i < len(lines) and (not lines[i].strip() or re.match(r"\d+\. ", lines[i].strip())):
                    i += 1
                continue
            story += [PageBreak(), heading(title, H1)]
            subsection = None
            i += 1
            continue
        if stripped.startswith("### "):
            story += [CondPageBreak(60 * mm), heading(stripped[4:], H2)]
            subsection = len(story) - 1  # a picture right after its heading and text stays with them
            i += 1
            continue
        if stripped.startswith("```"):
            block = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                block.append(lines[i].replace("…", "..."))
                i += 1
            i += 1
            code = Table([[Preformatted("\n".join(block), CODE)]], colWidths=[FRAME_W])
            code.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, -1), TINT), ("TOPPADDING", (0, 0), (-1, -1), 7),
                                      ("BOTTOMPADDING", (0, 0), (-1, -1), 7), ("LEFTPADDING", (0, 0), (-1, -1), 8)]))
            story += [code, Spacer(1, 8)]
            continue
        if stripped.startswith("|"):
            block = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                block.append(lines[i])
                i += 1
            story += table(block)
            continue
        m = IMG.fullmatch(stripped)
        if m:
            grouped = subsection is not None and all(isinstance(f, Paragraph) for f in story[subsection:])
            # With its heading and text above it, a picture leaves them room on the page.
            parts = image(m.group(1), m.group(2), max_h=FRAME_H - (120 if grouped else 30))
            caption = [Paragraph(inline(m.group(3)), CAPTION)] if m.group(3) else [Spacer(1, 8)]
            if len(parts) == 1:
                group = [Spacer(1, 4), parts[0]] + caption
                if grouped:
                    group = story[subsection:] + group
                    del story[subsection:]
                story.append(KeepTogether(group))
            else:
                story += [Spacer(1, 4)] + parts + caption
            i += 1
            continue
        if re.match(r"(- |\d+\. )", stripped):
            numbered = bool(re.match(r"\d+\. ", stripped))
            items = []
            while i < len(lines) and re.match(r"(- |\d+\. )", lines[i].strip()):
                items.append(re.sub(r"^(- |\d+\. )", "", lines[i].strip()))
                i += 1
            story += bullets(items, numbered)
            continue
        story.append(Paragraph(inline(stripped), P))
        i += 1
    return story


def version():
    props = open(os.path.join(T.REPO, "Directory.Build.props"), encoding="utf-8").read()
    return re.search(r"<Version>([^<]+)</Version>", props).group(1)


def main():
    with open(SOURCE, encoding="utf-8") as f:
        markdown = f.read()
    doc = Manual(OUT, version())
    doc.multiBuild(story_from(markdown))
    if MISSING:
        print("Characters missing from Inter (replaced):", " ".join(sorted(MISSING)))
    print(f"built {OUT}")


if __name__ == "__main__":
    main()
