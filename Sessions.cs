using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ECS;

internal sealed class SessionGroup {
    public string Cwd;
    public int Count;
    public DateTimeOffset? At;          // last transcript write, null if none was found

    /// <summary>Working within the last couple of minutes, as opposed to waiting for input.</summary>
    public bool Live => At != null && DateTimeOffset.UtcNow - At < TimeSpan.FromMinutes(2);

    /// <summary>The path, with the home folder shortened so it fits a narrow panel.</summary>
    public string Display {
        get {
            if (string.IsNullOrEmpty(Cwd)) return "(unknown path)";
            var path = Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var home = Accounts.Home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                ? "~" + path.Substring(home.Length)
                : path;
        }
    }
}

internal static class Sessions {
    private static readonly string Projects = Path.Combine(Accounts.DefaultDir, "projects");
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');

    /// <summary>
    /// Claude Code names a transcript folder after the working directory, with every
    /// character that is not a letter or digit turned into a dash.
    /// </summary>
    private static string ProjectFolder(string cwd) {
        var sb = new StringBuilder(cwd.Length);
        foreach (var ch in cwd.TrimEnd('\\', '/'))
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return sb.ToString();
    }

    /// <summary>Newest transcript write for a working directory, or null if there is none.</summary>
    private static DateTimeOffset? LastActivity(string cwd) {
        var dir = Path.Combine(Projects, ProjectFolder(cwd));
        string[] files;
        try { files = Directory.GetFiles(dir, "*.jsonl"); } catch { return null; }
        DateTime? newest = null;
        foreach (var f in files) {
            try {
                var t = File.GetLastWriteTimeUtc(f);
                if (newest == null || t > newest) newest = t;
            } catch { }
        }
        return newest == null ? null : new DateTimeOffset(newest.Value, TimeSpan.Zero);
    }

    /// <summary>
    /// Sessions that are actually running, one entry per working directory.
    ///
    /// Transcript timestamps alone were misleading: a session waiting for input stops
    /// writing and would vanish, while a background agent that had finished lingered.
    /// The process list is the truth; transcripts only say how recently each one worked.
    /// </summary>
    public static List<SessionGroup> Running() {
        var byCwd = new Dictionary<string, SessionGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcessesByName("claude")) {
            int pid;
            try { pid = p.Id; } catch { continue; } finally { p.Dispose(); }

            var (cwd, cmd) = Native.Inspect(pid);
            if (string.IsNullOrEmpty(cwd)) continue;
            // Claude Desktop's helper processes: they carry --type= and sit in system32.
            if (cmd != null && cmd.Contains("--type=", StringComparison.Ordinal)) continue;
            if (cwd.TrimEnd('\\').StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) continue;

            var key = cwd.TrimEnd('\\', '/');
            if (byCwd.TryGetValue(key, out var g)) g.Count++;
            else byCwd[key] = new SessionGroup { Cwd = key, Count = 1, At = LastActivity(key) };
        }

        return byCwd.Values
            .OrderByDescending(g => g.At ?? DateTimeOffset.MinValue)
            .ToList();
    }
}
