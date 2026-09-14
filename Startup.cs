using System;
using Microsoft.Win32;

namespace ECS;

/// <summary>
/// Run-at-sign-in, owned by the app rather than the installer so the tray menu can toggle
/// it. The per-user Run key needs no shortcut file and no administrator rights.
/// </summary>
internal static class Startup {
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ECS";

    private static string ExePath => Environment.ProcessPath;

    public static bool Enabled {
        get {
            try {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                var v = k?.GetValue(Name) as string;
                return !string.IsNullOrEmpty(v) && v.Contains("ECS.exe", StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
        }
    }

    public static void Toggle() => Set(!Enabled);

    public static void Set(bool on) {
        try {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (k == null) return;
            if (on) k.SetValue(Name, "\"" + ExePath + "\"");
            else k.DeleteValue(Name, false);
        } catch { }
    }
}
