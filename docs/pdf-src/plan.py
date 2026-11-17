import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import theme as T
from theme import *
from reportlab.lib.units import mm
from reportlab.platypus import Flowable
import spec_data as D
import plan_data as PD

NAMES = {f[0]: f[1] for _, _, fs in D.SECTIONS for f in fs}
SIZE_TXT = {"S": "Small", "M": "Medium", "L": "Large", "XL": "Extra large"}
story = []
P = lambda t, s=B: Paragraph(t, s)

def bullets(items):
    return [Paragraph(f"<font color='#F2F2F2'>—</font>&nbsp;&nbsp;{i}", ParagraphStyle("bl", parent=B, leftIndent=12, firstLineIndent=-12, spaceAfter=3)) for i in items]

class Roadmap(Flowable):
    """Phase sequence drawn as rounded nodes on a line, in two rows."""
    def wrap(self, aw, ah):
        self.aw = aw; return aw, 150
    def draw(self):
        c = self.canv; c.setLineCap(1)
        ph = PD.PHASES; per = 8
        rows = [ph[:per], ph[per:]]
        for r, row in enumerate(rows):
            y = 150 - 30 - r * 70
            step = self.aw / per
            c.setStrokeColor(LINE); c.setLineWidth(1)
            c.line(step / 2, y, step / 2 + step * (len(row) - 1), y)
            for i, p in enumerate(row):
                x = step / 2 + i * step
                key = p["id"] in ("P1", "P8")
                c.setStrokeColor(WHITE); c.setLineWidth(1.1)
                c.setFillColor(WHITE if key else BG)
                c.circle(x, y, 11, stroke=1, fill=1)
                c.setFillColor(BG if key else WHITE); c.setFont("InSB", 7.5); c.drawCentredString(x, y - 2.6, p["id"])
                c.setFillColor(TEXT); c.setFont("InM", 6.3)
                words = p["name"].split(); lines = []; cur = ""
                for w in words:
                    if c.stringWidth((cur + " " + w).strip(), "InM", 6.3) > step - 6:
                        lines.append(cur); cur = w
                    else: cur = (cur + " " + w).strip()
                lines.append(cur)
                for k, ln in enumerate(lines[:3]):
                    c.drawCentredString(x, y - 22 - k * 8, ln)
                c.setFillColor(MUTED); c.setFont("InSB", 5.5); c.drawCentredString(x, y + 16, p["size"])
        c.setFillColor(MUTED); c.setFont("In", 6.3)
        c.drawString(0, 2, "Filled nodes: the two highest-risk phases (audio core and DJ-style crossfading).  Letters above nodes: relative size.")

# ---------------------------------------------------------------- 1
story += T.h1("1. About this plan")
story.append(P("This plan sets the order in which AudioTastic Desktop is built and what each step needs. It is based on the <b>Feature Specification</b> "
               "(feature IDs such as XF-02 and UI codes such as MW7 refer to that document) and stays within the scope of the <b>Required Features</b> document."))
story.append(Spacer(1, 8))
story.append(T.table([
    ("Ordering principles", "Infrastructure first; the risky audio core early; shared building blocks (two-deck mixer, skin engine, plugin SDK, analysis queue) before the features that use them; native C++ work late and isolated; every phase leaves a working, releasable app."),
    ("Phases", f"{len(PD.PHASES)} phases, P0–P{len(PD.PHASES)-1}. Each phase lists its features, extra tasks, prerequisites, deliverables, exit criteria and risks. A phase is closed only when its exit criteria pass on Windows, macOS and Linux."),
    ("Split features", "A few features are built in two parts in different phases (e.g. PB-04 shared mode in P1, exclusive mode and ASIO in P6). The part is named in the task text; appendix A shows every feature's phase."),
    ("Sizes", "Relative effort only: S (small), M (medium), L (large), XL (extra large). No calendar dates are set; the order matters, not the duration."),
    ("Flexibility", "Phases P9–P13 depend only loosely on each other and can be reordered if priorities change; P0–P8 should be built in order."),
], head=("Topic", "Details")))

# ---------------------------------------------------------------- 2
story += T.h1("2. Roadmap")
story.append(Roadmap())
story.append(Spacer(1, 10))
story.append(T.table([(p["id"], p["name"], p["goal"], p["size"]) for p in PD.PHASES],
                     head=("Phase", "Name", "Goal", "Size"), widths=(15, 34, 106, 15), status_col=-1))

# ---------------------------------------------------------------- 3
story += T.h1("3. External prerequisites")
story.append(P("Accounts, licences, hardware and files to arrange outside the code, with the phase that first needs them. Start the slow ones (Apple Developer enrolment, ASIO licence) a phase early."))
story.append(Spacer(1, 6))
ext = []
for p in PD.PHASES:
    for e in p["prereq_ext"]:
        ext.append((p["id"], e))
