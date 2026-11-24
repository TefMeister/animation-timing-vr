r"""nav_test.py -- Hand Poser's controller scheme in Animation & Timing (2026-11-24): LT picks a section, the left stick walks its
sliders, the right stick tunes, A ticks, B unticks the section, X undo / Y redo on the last slider only, a held grip stops
everything. On OUR CLIPS' WHOLE HAND box, against the pretend game (test\fake_game.py, one invented clip) and a pretend Window View
block (the controllers), in build\navtest only: no real game, no real mouse (clicks are posted to the window), no real settings
(--data).

    python test\nav_test.py            -> PASS / FAIL lines, build\navtest\*.png

Needs Window View and the real Animation & Timing closed (the controller block and the window title are the same)."""
import ctypes, json, mmap, os, shutil, struct, subprocess, sys, threading, time
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
T = os.path.join(ROOT, "build", "navtest")
TITLE = "Animation & Timing"
user32 = ctypes.windll.user32
CTRL_MODE, STICK_Y, SQUEEZE_R, BUTTONS, LSTICK_Y, STICK_X, LSTICK_X = 536, 580, 576, 584, 624, 636, 640
B_A, B_Y, B_B, B_X = 1, 2, 4, 8
results = []


def check(name, ok, info=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + (" (" + info + ")" if info else ""), flush=True)


def put_f(view, off, v): view[off:off + 4] = struct.pack("<f", v)
def put_i(view, off, v): view[off:off + 4] = struct.pack("<i", v)


def pulse(view, off, value, hold=0.12, as_int=False):
    (put_i if as_int else put_f)(view, off, value)
    time.sleep(hold)
    (put_i if as_int else put_f)(view, off, 0)
    time.sleep(0.3)


def children(hwnd):
    out = []
    proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(h, _):
        n = user32.GetWindowTextLengthW(h)
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(h, buf, n + 1)
        out.append((h, buf.value))
        return True
    user32.EnumChildWindows(hwnd, proc(cb), 0)
    return out


def post_click(h, middle=False):
    lp = (8 << 16) | 120   # 120 px in: past the section tick at the head's left
    down, up, mk = (0x207, 0x208, 0x10) if middle else (0x201, 0x202, 1)
    user32.PostMessageW(h, down, mk, lp)
    time.sleep(0.05)
    user32.PostMessageW(h, up, 0, lp)
    time.sleep(0.4)


def grab(name):
    g = os.path.join(os.path.dirname(ROOT), "hand-poser-vr", "test", "grab_window.py")
    if os.path.exists(g):
        subprocess.run([sys.executable, g, TITLE, os.path.join(T, name)], check=False)


GAME = os.path.join(T, "FakeGame")
CLIPS = os.path.join(GAME, "reframework", "data", "visceral_re8", "clips")


def hand():   # the whole hand's six numbers on the held frame, as the edit file keeps them (x first)
    time.sleep(0.5)   # the window saves 120 ms after a change
    for name in os.listdir(CLIPS) if os.path.isdir(CLIPS) else []:
        if name.endswith(".edit.json"):
            try:
                with open(os.path.join(CLIPS, name), encoding="utf-8") as f:
                    for k in json.load(f).get("nudges", []):
                        if "hand" in k:
                            return [round(v, 3) for v in k["hand"]]
            except (OSError, ValueError):
                pass
    return [0.0] * 6


