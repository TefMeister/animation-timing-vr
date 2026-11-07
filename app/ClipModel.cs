// ClipModel.cs -- the maths and the files of OUR CLIPS (Visceral RE8's finger movements): the same frame edits the plugin applies
// (visceral-re8-vr src/shellclip.cpp apply_edits), done here too so the window shows the result at once and the self-test can
// check it with no game.
//
//   clips_state.json  (the plugin, twice a second) {gun, relative, clips:[{event, hands, csv_frames, frames, seconds, joints:[...],
//                     src:[...], t:[...], raw_t:[...]}], playing:[{event, key, hand, frame, held, blend}]}
//   <gun>_<event>.edit.json  (this window) {ops:[{op:"remove",i} | {op:"hold",i,n} | {op:"speed",from,to,x}],
//                     nudges:[{src, joints:{name:[x,y,z] deg}, hand:[x,y,z cm, rx,ry,rz deg]}]}
//   scrub.json  (this window) {seq, gun, event, hands, frame, hold}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

class Frame { public int Src; public double Dt; }

static class ClipMath {
    // the edited frame list: every CSV row once, then the ops in order (the plugin's rules exactly)
    public static List<Frame> Apply(IList<double> rawT, IList<object> ops) {
        var f = new List<Frame>();
        int m = rawT.Count;
        for (int i = 0; i < m; i++) {
            double dt = i + 1 < m ? rawT[i + 1] - rawT[i] : (i > 0 ? rawT[i] - rawT[i - 1] : 1.0 / 60.0);
            f.Add(new Frame { Src = i, Dt = Math.Max(0, dt) });
        }
        if (ops == null) return f;
        foreach (var o in ops) {
            var d = Json.Obj(o);
            if (d == null) continue;
            string op = Json.Str(d.ContainsKey("op") ? d["op"] : null);
            int cnt = f.Count;
            if (op == "remove") {
                int i = Num(d, "i", -1);
                if (i >= 0 && i < cnt && cnt > 2) f.RemoveAt(i);
            } else if (op == "hold") {
                int i = Num(d, "i", -1), n = Math.Max(1, Math.Min(600, Num(d, "n", 1)));
                if (i >= 0 && i < cnt) for (int k = 0; k < n; k++) f.Insert(i + 1, new Frame { Src = f[i].Src, Dt = f[i].Dt });
            } else if (op == "speed") {
                int a = Math.Max(0, Math.Min(cnt - 1, Num(d, "from", 0))), b = Math.Max(a, Math.Min(cnt - 1, Num(d, "to", cnt - 1)));
                double x = d.ContainsKey("x") ? Json.Num(d["x"], 1) : 1;
                if (x > 0.01) for (int i = a; i <= b; i++) f[i].Dt /= x;
            }
        }
        return f;
    }

    public static double[] Times(List<Frame> f) {
        var t = new double[f.Count];
        for (int k = 1; k < f.Count; k++) t[k] = t[k - 1] + f[k - 1].Dt;
        return t;
    }

    static int Num(Dictionary<string, object> d, string k, int dflt) { object v; return d.TryGetValue(k, out v) && v is double ? (int)(double)v : dflt; }

    // the nudge in force at edited frame k, from keys by source row: a key's own value on its frames, else interpolated along
    // the edited time between the keyed frames either side, flat outside (the plugin's rule)
    public static double[] At(List<Frame> f, double[] t, IList<NudgeKey> keys, int k, Func<NudgeKey, double[]> pick, int width) {
        var zero = new double[width];
        if (keys == null || keys.Count == 0 || f.Count == 0) return zero;
        var bySrc = new Dictionary<int, NudgeKey>();
        foreach (var key in keys) bySrc[key.Src] = key;
        var keyed = new List<int>();
        for (int i = 0; i < f.Count; i++) if (bySrc.ContainsKey(f[i].Src)) keyed.Add(i);
        if (keyed.Count == 0) return zero;
        int lo = -1, hi = -1;
        foreach (int kk in keyed) { if (kk <= k) lo = kk; if (kk >= k && hi < 0) hi = kk; }
        if (lo < 0) lo = hi;
        if (hi < 0) hi = lo;
        double[] pa = pick(bySrc[f[lo].Src]), pb = pick(bySrc[f[hi].Src]);
        double[] a = pa ?? pb ?? zero, b = pb ?? a;   // a key without this value takes its neighbour's
        double span = t[hi] - t[lo];
        double u = (hi == lo || span <= 0) ? 0 : Math.Max(0, Math.Min(1, (t[k] - t[lo]) / span));
        var out_ = new double[width];
        for (int i = 0; i < width; i++) out_[i] = a[i] + (b[i] - a[i]) * u;
        return out_;
    }
}

