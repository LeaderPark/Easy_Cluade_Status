using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ECS;

internal static class Program {
    [STAThread]
    private static void Main() {
        // Every stray copy polls the usage endpoint on its own, and the allowance is small.
        using var only = new Mutex(true, "ECS.SingleInstance", out var first);
        if (!first) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

        // A widget that sits on the desktop all day should degrade, not vanish. One bad
        // repaint or a failed fetch gets written down and swallowed.
        app.DispatcherUnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };

        app.Run(new MainWindow());
    }

    private static void Log(Exception ex) {
        try {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ECS");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                DateTime.Now.ToString("s") + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
        } catch {
            // Logging must never be the thing that kills the app.
        }
    }
}
