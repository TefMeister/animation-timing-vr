// TimingUi.cs -- the Animation & Timing window. Top: game and character, the game folder and script buttons. Then three boxes,
// each folding away: PLAYING NOW (the game's animations: freeze, step, slow motion, nudges keyed per frame), OUR CLIPS (Visceral
// RE8's finger movements: hold a frame, add / remove / retime frames, nudge the fingers and move the whole hand, keyed per
// frame) and TIMINGS (the mod's named seconds, live). Every change goes straight to the game's data folder; nothing to save.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

class TimingUi : Form {
    const int POLL_MS = 100, COMMAND_DEBOUNCE_MS = 30, FIND_GAME_EVERY_MS = 5000;
    public static readonly Color Back = Color.FromArgb(28, 30, 38), Panel2 = Color.FromArgb(38, 41, 52), Ink = Color.FromArgb(232, 232, 236);
    public static readonly Color Dim = Color.FromArgb(150, 155, 170), Good = Color.FromArgb(80, 190, 110), Bad = Color.FromArgb(220, 110, 90);

    readonly Settings settings = new Settings();
    public readonly GameLink Link = new GameLink();
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer(), sendTimer = new System.Windows.Forms.Timer(), nudgeTimer = new System.Windows.Forms.Timer();
    readonly Nudge nudge = new Nudge();
    readonly HashSet<string> ticked = new HashSet<string>();
    DateTime lastFind = DateTime.MinValue;

    Label status, scriptNote, nudgeNote;
    Button scriptButton;
    PlayingBox playing;
    ClipsBox clips;
    TimingsBox timings;
    SkinPanel body;

    // the command to the in-game script (anim_timing.lua)
    int seq;
    public bool Frozen;
    public double Speed = 1.0;
    int stepPending;
    Dictionary<string, object> setFrame;
    public readonly Dictionary<string, double[]> NudgeLive = new Dictionary<string, double[]>();
    public readonly List<string> WantRead = new List<string>();
    int reloadNudges;

    public TimingUi(string gameArg) {
        Text = "Animation & Timing";
        BackColor = Back; ForeColor = Ink;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(920, 1380);
        MinimumSize = new Size(640, 500);
        StartPosition = FormStartPosition.CenterScreen;
        string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "anim_timing.ico");
        try { Icon = new Icon(ico); } catch (Exception) { }
        DoubleBuffered = true;
        Backdrop.Init(this, ico, Back);

