"""Writes a small two-page PDF with ReportLab's canvas API: the standard 14 fonts (not embedded), vector paths, an
axial shading, an inline image, an outline and a link. Run by generate.sh; the output path is the first argument."""
import sys
from reportlab.lib.colors import Color, HexColor
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas

out = sys.argv[1]
c = canvas.Canvas(out, pagesize=A4, invariant=1, pageCompression=1)
c.setTitle("ReportLab fixture")
c.setAuthor("PdfPecker tests")
w, h = A4

c.bookmarkPage("p1")
c.addOutlineEntry("First page", "p1", level=0)
c.setFont("Helvetica-Bold", 20)
c.drawString(50, h - 70, "ReportLab canvas fixture")
c.setFont("Times-Roman", 12)
y = h - 100
for line in ["Standard 14 fonts are referenced by name and not embedded, so the",
             "renderer has to supply metrics and glyphs itself: Helvetica, Times,",
             "Courier, Symbol and ZapfDingbats. WinAnsi characters: æøå ÆØÅ € é."]:
    c.drawString(50, y, line)
    y -= 16
c.setFont("Courier", 11)
c.drawString(50, y - 6, "Courier: 0123456789 {}[]()")
c.setFont("Symbol", 12)
c.drawString(50, y - 24, "\u03b1\u03b2\u03b3\u03b4\u03c0 \u2211\u221e")
c.setFont("ZapfDingbats", 12)
c.drawString(150, y - 24, "\u2713\u2714\u2715\u2716 \u261b")

c.saveState()
clip = c.beginPath()
clip.roundRect(50, 520, 250, 80, 12)
c.clipPath(clip, stroke=0, fill=0)
c.linearGradient(50, 520, 300, 520, (HexColor("#1a4c8b"), HexColor("#4fb3d9")), extend=False)
c.restoreState()
c.setStrokeColor(HexColor("#c03030"))
c.setLineWidth(3)
c.setDash(6, 3)
c.bezier(50, 420, 120, 500, 200, 340, 300, 440)
c.setDash()
c.setFillColor(Color(0.9, 0.6, 0.1, alpha=0.6))
c.circle(400, 450, 50, stroke=0, fill=1)
c.setFillColor(HexColor("#2a8040"))
c.roundRect(350, 330, 150, 60, 10, stroke=1, fill=1)

# A 4 x 4 checkerboard written as an inline image.
from reportlab.lib.utils import ImageReader
try:
    from PIL import Image
    img = Image.new("RGB", (4, 4))
    for i in range(16):
        img.putpixel((i % 4, i // 4), (200, 40, 40) if (i % 4 + i // 4) % 2 else (240, 240, 240))
    c.drawInlineImage(img, 50, 250, 80, 80)
except ImportError:
    pass

c.setFillColor(HexColor("#1a4c8b"))
c.setFont("Helvetica", 12)
c.drawString(50, 200, "Go to page two")
c.linkRect("", "p2", (50, 195, 160, 212), relative=0)
c.showPage()

c.bookmarkPage("p2")
c.addOutlineEntry("Second page", "p2", level=0)
c.setFont("Helvetica-Oblique", 16)
c.drawString(50, h - 70, "Second page")
c.saveState()
c.translate(300, 500)
c.rotate(30)
c.setFont("Helvetica-Bold", 28)
c.setFillColor(HexColor("#888888"))
c.drawCentredString(0, 0, "Rotated text")
c.restoreState()
c.showPage()
c.save()