story.append(T.table(ext, head=("Phase", "Prerequisite"), widths=(14, 156)))

# ---------------------------------------------------------------- 4
story += T.h1("4. Testing strategy")
story.append(T.table(PD.TESTING, head=("Type", "Approach")))
story += T.h2("4.1 Definition of done (every feature)")
story += bullets(PD.DOD)

# ---------------------------------------------------------------- phases
LW = 30 * mm
def phase_block(p, n):
    out = [PageBreak()]
    out += T.h1(f"{n}. Phase {p['id']} — {p['name']}")
    out.append(Paragraph(f"<font name='InSB' size='7' color='#8E8E93'>SIZE</font>&nbsp;&nbsp;<font name='InM' color='#F2F2F2'>{SIZE_TXT[p['size']]}</font>"
                         f"&nbsp;&nbsp;&nbsp;&nbsp;<font name='InSB' size='7' color='#8E8E93'>NEEDS</font>&nbsp;&nbsp;<font name='InM' color='#F2F2F2'>{', '.join(p['prereq_int']) or 'nothing'}</font>", B))
    out.append(Spacer(1, 6))
    out.append(T.callout(p["goal"], "Goal"))
    out.append(Spacer(1, 6))
    out.append(Paragraph(f"<font name='InSB' size='7' color='#8E8E93'>WHY NOW</font>&nbsp;&nbsp;{p['why']}", B))
    if p["features"]:
        out += T.h2("Features")
        out.append(T.table([(fid, NAMES[fid], task) for fid, task in p["features"]],
                           head=("ID", "Feature", "What to build in this phase"), widths=(17, 40, 113), status_col=-1))
    if p["tasks"]:
        out += T.h2("Additional tasks")
        out += bullets(p["tasks"])
    rows = []
    rows.append(("Needs (phases)", ", ".join(p["prereq_int"]) or "—"))
    rows.append(("Needs (external)", "<br/>".join(p["prereq_ext"]) or "—"))
    rows.append(("Deliverables", "<br/>".join(p["deliverables"])))
    rows.append(("Exit criteria", "<br/>".join("— " + e for e in p["exit"])))
    rows.append(("Risks", "<br/>".join(p["risks"])))
    out += T.h2("Prerequisites, deliverables and exit")
    out.append(T.table(rows, widths=(32, 138)))
    return out

for n, p in enumerate(PD.PHASES, start=5):
    story += phase_block(p, n)

# ---------------------------------------------------------------- appendix
story.append(PageBreak())
story += T.h1("Appendix A. Feature to phase map")
ph_of = {}
for p in PD.PHASES:
    for fid, task in p["features"]:
        part = "part 1" if task.startswith("Part 1") else "part 2" if task.startswith("Part 2") else ""
        ph_of.setdefault(fid, []).append(p["id"] + (f" ({part})" if part else ""))
allf = [(f[0], f[1]) for _, _, fs in D.SECTIONS for f in fs]
half = (len(allf) + 1) // 2
rows = []
for i in range(half):
    a = allf[i]; b = allf[half + i] if half + i < len(allf) else ("", "")
    rows.append((a[0], a[1], ", ".join(ph_of.get(a[0], [])), b[0], b[1], ", ".join(ph_of.get(b[0], [])) if b[0] else ""))
st_ = ParagraphStyle("sm", parent=C, fontSize=7.6, leading=10)
stb = ParagraphStyle("smb", parent=CB, fontSize=7.6, leading=10)
idx = Table([[Paragraph(x, stb if j in (0, 3) else st_) for j, x in enumerate(r)] for r in rows],
            colWidths=[14 * mm, 44 * mm, 27 * mm, 14 * mm, 44 * mm, 27 * mm])
idx.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, -1), PANEL), ("ROUNDEDCORNERS", [6, 6, 6, 6]), ("BOX", (0, 0), (-1, -1), 0.6, LINE),
                         ("LINEBEFORE", (3, 0), (3, -1), 0.4, LINE), ("VALIGN", (0, 0), (-1, -1), "TOP"),
                         ("TOPPADDING", (0, 0), (-1, -1), 2.5), ("BOTTOMPADDING", (0, 0), (-1, -1), 2.5),
                         ("LEFTPADDING", (0, 0), (-1, -1), 6), ("TEXTCOLOR", (2, 0), (2, -1), MUTED)]
                        + [("LINEABOVE", (0, i), (-1, i), 0.3, LINE2) for i in range(1, len(rows))]))
story.append(idx)

T.build("AudioTastic_Implementation_Plan.pdf", "Implementation Plan",
        "Build order, prerequisites and exit criteria for every feature",
        "Version 1.1  ·  17 November 2026  ·  Based on Feature Specification v1.3", story, short_title="Implementation Plan  ·  v1.1")
print("built")