        BuildTop();
        body = new SkinPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6, 4, 6, 4) };
        Controls.Add(body);
        body.BringToFront();
        timings = new TimingsBox(this, Btn, settings.Get("open_timings", "1") == "1");
        clips = new ClipsBox(this, Btn, settings.Get("open_clips", "1") == "1");
        playing = new PlayingBox(this, Btn, settings.Get("open_playing", "1") == "1");
        // docked Top: the last added sits highest
        body.Controls.Add(timings); body.Controls.Add(clips); body.Controls.Add(playing);
        playing.FoldChanged += on => settings.Set("open_playing", on ? "1" : "0");
        clips.FoldChanged += on => settings.Set("open_clips", on ? "1" : "0");
        timings.FoldChanged += on => settings.Set("open_timings", on ? "1" : "0");

        string folder = gameArg ?? settings.Get("game", null);
        if (!GameLink.LooksLikeGame(folder)) folder = GameLink.FindRunningGame();
        if (GameLink.LooksLikeGame(folder)) UseFolder(folder, gameArg == null);

        poll.Interval = POLL_MS; poll.Tick += (s, e) => Poll(); poll.Start();
        nudgeTimer.Interval = 15; nudgeTimer.Tick += (s, e) => NudgeTick(); nudgeTimer.Start();
        sendTimer.Interval = COMMAND_DEBOUNCE_MS; sendTimer.Tick += (s, e) => { sendTimer.Stop(); Send(); };
        UpdateStatus();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { Backdrop.PaintUnder(this, e.Graphics, e.ClipRectangle); }

    // ---- building ---------------------------------------------------------------------------------------------------------
    public Button Btn(string text, EventHandler onClick, int width) {
        var b = new SkinButton { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(62, 67, 86), ForeColor = Ink, Height = 26, AutoSize = width == 0, Margin = new Padding(3) };
        b.FlatAppearance.BorderSize = 0;
        if (width > 0) b.Width = width;
        b.Click += onClick;
        return b;
    }
    public static FlowLayoutPanel Row(int height) {
        return new FlowLayoutPanel { Height = height, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(4, 2, 4, 0) };
    }

    void BuildTop() {
        var top = new SkinPanel { Dock = DockStyle.Top, Height = 92, Tint = Color.FromArgb(120, Back) };
        Controls.Add(top);
        status = new SkinLabel { Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 4, 0, 0), ForeColor = Dim };
        var gameRow = Row(32);
        gameRow.Dock = DockStyle.Top;
        gameRow.Controls.Add(Btn("GAME FOLDER...", (s, e) => PickFolder(), 0));
        scriptButton = Btn("PUT THE SCRIPT INTO THE GAME", (s, e) => InstallScript(), 0);
        gameRow.Controls.Add(scriptButton);
        scriptNote = new SkinLabel { AutoSize = true, ForeColor = Dim, Padding = new Padding(4, 6, 0, 0) };
        gameRow.Controls.Add(scriptNote);
        nudgeNote = new SkinLabel { Dock = DockStyle.Top, Height = 22, Padding = new Padding(8, 2, 0, 0), ForeColor = Dim,
            Text = "tick a slider's box and the controller steps it through Window View: X −1, Y −5, A +1, B +5; a held grip repeats" };
        top.Controls.Add(nudgeNote); top.Controls.Add(gameRow); top.Controls.Add(status);
    }

    // ---- the game folder and the script -----------------------------------------------------------------------------------
    void PickFolder() {
        using (var dlg = new FolderBrowserDialog { Description = "The game's install folder (the one with reframework\\ in it)", SelectedPath = Link.Folder ?? "" })
            if (dlg.ShowDialog(this) == DialogResult.OK) {
                if (!GameLink.LooksLikeGame(dlg.SelectedPath)) { MessageBox.Show(this, "No reframework\\ folder in there. Is REFramework installed?", "Animation & Timing"); return; }
                UseFolder(dlg.SelectedPath, true);
            }
    }
    void UseFolder(string folder, bool remember) {
        try { Link.Use(folder); } catch (Exception ex) { status.Text = "cannot use " + folder + ": " + ex.Message; return; }
        if (remember) settings.Set("game", folder);
        clips.FolderChanged();
        timings.FolderChanged();
        UpdateStatus();
    }
    void InstallScript() {
        try { Link.InstallScript(); scriptNote.Text = "copied; restart the game or Reset Scripts in REFramework"; }
        catch (Exception ex) { scriptNote.Text = ex.Message; }
        UpdateStatus();
    }
    public bool IsTicked(string key) { return ticked.Contains(key); }
    public void SetTicked(string key, bool on) { if (on) ticked.Add(key); else ticked.Remove(key); }

    // ---- the command to the script -----------------------------------------------------------------------------------------
    public void QueueSend() { sendTimer.Stop(); sendTimer.Start(); }
    public void Step(int dir) { stepPending = dir; QueueSend(); }
    public void SetFrame(string src, int i, double frame) {
        setFrame = new Dictionary<string, object>(); setFrame["src"] = src; setFrame["i"] = (double)i; setFrame["frame"] = frame;
        QueueSend();
    }
    public void NudgeFilesChanged() { reloadNudges++; QueueSend(); }

    void Send() {
        if (Link.Folder == null) return;
        var cmd = new Dictionary<string, object>();
        cmd["seq"] = (double)(++seq);
        cmd["freeze"] = Frozen;
        cmd["speed"] = Speed;
        if (stepPending != 0) cmd["step"] = (double)stepPending;
        if (setFrame != null) cmd["set_frame"] = setFrame;
        var live = new Dictionary<string, object>();
        foreach (var kv in NudgeLive) live[kv.Key] = kv.Value.Select(x => (object)Math.Round(x, 2)).ToList();
        cmd["nudge_live"] = live;
        cmd["want_read"] = WantRead.Cast<object>().ToList();
        cmd["reload_nudges"] = (double)reloadNudges;
        stepPending = 0; setFrame = null;
        try { GameLink.WriteWhole(Link.CommandFile, Json.Write(cmd)); } catch (IOException) { QueueSend(); }
    }

    // ---- polling -------------------------------------------------------------------------------------------------------------
    void Poll() {
        if (Link.Folder == null && (DateTime.Now - lastFind).TotalMilliseconds > FIND_GAME_EVERY_MS) {
            lastFind = DateTime.Now;
            string f = GameLink.FindRunningGame();
            if (f != null) UseFolder(f, true);
        }
        Dictionary<string, object> st = null;
        string text = GameLink.ReadAll(Link.StateFile);
        if (text != null) { try { st = Json.Obj(Json.Parse(text)); } catch (FormatException) { } }
        bool alive = Link.ScriptAlive() && st != null;
        playing.Update(alive ? st : null);
        clips.Poll();
        timings.Poll();
        UpdateStatus(alive ? st : null);
    }

    void UpdateStatus(Dictionary<string, object> st = null) {
        if (Link.Folder == null) { status.Text = "no game found: start one with REFramework, or press GAME FOLDER..."; status.ForeColor = Dim; }
        else if (st == null) { status.Text = Path.GetFileName(Link.Folder) + " · the in-game script is not running (put it into the game, then Reset Scripts)"; status.ForeColor = Dim; }
        else {
            string game = Json.Str(st.ContainsKey("game") ? st["game"] : null), ch = Json.Str(st.ContainsKey("character") ? st["character"] : null);
            bool connected = st.ContainsKey("connected") && Json.Bool(st["connected"]);
            string err = Json.Str(st.ContainsKey("error") ? st["error"] : null);
            status.Text = game + " · " + (connected ? ch + " · connected" : "no character yet (load into the game)") + (err != "" ? " · script error: " + err : "");
            status.ForeColor = connected ? Good : Dim;
        }
        scriptButton.Enabled = Link.Folder != null;
        if (Link.Folder != null && scriptNote.Text == "") scriptNote.Text = Link.ScriptUpToDate() ? "script in the game is current" : Link.ScriptInstalled() ? "an older script is in the game: put it in again" : "the script is not in the game yet";
    }

    // the controller buttons step the ticked sliders (Nudge.cs, through Window View)
    void NudgeTick() {
        bool started;
        double n = nudge.Poll(ticked.Count > 0, out started);
        if (n == 0) return;
        bool any = playing.Rows.Step(n, ticked) | clips.Step(n, ticked);
        if (!any) return;
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        // the game gets its animations back when the window closes
        Frozen = false; Speed = 1.0; NudgeLive.Clear(); Send();
        clips.LetGo();
        nudge.Dispose();
        base.OnFormClosed(e);
    }
}

