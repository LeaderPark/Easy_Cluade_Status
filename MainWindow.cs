using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;
using IOPath = System.IO.Path;
using System.Windows.Threading;

namespace ECS;

internal sealed class MainWindow : Window {
    // Palette mirrors the panel it replaces.
    private static readonly Brush Fg = Frozen("#ece7df");
    private static readonly Brush Dim = Frozen("#8d8578");
    private static readonly Brush Quiet = Frozen("#6f6960");
    private static readonly Brush Accent = Frozen("#d97757");
    private static readonly Brush Danger = Frozen("#e0806a");
    private static readonly Brush Fix = Frozen("#f4c071");
    private static readonly Brush Live = Frozen("#adec93");
    // Window labels share the Reconnect colour, and the claudeN number shares the
    // live-session green: two families instead of four competing hues.
    private static readonly Brush WindowLabel = Frozen("#f4c071");
    private static readonly Brush Badge = Frozen("#adec93");
    private static readonly Brush Track = Frozen("#4d4840");
    private static readonly Brush Card = Frozen("#26241f");
    private static readonly Brush CardLine = Frozen("#332f29");
    private static readonly Brush Line = Frozen("#38342d");
    private static readonly Brush Panel = Frozen("#1c1a18");
    private static readonly Brush GripIdle = Frozen("#403b34");
    private static readonly Brush GripHot = Frozen("#6f6960");

    // Colour follows the number. Severity still forces red: the server knows about locks
    // and caps that a percentage alone does not show.
    private static readonly (int Min, Brush Colour)[] Scale = {
        (90, Frozen("#f55c5c")), (75, Frozen("#f38a68")), (50, Frozen("#fcce88")), (0, Live),
    };

