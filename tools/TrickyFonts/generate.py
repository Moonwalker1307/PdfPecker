# Regenerates the two synthetic "tricky" TrueType fonts in tests/PdfPecker.Tests/Fixtures/fonts.
#
# Some older CJK fonts (MingLiU, PMingLiU, DFKai-SB) build their characters from shared strokes that their composite
# glyphs place at meaningless offsets, and only the glyph programs move the strokes to where they belong. The real
# fonts cannot be checked in, so these stand in for them. The family name is MingLiU so that the name check finds them.
#
# Usage (from the repository root): pip install fonttools, then
#   python3 tools/TrickyFonts/generate.py
#
# Tricky.ttf: "A" is a composite of the 100 by 100 square "stroke" placed at 500, 500, and its glyph program calls
# function 0 of the font program, which moves the four points 500 units left and 500 down, so the square lands on the
# origin as in the real fonts. "B" places the stroke by point matching: its second component's point 0 lands on point 2
# of the first, so the second square starts at the first square's top right corner.
#
# TrickyHostile.ttf is for HostileInputTests: "A" jumps back to itself forever, "B" calls a function that calls itself,
# and "C" loops over IUP of a glyph with 2,000 points 30,000 times. All three must stop within the interpreter's budget.
import array
import os
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib.tables import ttProgram
from fontTools.ttLib.tables._g_l_y_f import GlyphComponent, Glyph

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
fonts = os.path.join(root, "tests", "PdfPecker.Tests", "Fixtures", "fonts")

def program(assembly):
    p = ttProgram.Program()
    p.fromAssembly(assembly)
    return p

def square(size=100):
    pen = TTGlyphPen(None)
    pen.moveTo((0, 0)); pen.lineTo((0, size)); pen.lineTo((size, size)); pen.lineTo((size, 0)); pen.closePath()
    g = pen.glyph()
    g.program = program([])
    return g

def composite(parts, assembly):
    g = Glyph()
    g.numberOfContours = -1
    g.components = []
    for name, x, y, matching in parts:
        c = GlyphComponent()
        c.glyphName = name
        c.flags = 0
        if matching:
            c.firstPt, c.secondPt = x, y
        else:
            c.x, c.y = x, y
        g.components.append(c)
    g.program = program(assembly)
    return g

def many_points(count):
    pen = TTGlyphPen(None)
    pen.moveTo((0, 0))
    for i in range(1, count):
        pen.lineTo((i % 2 * 10 + i // 2, (i // 2) % 50))
    pen.closePath()
    g = pen.glyph()
    g.program = program([])
    return g

def build(name, glyphs, cmap, fpgm, prep=None):
    order = [".notdef"] + [n for n in glyphs]
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder(order)
    fb.setupCharacterMap(cmap)
    empty = TTGlyphPen(None).glyph()
    empty.program = program([])
    all_glyphs = {".notdef": empty, **glyphs}
    fb.setupGlyf(all_glyphs)
    glyf = fb.font["glyf"]
    for n in order:
        glyf[n].recalcBounds(glyf)
    # The left side bearings agree with the bounding boxes, as in a well-made font, so the origin stays at zero.
    fb.setupHorizontalMetrics({n: (1000, getattr(glyf[n], "xMin", 0)) for n in order})
    fb.setupHorizontalHeader(ascent=800, descent=-200)
    fb.setupNameTable({"familyName": "MingLiU", "styleName": "Regular"})
    fb.setupOS2()
    fb.setupPost()
    fb.font["fpgm"] = ttProgram_table("fpgm", fpgm)
    fb.font["prep"] = ttProgram_table("prep", prep or [])
    from fontTools.ttLib import newTable
    cvt = newTable("cvt ")
    cvt.values = array.array("h", [0])
    fb.font["cvt "] = cvt
    maxp = fb.font["maxp"]
    maxp.maxStackElements, maxp.maxStorage, maxp.maxFunctionDefs, maxp.maxTwilightPoints, maxp.maxZones = 64, 8, 4, 4, 2
    fb.font["head"].created = fb.font["head"].modified = 0
    fb.font.recalcTimestamp = False
    fb.font.save(os.path.join(fonts, name))

def ttProgram_table(tag, assembly):
    from fontTools.ttLib import newTable
    t = newTable(tag)
    t.program = program(assembly)
    return t

# Function 0 moves points 0 to 3 by -500 on both axes.
shift = ["PUSHB[ ] 0", "FDEF[ ]",
         "SVTCA[1]", "PUSHB[ ] 0 1 2 3 4", "SLOOP[ ]", "PUSHW[ ] -32000", "SHPIX[ ]",
         "SVTCA[0]", "PUSHB[ ] 0 1 2 3 4", "SLOOP[ ]", "PUSHW[ ] -32000", "SHPIX[ ]",
         "ENDF[ ]"]
build("Tricky.ttf",
      {"stroke": square(),
       "A": composite([("stroke", 500, 500, False)], ["PUSHB[ ] 0", "CALL[ ]"]),
       "B": composite([("stroke", 0, 0, False), ("stroke", 2, 0, True)], [])},
      {0x41: "A", 0x42: "B"}, shift)

recursion = ["PUSHB[ ] 0", "FDEF[ ]", "PUSHB[ ] 0", "CALL[ ]", "ENDF[ ]",
             "PUSHB[ ] 1", "FDEF[ ]", "SVTCA[1]", "IUP[1]", "ENDF[ ]"]
loop = composite([("stroke", 0, 0, False)], ["PUSHW[ ] -3", "JMPR[ ]"])
build("TrickyHostile.ttf",
      {"stroke": square(),
       "many": many_points(2000),
       "A": loop,
       "B": composite([("stroke", 0, 0, False)], ["PUSHB[ ] 0", "CALL[ ]"]),
       "C": composite([("many", 0, 0, False)], ["PUSHW[ ] 30000", "PUSHB[ ] 1", "LOOPCALL[ ]"])},
      {0x41: "A", 0x42: "B", 0x43: "C"}, recursion)