// a box with a fold button in its header, docked Top; the owner stacks them
class FoldBox : SkinPanel {
    protected readonly Button fold;
    protected readonly string title;
    protected bool open = true;
    protected int openHeight;
    public event Action<bool> FoldChanged;
    public const int SHUT_H = 34;

    protected FoldBox(string title, Func<string, EventHandler, int, Button> btn, bool startOpen, int openHeight) {
        this.title = title; this.openHeight = openHeight; open = startOpen;
        Dock = DockStyle.Top; Tint = Color.FromArgb(150, 30, 44, 66); Padding = new Padding(6, 2, 6, 2);
        fold = btn("", (s, e) => SetOpen(!open), 0);
        fold.Location = new Point(6, 4);
        fold.Font = new Font("Segoe UI Semibold", 10f);
        Controls.Add(fold);
    }
    protected void SetOpen(bool on) {
        open = on;
        fold.Text = title + (open ? "  ▲" : "  ▼");
        foreach (Control c in Controls) if (c != fold && !(c.Tag is string && (string)c.Tag == "header")) c.Visible = open;
        Height = open ? openHeight : SHUT_H;
        if (FoldChanged != null) FoldChanged(open);
    }
    protected void Grow(int h) { openHeight = h; if (open) Height = h; }

    // the box's parts, top to bottom under the header, each at its own height and the box's full width (a null is skipped)
    protected void Stack(IEnumerable<Control> parts) {
        int w = ClientSize.Width - 12, y = 36;
        foreach (var c in parts) {
            if (c == null) continue;
            c.Visible = open;
            c.SetBounds(6, y, w, c.Height);
            y += c.Height + 2;
        }
    }
}