    private static SolidColorBrush Frozen(string hex) {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private readonly UsageStore _store = new();
    private readonly StackPanel _accounts = new();
    private readonly StackPanel _sessions = new();
    private readonly List<Action> _tickers = new();
    private readonly string _statePath;
    private Tray _tray;
    private bool _busy;
    private Point _pressAt;
    private bool _dragged;

    public MainWindow() {
        _statePath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ECS", "window.json");

        Title = "ECS";
        Width = 258;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Pretendard, Segoe UI, Malgun Gothic");
        FontSize = 11;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var body = new StackPanel();
        body.Children.Add(Grip());
        body.Children.Add(new Border { Child = _accounts, Padding = new Thickness(5) });
        body.Children.Add(new Border { Child = _sessions, Padding = new Thickness(5, 0, 5, 5) });

        Content = new Border {
            Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Child = body, SnapsToDevicePixels = true,
        };

        // The whole panel is the refresh button now; dragging is a press that moves first.
        PreviewMouseLeftButtonDown += (_, e) => { _pressAt = e.GetPosition(this); _dragged = false; };
        MouseMove += (_, e) => {
            if (e.LeftButton != MouseButtonState.Pressed || _dragged) return;
            var d = e.GetPosition(this) - _pressAt;
            if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
            _dragged = true;
            DragMove();
        };
        MouseLeftButtonUp += (_, e) => {
            if (_dragged || IsControl(e.OriginalSource as DependencyObject)) return;
            _ = Reload(true);
        };

        LocationChanged += (_, _) => SaveState();
        Restore();

        Loaded += async (_, _) => {
            _tray = new Tray(
                AddAccount, () => _ = Reload(true), TogglePin, ToggleShow, Close,
                () => Topmost,
                () => Accounts.List().Select(a => (a.Id, ShortMail(a.Email))).ToList(),
                id => { Accounts.MoveUp(id); _ = Reload(false); },
                DeleteAccount,
                () => _ = Reload(false));
            Tick(TimeSpan.FromSeconds(20), Repaint);
            Tick(TimeSpan.FromSeconds(60), () => _ = Reload(false));
            await Reload(false);
        };
        Closed += (_, _) => _tray?.Dispose();
    }

    /// <summary>
    /// A place to grab the panel. Dragging works anywhere, but with the whole surface
    /// wired to refresh there was nowhere obviously safe to take hold of.
    /// </summary>
    private UIElement Grip() {
        var bar = new Border {
            Tag = "control", Height = 11, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move",
            Child = new System.Windows.Shapes.Rectangle {
                Width = 52, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = GripIdle,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        bar.MouseEnter += (_, _) => ((System.Windows.Shapes.Rectangle)bar.Child).Fill = GripHot;
        bar.MouseLeave += (_, _) => ((System.Windows.Shapes.Rectangle)bar.Child).Fill = GripIdle;
        return bar;
    }

    /// <summary>Anything tagged as a control handles its own click; the panel must not also refresh.</summary>
    private static bool IsControl(DependencyObject node) {
        for (; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Tag: "control" }) return true;
        return false;
    }

    private void ToggleShow() {
        if (IsVisible) { Hide(); return; }
        Show();
        Activate();
    }

    /// <summary>
    /// Remove a profile, credentials and all. Confirmed first: this cannot be undone and
    /// the login has to be done again from scratch.
    /// </summary>
    private void DeleteAccount(string id, string label) {
        var ok = Modal.Confirm(this, "Delete " + label + "?",
            "The account folder and its saved login are removed, and this cannot be undone. "
            + "Shared skills, agents, plugins and history are untouched.",
            "Delete");
        if (!ok) return;

        var acct = Accounts.List().Find(a => a.Id == id);
        if (acct == null) return;
        var err = Accounts.Delete(acct);
        if (err != null) { Modal.Alert(this, "Could not delete", err); return; }
        _ = Reload(false);
    }

    /// <summary>Ask for a name, make the folder, then open a terminal to log in with.</summary>
    private void AddAccount() {
        var dlg = new NameDialog(Accounts.Validate) { Owner = IsVisible ? this : null };
        if (dlg.ShowDialog() != true) return;

        var err = Accounts.Create(dlg.Value);
        if (err != null) { Modal.Alert(this, "Could not create the account", err); return; }
        _ = Reload(false);
    }

    private void Tick(TimeSpan every, Action act) {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = every };
        t.Tick += (_, _) => act();
        t.Start();
    }

    // ---- window state -------------------------------------------------------------

    private void Restore() {
        Topmost = true;
        double? x = null, y = null;
        try {
            var s = JsonNode.Parse(File.ReadAllText(_statePath));
            if (s?["x"] != null) x = s["x"].GetValue<double>();
            if (s?["y"] != null) y = s["y"].GetValue<double>();
            if (s?["pinned"] != null) Topmost = s["pinned"].GetValue<bool>();
        } catch { }

        // A monitor that is no longer attached would otherwise hide the widget for good.
        if (x == null || y == null || !OnScreen(x.Value, y.Value)) {
            var wa = SystemParameters.WorkArea;
            x = wa.Right - Width - 24;
            y = wa.Top + 24;
        }
        Left = x.Value;
        Top = y.Value;
    }

    private static bool OnScreen(double x, double y) {
        var vx = SystemParameters.VirtualScreenLeft;
        var vy = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        // Require a decent slice of the title bar to be reachable, not just one pixel.
        return x + 80 > vx && x + 40 < vx + vw && y + 24 > vy && y + 20 < vy + vh;
    }

    private void SaveState() {
        try {
            Directory.CreateDirectory(IOPath.GetDirectoryName(_statePath));
            File.WriteAllText(_statePath,
                new JsonObject { ["x"] = Left, ["y"] = Top, ["pinned"] = Topmost }.ToJsonString());
        } catch { }
    }

    private void TogglePin() { Topmost = !Topmost; SaveState(); }

    // ---- formatting ---------------------------------------------------------------

    /// <summary>Drop the top-level domain: it is the same for every account and costs width.</summary>
    private static string ShortMail(string email) {
        var at = email.LastIndexOf('@');
        if (at < 0) return email;
        var dot = email.LastIndexOf('.');
        return dot > at ? email.Substring(0, dot) : email;
    }

    private static string Ago(DateTimeOffset t) {
        var m = (int)Math.Round((DateTimeOffset.UtcNow - t).TotalMinutes);
        return m < 1 ? "just now" : m + "m ago";
    }

    private static string WaitText(DateTimeOffset until) {
        var m = (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalMinutes);
        return m > 0 ? "retry in " + m + "m" : "retrying soon";
    }

    /// <summary>Time left, not the wall clock: HH:MM for the session window, DD:HH:MM for weekly.</summary>
    private static string Remaining(DateTimeOffset at, bool withDays) {
        var span = at - DateTimeOffset.UtcNow;
        var mins = span > TimeSpan.Zero ? (int)span.TotalMinutes : 0;
        int h = mins / 60, m = mins % 60;
        return withDays
            ? $"{h / 24:00}:{h % 24:00}:{m:00}"
            : $"{h:00}:{m:00}";
    }

    private static Brush ColourFor(Bar b) {
        if (b.Severity == "critical") return Scale[0].Colour;
        foreach (var (min, colour) in Scale) if (b.Percent >= min) return colour;
        return Live;
    }

    // ---- gauges -------------------------------------------------------------------

    private static ShapePath Circle(Brush stroke, double r, double c) => new() {
        Data = new EllipseGeometry(new Point(c, c), r, r), Stroke = stroke, StrokeThickness = 3,
    };

    private static UIElement Gauge(Bar b) {
        const double r = 10, c = 13;
        // Stacked rather than overlaid, so the gap under the ring is an explicit margin.
        var grid = new StackPanel { Width = 26, Margin = new Thickness(1, 0, 1, 0) };
        var ring = new Grid { Width = 26, Height = 26 };

        // Both rings are drawn from geometry at the same radius. An Ellipse shape would
        // inset its stroke instead, leaving the full ring 1.5px smaller than a partial arc.
        ring.Children.Add(Circle(Track, r, c));

        if (b.Percent >= 100) {
            ring.Children.Add(Circle(ColourFor(b), r, c));
        } else if (b.Percent > 0) {
            var a = b.Percent * 3.6 * Math.PI / 180.0;
            var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
            fig.Segments.Add(new ArcSegment {
                Point = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)),
                Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = b.Percent > 50,
            });
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            ring.Children.Add(new ShapePath {
                Data = geo, Stroke = ColourFor(b), StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        ring.Children.Add(new TextBlock {
            Text = b.Percent.ToString(CultureInfo.InvariantCulture), Foreground = Fg, FontSize = 8,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });

        grid.Children.Add(ring);
        grid.Children.Add(new TextBlock {
            Text = b.Short, Foreground = Dim, FontSize = 7.5, LineHeight = 9,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            // The label counts toward the gauge's height: ring and label centre together
            // against the text beside them, as one block.
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        grid.ToolTip = b.Label + " " + b.Percent + "%";
        return grid;
    }

    // ---- rendering ----------------------------------------------------------------

    private void Repaint() { foreach (var t in _tickers) t(); }

    private async Task Reload(bool force) {
        if (force && _busy) return;
        _busy = force;

        var list = Accounts.List();
        Accounts.WriteShims(list);          // claude1..claudeN follow the order shown here
        Accounts.SyncShared(list);          // CLAUDE.md and settings.json follow the default profile
        _accounts.Children.Clear();
        _tickers.Clear();

        var pending = new List<Task>();
        for (var i = 0; i < list.Count; i++) {
            var a = list[i];
            var card = AccountCard(a, i + 1, out var fill);
            _accounts.Children.Add(card);
            if (!a.Expired) pending.Add(FillAsync(a, force, fill));
        }
        PaintSessions();
        Repaint();

        await Task.WhenAll(pending);
        _busy = false;
    }

    private async Task FillAsync(Account a, bool force, Action<Usage> fill) {
        var u = await _store.GetAsync(a, force);
        fill(u);
        Repaint();
    }

    private Border AccountCard(Account a, int index, out Action<Usage> fill) {
        var mail = new TextBlock {
            // WPF sets SemiBold a touch wider than the browser did; 10.5 keeps the
            // longest address on one line at this width.
            Foreground = Fg, FontWeight = FontWeights.SemiBold, FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "claude" + index + "  —  " + a.Dir,
        };
        mail.Inlines.Add(new System.Windows.Documents.Run(index + "  ") {
            Foreground = Badge, FontSize = 9, FontWeight = FontWeights.Bold });
        mail.Inlines.Add(new System.Windows.Documents.Run(ShortMail(a.Email)));
        var meta = new TextBlock { Foreground = Dim, FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis };
        var resets = new StackPanel();
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(mail);
        left.Children.Add(meta);
        left.Children.Add(resets);

        var right = new StackPanel {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6, 0, 0, 0),
        };

        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(left);

        if (a.Expired) {
            // Nothing but the name and the way out: the numbers would be stale anyway.
            meta.Visibility = Visibility.Collapsed;
            resets.Visibility = Visibility.Collapsed;
            var link = new TextBlock {
                Tag = "control", Text = "Reconnect", Foreground = Fix, FontSize = 10, Cursor = Cursors.Hand,
                TextDecorations = TextDecorations.Underline,
                ToolTip = a.ExpiryReason + " — opens a terminal on this account so you can run /login",
            };
            link.MouseLeftButtonDown += (_, e) => { e.Handled = true; Accounts.Reconnect(a); };
            right.Children.Add(link);
            fill = _ => { };
        } else {
            fill = u => Paint(u, meta, resets, right);
        }

        return new Border {
            Background = Card, BorderBrush = CardLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 6, 7, 6),
            Margin = new Thickness(0, 0, 0, 4), Child = row,
        };
    }

    private void Paint(Usage u, TextBlock meta, StackPanel resets, StackPanel gauges) {
        gauges.Children.Clear();
        if (u.HasBars) foreach (var b in u.Bars) gauges.Children.Add(Gauge(b));
        else gauges.Children.Add(new TextBlock {
            Text = u.Error ?? "", Foreground = Danger, FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Reset line: quiet label, loud number, recomputed on every tick.
        _tickers.Add(() => {
            resets.Children.Clear();
            // One line per window kind. The per-model weekly limit resets with the weekly
            // one, so a second weekly line would just repeat the same countdown.
            var seenGroups = new HashSet<string>();
            foreach (var b in u.Bars ?? new List<Bar>()) {
                if (!seenGroups.Add(b.Group ?? "weekly")) continue;
                // One window per line: the pair was too wide to read at a glance.
                var line = new TextBlock { FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis };
                line.Inlines.Add(new System.Windows.Documents.Run(b.Short + " ") {
                    Foreground = WindowLabel, FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new System.Windows.Documents.Run(
                    b.ResetsAt != null ? Remaining(b.ResetsAt.Value, b.Group != "session") : "Not started") {
                    Foreground = Fg, FontWeight = FontWeights.Bold, FontSize = 10,
                });
                resets.Children.Add(line);
            }
            resets.Visibility = resets.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            var bits = new List<string>();
            if (u.Note != null) bits.Add(u.Note);
            if (u.At != null && DateTimeOffset.UtcNow - u.At > UsageStore.StaleAfter) bits.Add(Ago(u.At.Value));
            if (u.BlockedUntil > DateTimeOffset.UtcNow) bits.Add(WaitText(u.BlockedUntil.Value));
            meta.Text = string.Join(" · ", bits);
            meta.Visibility = bits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void PaintSessions() {
        var rows = Sessions.Running();
        _sessions.Children.Clear();
        if (rows.Count == 0) return;

        var total = 0;
        foreach (var r in rows) total += r.Count;

        var box = new StackPanel();
        box.Children.Add(new TextBlock {
            Text = total == 1 ? "1 session running" : total + " sessions running", Foreground = Dim, FontSize = 9,
            Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (var r in rows) {
            var name = new TextBlock {
                Foreground = Fg, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (r.Live) name.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = Live });
            name.Inlines.Add(new System.Windows.Documents.Run(r.Display + (r.Count > 1 ? " ×" + r.Count : "")));

            var age = new TextBlock {
                // A running session that has gone quiet shows how long it has waited.
                Text = r.At != null ? Ago(r.At.Value) : "", Foreground = Dim, FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new DockPanel();
            DockPanel.SetDock(age, Dock.Right);
            row.Children.Add(age);
            row.Children.Add(name);

            // Not clickable any more, but the untrimmed path is still worth having on hover.
            box.Children.Add(new Border {
                Child = row, Padding = new Thickness(2, 1, 2, 1), ToolTip = r.Cwd,
            });
        }

        _sessions.Children.Add(new Border {
            Background = Card, BorderBrush = CardLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6), Child = box,
        });
    }
}
