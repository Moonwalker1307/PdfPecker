# Regenerates two COLR fonts in tests/PdfPecker.Tests/Fixtures/fonts.
#
# ColorGrid.ttf is the font ColorFontTests uses where the real colour fonts are too small: 300 colour glyphs, more than
# one Type 3 font holds, so the output has to switch fonts mid-text.
#
# Usage (from the repository root): pip install fonttools, then
#   python3 tools/ColorFonts/generate.py
#
# U+E000 to U+E12B are squares, 1000 units wide and 800 high from x 100, y 0. The first 150 are red, green and blue in
# turn, from the palette; the other 150 are painted in the text colour (palette index 0xFFFF). "A" is a plain glyph
# without colour, a square of the same size. Its ccmp feature joins emoji-style sequences into one glyph, as colour emoji
# fonts do: U+E000 with the skin tone U+1F3FD is yellow, U+E001 U+200D U+E002 magenta, the flag letters U+1F1E9 U+1F1F0
# cyan and U+1F3F4 with the tags of "gb" and the cancel tag orange. Alone, U+1F3FD, U+1F1E9, U+1F1F0 and U+1F3F4 are
# grey; the joiner and the tags draw nothing. U+E200 is a half transparent blue gradient over the whole square, drawn
# only inside a smaller square (composite mode source in), as the flags of Noto Color Emoji are shaded.
#
# ColorHostile.ttf is for HostileInputTests. U+E000 is a paint graph that doubles at each of 60 levels, so walking it as
# a tree visits 2^60 paints; U+E001 is 300 transforms nested in one another; U+E002 is a glyph whose clip glyph is a
# colour glyph that refers back to it.
import os
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib.tables import otTables as ot

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
fonts = os.path.join(root, "tests", "PdfPecker.Tests", "Fixtures", "fonts")

def square():
    pen = TTGlyphPen(None)
    pen.moveTo((100, 0)); pen.lineTo((100, 800)); pen.lineTo((900, 800)); pen.lineTo((900, 0)); pen.closePath()
    return pen.glyph()

def save(fb, name):
    # Fixed timestamps keep the files the same on every run.
    fb.font["head"].created = fb.font["head"].modified = 0
    fb.font.recalcTimestamp = False
    path = os.path.join(fonts, name)
    fb.font.save(path)
    print(path)

def empty():
    return TTGlyphPen(None).glyph()

colour = [f"c{i:03d}" for i in range(300)]
# The parts of the sequences and what they join into, after the other glyphs so their ids stay as they were.
parts = {0x1F3FD: "tone", 0x1F1E9: "letterD", 0x1F1F0: "letterK", 0x1F3F4: "blackflag", 0x200D: "zwj",
         0xE0067: "tagG", 0xE0062: "tagB", 0xE007F: "cancelTag"}
joined = {"toneE000": 3, "zwjE001E002": 4, "flagDK": 5, "flagGB": 6}
order = [".notdef", "space", "A", "square"] + colour + list(parts.values()) + list(joined) + ["small", "masked"]
fb = FontBuilder(1000, isTTF=True)
fb.setupGlyphOrder(order)
cmap = {0x20: "space", 0x41: "A"}
cmap.update({0xE000 + i: name for i, name in enumerate(colour)})
cmap.update(parts)
cmap[0xE200] = "masked"
fb.setupCharacterMap(cmap)
glyphs = {".notdef": empty(), "space": empty(), "A": square(), "square": square()}
glyphs.update({name: square() for name in colour})
glyphs.update({name: empty() if name in ("zwj", "tagG", "tagB", "cancelTag") else square() for name in parts.values()})
glyphs.update({name: square() for name in joined})
pen = TTGlyphPen(None)
pen.moveTo((300, 200)); pen.lineTo((300, 600)); pen.lineTo((700, 600)); pen.lineTo((700, 200)); pen.closePath()
glyphs["small"] = pen.glyph()
glyphs["masked"] = square()
fb.setupGlyf(glyphs)
fb.setupHorizontalMetrics({name: (0 if name in ("zwj", "tagG", "tagB", "cancelTag") else 1000 if name != ".notdef" else 500, 0)
                          for name in order})
