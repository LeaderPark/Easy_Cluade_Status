using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ECS;

internal sealed class Bar {
    public string Label, Short, Group, Severity;
    public int Percent;
    public DateTimeOffset? ResetsAt;
}

internal sealed class Account {
    public string Id, Dir, Email, Token;
    public bool Active, TokenExpired, LoginExpired, NeverLoggedIn;

    /// <summary>No usable token, whatever the reason: the row offers Reconnect instead.</summary>
    public bool Expired => NeverLoggedIn || LoginExpired || TokenExpired;

    public string ExpiryReason =>
        NeverLoggedIn ? "Not signed in" : LoginExpired ? "Login expired" : "Token expired";
}

/// <summary>Usage as shown for one account, plus why it might be out of date.</summary>
internal sealed class Usage {
    public List<Bar> Bars;
    public DateTimeOffset? At;
    public DateTimeOffset? BlockedUntil;
    public string Error, Note;
    public bool HasBars => Bars is { Count: > 0 };
}

internal static class Accounts {
    public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string DefaultDir = Path.Combine(Home, ".claude");
    public static readonly string Root = Path.Combine(Home, ".claude-accounts");

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    // Without the claude-code User-Agent the endpoint drops the caller into a punitive
    // bucket that allows only two or three calls an hour. With it, it behaves normally.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient() {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.Add("User-Agent", "claude-code/" + ClaudeVersion());
        c.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        return c;
    }

    private static JsonNode ReadJson(string path) {
        try { return JsonNode.Parse(File.ReadAllText(path)); } catch { return null; }
    }

    private static long Rank(string v) => v.Split('.').Aggregate(0L, (n, p) => n * 10000 + long.Parse(p));

    /// <summary>Claude Code's version, taken from whichever profile updated most recently.</summary>
    public static string ClaudeVersion() {
        string best = null;
        foreach (var dir in ProfileDirs()) {
            var v = ReadJson(Path.Combine(dir, ".last-update-result.json"))?["version_to"]?.GetValue<string>();
            if (v == null || !Regex.IsMatch(v, @"^\d+\.\d+\.\d+$")) continue;
            if (best == null || Rank(v) > Rank(best)) best = v;
        }
        return best ?? "2.1.270";
    }

    private static IEnumerable<string> ProfileDirs() {
        yield return DefaultDir;
        string[] subs;
        try { subs = Directory.GetDirectories(Root); } catch { yield break; }
        foreach (var d in subs) yield return d;
    }

    // This only changes when the user switches profile by hand, so cache it briefly.
    private static string _activeDir;
    private static DateTimeOffset _activeAt;

    /// <summary>The profile new terminals will use, from the persisted user variable.</summary>
    public static string ActiveDir() {
        if (_activeDir != null && DateTimeOffset.UtcNow - _activeAt < TimeSpan.FromSeconds(30)) return _activeDir;
        _activeAt = DateTimeOffset.UtcNow;
        _activeDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR", EnvironmentVariableTarget.User) ?? "";
        return _activeDir;
    }

    private static bool IsPast(JsonNode n) {
        try { return n != null && n.GetValue<long>() < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        catch { return false; }
    }

    // A user-chosen order, by account id. Anything not listed keeps its natural place
    // at the end, so a newly created profile simply appears last.
    private static readonly string OrderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ECS", "order.json");

    private static List<string> LoadOrder() {
        try {
            var arr = JsonNode.Parse(File.ReadAllText(OrderPath)) as JsonArray;
            var ids = new List<string>();
            if (arr != null) foreach (var n in arr) ids.Add(n.GetValue<string>());
            return ids;
        } catch { return new List<string>(); }
    }

    private static void SaveOrder(List<string> ids) {
        try {
            var arr = new JsonArray();
            foreach (var id in ids) arr.Add(id);
            Directory.CreateDirectory(Path.GetDirectoryName(OrderPath));
            File.WriteAllText(OrderPath, arr.ToJsonString());
        } catch { }
    }

    /// <summary>Move one account one place earlier in the order shown everywhere.</summary>
    public static void MoveUp(string id) {
        var ids = List().Select(a => a.Id).ToList();
        var i = ids.IndexOf(id);
        if (i <= 0) return;
        (ids[i - 1], ids[i]) = (ids[i], ids[i - 1]);
        SaveOrder(ids);
    }

    /// <summary>Every profile holding credentials: ~/.claude first, then ~/.claude-accounts/*.</summary>
    public static List<Account> List() {
        var active = ActiveDir().TrimEnd('\\').ToLowerInvariant();
        var list = new List<Account>();
        foreach (var dir in ProfileDirs()) {
            // A folder made but not yet logged into still belongs here: the row is how the
            // user finishes setting it up.
            var cred = ReadJson(Path.Combine(dir, ".credentials.json"))?["claudeAiOauth"];
            string token = null;
            try { token = cred?["accessToken"]?.GetValue<string>(); } catch { }

            // The default profile keeps its config at ~/.claude.json, not inside the folder.
            var cfg = ReadJson(Path.Combine(dir, ".claude.json"));
            var oauth = cfg?["oauthAccount"];
            if (oauth == null && dir == DefaultDir)
                oauth = ReadJson(Path.Combine(Home, ".claude.json"))?["oauthAccount"];

            var name = Path.GetFileName(dir);
            list.Add(new Account {
                Id = name == ".claude" ? "default" : name,
                Dir = dir,
                Email = oauth?["emailAddress"]?.GetValue<string>() ?? name,
                Token = token,
                NeverLoggedIn = token == null,
                TokenExpired = IsPast(cred?["expiresAt"]),
                LoginExpired = IsPast(cred?["refreshTokenExpiresAt"]),
                Active = dir == DefaultDir
                    ? active.Length == 0 || active == DefaultDir.ToLowerInvariant()
                    : dir.TrimEnd('\\').ToLowerInvariant() == active,
            });
        }

        var order = LoadOrder();
        return list
            .OrderBy(a => { var i = order.IndexOf(a.Id); return i < 0 ? int.MaxValue : i; })
            .ToList();
    }

    private static readonly Dictionary<string, string> Labels = new() {
        ["session"] = "Current session", ["weekly_all"] = "Current week", ["weekly_scoped"] = "Current week",
    };
    private static readonly Dictionary<string, string> Shorts = new() {
        ["session"] = "5H", ["weekly_all"] = "7D", ["weekly_scoped"] = "7D",
    };

    private static DateTimeOffset? ParseTime(JsonNode n) {
        string s = null;
        try { s = n?.GetValue<string>(); } catch { }
        if (s == null) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    private static int Pct(JsonNode n) {
        try { return Math.Clamp((int)Math.Round(n?.GetValue<double>() ?? 0), 0, 100); } catch { return 0; }
    }

    /// <summary>Pure: the usage payload becomes the gauges we draw.</summary>
    public static List<Bar> ParseLimits(JsonNode data) {
        var bars = new List<Bar>();
        if (data?["limits"] is JsonArray limits) {
            foreach (var l in limits) {
                string kind = null;
                try { kind = l?["kind"]?.GetValue<string>(); } catch { }
                if (kind == null || !Labels.TryGetValue(kind, out var baseLabel)) continue;
                string model = null;
                try { model = l["scope"]?["model"]?["display_name"]?.GetValue<string>(); } catch { }
                string group = null;
                try { group = l["group"]?.GetValue<string>(); } catch { }
                string sev = null;
                try { sev = l["severity"]?.GetValue<string>(); } catch { }
                bars.Add(new Bar {
                    Label = model == null ? baseLabel : baseLabel + " " + model,
                    Short = model?.Split(' ')[0] ?? Shorts[kind],
                    Group = group ?? (kind == "session" ? "session" : "weekly"),
                    Percent = Pct(l["percent"]),
                    ResetsAt = ParseTime(l["resets_at"]),
                    Severity = sev ?? "normal",
                });
            }
        }
        if (bars.Count == 0 && data?["five_hour"] != null) {
            bars.Add(Legacy(data["five_hour"], "Current session", "5H", "session"));
            if (data["seven_day"] != null) bars.Add(Legacy(data["seven_day"], "Current week", "7D", "weekly"));
        }
        return bars;
    }

    private static Bar Legacy(JsonNode n, string label, string shortName, string group) => new() {
        Label = label, Short = shortName, Group = group, Severity = "normal",
        Percent = Pct(n["utilization"]), ResetsAt = ParseTime(n["resets_at"]),
    };

    /// <summary>Live plan limits for one account.</summary>
    public static async Task<Usage> FetchUsage(string token) {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Add("Authorization", "Bearer " + token);
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req).ConfigureAwait(false); }
        catch { return new Usage { Error = "Offline" }; }

        using (res) {
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new Usage { Error = "Login expired" };
            if (res.StatusCode == HttpStatusCode.TooManyRequests) {
                // retry-after may be absent or zero; never poll tighter than a minute.
                var secs = res.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0;
                if (secs <= 0) secs = 300;
                return new Usage {
                    Error = "Rate limited",
                    BlockedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, secs)),
                };
            }
            if (!res.IsSuccessStatusCode) return new Usage { Error = "HTTP " + (int)res.StatusCode };
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new Usage { Bars = ParseLimits(JsonNode.Parse(body)), At = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>
    /// Usage as Claude Code itself last cached it. Free: no request, so it spends none of
    /// the endpoint's small hourly allowance. Only fresh while that account is in use.
    /// </summary>
    public static Usage LocalUsage(Account a) {
        var files = new List<string> { Path.Combine(a.Dir, ".claude.json") };
        if (a.Dir == DefaultDir) files.Add(Path.Combine(Home, ".claude.json"));

        Usage best = null;
        foreach (var f in files) {
            var cfg = ReadJson(f);
            var c = cfg?["cachedUsageUtilization"];
            if (c?["utilization"] == null || c["fetchedAtMs"] == null) continue;

            string owner = null, cached = null;
            try { owner = cfg["oauthAccount"]?["accountUuid"]?.GetValue<string>(); } catch { }
            try { cached = c["accountUuid"]?.GetValue<string>(); } catch { }
            if (cached != null && owner != null && cached != owner) continue;   // left by another login

            var bars = ParseLimits(c["utilization"]);
            if (bars.Count == 0) continue;
            DateTimeOffset at;
            try { at = DateTimeOffset.FromUnixTimeMilliseconds(c["fetchedAtMs"].GetValue<long>()); }
            catch { continue; }
            if (best == null || at > best.At) best = new Usage { Bars = bars, At = at };
        }
        return best;
    }

    // Shared with the default profile so a skill, agent or plugin added in one place shows
    // up everywhere. Only the login stays per account.
    private static readonly string[] SharedDirs = {
        "agents", "commands", "skills", "plugins", "projects", "tasks", "file-history", "shell-snapshots",
    };

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, @"^[A-Za-z0-9._-]{1,32}$");

    /// <summary>
    /// Create a profile the same way the shell's New-ClaudeAccount does: tooling and history
    /// junctioned to the default profile, settings copied, MCP servers seeded. The new profile
    /// has no credentials yet, so a terminal is opened for /login.
    /// </summary>
    /// <summary>Why this name cannot be used, or null if it can.</summary>
    public static string Validate(string name) {
        if (!IsValidName(name)) return "letters, digits, . _ - only";
        if (Directory.Exists(Path.Combine(Root, name))) return "that name is taken";
        return null;
    }

    public static string Create(string name) {
        var reason = Validate(name);
        if (reason != null) return reason;
        var dir = Path.Combine(Root, name);

        try {
            // First run: the accounts root does not exist yet either.
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(dir);

            var settings = Path.Combine(DefaultDir, "settings.json");
            if (File.Exists(settings)) File.Copy(settings, Path.Combine(dir, "settings.json"), true);

            // Seed the memory import here too: SyncShared only sees accounts that already
            // have credentials, so without this the first session would run without it.
            var sharedMemory = Path.Combine(DefaultDir, "CLAUDE.md");
            if (File.Exists(sharedMemory))
                File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "@" + sharedMemory + Environment.NewLine);

            foreach (var shared in SharedDirs) {
                var target = Path.Combine(DefaultDir, shared);
                Directory.CreateDirectory(target);
                // mklink /J makes a junction without administrator rights.
                var psi = new System.Diagnostics.ProcessStartInfo {
                    FileName = "cmd.exe",
                    Arguments = "/c mklink /J \"" + Path.Combine(dir, shared) + "\" \"" + target + "\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(5000);
            }

            var seed = new JsonObject { ["hasCompletedOnboarding"] = true };
            var def = ReadJson(Path.Combine(Home, ".claude.json"));
            if (def?["mcpServers"] != null) seed["mcpServers"] = def["mcpServers"].DeepClone();
            foreach (var k in new[] { "officialMarketplaceAutoInstalled", "officialMarketplaceAutoInstallAttempted" })
                if (def?[k] != null) seed[k] = def[k].DeepClone();
            File.WriteAllText(Path.Combine(dir, ".claude.json"), seed.ToJsonString());
        } catch (Exception e) {
            return "Create failed: " + e.Message;
        }

        OpenTerminal(dir, name, "New account");
        return null;
    }

    /// <summary>
    /// Remove a profile. Junctions are unlinked first so nothing inside the default profile
    /// can be reached through them; the default profile itself is never a candidate.
    /// </summary>
    public static string Delete(Account a) {
        if (a.Dir == DefaultDir) return "The default account cannot be deleted";
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetFullPath(a.Dir).TrimEnd(Path.DirectorySeparatorChar);
        if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "Not an account folder";

        try {
            foreach (var sub in Directory.GetDirectories(dir)) {
                var attr = File.GetAttributes(sub);
                if ((attr & FileAttributes.ReparsePoint) != 0) Directory.Delete(sub, false);
            }
            Directory.Delete(dir, true);
            return null;
        } catch (Exception e) {
            return "Delete failed: " + e.Message;
        }
    }

    // Shims go next to Claude Code's own launcher: that folder is already on PATH, so
    // claude1..claudeN work from PowerShell, cmd and Git Bash with nothing else to set up.
    // The native installer uses ~/.local/bin, but an npm install puts it elsewhere.
    private static readonly (string Dir, string Exe) Launcher = FindLauncher();

    private static (string, string) FindLauncher() {
        var native = Path.Combine(Home, ".local", "bin", "claude.exe");
        if (File.Exists(native)) return (Path.GetDirectoryName(native), native);

        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
            var dir = raw.Trim();
            if (dir.Length == 0) continue;
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude.bat" }) {
                try {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) return (dir, p);
                } catch { /* a malformed PATH entry */ }
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Write claude1..claudeN, one per account in the order the panel shows them, and
    /// delete any left over from a shorter list. Rewrites only what actually changed.
    /// </summary>
    public static void WriteShims(List<Account> accounts) {
        if (Launcher.Dir == null) return;      // Claude Code is not on PATH; nothing to wrap
        try {
            for (var i = 0; i < accounts.Count; i++) {
                var file = Path.Combine(Launcher.Dir, "claude" + (i + 1) + ".cmd");
                var body = string.Join(Environment.NewLine,
                    "@echo off",
                    ":: " + accounts[i].Email + " - written by ECS, do not edit",
                    "set \"CLAUDE_CONFIG_DIR=" + accounts[i].Dir + "\"",
                    "call \"" + Launcher.Exe + "\" %*",
                    "");
                if (File.Exists(file) && File.ReadAllText(file) == body) continue;
                File.WriteAllText(file, body);
            }
            for (var i = accounts.Count; i < accounts.Count + 12; i++) {
                var stale = Path.Combine(Launcher.Dir, "claude" + (i + 1) + ".cmd");
                if (File.Exists(stale)) File.Delete(stale);
            }
        } catch { /* the shims are a convenience, never a reason to fail a refresh */ }
    }

    /// <summary>
    /// Keep the pieces that cannot be junctioned in step with the default profile.
    ///
    /// CLAUDE.md becomes a one-line @-import rather than a copy, so the text lives in one
    /// place. settings.json is copied, which also carries the status line (its command is
    /// an absolute path, so the script itself need not be duplicated).
    ///
    /// MCP servers are deliberately left alone: they live in .claude.json next to project
    /// history and onboarding state, and live sessions rewrite that file constantly.
    /// </summary>
    public static void SyncShared(List<Account> accounts) {
        var sharedMemory = Path.Combine(DefaultDir, "CLAUDE.md");
        var sharedSettings = Path.Combine(DefaultDir, "settings.json");
        var importLine = "@" + sharedMemory + Environment.NewLine;

        foreach (var a in accounts) {
            if (a.Dir == DefaultDir) continue;      // the default profile owns the originals
            try {
                if (File.Exists(sharedMemory)) {
                    var target = Path.Combine(a.Dir, "CLAUDE.md");
                    if (!File.Exists(target) || File.ReadAllText(target) != importLine) {
                        Backup(target);
                        File.WriteAllText(target, importLine);
                    }
                }
                if (File.Exists(sharedSettings)) {
                    var target = Path.Combine(a.Dir, "settings.json");
                    var want = File.ReadAllText(sharedSettings);
                    if (!File.Exists(target) || File.ReadAllText(target) != want) {
                        Backup(target);
                        File.WriteAllText(target, want);
                    }
                }
            } catch { /* a sync failure must never break a refresh */ }
        }
    }

    /// <summary>Keep the first version we ever replaced, and only that one.</summary>
    private static void Backup(string file) {
        try {
            if (!File.Exists(file)) return;
            var bak = file + ".bak-ecs";
            if (!File.Exists(bak)) File.Copy(file, bak);
        } catch { }
    }

    /// <summary>
    /// Open a console already pointed at one profile. The variable is handed over through
    /// the child's environment rather than a "set" command: routing it through
    /// `cmd /c start "" cmd /k ...` let the outer shell swallow everything after the
    /// first &amp;&amp;, so only the assignment reached the new window.
    /// </summary>
    private static void OpenTerminal(string dir, string label, string what) {
        var psi = new System.Diagnostics.ProcessStartInfo {
            FileName = "cmd.exe",
            Arguments = "/k echo [" + label + "] " + what + ": type /login & echo. & claude",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Home,
        };
        psi.Environment["CLAUDE_CONFIG_DIR"] = dir;
        try { System.Diagnostics.Process.Start(psi)?.Dispose(); } catch { }
    }

    /// <summary>Open a terminal already pointed at this account so /login can be run there.</summary>
    public static void Reconnect(Account a) => OpenTerminal(a.Dir, Path.GetFileName(a.Dir), "Reconnect");
}
