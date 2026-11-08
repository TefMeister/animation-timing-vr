// NudgeRows.cs -- one row per joint: its name, three sliders (X / Y / Z, a turn in degrees ON TOP of the animation), the live
// numbers, a tick box per slider for the controller buttons (Nudge.cs), and a cross to take the row away. Used twice: for the
// game's own animations (PlayingBox) and for our clips (ClipsBox); the owner decides what a change means.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

class NudgeRows : SkinPanel {
    public const double MAX_DEG = 90;
    const int STEPS = 10;   // tenths of a degree
    static readonly Color Ink = Color.FromArgb(232, 232, 236), Dim = Color.FromArgb(150, 155, 170);
    public static readonly string[] AXES = { "X", "Y", "Z" };

    class Row {
        public string Joint;
        public SkinPanel Panel;
        public FancySlider[] Bars = new FancySlider[3];
        public Label[] Values = new Label[3];
        public CheckBox[] Ticks = new CheckBox[3];
    }
    readonly List<Row> rows = new List<Row>();
    readonly string tag;                      // "anim" / "clip": the tick keys' prefix
    readonly Func<string, bool> isTicked;
    readonly Action<string, bool> setTicked;
    bool syncing;

    // a slider moved (by hand or by a controller step): the joint and its three degrees
    public event Action<string, double[]> Changed;
    public event Action<string> Removed;
    public event Action HeightChanged;

    public NudgeRows(string tag, Func<string, bool> isTicked, Action<string, bool> setTicked) {
        this.tag = tag; this.isTicked = isTicked; this.setTicked = setTicked;
        Height = 0; Tint = Color.FromArgb(60, 20, 24, 36); Padding = new Padding(0);
        Layout += (s, e) => { for (int i = 0; i < rows.Count; i++) rows[i].Panel.SetBounds(0, i * 34, ClientSize.Width, 34); };
    }

    public IEnumerable<string> Joints { get { return rows.Select(r => r.Joint); } }
    public bool Has(string joint) { return rows.Any(r => r.Joint == joint); }
    public int Count { get { return rows.Count; } }

    public double[] Get(string joint) {
        var r = rows.FirstOrDefault(x => x.Joint == joint);
        return r == null ? null : Enumerable.Range(0, 3).Select(i => HandAxes.Sign(r.Joint, i) * r.Bars[i].Value / (double)STEPS).ToArray();
    }
    public Dictionary<string, double[]> All() {
        var d = new Dictionary<string, double[]>();
        foreach (var r in rows) d[r.Joint] = Get(r.Joint);
        return d;
    }

    public void Set(string joint, double[] xyz) {   // by code: no Changed event
        var r = rows.FirstOrDefault(x => x.Joint == joint);
        if (r == null) return;
        syncing = true;
        for (int i = 0; i < 3; i++) { r.Bars[i].Value = (int)Math.Round(HandAxes.Sign(joint, i) * (xyz == null ? 0 : xyz[i]) * STEPS); ShowValue(r, i); }
        syncing = false;
    }