fb.setupHorizontalHeader(ascent=900, descent=-100)
fb.setupNameTable({"familyName": "ColorGrid", "styleName": "Regular"})
fb.setupOS2(sTypoAscender=900, sTypoDescender=-100, usWinAscent=900, usWinDescent=100, sCapHeight=800)
fb.setupPost()
fb.setupCPAL([[(1.0, 0.0, 0.0, 1.0), (0.0, 0.5, 0.0, 1.0), (0.0, 0.0, 1.0, 1.0), (1.0, 1.0, 0.0, 1.0),
               (1.0, 0.0, 1.0, 1.0), (0.0, 1.0, 1.0, 1.0), (1.0, 0.5, 0.0, 1.0), (0.5, 0.5, 0.5, 1.0)]])
def solid(index):
    return {"Format": ot.PaintFormat.PaintGlyph, "Glyph": "square",
            "Paint": {"Format": ot.PaintFormat.PaintSolid, "PaletteIndex": index, "Alpha": 1.0}}
colr = {name: solid(i % 3 if i < 150 else 0xFFFF) for i, name in enumerate(colour)}
colr.update({name: solid(7) for name in ("tone", "letterD", "letterK", "blackflag")})
colr.update({name: solid(index) for name, index in joined.items()})
colr["masked"] = {
    "Format": ot.PaintFormat.PaintComposite, "CompositeMode": ot.CompositeMode.SRC_IN,
    "SourcePaint": {"Format": ot.PaintFormat.PaintGlyph, "Glyph": "square", "Paint": {
        "Format": ot.PaintFormat.PaintLinearGradient, "x0": 100, "y0": 0, "x1": 900, "y1": 0, "x2": 100, "y2": 800,
        "ColorLine": {"ColorStop": [{"StopOffset": 0, "PaletteIndex": 2, "Alpha": 0.5},
                                    {"StopOffset": 1, "PaletteIndex": 2, "Alpha": 0.5}]}}},
    "BackdropPaint": solid(0) | {"Glyph": "small"},
}
fb.setupCOLR(colr, version=1)
fb.addOpenTypeFeatures("""
languagesystem DFLT dflt;
feature ccmp {
    sub c000 tone by toneE000;
    sub c001 zwj c002 by zwjE001E002;
    sub letterD letterK by flagDK;
    sub blackflag tagG tagB cancelTag by flagGB;
} ccmp;
""")
save(fb, "ColorGrid.ttf")

levels = [f"level{i:02d}" for i in range(60)]
order = [".notdef", "square", "bomb", "deep", "loop"] + levels
fb = FontBuilder(1000, isTTF=True)
fb.setupGlyphOrder(order)
fb.setupCharacterMap({0xE000: "bomb", 0xE001: "deep", 0xE002: "loop"})
fb.setupGlyf({name: square() if name != ".notdef" else empty() for name in order})
fb.setupHorizontalMetrics({name: (1000, 0) for name in order})
fb.setupHorizontalHeader(ascent=900, descent=-100)
fb.setupNameTable({"familyName": "ColorHostile", "styleName": "Regular"})
fb.setupOS2(sTypoAscender=900, sTypoDescender=-100, usWinAscent=900, usWinDescent=100)
fb.setupPost()
fb.setupCPAL([[(1.0, 0.0, 0.0, 1.0)]])
solid = {"Format": ot.PaintFormat.PaintGlyph, "Glyph": "square", "Paint": {"Format": ot.PaintFormat.PaintSolid, "PaletteIndex": 0, "Alpha": 1.0}}
colr = {"level00": solid}
for i in range(1, 60):
    below = {"Format": ot.PaintFormat.PaintColrGlyph, "Glyph": levels[i - 1]}
    colr[levels[i]] = (ot.PaintFormat.PaintColrLayers, [below, below])
colr["bomb"] = {"Format": ot.PaintFormat.PaintColrGlyph, "Glyph": levels[-1]}
deep = solid
for i in range(300):
    deep = {"Format": ot.PaintFormat.PaintTranslate, "dx": 1, "dy": 0, "Paint": deep}
colr["deep"] = deep
colr["loop"] = {"Format": ot.PaintFormat.PaintGlyph, "Glyph": "loop", "Paint": {"Format": ot.PaintFormat.PaintColrGlyph, "Glyph": "loop"}}
fb.setupCOLR(colr, version=1)
save(fb, "ColorHostile.ttf")