class NudgeKey {
    public int Src;
    public Dictionary<string, double[]> Joints = new Dictionary<string, double[]>();   // name -> x, y, z degrees
    public double[] Hand;                                                              // x, y, z cm, rx, ry, rz deg; null = none
}

// one clip as the plugin describes it, plus its edit file
class ClipInfo {
    public string Gun, Event, Hands;
    public int CsvFrames;
    public List<string> Joints = new List<string>();
    public List<double> RawT = new List<double>();
    public List<int> Src = new List<int>();        // as the plugin has it (its edited list)
    public List<double> T = new List<double>();
    public double Seconds;
    public string Key { get { return Gun + "_" + Event; } }

    public static ClipInfo From(string gun, Dictionary<string, object> d) {
        var c = new ClipInfo { Gun = gun, Event = Json.Str(d.ContainsKey("event") ? d["event"] : null), Hands = Json.Str(d.ContainsKey("hands") ? d["hands"] : null) };
        c.CsvFrames = (int)Json.Num(d.ContainsKey("csv_frames") ? d["csv_frames"] : null, 0);
        c.Seconds = Json.Num(d.ContainsKey("seconds") ? d["seconds"] : null, 0);
        if (d.ContainsKey("joints")) foreach (var j in Json.Arr(d["joints"]) ?? new List<object>()) c.Joints.Add(Json.Str(j));
        if (d.ContainsKey("raw_t")) foreach (var x in Json.Arr(d["raw_t"]) ?? new List<object>()) c.RawT.Add(Json.Num(x, 0));
        if (d.ContainsKey("src")) foreach (var x in Json.Arr(d["src"]) ?? new List<object>()) c.Src.Add((int)Json.Num(x, 0));
        if (d.ContainsKey("t")) foreach (var x in Json.Arr(d["t"]) ?? new List<object>()) c.T.Add(Json.Num(x, 0));
        return c;
    }
}

// the edit file of one clip: read, changed, written; the frame list recomputed from the raw times each time
class ClipEdit {
    public readonly List<object> Ops = new List<object>();
    public readonly List<NudgeKey> Nudges = new List<NudgeKey>();
    public List<Frame> Frames = new List<Frame>();
    public double[] T = new double[0];
    readonly ClipInfo clip;
    public readonly string Path;

    public ClipEdit(ClipInfo clip, string clipsDir) {
        this.clip = clip;
        Path = clipsDir == null ? null : System.IO.Path.Combine(clipsDir, clip.Key + ".edit.json");
        Load();
    }

    public void Load() {
        Ops.Clear(); Nudges.Clear();
        string text = GameLink.ReadAll(Path);
        if (text != null) {
            try {
                var d = Json.Obj(Json.Parse(text));
                if (d != null) {
                    if (d.ContainsKey("ops")) Ops.AddRange(Json.Arr(d["ops"]) ?? new List<object>());
                    if (d.ContainsKey("nudges")) foreach (var e in Json.Arr(d["nudges"]) ?? new List<object>()) {
                        var k = Json.Obj(e);
                        if (k == null || !k.ContainsKey("src")) continue;
                        var key = new NudgeKey { Src = (int)Json.Num(k["src"], 0) };
                        var js = k.ContainsKey("joints") ? Json.Obj(k["joints"]) : null;
                        if (js != null) foreach (var kv in js) { var a = Json.Arr(kv.Value); if (a != null && a.Count == 3) key.Joints[kv.Key] = a.Select(x => Json.Num(x, 0)).ToArray(); }
                        var h = k.ContainsKey("hand") ? Json.Arr(k["hand"]) : null;
                        if (h != null && h.Count == 6) key.Hand = h.Select(x => Json.Num(x, 0)).ToArray();
                        Nudges.Add(key);
                    }
                }
            } catch (FormatException) { }
        }
        Recompute();
    }

