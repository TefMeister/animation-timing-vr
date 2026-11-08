// PlayingBox.cs -- PLAYING NOW: the animations playing on the player, from the in-game script's state.json. FREEZE / PLAY, a frame
// back and forth, slow motion, a frame to jump the chosen layer to; then NUDGE rows (a turn on top of the animation) with quick-add
// buttons for the fingers and wrists and a filter over the whole skeleton, and KEEP ON THIS FRAME, which writes the rows' deltas as
// a key on the chosen layer's current frame into nudges\<game>\<src>_<bank>_<motion>.json (the script plays keys back by itself).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

class PlayingBox : FoldBox {
    const int BASE_H = 262;
    readonly TimingUi ui;
    readonly ListBox layers;
    readonly Button freeze, keep, delKey, clearKeys, jointsBtn;
    readonly FancySlider slow;
    readonly Label slowValue, keysNote, where;
    readonly TextBox frameBox, filter;
    readonly ListBox allJoints;
    readonly FlowLayoutPanel filterRow, ctlRow, quickRow, keysRow;
    public readonly NudgeRows Rows;
    List<Dictionary<string, object>> layerList = new List<Dictionary<string, object>>();
    List<string> skeleton = new List<string>();
    string game = "";
    bool jointsOpen;

    public PlayingBox(TimingUi ui, Func<string, EventHandler, int, Button> btn, bool startOpen) : base("PLAYING NOW", btn, startOpen, BASE_H) {
        this.ui = ui;
        where = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Dim, Location = new Point(200, 9), Tag = "header", Text = "the animations on the player, one line each; pick one to jump its frame" };
        Controls.Add(where);
        layers = new ListBox { BackColor = TimingUi.Panel2, ForeColor = TimingUi.Ink, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f), Height = 76, IntegralHeight = false };
        Controls.Add(layers);
        var ctl = ctlRow = TimingUi.Row(32);
        freeze = btn("FREEZE", (s, e) => { ui.Frozen = !ui.Frozen; ui.QueueSend(); ShowFreeze(); }, 90);
        ctl.Controls.Add(freeze);
        ctl.Controls.Add(btn("◀ −1", (s, e) => { if (!ui.Frozen) { ui.Frozen = true; ShowFreeze(); } ui.Step(-1); }, 60));
        ctl.Controls.Add(btn("+1 ▶", (s, e) => { if (!ui.Frozen) { ui.Frozen = true; ShowFreeze(); } ui.Step(+1); }, 60));
        ctl.Controls.Add(new SkinLabel { Text = "   slow motion", AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(0, 6, 0, 0) });
        slow = new FancySlider { Minimum = 5, Maximum = 100, Value = 100, SmallChange = 5, LargeChange = 20, Width = 220, Height = 28 };
        slow.Scroll += (s, e) => { ui.Speed = slow.Value / 100.0; slowValue.Text = slow.Value + " %"; ui.QueueSend(); };
        ctl.Controls.Add(slow);
        slowValue = new SkinLabel { Text = "100 %", AutoSize = true, ForeColor = TimingUi.Ink, Padding = new Padding(2, 6, 8, 0) };
        ctl.Controls.Add(slowValue);
        ctl.Controls.Add(new SkinLabel { Text = "frame", AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(0, 6, 0, 0) });
        frameBox = new TextBox { Width = 56, BackColor = TimingUi.Panel2, ForeColor = TimingUi.Ink, BorderStyle = BorderStyle.FixedSingle };
        ctl.Controls.Add(frameBox);
        ctl.Controls.Add(btn("GO", (s, e) => JumpFrame(), 44));
        Controls.Add(ctl);

        var quick = quickRow = TimingUi.Row(32);
        quick.Controls.Add(new SkinLabel { Text = "NUDGE:", AutoSize = true, ForeColor = TimingUi.Dim, Font = new Font("Segoe UI Semibold", 9f), Padding = new Padding(0, 6, 4, 0) });
        quick.Controls.Add(btn("LEFT FINGERS", (s, e) => QuickAdd(n => NudgeRows.IsFinger(n, "l")), 0));
        quick.Controls.Add(btn("RIGHT FINGERS", (s, e) => QuickAdd(n => NudgeRows.IsFinger(n, "r")), 0));
        quick.Controls.Add(btn("LEFT WRIST", (s, e) => QuickAdd(n => NudgeRows.IsWrist(n, "l")), 0));
        quick.Controls.Add(btn("RIGHT WRIST", (s, e) => QuickAdd(n => NudgeRows.IsWrist(n, "r")), 0));
        jointsBtn = btn("ANY JOINT  ▼", (s, e) => ShowJoints(!jointsOpen), 0);
        quick.Controls.Add(jointsBtn);
        quick.Controls.Add(btn("ALL TO ZERO", (s, e) => { foreach (var j in Rows.Joints.ToList()) { Rows.Set(j, null); ui.NudgeLive[j] = new double[3]; } ui.QueueSend(); }, 0));
        Controls.Add(quick);
        filterRow = TimingUi.Row(120);
        filterRow.Visible = false;
        filter = new TextBox { Width = 200, BackColor = TimingUi.Panel2, ForeColor = TimingUi.Ink, BorderStyle = BorderStyle.FixedSingle };
        filter.TextChanged += (s, e) => FillJoints();
        allJoints = new ListBox { Width = 320, Height = 112, BackColor = TimingUi.Panel2, ForeColor = TimingUi.Ink, BorderStyle = BorderStyle.None, IntegralHeight = false };
        allJoints.DoubleClick += (s, e) => AddSelected();
        filterRow.Controls.Add(new SkinLabel { Text = "type part of a name:", AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(0, 6, 0, 0) });
        filterRow.Controls.Add(filter); filterRow.Controls.Add(allJoints); filterRow.Controls.Add(btn("ADD", (s, e) => AddSelected(), 60));
        Controls.Add(filterRow);

        Rows = new NudgeRows("anim", ui.IsTicked, ui.SetTicked);
        Rows.Changed += (joint, xyz) => { ui.NudgeLive[joint] = xyz; ui.QueueSend(); };
        Rows.Removed += joint => { ui.NudgeLive.Remove(joint); ui.WantRead.Remove(joint); ui.QueueSend(); Relayout(); };
        Rows.HeightChanged += Relayout;
        Controls.Add(Rows);

        var keys = keysRow = TimingUi.Row(32);
        keep = btn("KEEP ON THIS FRAME", (s, e) => KeepKey(), 0);
        keys.Controls.Add(keep);
        delKey = btn("DELETE THE KEY ON THIS FRAME", (s, e) => DeleteKey(), 0);
        keys.Controls.Add(delKey);
        clearKeys = btn("CLEAR THIS ANIMATION'S KEYS", (s, e) => ClearKeys(), 0);
        keys.Controls.Add(clearKeys);
        keysNote = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(6, 6, 0, 0) };
        keys.Controls.Add(keysNote);
        Controls.Add(keys);

        layers.SelectedIndexChanged += (s, e) => ShowKeys();
        Layout += (s, e) => DoLayout();
        ShowFreeze();
        SetOpen(open);
        Relayout();
    }

    void ShowFreeze() { freeze.Text = ui.Frozen ? "PLAY ▶" : "FREEZE ⏸"; freeze.BackColor = ui.Frozen ? Color.FromArgb(150, 90, 40) : Color.FromArgb(62, 67, 86); }

    void Relayout() { Grow(BASE_H + Rows.Height + (jointsOpen ? 120 : 0)); PerformLayout(); }

    // stacked by hand: header, the layer list, the controls, the quick-add row, the filter (when open), the rows, the key buttons
    void DoLayout() {
        Stack(new Control[] { layers, ctlRow, quickRow, jointsOpen ? filterRow : null, Rows, keysRow });
        if (!jointsOpen) filterRow.Visible = false;
    }

    void ShowJoints(bool on) { jointsOpen = on; jointsBtn.Text = on ? "ANY JOINT  ▲" : "ANY JOINT  ▼"; filterRow.Visible = on && open; if (on) FillJoints(); Relayout(); }
    void FillJoints() {
        allJoints.BeginUpdate(); allJoints.Items.Clear();
        string f = filter.Text.Trim().ToLowerInvariant();
        foreach (var j in skeleton) if (f == "" || j.ToLowerInvariant().Contains(f)) allJoints.Items.Add(j);
        allJoints.EndUpdate();
    }
    void AddSelected() { if (allJoints.SelectedItem != null) AddJoint((string)allJoints.SelectedItem); }
    void QuickAdd(Func<string, bool> pick) { foreach (var j in skeleton.Where(pick)) AddJoint(j); }
    void AddJoint(string j) {
        if (Rows.Has(j)) return;
        Rows.Add(j);
        if (!ui.WantRead.Contains(j)) ui.WantRead.Add(j);
        ui.NudgeLive[j] = new double[3];
        ui.QueueSend();
    }

    void JumpFrame() {
        var L = Selected();
        double f;
        if (L == null || !double.TryParse(frameBox.Text, out f)) return;
        if (!ui.Frozen) { ui.Frozen = true; ShowFreeze(); }
        ui.SetFrame(Json.Str(L["src"]), (int)Json.Num(L["i"], 0), f);
    }

    Dictionary<string, object> Selected() {
        int i = layers.SelectedIndex;
        return i >= 0 && i < layerList.Count ? layerList[i] : (layerList.Count > 0 ? layerList[0] : null);
    }

    // ---- the state from the script -------------------------------------------------------------------------------------------
    public void Update(Dictionary<string, object> st) {
        if (st == null) { if (layers.Items.Count == 0 || (string)layers.Items[0] != "(the in-game script is not running)") { layers.Items.Clear(); layers.Items.Add("(the in-game script is not running)"); } return; }
        game = Json.Str(st.ContainsKey("game") ? st["game"] : null);
        bool frozen = st.ContainsKey("frozen") && Json.Bool(st["frozen"]);
        if (frozen != ui.Frozen && !ui.Frozen) { /* the script's own menu button froze it */ ui.Frozen = frozen; ShowFreeze(); }
        var newList = new List<Dictionary<string, object>>();
        if (st.ContainsKey("layers")) foreach (var l in Json.Arr(st["layers"]) ?? new List<object>()) { var o = Json.Obj(l); if (o != null) newList.Add(o); }
        int sel = layers.SelectedIndex;
        layerList = newList;
        layers.BeginUpdate();
        layers.Items.Clear();
        foreach (var L in layerList) {
            string name = Json.Str(L.ContainsKey("name") ? L["name"] : null);
            layers.Items.Add(string.Format("{0,-14} {1}  bank {2,-5} motion {3,-6} frame {4,6:0.0} / {5,-6:0}  x{6:0.00}  {7}",
                Json.Str(L["src"]), (int)Json.Num(L["i"], 0), (int)Json.Num(L["bank"], -1), (int)Json.Num(L["motion"], -1), Json.Num(L["frame"], -1), Json.Num(L["end"], -1), Json.Num(L["speed"], -1), name));
        }
        if (sel >= 0 && sel < layers.Items.Count) layers.SelectedIndex = sel;
        layers.EndUpdate();
        var sk = new List<string>();
        if (st.ContainsKey("joints")) foreach (var j in Json.Arr(st["joints"]) ?? new List<object>()) { var a = Json.Arr(j); if (a != null && a.Count >= 1) sk.Add(Json.Str(a[0])); }
        if (sk.Count != skeleton.Count) { skeleton = sk; if (jointsOpen) FillJoints(); }
        ShowKeys();
    }

    // ---- the keys (nudges\<game>\<src>_<bank>_<motion>.json) -----------------------------------------------------------------------
    string KeyFile(Dictionary<string, object> L) {
        string dir = ui.Link.NudgesDir(game);
        return dir == null || L == null ? null : Path.Combine(dir, string.Format("{0}_{1}_{2}.json", Json.Str(L["src"]), (int)Json.Num(L["bank"], -1), (int)Json.Num(L["motion"], -1)));
    }
    List<Dictionary<string, object>> LoadKeys(string file) {
        var out_ = new List<Dictionary<string, object>>();
        string text = GameLink.ReadAll(file);
        if (text == null) return out_;
        try {
            var d = Json.Obj(Json.Parse(text));
            if (d != null && d.ContainsKey("keys")) foreach (var k in Json.Arr(d["keys"]) ?? new List<object>()) { var o = Json.Obj(k); if (o != null) out_.Add(o); }
        } catch (FormatException) { }
        return out_;
    }
    void SaveKeys(string file, List<Dictionary<string, object>> keys) {
        keys.Sort((a, b) => Json.Num(a["frame"], 0).CompareTo(Json.Num(b["frame"], 0)));
        var d = new Dictionary<string, object>(); d["keys"] = keys.Cast<object>().ToList();
        if (keys.Count == 0) { try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } }
        else GameLink.WriteWhole(file, Json.Write(d));
        ui.NudgeFilesChanged();
        ShowKeys();
    }
    int FrameOf(Dictionary<string, object> L) { return (int)Math.Round(Json.Num(L["frame"], 0)); }

    void KeepKey() {
        var L = Selected();
        if (L == null || Rows.Count == 0) { keysNote.Text = "pick a layer and add a joint first"; return; }
        if (!Rows.All().Values.Any(v => v.Any(x => Math.Abs(x) > 1e-6))) { keysNote.Text = "every slider is at zero: move one first, then keep"; return; }
        string file = KeyFile(L);
        var keys = LoadKeys(file);
        int frame = FrameOf(L);
        var key = keys.FirstOrDefault(k => (int)Math.Round(Json.Num(k["frame"], -1)) == frame);
        if (key == null) { key = new Dictionary<string, object>(); key["frame"] = (double)frame; key["joints"] = new Dictionary<string, object>(); keys.Add(key); }
        var js = Json.Obj(key["joints"]) ?? new Dictionary<string, object>();
        // the script plays saved key + live row, so a row's value is ADDED to what the key already holds for that joint (2026-11-08)
        foreach (var kv in Rows.All()) {
            if (!kv.Value.Any(x => Math.Abs(x) > 1e-6)) continue;
            var old = js.ContainsKey(kv.Key) ? Json.Arr(js[kv.Key]) : null;
            var sum = new double[3];
            for (int i = 0; i < 3; i++) sum[i] = Math.Round(kv.Value[i] + (old != null && old.Count == 3 ? Json.Num(old[i], 0) : 0), 2);
            if (sum.Any(x => Math.Abs(x) > 1e-6)) js[kv.Key] = sum.Select(x => (object)x).ToList(); else js.Remove(kv.Key);
        }
        key["joints"] = js;
        SaveKeys(file, keys);
        // the key now carries the deltas: the live ones go to zero so nothing is doubled (the script plays the key)
        foreach (var j in Rows.Joints.ToList()) { Rows.Set(j, null); ui.NudgeLive[j] = new double[3]; }
        ui.QueueSend();
        keysNote.Text = "kept on frame " + frame;
    }
    void DeleteKey() {
        var L = Selected();
        if (L == null) return;
        string file = KeyFile(L);
        var keys = LoadKeys(file);
        int frame = FrameOf(L);
        keys.RemoveAll(k => (int)Math.Round(Json.Num(k["frame"], -1)) == frame);
        SaveKeys(file, keys);
    }
    void ClearKeys() {
        var L = Selected();
        if (L == null) return;
        SaveKeys(KeyFile(L), new List<Dictionary<string, object>>());
    }
    void ShowKeys() {
        var L = Selected();
        if (L == null) { keysNote.Text = ""; return; }
        var keys = LoadKeys(KeyFile(L));
        keysNote.Text = keys.Count == 0 ? "no keys on this animation yet" : "keys on frames: " + string.Join(", ", keys.Select(k => ((int)Math.Round(Json.Num(k["frame"], 0))).ToString()).ToArray());
    }
}
