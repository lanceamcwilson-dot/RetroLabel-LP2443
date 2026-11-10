"""16-bit style banner: original scene rendered small, palette-limited, pixel font, scaled up 3x with no smoothing."""
import re
from PIL import Image, ImageDraw
from playwright.sync_api import sync_playwright
SRC = "/home/claude/ZebraLabelPrinter/docs/images/hero.svg"
OUT = "/home/claude/ZebraLabelPrinter/docs/images/hero-16bit.png"
W, H, S = 400, 147, 3

# 1. scene only (drop text), rendered at low resolution
svg = open(SRC).read()
svg = re.sub(r'<!-- title -->.*?</g>\s*<text[^>]*>LP2443[^<]*</text>\s*<text[^>]*>PDF[^<]*</text>\s*<g transform="translate\(62 288\)">.*?</g>', '', svg, flags=re.S)

open("/tmp/px/scene.svg", "w").write(svg)
with sync_playwright() as p:
    b = p.chromium.launch(); pg = b.new_page(viewport={"width": 1200, "height": 440})
    pg.goto("file:///tmp/px/scene.svg"); pg.wait_for_timeout(300); pg.screenshot(path="/tmp/px/scene.png"); b.close()
img = Image.open("/tmp/px/scene.png").convert("RGB").resize((W, H), Image.BOX)

# 2. limited palette like a 16-bit console (no dithering -> flat pixel shading)
from PIL import ImageOps
img = ImageOps.posterize(img, 3)   # 8 levels per channel: flat, banded 16-bit-era shading

# 3. hand-made 5x7 pixel font
F = {
'A':["01110","10001","10001","11111","10001","10001","10001"],'B':["11110","10001","10001","11110","10001","10001","11110"],
'C':["01111","10000","10000","10000","10000","10000","01111"],'D':["11110","10001","10001","10001","10001","10001","11110"],
'E':["11111","10000","10000","11110","10000","10000","11111"],'F':["11111","10000","10000","11110","10000","10000","10000"],
'G':["01111","10000","10000","10111","10001","10001","01111"],'H':["10001","10001","10001","11111","10001","10001","10001"],
'I':["11111","00100","00100","00100","00100","00100","11111"],'K':["10001","10010","10100","11000","10100","10010","10001"],
'L':["10000","10000","10000","10000","10000","10000","11111"],'M':["10001","11011","10101","10101","10001","10001","10001"],
'N':["10001","11001","10101","10011","10001","10001","10001"],'O':["01110","10001","10001","10001","10001","10001","01110"],
'P':["11110","10001","10001","11110","10000","10000","10000"],'R':["11110","10001","10001","11110","10100","10010","10001"],
'S':["01111","10000","10000","01110","00001","00001","11110"],'T':["11111","00100","00100","00100","00100","00100","00100"],
'U':["10001","10001","10001","10001","10001","10001","01110"],'V':["10001","10001","10001","10001","10001","01010","00100"],
'W':["10001","10001","10001","10101","10101","11011","10001"],'X':["10001","10001","01010","00100","01010","10001","10001"],
'Y':["10001","10001","01010","00100","00100","00100","00100"],'Z':["11111","00001","00010","00100","01000","10000","11111"],
'0':["01110","10011","10101","10101","10101","11001","01110"],'1':["00100","01100","00100","00100","00100","00100","01110"],
'2':["01110","10001","00001","00110","01000","10000","11111"],'3':["11110","00001","00001","01110","00001","00001","11110"],
'4':["00010","00110","01010","10010","11111","00010","00010"],'6':["00110","01000","10000","11110","10001","10001","01110"],
'K2':[],' ':["00000"]*7,'.':["00000","00000","00000","00000","00000","01100","01100"],
'-':["00000","00000","00000","11111","00000","00000","00000"],'>':["01000","00100","00010","00001","00010","00100","01000"],
'*':["00000","00100","10101","01110","10101","00100","00000"],'x':["00000","00000","10001","01010","00100","01010","10001"],
}
def text(d, x, y, s, col, scale=1, shadow=None, sx=1, sy=1):
    if shadow:
        text(d, x + sx * scale, y + sy * scale, s, shadow, scale)
    cx = x
    for ch in s:
        g = F.get(ch, F[' '])
        for r, row in enumerate(g):
            for c, bit in enumerate(row):
                if bit == '1': d.rectangle([cx + c * scale, y + r * scale, cx + (c + 1) * scale - 1, y + (r + 1) * scale - 1], fill=col)
        cx += 6 * scale
    return cx

d = ImageDraw.Draw(img)
YEL, PINK, CREAM, LAV, GRN, INK = (255, 211, 92), (255, 61, 127), (241, 234, 217), (201, 184, 232), (60, 255, 143), (27, 22, 51)
# chunky title with 2-layer shadow
text(d, 22, 38, "RETROLABEL", YEL, scale=3, shadow=PINK, sx=1, sy=1)
text(d, 22, 72, "LP2443 * EPL2 * 203 DPI", CREAM)
text(d, 22, 86, "PDF > 4X6 LABEL * NO DRIVER", LAV)
# "press start"-style pill
d.rectangle([20, 102, 24 + 6 * 24 + 2, 102 + 13], fill=GRN)
text(d, 24, 105, "STILL PRINTING SINCE Y2K", INK)
# blinking-cursor style prompt
text(d, 22, 124, "> PRESS PRINT", YEL)
d.rectangle([22 + 6 * 13 + 2, 124, 22 + 6 * 13 + 6, 130], fill=YEL)

# 4. scale up 3x, hard pixels
big = img.resize((W * S, H * S), Image.NEAREST)
big.save(OUT, optimize=True)
print(OUT, big.size)
