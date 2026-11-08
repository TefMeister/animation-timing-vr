// ClipsBox.cs -- OUR CLIPS (Visceral RE8 only): the finger movements the mod plays on the docked hands, as the plugin lists them in
// clips_state.json. Pick one, PLAY it once on the hand, or HOLD it on a frame and step; ADD FRAME (a hold), REMOVE FRAME, SLOWER /
// FASTER (the whole clip), UNDO. Six HAND sliders move and turn the whole hand on the gun at this frame; NUDGE rows turn the clip's
// finger joints. Any slider change on a held frame becomes (or updates) the key on that frame's source row in
// <gun>_<event>.edit.json; the plugin re-reads it within a quarter second, so the hand moves as you drag.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

class ClipsBox : FoldBox {
    const int BASE_H = 375, SAVE_DEBOUNCE_MS = 120, STEPS = 10, PART_STEPS = 10, PART_ROW_H = 34;   // PART_STEPS: slider steps per percent
    Control pickRow, ctlRow, edRow, handBox, quickRow, keysRow;
    static readonly double[] HAND_RANGE = { 15, 15, 15, 180, 180, 180 };
    static readonly string[] HAND_WORDS = { "right / left (x)", "up / down (y)", "forward / back (z)", "tilt (rx)", "turn (ry)", "roll (rz)" };
    static readonly string[] HAND_UNIT = { "cm", "cm", "cm", "°", "°", "°" };
    readonly TimingUi ui;
    readonly Label where, frameLabel, keysNote, playingNote, clipLen;
    readonly ComboBox clipPick;
    readonly RadioButton handL, handR, handBoth;
    readonly Button hold;
    readonly CheckBox copyFwd, copyBack;   // COPY ON +1 / COPY ON −1: that button carries this frame's pose onto the frame it steps to
    string copyNote;
    readonly FancySlider[] hand = new FancySlider[6];
    readonly Label[] handValue = new Label[6];
    readonly CheckBox[] handTick = new CheckBox[6];
    readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
    public readonly NudgeRows Rows;
    // 2026-11-08 (Tefa: "only half of the solution if the drum doesn't travel frame by frame as well"): GUN PARTS, one slider per
    // part that moves with this clip (the Wolfsbane's drum): how far it has gone at this frame, 100 % = one chamber; moved on a
    // held frame it becomes a key; STRAIGHT AGAIN takes this part's keys off (back to an even 0 -> 100 % over the clip)
    class PartRow { public string Name; public FancySlider S; public Label V; public CheckBox Tick; }
    readonly FlowLayoutPanel partsPanel;
    readonly List<PartRow> partRows = new List<PartRow>();
    readonly Func<string, EventHandler, int, Button> mkBtn;
    ClipsState state = new ClipsState();
    ClipInfo clip;
    ClipEdit edit;
    int frame;
    bool holding, syncing, present;
    int scrubSeq;
    DateTime lastState = DateTime.MinValue;

