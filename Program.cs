using System;
using System.Threading;
using System.Windows;

namespace ECS;

internal static class Program {
    [STAThread]
    private static void Main() {
        // Every stray copy polls the usage endpoint on its own, and the allowance is small.
        using var only = new Mutex(true, "ECS.SingleInstance", out var first);
        if (!first) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new MainWindow());
    }
}
