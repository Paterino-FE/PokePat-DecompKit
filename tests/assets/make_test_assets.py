"""Generates the synthetic test PNGs used by the integration tests.

Run with:  python tests/assets/make_test_assets.py   (needs Pillow)
The output is committed, so this only needs to be re-run when the art changes.
"""
import os
import random

from PIL import Image

OUT = os.path.dirname(os.path.abspath(__file__))
random.seed(7)
W, H = 128, 64
T = (0, 0, 0, 0)
mid = Image.new("RGBA", (W, H), T)
top = Image.new("RGBA", (W, H), T)

greens = [(56, 120, 48), (72, 152, 64), (96, 176, 80), (128, 200, 96), (40, 96, 40)]
# Row 0: grass variations
for mx in range(8):
    for y in range(16):
        for x in range(16):
            c = greens[(x * 3 + y * 5 + mx * 7 + random.randint(0, 2)) % len(greens)]
            mid.putpixel((mx * 16 + x, y), c + (255,))
# Row 1: sand / path
sand = [(216, 192, 136), (200, 176, 120), (232, 208, 160), (184, 160, 104)]
for mx in range(8):
    for y in range(16, 32):
        for x in range(16):
            mid.putpixel((mx * 16 + x, y), sand[(x + y * 2 + mx) % 4] + (255,))
# Row 2: a small house (4 metatiles) and water-like blocks (4 metatiles)
roof = [(168, 48, 48), (200, 72, 64), (120, 32, 32)]
wall = [(240, 232, 208), (208, 200, 176), (96, 80, 64)]
for mx in range(4):
    for y in range(32, 48):
        for x in range(16):
            gx = mx * 16 + x
            c = roof[(gx // 2 + y) % 3] if y < 40 else wall[(gx // 4 + y) % 3]
            if y >= 42 and 20 <= gx < 28:
                c = (64, 48, 32)  # door
            if 41 <= y <= 44 and (gx % 16) in (4, 5, 10, 11) and mx != 1:
                c = (120, 168, 232)  # windows
            mid.putpixel((gx, y), c + (255,))
blues = [(40, 88, 184), (56, 112, 208), (88, 144, 232), (152, 192, 248), (24, 64, 152)]
for mx in range(4, 8):
    for y in range(32, 48):
        for x in range(16):
            mid.putpixel((mx * 16 + x, y), blues[(x + y + mx * 2) % 5] + (255,))
# Row 3: flowers on grass (middle) with a tree canopy on the top layer
flower = [(248, 248, 248), (248, 200, 48), (232, 80, 120)]
for mx in range(8):
    for y in range(48, 64):
        for x in range(16):
            c = greens[(x + y) % 3]
            if x % 6 == 2 and y % 6 == 2:
                c = flower[(mx + x) % 3]
            mid.putpixel((mx * 16 + x, y), c + (255,))
leaf = [(32, 104, 56), (48, 136, 72), (24, 72, 40), (80, 168, 96)]
for mx in range(2, 6):
    for y in range(48, 64):
        for x in range(16):
            gx = mx * 16 + x
            if (gx - 64) ** 2 / 1024 + (y - 56) ** 2 / 90 < 1.0:
                top.putpixel((gx, y), leaf[(gx * 7 + y * 3) % 4] + (255,))

flat = mid.copy()
flat.alpha_composite(top)
mid.save(os.path.join(OUT, "test_middle.png"))
top.save(os.path.join(OUT, "test_top.png"))
flat.save(os.path.join(OUT, "test_single.png"))

# Negative case 1: one 8x8 tile (tile x=1, y=2 -> metatile (0,1)) gets 64 distinct colors.
bad = flat.copy()
for i in range(64):
    bad.putpixel((8 + i % 8, 16 + i // 8), ((i * 4) % 256, (i * 37) % 256, (i * 91) % 256, 255))
bad.save(os.path.join(OUT, "test_too_many_colors.png"))

# Negative case 2: dimensions that are not multiples of 16.
Image.new("RGBA", (120, 50), (10, 10, 10, 255)).save(os.path.join(OUT, "test_bad_size.png"))
print("written to", OUT)
