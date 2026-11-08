r"""fake_game.py -- stands in for the in-game script AND for Visceral RE8's plugin, so the window can be tried with no game.

    python test\fake_game.py <folder> [seconds]

Makes <folder>\FakeGame\reframework\{autorun, data\anim_timing, data\visceral_re8\clips} and then, like the script, writes
state.json a few times a second with a Village-style skeleton and two animation layers that advance (or stand still when
frozen, or step on command), and like the plugin writes clips_state.json twice a second with one invented clip, re-reading
<gun>_<event>.edit.json and scrub.json the way the plugin does. Prints each new command it sees:
    COMMAND seq=3 freeze=True step=1 nudge_live=2
    SCRUB seq=2 round_in hands=1 frame=7 hold=True
    TIMINGS drum_turn_s=0.55
"""
import json, os, sys, time

JOINTS = [("root", -1), ("Hip", 0), ("Spine_0", 1), ("Spine_1", 2), ("L_Shoulder", 3), ("L_UpperArm", 4), ("L_Forearm", 5), ("L_Hand", 6)]
for f in ("Index", "Middle", "Ring", "Pinky", "Thumb"):
    base = len(JOINTS)
    JOINTS += [("L_%sF1" % f, 7), ("L_%sF2" % f, base), ("L_%sF3" % f, base + 1)]
JOINTS += [("R_Shoulder", 3), ("R_UpperArm", len(JOINTS)), ("R_Forearm", len(JOINTS) + 1), ("R_Hand", len(JOINTS) + 2)]
wrist_r = len(JOINTS) - 1
for f in ("Index", "Middle", "Ring", "Pinky", "Thumb"):
    base = len(JOINTS)
    JOINTS += [("R_%sF1" % f, wrist_r), ("R_%sF2" % f, base), ("R_%sF3" % f, base + 1)]
JOINTS += [("L_Thigh", 1), ("L_Calf", len(JOINTS)), ("L_Foot", len(JOINTS) + 1), ("R_Thigh", 1), ("R_Calf", len(JOINTS) + 3), ("R_Foot", len(JOINTS) + 4)]

ANIM = {name: [round((i * 7) % 60 - 20.0, 1), round((i * 3) % 40 - 10.0, 1), round((i * 11) % 90 - 45.0, 1)] for i, (name, _) in enumerate(JOINTS)}
LAYERS = [{"src": "body", "i": 0, "bank": 0, "motion": 310, "frame": 0.0, "end": 120.0, "speed": 1.0, "name": "pl_wp_ri3008_reload"},
          {"src": "body", "i": 1, "bank": 2, "motion": 15, "frame": 40.0, "end": 60.0, "speed": 1.0, "name": "pl_idle_upper"}]
CLIP_T = [round(k / 30.0, 4) for k in range(13)]
CLIP_JOINTS = ["L_IndexF1", "L_IndexF2", "L_MiddleF1", "L_ThumbF1", "L_ThumbF2"]
TIMINGS = [("cover_swing_s", 0.33, "s", "the loading cover swings open or shut"), ("drum_turn_s", 0.40, "s", "the drum turns one chamber"),
           ("hammer_press_cm", 1.5, "cm", "how far the palm goes down in that press"), ("clip_blend_s", 0.12, "s", "a finger movement blends in and out")]


def apply_ops(raw_t, ops):   # the plugin's rules (shellclip.cpp apply_edits), for the fake's own frame list
    m = len(raw_t)
    f = [[i, max(0.0, (raw_t[i + 1] - raw_t[i]) if i + 1 < m else (raw_t[i] - raw_t[i - 1] if i > 0 else 1 / 60))] for i in range(m)]
    for o in ops or []:
        op, cnt = o.get("op"), len(f)
        if op == "remove":
            i = int(o.get("i", -1))
            if 0 <= i < cnt and cnt > 2: del f[i]
        elif op == "hold":
            i, n = int(o.get("i", -1)), max(1, min(600, int(o.get("n", 1))))
            if 0 <= i < cnt:
                for _ in range(n): f.insert(i + 1, list(f[i]))
        elif op == "speed":
            a = max(0, min(cnt - 1, int(o.get("from", 0)))); b = max(a, min(cnt - 1, int(o.get("to", cnt - 1)))); x = float(o.get("x", 1))
            if x > 0.01:
                for i in range(a, b + 1): f[i][1] /= x
    return f