    public void Add(string joint) {
        if (Has(joint)) return;
        var r = new Row { Joint = joint };
        r.Panel = new SkinPanel { Height = 34, Tint = Color.FromArgb(40, 30, 34, 50) };
        if (TimingUi.NameSection != null) TimingUi.NameSection(r.Panel, "rows:" + tag + ":" + joint);
        var cross = new SkinButton { Text = "✕", Width = 26, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 50, 56), ForeColor = Ink, Location = new Point(4, 5) };
        cross.FlatAppearance.BorderSize = 0;
        cross.Click += (s, e) => Remove(joint);
        r.Panel.Controls.Add(cross);
        r.Panel.Controls.Add(new SkinLabel { Text = joint, AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 9.5f), Location = new Point(34, 8), Tag = "name" });
        for (int i = 0; i < 3; i++) {
            int ax = i;
            string key = "nudge:" + tag + ":" + joint + ":" + AXES[i];
            r.Ticks[i] = new SkinCheckBox { Width = 24, Height = 24, Checked = isTicked(key), Tag = key };
            r.Ticks[i].CheckedChanged += (s, e) => setTicked(key, r.Ticks[ax].Checked);
            r.Bars[i] = new FancySlider { Minimum = (int)(-MAX_DEG * STEPS), Maximum = (int)(MAX_DEG * STEPS), SmallChange = 5, LargeChange = 50, Height = 28, Signed = true };
            {   // 2026-11-08: the ends say what this axis does on this joint, the number shown turned round where + is on the wrong end (HandAxes.cs)
                string lw, rw; bool fl;
                if (HandAxes.Ends(joint, i, out lw, out rw, out fl)) { r.Bars[i].LeftText = lw; r.Bars[i].RightText = rw; }
            }
            r.Bars[i].Scroll += (s, e) => { ShowValue(r, ax); if (!syncing && Changed != null) Changed(joint, Get(joint)); };
            r.Values[i] = new SkinLabel { AutoSize = true, ForeColor = Dim, Font = new Font("Consolas", 8.5f) };
            r.Panel.Controls.Add(new SkinLabel { Text = AXES[i], AutoSize = true, ForeColor = Dim, Tag = "ax" + i });
            r.Panel.Controls.Add(r.Ticks[i]); r.Panel.Controls.Add(r.Bars[i]); r.Panel.Controls.Add(r.Values[i]);
            ShowValue(r, i);
        }
        r.Panel.Layout += (s, e) => LayoutRow(r);
        rows.Add(r);
        Controls.Add(r.Panel);
        Height = rows.Count * 34;
        PerformLayout();
        if (HeightChanged != null) HeightChanged();
    }

    public void Remove(string joint) {
        var r = rows.FirstOrDefault(x => x.Joint == joint);
        if (r == null) return;
        rows.Remove(r);
        Controls.Remove(r.Panel);
        r.Panel.Dispose();
        Height = rows.Count * 34;
        PerformLayout();
        if (HeightChanged != null) HeightChanged();
        if (Removed != null) Removed(joint);
    }

    public void Clear() { foreach (var j in Joints.ToList()) Remove(j); }

    void ShowValue(Row r, int i) { r.Values[i].Text = string.Format("{0:+0.0;-0.0;0.0}", r.Bars[i].Value / (double)STEPS); }

    // the name on the left (fixed width), then three equal columns: axis letter, tick, slider, number
    void LayoutRow(Row r) {
        int w = r.Panel.ClientSize.Width, nameW = 150, x0 = 34 + nameW, col = Math.Max(80, (w - x0 - 6) / 3);
        for (int i = 0; i < 3; i++) {
            int x = x0 + i * col;
            foreach (Control c in r.Panel.Controls) if ((c.Tag as string) == "ax" + i) c.Location = new Point(x, 8);
            r.Ticks[i].Location = new Point(x + 14, 5);
            r.Bars[i].Location = new Point(x + 38, 3);
            r.Bars[i].Width = Math.Max(40, col - 38 - 48);
            r.Values[i].Location = new Point(x + col - 46, 9);
        }
    }

    // the controller buttons: ticked sliders step by n degrees (Nudge.cs); true when any moved
    public bool Step(double n, HashSet<string> ticked) {
        bool any = false;
        foreach (var r in rows)
            for (int i = 0; i < 3; i++) {
                if (!ticked.Contains("nudge:" + tag + ":" + r.Joint + ":" + AXES[i])) continue;
                r.Bars[i].Value += (int)Math.Round(n * STEPS);
                ShowValue(r, i);
                any = true;
                if (Changed != null) Changed(r.Joint, Get(r.Joint));
            }
        return any;
    }

    // which joints are a hand's: Village (L_IndexF1, L_Thumb3, L_Hand) and RE2 / RE3 (l_hand_index_0, l_arm_wrist)
    public static bool IsFinger(string name, string side) {
        string n = name.ToLowerInvariant();
        if (!n.StartsWith(side + "_")) return false;
        return n.Contains("index") || n.Contains("middle") || n.Contains("ring") || n.Contains("pinky") || n.Contains("little") || n.Contains("thumb");
    }
    public static bool IsWrist(string name, string side) {
        string n = name.ToLowerInvariant();
        return n == side + "_hand" || n == side + "_arm_wrist";
    }
}
