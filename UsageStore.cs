using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ECS;

/// <summary>
/// Decides when the widget may touch the network and remembers what it last learned.
///
/// The endpoint allows only a couple of calls per account per fixed hour, and Claude Code
/// itself spends from the same allowance. So: the account in use refreshes often, the rest
/// rarely, a block is obeyed to the second, and the numbers survive a restart on disk.
/// </summary>
internal sealed class UsageStore {
    private static readonly TimeSpan ForceGap = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(90);

    private sealed class Entry {
        public List<Bar> Bars;
        public DateTimeOffset? At, BlockedUntil;
        public int Strikes;
        public string LastError;
    }

    private readonly Dictionary<string, Entry> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);        // one request at a time
    private readonly string _path;

    public UsageStore() {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ECS", "usage-cache.json");
        Load();
    }

    private void Load() {
        try {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            if (root == null) return;
            foreach (var (id, node) in root) {
                var e = new Entry();
                if (node?["at"] != null) e.At = DateTimeOffset.FromUnixTimeMilliseconds(node["at"].GetValue<long>());
                if (node?["blockedUntil"] != null)
                    e.BlockedUntil = DateTimeOffset.FromUnixTimeMilliseconds(node["blockedUntil"].GetValue<long>());
                if (node?["strikes"] != null) e.Strikes = node["strikes"].GetValue<int>();
                if (node?["error"] != null) e.LastError = node["error"].GetValue<string>();
                if (node?["bars"] != null) e.Bars = Accounts.ParseLimits(node["bars"]);
                _cache[id] = e;
            }
        } catch { /* first run, or the file was damaged */ }
    }

    private void Save() {
        try {
            var root = new JsonObject();
            foreach (var (id, e) in _cache) {
                var live = e.BlockedUntil > DateTimeOffset.UtcNow;
                if (e.Bars == null && !live) continue;
                var o = new JsonObject();
                if (e.At != null) o["at"] = e.At.Value.ToUnixTimeMilliseconds();
                if (live) o["blockedUntil"] = e.BlockedUntil.Value.ToUnixTimeMilliseconds();
                if (e.Strikes > 0) o["strikes"] = e.Strikes;
                if (e.LastError != null) o["error"] = e.LastError;
                if (e.Bars != null) o["bars"] = Serialize(e.Bars);
                root[id] = o;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, root.ToJsonString());
        } catch { /* cache only */ }
    }

    /// <summary>Store bars in the same shape the API sends, so loading reuses ParseLimits.</summary>
    private static JsonObject Serialize(List<Bar> bars) {
        var arr = new JsonArray();
        foreach (var b in bars) {
            var o = new JsonObject {
                ["kind"] = b.Group == "session" ? "session" : b.Short == "7D" ? "weekly_all" : "weekly_scoped",
                ["group"] = b.Group,
                ["percent"] = b.Percent,
                ["severity"] = b.Severity,
            };
            if (b.ResetsAt != null) o["resets_at"] = b.ResetsAt.Value.ToString("o");
            if (b.Group != "session" && b.Short != "7D")
                o["scope"] = new JsonObject { ["model"] = new JsonObject { ["display_name"] = b.Short } };
            arr.Add(o);
        }
        return new JsonObject { ["limits"] = arr };
    }

    private Entry Get(string id) {
        if (!_cache.TryGetValue(id, out var e)) _cache[id] = e = new Entry();
        return e;
    }

    private static Usage Serve(Entry e) {
        var u = new Usage { Bars = e.Bars, At = e.At };
        if (e.BlockedUntil > DateTimeOffset.UtcNow) u.BlockedUntil = e.BlockedUntil;
        if (e.Bars == null) u.Error = e.LastError ?? "Rate limited";
        return u;
    }

    /// <summary>Adopt Claude Code's own cache whenever it is ahead of ours. Costs nothing.</summary>
    private void AdoptLocal(Account a) {
        var local = Accounts.LocalUsage(a);
        if (local?.At == null) return;
        var e = Get(a.Id);
        if (e.At >= local.At) return;
        e.Bars = local.Bars;
        e.At = local.At;
        Save();
    }

    /// <summary>Current numbers for one account, fetching only when the policy allows it.</summary>
    public async Task<Usage> GetAsync(Account a, bool force) {
        AdoptLocal(a);
        var e = Get(a.Id);
        // "Manual only" means a click is the only thing that may spend a request.
        if (!force && AppSettings.ManualOnly) return Serve(e);
        var ttl = force ? ForceGap
                : a.Active ? AppSettings.ActiveInterval
                : AppSettings.IdleInterval;
        var now = DateTimeOffset.UtcNow;

        if (e.Bars != null && e.At != null && now - e.At < ttl) return Serve(e);
        if (e.BlockedUntil > now) return Serve(e);           // a block is never forced through
        if (a.Expired) return Serve(e);                      // no point asking with a dead token

        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            now = DateTimeOffset.UtcNow;
            if (e.Bars != null && e.At != null && now - e.At < ttl) return Serve(e);
            if (e.BlockedUntil > now) return Serve(e);

            var fresh = await Accounts.FetchUsage(a.Token).ConfigureAwait(false);
            await Task.Delay(800).ConfigureAwait(false);     // space out the queue

            if (fresh.BlockedUntil != null) {
                // Retrying the instant the server allows it is what kept re-arming the block.
                e.Strikes++;
                e.BlockedUntil = fresh.BlockedUntil.Value.AddMinutes(Math.Min(3, e.Strikes));
                e.LastError = fresh.Error;
                Save();
                return Serve(e);
            }
            if (fresh.Error != null) {
                e.LastError = fresh.Error;
                var served = Serve(e);
                served.Note = fresh.Error;
                return served;
            }
            e.Bars = fresh.Bars;
            e.At = fresh.At;
            e.BlockedUntil = null;
            e.Strikes = 0;
            e.LastError = null;
            Save();
            return Serve(e);
        } finally { _gate.Release(); }
    }
}
