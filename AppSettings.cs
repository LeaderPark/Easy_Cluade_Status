using System;
using System.IO;
using System.Text.Json.Nodes;

namespace ECS;

/// <summary>
/// ECS's own preferences, kept apart from anything Claude Code owns.
/// </summary>
internal static class AppSettings {
    private static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ECS", "settings.json");

    /// <summary>
    /// How often the account in use may be re-fetched, in minutes. Zero means the panel
    /// never reaches the network on its own and only a click or the menu refreshes it.
    /// Idle accounts are fetched far less often; see <see cref="IdleInterval"/>.
    /// </summary>
    public static int RefreshMinutes { get; private set; } = 3;

    public static readonly int[] Choices = { 0, 1, 3, 5, 10, 30 };

    static AppSettings() {
        try {
            var n = JsonNode.Parse(File.ReadAllText(Path_))?["refreshMinutes"];
            if (n != null) RefreshMinutes = Math.Clamp(n.GetValue<int>(), 0, 240);
        } catch { /* first run */ }
    }

    public static void SetRefresh(int minutes) {
        RefreshMinutes = Math.Clamp(minutes, 0, 240);
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_));
            File.WriteAllText(Path_, new JsonObject { ["refreshMinutes"] = RefreshMinutes }.ToJsonString());
        } catch { }
    }

    public static string Label(int minutes) => minutes == 0 ? "Manual only" : minutes + " min";

    /// <summary>Automatic fetching is off entirely.</summary>
    public static bool ManualOnly => RefreshMinutes == 0;

    public static TimeSpan ActiveInterval => TimeSpan.FromMinutes(Math.Max(1, RefreshMinutes));

    /// <summary>
    /// Accounts you are not working in barely move, and the endpoint's allowance is small,
    /// so they are checked a good deal less often than the active one.
    /// </summary>
    public static TimeSpan IdleInterval => TimeSpan.FromMinutes(Math.Max(1, RefreshMinutes) * 6);
}
