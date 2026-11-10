"""Generates the PDF fixtures used by the self-test and UI screenshots. Requires: pip install reportlab"""
from reportlab.pdfgen import canvas
from reportlab.lib.units import inch
from reportlab.graphics.barcode import code128
import os
out = os.path.join(os.path.dirname(__file__), "data")
PT_PER_DOT = 72.0 / 203.0

def shipping(c, w, h, title="SHIP TO"):
    c.setLineWidth(2); c.rect(8, 8, w - 16, h - 16)
    c.setFont("Helvetica-Bold", 22); c.drawString(24, h - 46, "ACME LOGISTICS")
    c.setFont("Helvetica", 12); c.drawString(24, h - 64, "Priority Mail  |  1 of 1  |  2.4 lb")
    c.line(8, h - 76, w - 8, h - 76)
    c.setFont("Helvetica-Bold", 14); c.drawString(24, h - 100, title)
    c.setFont("Helvetica", 18)
    for i, t in enumerate(["JANE SAMPLE", "100 TEST STREET", "ANYTOWN ST 00000"]):
        c.drawString(24, h - 126 - i * 24, t)
    c.line(8, h - 220, w - 8, h - 220)
    bc = code128.Code128("9400111899223456789012", barWidth=0.0145 * inch, barHeight=1.5 * inch, quiet=False)
    bc.drawOn(c, (w - bc.width) / 2, h - 440)
    c.setFont("Helvetica", 11); c.drawCentredString(w / 2, h - 456, "9400 1118 9922 3456 7890 12")
    c.setFont("Helvetica-Bold", 40); c.drawCentredString(w / 2, 30, "P A 1")

# 1. portrait 4x6
c = canvas.Canvas(f"{out}/label_4x6.pdf", pagesize=(4 * 72, 6 * 72)); shipping(c, 4 * 72, 6 * 72); c.save()
# 2. landscape 6x4 (content drawn rotated so it is a real landscape page)
c = canvas.Canvas(f"{out}/label_6x4_landscape.pdf", pagesize=(6 * 72, 4 * 72))
c.setLineWidth(2); c.rect(8, 8, 6 * 72 - 16, 4 * 72 - 16)
c.setFont("Helvetica-Bold", 28); c.drawString(30, 4 * 72 - 60, "LANDSCAPE LABEL")
bc = code128.Code128("LANDSCAPE123", barWidth=0.0145 * inch, barHeight=1.2 * inch, quiet=False); bc.drawOn(c, 30, 70)
c.save()
# 3. three pages
c = canvas.Canvas(f"{out}/multi_3page.pdf", pagesize=(4 * 72, 6 * 72))
for i in range(3):
    shipping(c, 4 * 72, 6 * 72, f"SHIP TO  (page {i + 1})"); c.showPage()
c.save()
# 4. exact-grid bars: row of rectangles on exact 203-dpi dot boundaries, runs (black,white) = 1,3,2,2,4,4,8,8,3,1 dots
c = canvas.Canvas(f"{out}/bars_exact.pdf", pagesize=(4 * 72, 6 * 72)); c.setFillColorRGB(0, 0, 0)
x = 40; H = 6 * 72
runs = [1, 3, 2, 2, 4, 4, 8, 8, 3, 1]
for i, r in enumerate(runs):
    if i % 2 == 0:
        c.rect(x * PT_PER_DOT, H - 300 * PT_PER_DOT, r * PT_PER_DOT, 100 * PT_PER_DOT, stroke=0, fill=1)
    x += r
c.save()
print("fixtures written to", out)
# 5. Amazon-style return printout: letter page, instructions text, label shrunk ~90% inside a dashed cut box,
#    and a second box split across the page break (open top on page 2).
c = canvas.Canvas(f"{out}/fullpage_return.pdf", pagesize=(8.5 * 72, 11 * 72))
c.setFont("Helvetica-Bold", 12); c.drawString(40, 740, "Additional instructions for mailing your package")
c.setFont("Helvetica", 9)
for i in range(8): c.drawString(50, 720 - i * 13, "• Lorem ipsum instruction text that must not be printed on the thermal label, line %d." % i)
c.setDash(4, 3); c.setStrokeGray(0.55); c.setLineWidth(0.8)
bx, by, bw, bh = 90, 180, 5.4 * 72, 3.5 * 72
c.rect(bx, by, bw, bh)
c.setDash(); c.setStrokeGray(0); c.saveState(); c.translate(bx + 20, by + 10); c.rotate(90)
c.setFont("Helvetica-Bold", 16); c.drawString(10, -40, "SHIP TO: TEST RETURNS")
bc = code128.Code128("1ZTEST0000001", barWidth=0.012 * inch, barHeight=0.9 * inch, quiet=False); bc.drawOn(c, 10, -150)
c.restoreState()
c.setDash(4, 3); c.setStrokeGray(0.55); c.line(150, 60, 150, 0); c.line(450, 60, 450, 0)  # box continues to next page
c.showPage()
c.setDash(4, 3); c.setStrokeGray(0.55); c.setLineWidth(0.8)
c.line(150, 792, 150, 520); c.line(450, 792, 450, 520); c.line(150, 520, 450, 520)
c.setDash(); c.setFont("Helvetica-Bold", 30); c.drawString(200, 640, "UN 3481")
c.save()
# 6. eBay/UPS-style: a 4x6 label scaled up to fill a letter page (outer border ~ page size, solid section lines inside)
c = canvas.Canvas(f"{out}/page_filling_label.pdf", pagesize=(8.5 * 72, 11 * 72))
c.setLineWidth(2); c.rect(40, 8, 8.5 * 72 - 80, 11 * 72 - 16)
c.setLineWidth(3); c.line(40, 520, 8.5 * 72 - 40, 520); c.line(40, 380, 8.5 * 72 - 40, 380); c.line(40, 300, 8.5 * 72 - 40, 300); c.line(200, 520, 200, 380)
c.setFont("Helvetica-Bold", 30); c.drawString(60, 560, "SHIP TO: SAMPLE"); c.drawString(220, 470, "ZZ 123 4-56")
bc = code128.Code128("1ZSAMPLE000123", barWidth=0.03 * inch, barHeight=1.4 * inch, quiet=False); bc.drawOn(c, 80, 120)
c.save()
# 7. Not a label: a letter-size text-only invoice (the Downloads watcher must ignore it)
c = canvas.Canvas(f"{out}/not_a_label_invoice.pdf", pagesize=(8.5 * 72, 11 * 72))
c.setFont("Helvetica-Bold", 20); c.drawString(72, 700, "INVOICE #1001")
c.setFont("Helvetica", 11)
for i in range(20): c.drawString(72, 660 - i * 18, f"Line item {i + 1} ............................................ $ {i * 3 + 9}.99")
c.save()