def read_json(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


def write_json(path, obj):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(obj, fh)
    os.replace(tmp, path)


def main():
    root = sys.argv[1]
    seconds = float(sys.argv[2]) if len(sys.argv) > 2 else 60
    game = os.path.join(root, "FakeGame")
    anim = os.path.join(game, "reframework", "data", "anim_timing")
    vis = os.path.join(game, "reframework", "data", "visceral_re8")
    clips = os.path.join(vis, "clips")
    for d in (os.path.join(game, "reframework", "autorun"), anim, clips):
        os.makedirs(d, exist_ok=True)
    print("GAME FOLDER", game, flush=True)
    state_file, cmd_file = os.path.join(anim, "state.json"), os.path.join(anim, "command.json")
    write_json(os.path.join(vis, "timings_known.json"), {"items": [{"key": k, "default": d, "value": d, "set": False, "unit": u, "words": w} for k, d, u, w in TIMINGS]})
    last_seq, last_scrub, frozen, speed, nudge_live, want = -1, -1, False, 1.0, {}, []
    held, held_frame, play_t, last_tim = False, 0, None, None
    end, next_state, next_clips, last_t = time.time() + seconds, 0, 0, time.time()
    while time.time() < end:
        now = time.time(); dt = now - last_t; last_t = now
        cmd = read_json(cmd_file)
        if cmd and cmd.get("seq") != last_seq:
            last_seq = cmd["seq"]
            frozen, speed = bool(cmd.get("freeze")), float(cmd.get("speed", 1.0))
            nudge_live, want = cmd.get("nudge_live", {}), cmd.get("want_read", [])
            step = int(cmd.get("step", 0) or 0)
            sf = cmd.get("set_frame")
            for L in LAYERS:
                if step: L["frame"] = max(0.0, min(L["end"], L["frame"] + step))
                if sf and sf.get("src") == L["src"] and int(sf.get("i", -1)) == L["i"]: L["frame"] = max(0.0, min(L["end"], float(sf.get("frame", 0))))
            print("COMMAND seq=%s freeze=%s speed=%.2f step=%d set_frame=%s nudge_live=%d want_read=%d reload=%s" % (
                last_seq, frozen, speed, step, "yes" if sf else "no", len(nudge_live), len(want), cmd.get("reload_nudges", 0)), flush=True)
            next_state = 0
        for L in LAYERS:
            L["speed"] = 0.0 if frozen else speed
            if not frozen:
                L["frame"] += 60.0 * speed * dt
                if L["frame"] > L["end"]: L["frame"] -= L["end"]
        if now >= next_state:
            next_state = now + (0.12 if (frozen or nudge_live or want) else 1.0)
            angles = {n: [a + b for a, b in zip(ANIM[n], nudge_live.get(n, [0, 0, 0]))] for n in set(want) | set(nudge_live) if n in ANIM}
            nud = []
            ndir = os.path.join(anim, "nudges", "re8")
            if os.path.isdir(ndir):
                for fn in sorted(os.listdir(ndir)):
                    if fn.endswith(".json"):
                        d = read_json(os.path.join(ndir, fn)) or {}
                        nud.append([fn[:-5], len(d.get("keys", []))])
            write_json(state_file, {"game": "re8", "character": "pl1000", "connected": True, "frozen": frozen, "speed": speed, "seq_seen": last_seq,
                                    "frame": int(now * 60) % 100000, "error": "", "layers": [dict(L, frame=round(L["frame"], 1)) for L in LAYERS],
                                    "joints": [list(j) for j in JOINTS], "angles": angles, "nudge_files": nud})
        # ---- the plugin's side ----
        sc = read_json(os.path.join(clips, "scrub.json"))
        if sc and sc.get("seq") != last_scrub:
            last_scrub = sc["seq"]
            if sc.get("hold"): held, held_frame = True, int(sc.get("frame", 0))
            elif held: held = False
            elif int(sc.get("frame", -1)) < 0: play_t = now
            print("SCRUB seq=%s %s hands=%s frame=%s hold=%s" % (last_scrub, sc.get("event"), sc.get("hands"), sc.get("frame"), sc.get("hold")), flush=True)
            next_clips = 0
        tim = read_json(os.path.join(vis, "timings.json"))
        if tim is not None and tim != last_tim:
            last_tim = tim
            print("TIMINGS " + " ".join("%s=%s" % kv for kv in sorted(tim.items())), flush=True)
            write_json(os.path.join(vis, "timings_known.json"), {"items": [{"key": k, "default": d, "value": tim.get(k, d), "set": k in tim, "unit": u, "words": w} for k, d, u, w in TIMINGS]})
        if now >= next_clips:
            next_clips = now + 0.5
            ed = read_json(os.path.join(clips, "ri3008_round_in.edit.json")) or {}
            frames = apply_ops(CLIP_T, ed.get("ops"))
            t, acc = [], 0.0
            for fr in frames: t.append(round(acc, 4)); acc += fr[1]
            playing = []
            if held: playing.append({"event": "round_in", "key": "ri3008_round_in", "hand": "left", "frame": float(max(0, min(len(frames) - 1, held_frame))), "held": True, "blend": 1.0})
            elif play_t is not None and now - play_t < (t[-1] if t else 0) + 0.3:
                k = next((i for i, tt in enumerate(t) if tt > now - play_t), len(t) - 1)
                playing.append({"event": "round_in", "key": "ri3008_round_in", "hand": "left", "frame": float(k), "held": False, "blend": 1.0})
            write_json(os.path.join(clips, "clips_state.json"), {"gun": "ri3008", "relative": True, "clips": [
                {"event": "round_in", "hands": "left", "csv_frames": len(CLIP_T), "frames": len(frames), "seconds": t[-1] if t else 0, "joints": CLIP_JOINTS,
                 "raw_t": CLIP_T, "src": [fr[0] for fr in frames], "t": t, "parts": ["drum"]}], "playing": playing})   # the drum turns with it (2026-11-08)
        time.sleep(0.03)
    print("DONE last_seq=%s last_scrub=%s" % (last_seq, last_scrub))


if __name__ == "__main__":
    main()
