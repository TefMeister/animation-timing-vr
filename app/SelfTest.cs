// SelfTest.cs -- `AnimTiming.exe --selftest <folder>`: no window, no game. Exercises the clip maths (the same rules as the
// plugin's apply_edits), the nudge interpolation, the edit file round trip, the JSON, the key-file shape PlayingBox writes and
// the joint pickers. Prints one line per check and PASS / FAIL at the end.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class SelfTest {
    static int fails;

    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fails++; }
    static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-6; }

    public static bool Run(string folder) {
        fails = 0;
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }   // a run starts clean
        Directory.CreateDirectory(folder);
        Console.WriteLine("Animation & Timing self-test in " + folder);

        // ---- the frame list and the ops, the plugin's rules -------------------------------------------------------------------
        var raw = new List<double> { 0.0, 0.1, 0.2, 0.3, 0.4 };
        var f = ClipMath.Apply(raw, null);
        Check(f.Count == 5 && f.All(x => Near(x.Dt, 0.1)), "raw clip: five frames, 0.1 s apart (last takes the previous dt)");
        var t = ClipMath.Times(f);
        Check(Near(t[4], 0.4), "times accumulate: last frame at 0.4 s");

        var ops = new List<object>();
        ops.Add(Op("hold", "i", 2, "n", 3));
        f = ClipMath.Apply(raw, ops);
        Check(f.Count == 8 && f[2].Src == 2 && f[3].Src == 2 && f[5].Src == 2 && f[6].Src == 3, "hold 3 on frame 2: three copies after it, the rest shifts");
        ops.Add(Op("remove", "i", 0));
        f = ClipMath.Apply(raw, ops);
        Check(f.Count == 7 && f[0].Src == 1, "remove frame 0: the list starts at row 1");
        ops.Add(Op("speed", "from", 0, "to", 6, "x", 2.0));
        f = ClipMath.Apply(raw, ops);
        t = ClipMath.Times(f);
        Check(Near(f[0].Dt, 0.05) && Near(t[6], 0.3), "speed x2 over all: every dt halves, the clip ends at 0.3 s");
        var tiny = ClipMath.Apply(new List<double> { 0, 0.1 }, new List<object> { Op("remove", "i", 0) });
        Check(tiny.Count == 2, "a two-frame clip never loses a frame");

        // ---- the nudges by source row, interpolated along the edited time -----------------------------------------------------
        f = ClipMath.Apply(raw, null); t = ClipMath.Times(f);
        var keys = new List<NudgeKey> {
            new NudgeKey { Src = 1, Hand = new double[] { 0, 0, 0, 0, 0, 0 } },
            new NudgeKey { Src = 3, Hand = new double[] { 2, 0, 0, 0, 10, 0 } } };
        var at0 = ClipMath.At(f, t, keys, 0, k => k.Hand, 6);
        var at2 = ClipMath.At(f, t, keys, 2, k => k.Hand, 6);
        var at4 = ClipMath.At(f, t, keys, 4, k => k.Hand, 6);
        Check(Near(at0[0], 0) && Near(at0[4], 0), "before the first key: flat (the first key's value)");
        Check(Near(at2[0], 1) && Near(at2[4], 5), "between the keys: half way (1 cm, 5 deg)");
        Check(Near(at4[0], 2) && Near(at4[4], 10), "after the last key: flat (the last key's value)");
        keys[1].Joints["L_IndexF1"] = new double[] { 0, 0, 30 };
        var j2 = ClipMath.At(f, t, keys, 2, k => { double[] v; return k.Joints.TryGetValue("L_IndexF1", out v) ? v : null; }, 3);
        Check(Near(j2[2], 30), "a joint keyed on one key only: the other key borrows it (flat, not fading from zero)");
        // a hold op: the key sticks to its SOURCE row through the inserted copies
        var f2 = ClipMath.Apply(raw, new List<object> { Op("hold", "i", 3, "n", 2) }); var t2 = ClipMath.Times(f2);
        var h5 = ClipMath.At(f2, t2, keys, 5, k => k.Hand, 6);
        Check(Near(h5[0], 2), "after a hold, the key on row 3 still lands on every frame that came from row 3");

        // ---- the edit file round trip ------------------------------------------------------------------------------------------
        var clipsDir = Path.Combine(folder, "clips");
        Directory.CreateDirectory(clipsDir);
        var info = new ClipInfo { Gun = "ri3008", Event = "round_in", Hands = "left", CsvFrames = 5 };
        info.RawT.AddRange(raw); info.Joints.AddRange(new[] { "L_IndexF1", "L_Thumb1" });
        var ed = new ClipEdit(info, clipsDir);
        ed.Hold(2, 3); ed.Speed(0, 7, 0.8);
        var key = ed.KeyAtOrNew(ed.Frames[4].Src);
        key.Hand = new double[] { 1.5, 0, -0.5, 0, 12, 0 };
        key.Joints["L_IndexF1"] = new double[] { 0, 0, 20 };
        ed.Save();
        Check(File.Exists(ed.Path), "edit file written: " + Path.GetFileName(ed.Path));
        var ed2 = new ClipEdit(info, clipsDir);
        Check(ed2.Ops.Count == 2 && ed2.Frames.Count == 8, "re-read: two ops, eight frames");
        var k2 = ed2.KeyAt(2);
        Check(k2 != null && k2.Hand != null && Near(k2.Hand[4], 12) && k2.Joints.ContainsKey("L_IndexF1"), "re-read: the key on row 2 keeps its hand move and its finger turn");
        Check(ed2.KeyedFrames().SequenceEqual(new[] { 2, 3, 4, 5 }), "keyed frames after the hold: 2, 3, 4, 5 (all from row 2)");
        Check(ed2.Undo() && ed2.Ops.Count == 1 && ed2.Frames.Count == 8, "undo takes the last op (the speed) off");
        ed2.Undo();
        Check(ed2.Frames.Count == 5, "undo again: back to the raw five frames");

        // ---- COPY ON +1 (Tefa 2026-11-08): this frame's pose becomes the next frame's key, forward only, never over a key -------
        ed2.CopyForward(2, null);
        var c3 = ed2.KeyAt(3);
        Check(c3 != null && c3.Hand != null && Near(c3.Hand[4], 12) && Near(c3.Hand[0], 1.5) && c3.Joints.ContainsKey("L_IndexF1") && Near(c3.Joints["L_IndexF1"][2], 20), "copy on +1: frame 3 gets frame 2's whole hand and its finger turn");
        c3.Hand[4] = 5;
        string again = ed2.CopyForward(2, null);
        Check(Near(ed2.KeyAt(3).Hand[4], 5) && again.Contains("already has its own key"), "copy on +1 never overwrites a key already on the next frame");
        Check(ed2.CopyForward(4, null).StartsWith("last frame") && ed2.Nudges.Count == 2, "copy on +1 from the last frame does nothing");
        ed2.CopyStep(2, -1, null);
        var c1 = ed2.KeyAt(1);
        Check(c1 != null && Near(c1.Hand[4], 12) && Near(c1.Joints["L_IndexF1"][2], 20), "copy on −1: frame 1 gets frame 2's whole hand and its finger turn");
        Check(ed2.CopyStep(2, -1, null).Contains("already has its own key") && Near(ed2.KeyAt(1).Hand[4], 12), "copy on −1 never overwrites a key already on the frame before");
        Check(ed2.CopyStep(0, -1, null).StartsWith("first frame") && ed2.Nudges.Count == 3, "copy on −1 from the first frame does nothing");
        Check(ed2.CopyStep(2, 3, null).Contains("one step only") && ed2.Nudges.Count == 3, "a copy is never more than one step");
        var bare = new ClipEdit(new ClipInfo { Gun = "x", Event = "bare", RawT = { 0, 0.1, 0.2 } }, null);
        Check(bare.CopyForward(0, new[] { "L_Thumb1" }).Contains("nothing to copy") && bare.Nudges.Count == 0, "copy on +1 with no changes anywhere makes no key");
        bare.Hold(0, 1);
        Check(bare.CopyForward(0, null).Contains("same row") && bare.Nudges.Count == 0, "copy on +1 onto an added copy of the same row: nothing to do");

        // ---- clips_state.json as the plugin writes it ----------------------------------------------------------------------------
        File.WriteAllText(Path.Combine(clipsDir, "clips_state.json"),
            "{\"gun\":\"ri3008\",\"relative\":true,\"clips\":[\n  {\"event\":\"round_in\",\"hands\":\"left\",\"csv_frames\":5,\"frames\":5,\"seconds\":0.4,\"joints\":[\"L_IndexF1\"],\"raw_t\":[0,0.1,0.2,0.3,0.4],\"src\":[0,1,2,3,4],\"t\":[0,0.1,0.2,0.3,0.4]}],\"playing\":[{\"event\":\"round_in\",\"key\":\"ri3008_round_in\",\"hand\":\"left\",\"frame\":2.00,\"held\":true,\"blend\":1.00}]}\n");
        var st = ClipsState.Read(clipsDir);
        Check(st.Gun == "ri3008" && st.Relative && st.Clips.Count == 1 && st.Clips[0].RawT.Count == 5 && st.Playing.Count == 1, "clips_state.json read: gun, one clip with raw times, one play");

        // ---- JSON --------------------------------------------------------------------------------------------------------------
        var d = new Dictionary<string, object>(); d["seq"] = 3.0; d["freeze"] = true; d["nudge_live"] = new Dictionary<string, object> { { "L_Hand", new List<object> { 1.5, -2.0, 0.0 } } };
        var back = Json.Obj(Json.Parse(Json.Write(d)));
        Check(back != null && Json.Num(back["seq"], 0) == 3 && Json.Bool(back["freeze"]) && Json.Arr(Json.Obj(back["nudge_live"])["L_Hand"]).Count == 3, "JSON round trip of a command");

        // ---- the key file shape (PlayingBox) ---------------------------------------------------------------------------------
        var nud = new Dictionary<string, object>();
        var k1 = new Dictionary<string, object>(); k1["frame"] = 12.0; k1["joints"] = new Dictionary<string, object> { { "L_Hand", new List<object> { 0.0, 5.0, 0.0 } } };
        nud["keys"] = new List<object> { k1 };
        string nf = Path.Combine(folder, "nudges", "re8", "body_0_310.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nf));
        GameLink.WriteWhole(nf, Json.Write(nud));
        var nb = Json.Obj(Json.Parse(File.ReadAllText(nf)));
        Check(nb != null && Json.Arr(nb["keys"]).Count == 1, "a nudge key file for the script: {keys:[{frame, joints}]}");

        // ---- the joint pickers -----------------------------------------------------------------------------------------------
        Check(NudgeRows.IsFinger("L_IndexF1", "l") && NudgeRows.IsFinger("l_hand_thumb_2", "l") && !NudgeRows.IsFinger("R_IndexF1", "l") && !NudgeRows.IsFinger("L_Hand", "l"), "finger picker: Village and RE2 names, the right side excluded");
        Check(NudgeRows.IsWrist("L_Hand", "l") && NudgeRows.IsWrist("r_arm_wrist", "r") && !NudgeRows.IsWrist("L_Forearm", "l"), "wrist picker");
        Check(GameLink.Safe("re8 / Village") == "re8 _ Village", "a game name becomes a folder name");

        Console.WriteLine(fails == 0 ? "PASS" : "FAIL (" + fails + ")");
        return fails == 0;
    }

    static Dictionary<string, object> Op(string op, params object[] kv) {
        var o = new Dictionary<string, object>(); o["op"] = op;
        for (int i = 0; i + 1 < kv.Length; i += 2) o[(string)kv[i]] = Convert.ToDouble(kv[i + 1]);
        return o;
    }
}