    public void Recompute() {
        Frames = ClipMath.Apply(clip.RawT, Ops);
        T = ClipMath.Times(Frames);
    }

    public NudgeKey KeyAt(int src) { return Nudges.FirstOrDefault(k => k.Src == src); }
    public NudgeKey KeyAtOrNew(int src) {
        var k = KeyAt(src);
        if (k == null) { k = new NudgeKey { Src = src }; Nudges.Add(k); Nudges.Sort((a, b) => a.Src.CompareTo(b.Src)); }
        return k;
    }

    public double[] HandAt(int k) { return ClipMath.At(Frames, T, Nudges, k, key => key.Hand, 6); }
    public double[] JointAt(int k, string joint) { return ClipMath.At(Frames, T, Nudges, k, key => { double[] v; return key.Joints.TryGetValue(joint, out v) ? v : null; }, 3); }
    public int[] KeyedFrames() {
        var srcs = new HashSet<int>(Nudges.Select(n => n.Src));
        return Frames.Select((f, i) => srcs.Contains(f.Src) ? i : -1).Where(i => i >= 0).ToArray();
    }

    public void Save() {
        if (Path == null) return;
        var d = new Dictionary<string, object>();
        d["ops"] = Ops;
        var nl = new List<object>();
        foreach (var k in Nudges) {
            if (k.Joints.Count == 0 && k.Hand == null) continue;
            var e = new Dictionary<string, object>();
            e["src"] = (double)k.Src;
            var js = new Dictionary<string, object>();
            foreach (var kv in k.Joints) if (kv.Value.Any(x => Math.Abs(x) > 1e-6)) js[kv.Key] = kv.Value.Select(x => (object)Math.Round(x, 2)).ToList();
            e["joints"] = js;
            if (k.Hand != null) e["hand"] = k.Hand.Select(x => (object)Math.Round(x, 3)).ToList();
            nl.Add(e);
        }
        d["nudges"] = nl;
        GameLink.WriteWhole(Path, Json.Write(d));
    }

    // the ops the window adds
    public void Remove(int i) { Ops.Add(Op("remove", "i", i)); Recompute(); }
    public void Hold(int i, int n) { var o = Op("hold", "i", i); o["n"] = (double)n; Recompute(); }
    public void Speed(int from, int to, double x) { var o = Op("speed", "from", from); o["to"] = (double)to; o["x"] = x; Recompute(); }
    public bool Undo() { if (Ops.Count == 0) return false; Ops.RemoveAt(Ops.Count - 1); Recompute(); return true; }
    Dictionary<string, object> Op(string op, string k, int v) { var o = new Dictionary<string, object>(); o["op"] = op; o[k] = (double)v; Ops.Add(o); return o; }
}

// the plugin's clips_state.json, read
class ClipsState {
    public string Gun = "";
    public bool Relative;
    public readonly List<ClipInfo> Clips = new List<ClipInfo>();
    public readonly List<Dictionary<string, object>> Playing = new List<Dictionary<string, object>>();

    public static ClipsState Read(string clipsDir) {
        var s = new ClipsState();
        string text = clipsDir == null ? null : GameLink.ReadAll(System.IO.Path.Combine(clipsDir, "clips_state.json"));
        if (text == null) return s;
        try {
            var d = Json.Obj(Json.Parse(text));
            if (d == null) return s;
            s.Gun = Json.Str(d.ContainsKey("gun") ? d["gun"] : null);
            s.Relative = d.ContainsKey("relative") && Json.Bool(d["relative"]);
            if (d.ContainsKey("clips")) foreach (var c in Json.Arr(d["clips"]) ?? new List<object>()) { var o = Json.Obj(c); if (o != null) s.Clips.Add(ClipInfo.From(s.Gun, o)); }
            if (d.ContainsKey("playing")) foreach (var p in Json.Arr(d["playing"]) ?? new List<object>()) { var o = Json.Obj(p); if (o != null) s.Playing.Add(o); }
        } catch (FormatException) { }
        return s;
    }
}
