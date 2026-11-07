// AnimTiming.cs -- start-up, settings, and the link to the game folder (the files under reframework\data\anim_timing\ for the
// in-game script, and under reframework\data\visceral_re8\ for Visceral RE8's plugin).
//
//   AnimTiming.exe                      the window
//   AnimTiming.exe --game <folder>      use this game folder instead of looking for a running game
//   AnimTiming.exe --selftest <folder>  no window: exercise the maths and the file shapes in <folder>, print PASS/FAIL
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

static class Program {
    public static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnimTimingVR");

    [STAThread]
    static int Main(string[] args) {
        string gameArg = null;
        for (int i = 0; i < args.Length; i++) {
            if (args[i] == "--game" && i + 1 < args.Length) gameArg = args[++i];
            else if (args[i] == "--selftest" && i + 1 < args.Length) return SelfTest.Run(args[++i]) ? 0 : 1;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TimingUi(gameArg));
        return 0;
    }
}

// settings.txt: one `key=value` per line, in %APPDATA%\AnimTimingVR.
class Settings {
    readonly string file = Path.Combine(Program.AppData, "settings.txt");
    readonly Dictionary<string, string> values = new Dictionary<string, string>();

    public Settings() {
        try {
            if (File.Exists(file))
                foreach (var line in File.ReadAllLines(file)) {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
        } catch (IOException) { }
    }

    public string Get(string key, string dflt) { string v; return values.TryGetValue(key, out v) ? v : dflt; }

    public void Set(string key, string value) {
        values[key] = value;
        try {
            Directory.CreateDirectory(Program.AppData);
            File.WriteAllLines(file, values.Select(kv => kv.Key + "=" + kv.Value).ToArray());
        } catch (IOException) { }
    }
}

// The game folder and the files. `Folder` is the game's install folder (the one with reframework\ in it).
class GameLink {
    public const string SCRIPT_NAME = "anim_timing.lua";
    public const double STALE_SEC = 3.0;        // state.json older than this = the script is not running

    public string Folder { get; private set; }
    public string DataDir { get { return Folder == null ? null : Path.Combine(Folder, "reframework", "data", "anim_timing"); } }
    public string StateFile { get { return Folder == null ? null : Path.Combine(DataDir, "state.json"); } }
    public string CommandFile { get { return Folder == null ? null : Path.Combine(DataDir, "command.json"); } }
    public string NudgesDir(string game) { return Folder == null ? null : Path.Combine(DataDir, "nudges", Safe(game)); }
    public string ScriptTarget { get { return Folder == null ? null : Path.Combine(Folder, "reframework", "autorun", SCRIPT_NAME); } }
    public string ScriptSource { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SCRIPT_NAME); } }
    // Visceral RE8's side (the plugin): only in a game that has its data folder
    public string VisceralDir { get { return Folder == null ? null : Path.Combine(Folder, "reframework", "data", "visceral_re8"); } }
    public string ClipsDir { get { return Folder == null ? null : Path.Combine(VisceralDir, "clips"); } }
    public bool HasVisceral { get { try { return VisceralDir != null && Directory.Exists(VisceralDir); } catch (Exception) { return false; } } }

    public static string Safe(string n) {
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n.Trim();
    }

    public void Use(string folder) {
        Folder = folder;
        if (folder != null) Directory.CreateDirectory(DataDir);
    }

    public static bool LooksLikeGame(string folder) {
        return folder != null && Directory.Exists(Path.Combine(folder, "reframework"));
    }

    // A running game with REFramework beside its exe. Games we cannot look into (access denied) are skipped.
    public static string FindRunningGame() {
        foreach (var p in Process.GetProcesses()) {
            string exe = null;
            try { exe = p.MainModule.FileName; } catch (Exception) { continue; }
            string dir = Path.GetDirectoryName(exe);
            if (LooksLikeGame(dir)) return dir;
        }
        return null;
    }

    public static bool Fresh(string file, double sec) {
        try { return File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalSeconds < sec; }
        catch (IOException) { return false; }
    }
    public bool ScriptAlive() { return Fresh(StateFile, STALE_SEC); }

    public static string ReadAll(string file) {
        try { return file != null && File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (IOException) { return null; }
    }

    // Written to a side file and swapped in, so the reader never sees half a file.
    public static void WriteWhole(string file, string text) {
        if (file == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
    }

    public bool ScriptInstalled() { return Folder != null && File.Exists(ScriptTarget); }

    public bool ScriptUpToDate() {
        return ScriptInstalled() && File.Exists(ScriptSource) && File.ReadAllText(ScriptSource) == File.ReadAllText(ScriptTarget);
    }

    public void InstallScript() {
        if (!File.Exists(ScriptSource)) throw new FileNotFoundException(SCRIPT_NAME + " is not next to AnimTiming.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ScriptTarget));
        File.Copy(ScriptSource, ScriptTarget, true);
    }
}
