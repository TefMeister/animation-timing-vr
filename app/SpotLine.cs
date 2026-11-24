// SpotLine.cs -- where the hands are, by their numbers, read from Hand Poser (2026-11-24, Tefa: every hand spot numbered, "L / R per
// gun, H holsters, A ammo pouch, M map, F first aid", the same in every app). Hand Poser writes reframework/data/hand_poser/
// spot_now.json while it is open; this turns it into " · LEFT L5 docked on the drum · RIGHT R3 on the cover" for the status line.
// Nothing (an empty string) when Hand Poser is closed or the file is older than STALE_SEC.
using System;
using System.Collections.Generic;
using System.IO;

static class SpotLine {
    const double STALE_SEC = 5.0;
    public static string Read(string gameFolder) {
        try {
            if (gameFolder == null) return "";
            string f = Path.Combine(gameFolder, "reframework", "data", "hand_poser", "spot_now.json");
            if (!File.Exists(f)) return "";
            var o = Json.Parse(File.ReadAllText(f)) as Dictionary<string, object>;
            if (o == null) return "";
            object t, l, r;
            double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            if (!o.TryGetValue("t", out t) || !(t is double) || Math.Abs(now - (double)t) > STALE_SEC) return "";
            string left = o.TryGetValue("left", out l) ? l as string ?? "" : "", right = o.TryGetValue("right", out r) ? r as string ?? "" : "";
            return (left != "" ? " · LEFT " + left : "") + (right != "" ? " · RIGHT " + right : "");
        } catch (Exception) { return ""; }   // half-written or locked: next poll
    }
}