    public ClipsBox(TimingUi ui, Func<string, EventHandler, int, Button> btn, bool startOpen) : base("OUR CLIPS (Visceral RE8)", btn, startOpen, BASE_H) {
        this.ui = ui;
        mkBtn = btn;
        where = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Dim, Location = new Point(250, 9), Tag = "header" };
        Controls.Add(where);
        var pick = TimingUi.Row(32);
        pick.Controls.Add(new SkinLabel { Text = "clip:", AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(0, 6, 0, 0) });
        clipPick = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = TimingUi.Panel2, ForeColor = TimingUi.Ink, FlatStyle = FlatStyle.Flat };
        clipPick.SelectedIndexChanged += (s, e) => PickClip();
        pick.Controls.Add(clipPick);
        pick.Controls.Add(new SkinLabel { Text = "  on the", AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(0, 6, 0, 0) });
        handL = new SkinRadioButton { Text = "LEFT", AutoSize = true, ForeColor = TimingUi.Ink, Padding = new Padding(4, 4, 0, 0) };
        handR = new SkinRadioButton { Text = "RIGHT", AutoSize = true, ForeColor = TimingUi.Ink, Padding = new Padding(4, 4, 0, 0) };
        handBoth = new SkinRadioButton { Text = "BOTH", AutoSize = true, ForeColor = TimingUi.Ink, Padding = new Padding(4, 4, 0, 0), Checked = true };
        pick.Controls.Add(handL); pick.Controls.Add(handR); pick.Controls.Add(handBoth);
        pick.Controls.Add(new SkinLabel { Text = "hand", AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(0, 6, 0, 0) });
        Controls.Add(pick);

        var ctl = TimingUi.Row(32);
        ctl.Controls.Add(btn("PLAY ONCE", (s, e) => PlayOnce(), 0));
        hold = btn("HOLD A FRAME", (s, e) => { if (holding) LetGo(); else Hold(frame); }, 130);
        ctl.Controls.Add(hold);
        ctl.Controls.Add(btn("◀ −1", (s, e) => StepCopy(-1), 60));
        ctl.Controls.Add(btn("+1 ▶", (s, e) => StepCopy(+1), 60));
        copyFwd = new SkinCheckBox { Text = "COPY ON +1", AutoSize = true, ForeColor = TimingUi.Ink, Padding = new Padding(2, 4, 0, 0) };
        copyBack = new SkinCheckBox { Text = "COPY ON −1", AutoSize = true, ForeColor = TimingUi.Ink, Padding = new Padding(2, 4, 0, 0) };
        ctl.Controls.Add(copyBack);
        ctl.Controls.Add(copyFwd);
        frameLabel = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Ink, Font = new Font("Consolas", 9.5f), Padding = new Padding(8, 6, 8, 0) };
        ctl.Controls.Add(frameLabel);
        Controls.Add(ctl);

        var ed = TimingUi.Row(32);
        ed.Controls.Add(new SkinLabel { Text = "FRAMES:", AutoSize = true, ForeColor = TimingUi.Dim, Font = new Font("Segoe UI Semibold", 9f), Padding = new Padding(0, 6, 4, 0) });
        ed.Controls.Add(btn("ADD ONE HERE", (s, e) => Op(x => x.Hold(frame, 1)), 0));
        ed.Controls.Add(btn("REMOVE THIS ONE", (s, e) => Op(x => x.Remove(frame)), 0));
        ed.Controls.Add(btn("SLOWER (whole clip)", (s, e) => Op(x => x.Speed(0, x.Frames.Count - 1, 0.8)), 0));
        ed.Controls.Add(btn("FASTER", (s, e) => Op(x => x.Speed(0, x.Frames.Count - 1, 1.25)), 0));
        ed.Controls.Add(btn("UNDO", (s, e) => Op(x => x.Undo()), 60));
        ed.Controls.Add(btn("RESET FRAMES", (s, e) => Op(x => { x.Ops.Clear(); x.Recompute(); }), 0));
        clipLen = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(8, 6, 0, 0) };
        ed.Controls.Add(clipLen);
        Controls.Add(ed);

        // the whole hand: two columns of three, like Hand Poser's docked-wrist box
        var handPanel = new SkinPanel { Height = 150, Tint = Color.FromArgb(40, 30, 34, 50) };
        if (TimingUi.NameSection != null) TimingUi.NameSection(handPanel, "clip:hand");
        handPanel.Controls.Add(new SkinLabel { Text = "WHOLE HAND ON THE GUN, at this frame (the gun's own axes)", AutoSize = true, ForeColor = TimingUi.Dim, Font = new Font("Segoe UI Semibold", 9f), Location = new Point(4, 2) });
        for (int i = 0; i < 6; i++) {
            int ax = i;
            string key = "clip:hand:" + i;
            handTick[i] = new SkinCheckBox { Width = 24, Height = 24, Checked = ui.IsTicked(key), Tag = key };
            handTick[i].CheckedChanged += (s, e) => ui.SetTicked(key, handTick[ax].Checked);
            hand[i] = new FancySlider { Minimum = (int)(-HAND_RANGE[i] * STEPS), Maximum = (int)(HAND_RANGE[i] * STEPS), SmallChange = 5, LargeChange = 50, Height = 28, Signed = true };
            hand[i].Scroll += (s, e) => { ShowHand(ax); if (!syncing) HandChanged(); };
            handValue[i] = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Ink, Font = new Font("Consolas", 8.5f) };
            handPanel.Controls.Add(new SkinLabel { Text = HAND_WORDS[i], AutoSize = true, ForeColor = TimingUi.Dim, Tag = "w" + i });
            handPanel.Controls.Add(handTick[i]); handPanel.Controls.Add(hand[i]); handPanel.Controls.Add(handValue[i]);
            ShowHand(i);
        }
        handPanel.Layout += (s, e) => {
            int w = handPanel.ClientSize.Width, col = (w - 12) / 2;
            for (int i = 0; i < 6; i++) {
                int x = 6 + (i % 2) * col, y = 24 + (i / 2) * 42;
                foreach (Control c in handPanel.Controls) if ((c.Tag as string) == "w" + i) c.Location = new Point(x + 30, y - 2);
                handTick[i].Location = new Point(x, y + 10);
                hand[i].Location = new Point(x + 28, y + 12);
                hand[i].Width = Math.Max(60, col - 28 - 80);
                handValue[i].Location = new Point(x + col - 76, y + 15);
            }
        };
        Controls.Add(handPanel);
        partsPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Height = 0, Visible = false };
        Controls.Add(partsPanel);

        var quick = TimingUi.Row(32);
        quick.Controls.Add(new SkinLabel { Text = "FINGERS:", AutoSize = true, ForeColor = TimingUi.Dim, Font = new Font("Segoe UI Semibold", 9f), Padding = new Padding(0, 6, 4, 0) });
        quick.Controls.Add(btn("LEFT FINGERS", (s, e) => QuickAdd("L_"), 0));
        quick.Controls.Add(btn("RIGHT FINGERS", (s, e) => QuickAdd("R_"), 0));
        quick.Controls.Add(btn("HIDE ROWS", (s, e) => Rows.Clear(), 0));
        keysNote = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(8, 6, 0, 0) };
        quick.Controls.Add(keysNote);
        Controls.Add(quick);
        Rows = new NudgeRows("clip", ui.IsTicked, ui.SetTicked);
        Rows.Changed += (joint, xyz) => JointChanged(joint, xyz);
        Rows.HeightChanged += Relayout;
        Controls.Add(Rows);
        var keys = TimingUi.Row(30);
        keys.Controls.Add(btn("DELETE THE KEY ON THIS FRAME", (s, e) => { if (edit != null) { edit.Nudges.RemoveAll(k => frame < edit.Frames.Count && k.Src == edit.Frames[frame].Src); edit.Save(); ShowFrame(); } }, 0));
        keys.Controls.Add(btn("CLEAR ALL KEYS", (s, e) => { if (edit != null) { edit.Nudges.Clear(); edit.Save(); ShowFrame(); } }, 0));
        playingNote = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Dim, Padding = new Padding(8, 6, 0, 0) };
        keys.Controls.Add(playingNote);
        Controls.Add(keys);

        saveTimer.Interval = SAVE_DEBOUNCE_MS; saveTimer.Tick += (s, e) => { saveTimer.Stop(); if (edit != null) { try { edit.Save(); } catch (IOException) { saveTimer.Start(); } } };
        pickRow = pick; ctlRow = ctl; edRow = ed; handBox = handPanel; quickRow = quick; keysRow = keys;
        Layout += (s, e) => DoLayout();
        SetOpen(open);
        Relayout();
    }

    void Relayout() { Grow(BASE_H + Rows.Height + (partRows.Count > 0 ? partsPanel.Height + 2 : 0)); PerformLayout(); }

    void DoLayout() {
        Stack(new[] { pickRow, ctlRow, edRow, handBox, partRows.Count > 0 ? partsPanel : null, quickRow, Rows, keysRow });
        if (partRows.Count == 0) partsPanel.Visible = false;
        foreach (var pr in partRows) pr.S.Width = Math.Max(120, partsPanel.ClientSize.Width - 600);
    }

    // ---- the gun parts (2026-11-08) ------------------------------------------------------------------------------------------------
    void RebuildParts() {
        var want = clip == null ? new List<string>() : clip.Parts;
        if (want.SequenceEqual(partRows.Select(r => r.Name))) return;
        partsPanel.Controls.Clear(); partRows.Clear();
        foreach (var name in want) {
            var pr = new PartRow { Name = name };
            var row = TimingUi.Row(PART_ROW_H - 2);
            string key = "clip:part:" + name;
            pr.Tick = new SkinCheckBox { Width = 24, Height = 24, Checked = ui.IsTicked(key), Tag = key };
            if (TimingUi.NameSection != null) TimingUi.NameSection(row, key);
            pr.Tick.CheckedChanged += (s, e) => ui.SetTicked(key, pr.Tick.Checked);
            row.Controls.Add(pr.Tick);
            row.Controls.Add(new SkinLabel { Text = name.ToUpperInvariant() + ": how far round (100 % = one chamber)", AutoSize = true, ForeColor = TimingUi.Ink, Padding = new Padding(2, 6, 4, 0) });
            pr.S = new FancySlider { Minimum = -25 * PART_STEPS, Maximum = 125 * PART_STEPS, SmallChange = 5, LargeChange = 50, Height = 28, Width = 300 };
            pr.S.Scroll += (s, e) => { ShowPart(pr); if (!syncing) PartChanged(pr); };
            row.Controls.Add(pr.S);
            pr.V = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Ink, Font = new Font("Consolas", 8.5f), Padding = new Padding(4, 7, 4, 0), MinimumSize = new Size(86, 0) };
            row.Controls.Add(pr.V);
            row.Controls.Add(mkBtn("STRAIGHT AGAIN", (s, e) => { if (edit == null) return; edit.PartsStraight(pr.Name); try { edit.Save(); } catch (IOException) { saveTimer.Start(); } ShowFrame(); }, 0));
            row.Width = 2000;
            partsPanel.Controls.Add(row);
            partRows.Add(pr);
        }
        partsPanel.Height = partRows.Count * PART_ROW_H;
        Relayout();
    }
    void ShowPart(PartRow pr) {
        bool keyed = edit != null && edit.PartKeyed(frame, pr.Name);
        pr.V.Text = string.Format("{0:0.0} %{1}", pr.S.Value / (double)PART_STEPS, keyed ? "  KEY" : "");
    }
    void PartChanged(PartRow pr) {
        if (edit == null || frame >= edit.Frames.Count) return;
        var key = edit.KeyAtOrNew(edit.Frames[frame].Src);
        key.Parts[pr.Name] = pr.S.Value / (100.0 * PART_STEPS);
        saveTimer.Stop(); saveTimer.Start();
        if (!holding) Hold(frame);
        ShowFrame();
    }

    public void FolderChanged() { present = ui.Link.HasVisceral; Visible = present; state = new ClipsState(); clip = null; edit = null; clipPick.Items.Clear(); }

    // ---- the plugin's state ------------------------------------------------------------------------------------------------------
    public void Poll() {
        if (!present) { if (ui.Link.HasVisceral) FolderChanged(); return; }
        string f = ui.Link.ClipsDir == null ? null : Path.Combine(ui.Link.ClipsDir, "clips_state.json");
        DateTime mt;
        try { mt = File.Exists(f) ? File.GetLastWriteTime(f) : DateTime.MinValue; } catch (IOException) { return; }
        if (mt == lastState) return;
        lastState = mt;
        bool alive = GameLink.Fresh(f, 3.0);
        var st = ClipsState.Read(ui.Link.ClipsDir);
        if (!alive) { where.Text = "the mod is not running (no clips_state.json lately)"; return; }
        state = st;
        where.Text = st.Gun == "" ? "no gun in hand" : "gun " + st.Gun + ": " + st.Clips.Count + " clip(s)" + (st.Relative ? " · relative mode" : "");
        var events = st.Clips.Select(c => c.Event).ToList();
        var shown = clipPick.Items.Cast<string>().ToList();
        if (!events.SequenceEqual(shown)) {
            string keep = clipPick.SelectedItem as string;
            clipPick.BeginUpdate(); clipPick.Items.Clear();
            foreach (var e in events) clipPick.Items.Add(e);
            clipPick.EndUpdate();
            if (keep != null && events.Contains(keep)) clipPick.SelectedItem = keep; else if (events.Count > 0) clipPick.SelectedIndex = 0;
        } else if (clip != null) {   // the same clip, maybe re-edited by the plugin's reading of our file: keep our edit model, refresh its times
            var fresh = st.Clips.FirstOrDefault(c => c.Event == clip.Event);
            if (fresh != null) { clip = fresh; RebuildParts(); if (edit != null && clip.RawT.Count > 0) edit.Recompute(); ShowFrame(); }
        }
        var pl = st.Playing.Where(p => clip != null && Json.Str(p["key"]) == clip.Key).ToList();
        playingNote.Text = pl.Count == 0 ? "" : string.Join("  ", pl.Select(p => string.Format("{0} hand: frame {1:0.0}{2}", Json.Str(p["hand"]), Json.Num(p["frame"], 0), Json.Bool(p["held"]) ? " HELD" : "")).ToArray());
    }

    void PickClip() {
        string ev = clipPick.SelectedItem as string;
        var c = state.Clips.FirstOrDefault(x => x.Event == ev);
        if (c == null) return;
        if (holding) LetGo();
        clip = c;
        edit = new ClipEdit(clip, ui.Link.ClipsDir);
        frame = 0;
        copyNote = null;
        handL.Enabled = c.Hands != "right"; handR.Enabled = c.Hands != "left"; handBoth.Enabled = c.Hands == "both";
        if (c.Hands == "left") handL.Checked = true; else if (c.Hands == "right") handR.Checked = true; else handBoth.Checked = true;
        Rows.Clear();
        RebuildParts();
        ShowFrame();
    }

    int Hands() { return handL.Checked ? 1 : handR.Checked ? 2 : 3; }

    void Scrub(bool holdIt, int f) {
        if (clip == null || ui.Link.ClipsDir == null) return;
        var d = new Dictionary<string, object>();
        d["seq"] = (double)(++scrubSeq); d["gun"] = clip.Gun; d["event"] = clip.Event; d["hands"] = (double)Hands(); d["frame"] = (double)f; d["hold"] = holdIt;
        try { GameLink.WriteWhole(Path.Combine(ui.Link.ClipsDir, "scrub.json"), Json.Write(d)); } catch (IOException) { }
    }
    void PlayOnce() { if (holding) LetGo(); Scrub(false, -1); }
    void Hold(int f) {
        if (edit == null) return;
        frame = Math.Max(0, Math.Min(edit.Frames.Count - 1, f));
        holding = true;
        hold.Text = "LET GO"; hold.BackColor = Color.FromArgb(150, 90, 40);
        Scrub(true, frame);
        ShowFrame();
    }
    // the −1 / +1 buttons only (never a typed frame or a pick): with that direction's box ticked, copy the pose one step first
    void StepCopy(int dir) {
        copyNote = null;
        bool on = dir > 0 ? copyFwd.Checked : copyBack.Checked;
        if (on && edit != null && frame + dir >= 0 && frame + dir < edit.Frames.Count) {
            copyNote = edit.CopyStep(frame, dir, Rows.Joints);
            saveTimer.Stop();
            try { edit.Save(); } catch (IOException) { saveTimer.Start(); }
        }
        Hold(frame + dir);
    }
    public void LetGo() {
        if (!holding) return;
        holding = false;
        hold.Text = "HOLD A FRAME"; hold.BackColor = Color.FromArgb(62, 67, 86);
        Scrub(false, 0);
    }

    void Op(Action<ClipEdit> what) {
        if (edit == null) return;
        what(edit);
        try { edit.Save(); } catch (IOException) { saveTimer.Start(); }
        frame = Math.Max(0, Math.Min(edit.Frames.Count - 1, frame));
        if (holding) Scrub(true, frame);
        ShowFrame();
    }

    // the sliders show this frame's values (a key's own, or the interpolated ones between keys)
    void ShowFrame() {
        if (edit == null || clip == null) { frameLabel.Text = ""; return; }
        int n = edit.Frames.Count;
        if (n == 0) { frameLabel.Text = "(no frames)"; return; }
        frame = Math.Max(0, Math.Min(n - 1, frame));
        var fr = edit.Frames[frame];
        bool keyed = edit.KeyAt(fr.Src) != null;
        frameLabel.Text = string.Format("frame {0}/{1}  {2:0.000} s  row {3}{4}", frame, n - 1, edit.T[frame], fr.Src, keyed ? "  KEY" : "");
        clipLen.Text = string.Format("{0} rows in the file, {1} s long", clip.CsvFrames, edit.T.Length > 0 ? (edit.T[n - 1] + fr.Dt).ToString("0.00") : "?");
        syncing = true;
        var h = edit.HandAt(frame);
        for (int i = 0; i < 6; i++) { hand[i].Value = (int)Math.Round(h[i] * STEPS); ShowHand(i); }
        foreach (var j in Rows.Joints.ToList()) Rows.Set(j, edit.JointAt(frame, j));
        foreach (var pr in partRows) { pr.S.Value = (int)Math.Round(edit.PartAt(frame, pr.Name) * 100 * PART_STEPS); ShowPart(pr); }
        syncing = false;
        var kf = edit.KeyedFrames();
        keysNote.Text = (copyNote != null ? copyNote + "  ·  " : "") + (kf.Length == 0 ? "no keys yet: move a slider on a held frame to make one" : "keys on frames: " + string.Join(", ", kf.Select(x => x.ToString()).ToArray()) + "  (" + edit.Ops.Count + " frame edit(s))");
    }
    void ShowHand(int i) { handValue[i].Text = string.Format("{0:+0.0;-0.0;0.0} {1}", hand[i].Value / (double)STEPS, HAND_UNIT[i]); }

    void HandChanged() {
        if (edit == null || frame >= edit.Frames.Count) return;
        var key = edit.KeyAtOrNew(edit.Frames[frame].Src);
        key.Hand = hand.Select(s => s.Value / (double)STEPS).ToArray();
        saveTimer.Stop(); saveTimer.Start();
        if (!holding) Hold(frame);
        ShowFrame();
    }
    void JointChanged(string joint, double[] xyz) {
        if (edit == null || frame >= edit.Frames.Count) return;
        var key = edit.KeyAtOrNew(edit.Frames[frame].Src);
        key.Joints[joint] = xyz;
        saveTimer.Stop(); saveTimer.Start();
        if (!holding) Hold(frame);
        ShowFrame();
    }
    void QuickAdd(string side) {
        if (clip == null) return;
        foreach (var j in clip.Joints.Where(x => x.StartsWith(side))) if (!Rows.Has(j)) Rows.Add(j);
        ShowFrame();
    }

    public bool Step(double n, HashSet<string> ticked) {
        bool any = false;
        for (int i = 0; i < 6; i++) {
            if (!ticked.Contains("clip:hand:" + i)) continue;
            hand[i].Value += (int)Math.Round(n * (i < 3 ? 0.5 : 1.0) * STEPS);   // positions half a centimetre a press, angles a degree
            ShowHand(i);
            any = true;
        }
        if (any) HandChanged();
        foreach (var pr in partRows) {   // a gun part: a percent a press
            if (!ticked.Contains("clip:part:" + pr.Name)) continue;
            pr.S.Value += (int)Math.Round(n * PART_STEPS);
            ShowPart(pr); PartChanged(pr);
            any = true;
        }
        return Rows.Step(n, ticked) | any;
    }
}
