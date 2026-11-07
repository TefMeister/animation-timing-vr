r"""click.py -- presses buttons in a window by their captions, for a test with no hands (BM_CLICK to the child window).
    python test\click.py "Animation & Timing" "HOLD A FRAME" "+1 ▶" ...
Prints one line per caption: clicked, or not found."""
import ctypes, sys, time
from ctypes import wintypes

user32 = ctypes.windll.user32
BM_CLICK = 0x00F5
title, captions = sys.argv[1], sys.argv[2:]
top = user32.FindWindowW(None, title)
if not top:
    sys.exit("no window called " + title)
EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)


def find(caption):
    found = []
    def cb(h, _):
        n = user32.GetWindowTextLengthW(h)
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(h, buf, n + 1)
        if buf.value == caption:
            found.append(h)
        return True
    user32.EnumChildWindows(top, EnumProc(cb), 0)
    return found[0] if found else None


for c in captions:
    h = find(c)
    if h:
        user32.SendMessageW(h, BM_CLICK, 0, 0)
        print("clicked", c, flush=True)
    else:
        print("NOT FOUND", c, flush=True)
    time.sleep(0.4)