def main():
    if user32.FindWindowW(None, TITLE):
        sys.exit("close the real Animation & Timing first")
    shutil.rmtree(T, ignore_errors=True)
    os.makedirs(os.path.join(T, "appdata"))
    wv = mmap.mmap(-1, 644, tagname=r"Local\FlatToVR_WindowView_v2")
    put_i(wv, CTRL_MODE, 1)
    fake = subprocess.Popen([sys.executable, os.path.join(HERE, "fake_game.py"), T, "90"], stdout=open(os.path.join(T, "fake.out"), "w"), stderr=subprocess.STDOUT)
    time.sleep(2)
    app = subprocess.Popen([os.path.join(ROOT, "dist", "AnimTiming.exe"), "--game", GAME, "--data", os.path.join(T, "appdata")])
    try:
        time.sleep(5)
        hwnd = user32.FindWindowW(None, TITLE)
        check("the window is up", bool(hwnd))
        kids = children(hwnd)
        head = [h for h, t in kids if t.startswith("WHOLE HAND ON THE GUN")]
        check("the clip's WHOLE HAND box is there", bool(head), ", ".join(sorted(set(t for _, t in kids if t))[:14]))
        check("one undo pair on the window", sum(1 for _, t in kids if t == "↶") == 1 and sum(1 for _, t in kids if t == "↷") == 1)
        if not head:
            return
        grab("1-open.png")
        post_click(head[0], middle=True)   # LT: the whole hand's section ticked, its first slider (left / right, x) selected
        grab("2-section.png")
        pulse(wv, STICK_X, 1.0)            # RS right: x half a centimetre right (the file keeps x turned round: -0.5)
        h = hand()
        check("LT picks the section and its first slider; RS steps it", h[0] == -0.5 and h[1] == 0, str(h))
        pulse(wv, BUTTONS, B_X, as_int=True)   # X: undo
        check("X undoes it", hand()[0] == 0, str(hand()))
        pulse(wv, BUTTONS, B_Y, as_int=True)   # Y: redo
        check("Y redoes it", hand()[0] == -0.5, str(hand()))
        pulse(wv, BUTTONS, B_X, as_int=True)
        pulse(wv, LSTICK_X, 1.0)           # LS right: the next slider in the row (down / up, y)
        pulse(wv, STICK_X, 1.0)
        h = hand()
        check("LS right walks to the next slider, RS steps only that one", h[1] == 0.5 and h[0] == 0, str(h))
        pulse(wv, BUTTONS, B_A, as_int=True)   # A: tick y
        pulse(wv, LSTICK_X, -1.0)              # LS left: x again
        pulse(wv, BUTTONS, B_A, as_int=True)   # A: tick it too
        grab("3-two-ticked.png")
        pulse(wv, STICK_X, 1.0)                # both ticked sliders step
        h = hand()
        check("A ticks two sliders; RS steps both", h[0] == -0.5 and h[1] == 1.0, str(h))
        pulse(wv, BUTTONS, B_X, as_int=True)   # X: undo the two together
        h = hand()
        check("X undoes both (one press moved them together)", h[0] == 0 and h[1] == 0.5, str(h))
        pulse(wv, BUTTONS, B_B, as_int=True)   # B: the section's slider ticks off
        pulse(wv, STICK_X, 1.0)                # now only the selected one (x) steps
        h = hand()
        check("B unticks the section's sliders: RS steps the selected one only", h[0] == -0.5 and h[1] == 0.5, str(h))
        put_f(wv, SQUEEZE_R, 1.0)              # the right grip held (a window carried in Window View) ...
        time.sleep(0.2)
        pulse(wv, STICK_X, 1.0)                # ... the right stick does nothing
        pulse(wv, BUTTONS, B_X, as_int=True)   # ... nor X
        put_f(wv, SQUEEZE_R, 0.0)
        h = hand()
        check("a grip held: no button or stick moves a slider", h[0] == -0.5 and h[1] == 0.5, str(h))
        grab("4-end.png")
    finally:
        h = user32.FindWindowW(None, TITLE)
        if h:
            user32.PostMessageW(h, 0x10, 0, 0)   # WM_CLOSE: the window's own close
        time.sleep(2)
        if app.poll() is None:
            app.terminate()
        fake.terminate()
        wv.close()
    print("ALL PASS" if results and all(results) else "SOME FAILED", flush=True)


if __name__ == "__main__":
    main()
