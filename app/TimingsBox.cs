// TimingsBox.cs -- TIMINGS (Visceral RE8 only): the mod's named seconds and sizes, one slider each, from the plugin's
// timings_known.json (key, default, unit, words, value in force). A change writes visceral_re8\timings.json {key: value};
// RESET takes the key out (back to the default). The plugin re-reads the file within half a second.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

class TimingsBox : FoldBox {
    const int ROW_H = 40, STEPS = 1000, SAVE_DEBOUNCE_MS = 120;
    class Item { public string Key, Unit, Words; public double Default, Max; public bool Set; public FancySlider Bar; public Label Value; public Button Reset; public Label Words_; }
    readonly TimingUi ui;
    readonly Label where;
    readonly List<Item> items = new List<Item>();
    readonly SkinPanel list;
    readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
    readonly Dictionary<string, double> values = new Dictionary<string, double>();   // what timings.json holds
    DateTime lastKnown = DateTime.MinValue;
    bool present, syncing;

    public TimingsBox(TimingUi ui, Func<string, EventHandler, int, Button> btn, bool startOpen) : base("TIMINGS (Visceral RE8)", btn, startOpen, 80) {
        this.ui = ui;
        where = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Dim, Location = new Point(230, 9), Tag = "header", Text = "the mod's named seconds, live; RESET = the built-in number" };
        Controls.Add(where);
        list = new SkinPanel { Tint = Color.FromArgb(40, 30, 34, 50) };
        list.Layout += (s, e) => LayoutList();
        Controls.Add(list);
        saveTimer.Interval = SAVE_DEBOUNCE_MS; saveTimer.Tick += (s, e) => { saveTimer.Stop(); Save(); };
        Layout += (s, e) => { list.SetBounds(6, 36, ClientSize.Width - 12, items.Count * ROW_H + 6); list.Visible = open; };
        SetOpen(open);
    }

    string Known { get { return ui.Link.VisceralDir == null ? null : Path.Combine(ui.Link.VisceralDir, "timings_known.json"); } }
    string File_ { get { return ui.Link.VisceralDir == null ? null : Path.Combine(ui.Link.VisceralDir, "timings.json"); } }

    public void FolderChanged() { present = ui.Link.HasVisceral; Visible = present; lastKnown = DateTime.MinValue; }

    public void Poll() {
        if (!present) { if (ui.Link.HasVisceral) FolderChanged(); return; }
        DateTime mt;
        try { mt = Known != null && File.Exists(Known) ? File.GetLastWriteTime(Known) : DateTime.MinValue; } catch (IOException) { return; }
        if (mt == lastKnown) return;
        lastKnown = mt;
        Rebuild();
    }

    void Rebuild() {
        string text = GameLink.ReadAll(Known);
        if (text == null) { where.Text = "the mod has not written its timings list yet (start the game with the mod once)"; return; }
        Dictionary<string, object> d;
        try { d = Json.Obj(Json.Parse(text)); } catch (FormatException) { return; }
        if (d == null || !d.ContainsKey("items")) return;
        // the plugin's current values (its reading of timings.json) seed ours
        values.Clear();
        string cur = GameLink.ReadAll(File_);
        if (cur != null) { try { var c = Json.Obj(Json.Parse(cur)); if (c != null) foreach (var kv in c) if (kv.Value is double) values[kv.Key] = (double)kv.Value; } catch (FormatException) { } }
        foreach (var it in items) { list.Controls.Remove(it.Bar); list.Controls.Remove(it.Value); list.Controls.Remove(it.Reset); list.Controls.Remove(it.Words_); }
        items.Clear();
        foreach (var o in Json.Arr(d["items"]) ?? new List<object>()) {
            var e = Json.Obj(o);
            if (e == null) continue;
            var it = new Item { Key = Json.Str(e["key"]), Unit = Json.Str(e["unit"]), Words = Json.Str(e["words"]), Default = Json.Num(e["default"], 0), Set = e.ContainsKey("set") && Json.Bool(e["set"]) };
            it.Max = it.Unit == "cm" ? 10.0 : Math.Max(1.0, Math.Ceiling(it.Default * 4 * 10) / 10);
            it.Words_ = new SkinLabel { Text = it.Words, AutoSize = true, ForeColor = TimingUi.Dim };
            it.Bar = new FancySlider { Minimum = 0, Maximum = STEPS, SmallChange = 5, LargeChange = 50, Height = 28 };
            var item = it;
            it.Bar.Scroll += (s, ev) => { if (syncing) return; values[item.Key] = Math.Round(item.Bar.Value / (double)STEPS * item.Max, 3); item.Set = true; ShowValue(item); saveTimer.Stop(); saveTimer.Start(); };
            it.Value = new SkinLabel { AutoSize = true, ForeColor = TimingUi.Ink, Font = new Font("Consolas", 8.5f) };
            it.Reset = ui.Btn("RESET", (s, ev) => { values.Remove(item.Key); item.Set = false; Sync(item); saveTimer.Stop(); saveTimer.Start(); }, 58);
            it.Reset.Height = 22;
            list.Controls.Add(it.Words_); list.Controls.Add(it.Bar); list.Controls.Add(it.Value); list.Controls.Add(it.Reset);
            items.Add(it);
            Sync(it);
        }
        where.Text = items.Count + " timings from the mod; RESET = the built-in number";
        Grow(44 + items.Count * ROW_H + 6);
        PerformLayout();
    }

    double ValueOf(Item it) { double v; return values.TryGetValue(it.Key, out v) ? v : it.Default; }
    void Sync(Item it) {
        syncing = true;
        it.Bar.Value = (int)Math.Round(ValueOf(it) / it.Max * STEPS);
        syncing = false;
        ShowValue(it);
    }
    void ShowValue(Item it) {
        double v = ValueOf(it);
        bool set = values.ContainsKey(it.Key);
        it.Value.Text = string.Format("{0:0.000} {1}{2}", v, it.Unit, set ? string.Format("  (built-in {0:0.000})", it.Default) : "");
        it.Value.ForeColor = set ? Color.FromArgb(240, 200, 120) : TimingUi.Ink;
    }

    void LayoutList() {
        int w = list.ClientSize.Width;
        for (int i = 0; i < items.Count; i++) {
            var it = items[i];
            int y = 4 + i * ROW_H;
            it.Words_.Location = new Point(6, y);
            it.Bar.SetBounds(6, y + 14, Math.Max(80, w - 6 - 250), 26);
            it.Value.Location = new Point(w - 240, y + 18);
            it.Reset.Location = new Point(w - 64, y + 14);
        }
    }

    void Save() {
        if (File_ == null) return;
        var d = new Dictionary<string, object>();
        foreach (var kv in values) d[kv.Key] = kv.Value;
        try { GameLink.WriteWhole(File_, Json.Write(d)); } catch (IOException) { saveTimer.Start(); }
    }
}
