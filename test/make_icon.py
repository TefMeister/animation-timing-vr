r"""make_icon.py -- draws app\anim_timing.ico: a film strip with one frame lit and a clock hand over it (animation + timing).
    python test\make_icon.py"""
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def frame(size):
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    s = size / 64.0
    # the strip
    d.rounded_rectangle([4 * s, 14 * s, 60 * s, 50 * s], radius=6 * s, fill=(38, 41, 52, 255), outline=(120, 130, 160, 255), width=max(1, int(2 * s)))
    for k in range(5):   # the holes along both edges
        x = (9 + k * 11) * s
        d.rectangle([x, 17 * s, x + 5 * s, 20 * s], fill=(20, 22, 30, 255))
        d.rectangle([x, 44 * s, x + 5 * s, 47 * s], fill=(20, 22, 30, 255))
    for k in range(3):   # three frames, the middle one lit
        x = (9 + k * 16) * s
        lit = k == 1
        d.rectangle([x, 23 * s, x + 13 * s, 41 * s], fill=(240, 170, 70, 255) if lit else (62, 67, 86, 255))
    # the clock hand over the lit frame
    cx, cy = 31.5 * s, 32 * s
    d.ellipse([cx - 9 * s, cy - 9 * s, cx + 9 * s, cy + 9 * s], outline=(28, 30, 38, 255), width=max(1, int(2 * s)))
    d.line([cx, cy, cx, cy - 7 * s], fill=(28, 30, 38, 255), width=max(1, int(2 * s)))
    d.line([cx, cy, cx + 5 * s, cy + 3 * s], fill=(28, 30, 38, 255), width=max(1, int(2 * s)))
    return im


out = os.path.join(HERE, "app", "anim_timing.ico")
frame(256).save(out, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("wrote", out)
