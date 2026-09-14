using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace ECS;

/// <summary>
/// The notification-area icon and its menu. The panel itself carries no chrome any more,
/// so every command lives here: adding an account, pinning, refreshing, quitting.
/// </summary>
internal sealed class Tray : IDisposable {
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _pin;
    private readonly WinForms.ToolStripMenuItem _boot;
    private IntPtr _iconHandle;

    // ToolStripMenuItem.Checked draws nothing while the image margin is hidden, so the
    // state is written into the label itself. The blanks keep both states aligned.
    private static string Mark(string text, bool on) => (on ? "✓  " : "     ") + text;

    private readonly WinForms.ToolStripMenuItem _order;
    private readonly WinForms.ToolStripMenuItem _delete;
    private readonly WinForms.ToolStripMenuItem _interval;

    public Tray(Action onAdd, Action onRefresh, Action onTogglePin, Action onToggleShow, Action onQuit,
                Func<bool> pinned,
                Func<List<(string Id, string Label)>> accounts, Action<string> onMoveUp,
                Action<string, string> onDelete, Action onSettingChanged) {
        var menu = new WinForms.ContextMenuStrip {
            ShowImageMargin = false,
            BackColor = Color.FromArgb(0x24, 0x22, 0x1e),
            ForeColor = Color.FromArgb(0xec, 0xe7, 0xdf),
            RenderMode = WinForms.ToolStripRenderMode.System,
        };

        menu.Items.Add(Item(Mark("Refresh now", false), onRefresh));
        menu.Items.Add(Item(Mark("Add account…", false), onAdd));
        _order = new WinForms.ToolStripMenuItem(Mark("Account order", false));
        _order.DropDown.BackColor = menu.BackColor;
        _order.DropDown.ForeColor = menu.ForeColor;
        menu.Items.Add(_order);
        _delete = new WinForms.ToolStripMenuItem(Mark("Delete account", false));
        _delete.DropDown.BackColor = menu.BackColor;
        _delete.DropDown.ForeColor = menu.ForeColor;
        menu.Items.Add(_delete);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _interval = new WinForms.ToolStripMenuItem(Mark("Refresh interval", false));
        _interval.DropDown.BackColor = menu.BackColor;
        _interval.DropDown.ForeColor = menu.ForeColor;
        foreach (var m in AppSettings.Choices) {
            var minutes = m;
            var item = new WinForms.ToolStripMenuItem(Mark(AppSettings.Label(minutes), false));
            item.Click += (_, _) => { AppSettings.SetRefresh(minutes); onSettingChanged(); };
            _interval.DropDownItems.Add(item);
        }
        menu.Items.Add(_interval);

        _pin = Item(Mark("Always on top", true), onTogglePin);
        _boot = Item(Mark("Start with Windows", true), Startup.Toggle);
        menu.Items.Add(_pin);
        menu.Items.Add(_boot);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Item(Mark("Quit", false), onQuit));

        // The menu is built once; the marks are rewritten each time it opens.
        menu.Opening += (_, _) => {
            _pin.Text = Mark("Always on top", pinned());
            for (var i = 0; i < AppSettings.Choices.Length; i++)
                _interval.DropDownItems[i].Text =
                    Mark(AppSettings.Label(AppSettings.Choices[i]),
                         AppSettings.Choices[i] == AppSettings.RefreshMinutes);
            var list = accounts();
            BuildOrderMenu(list, onMoveUp);
            BuildDeleteMenu(list, onDelete);
        };

        // Reordering takes several clicks, so keep the menu up while it is being used.
        menu.Closing += (_, e) => {
            if (e.CloseReason == WinForms.ToolStripDropDownCloseReason.ItemClicked && _reordering) {
                _reordering = false;
                e.Cancel = true;
            }
        };

        _icon = new WinForms.NotifyIcon {
            Icon = BuildIcon(),
            Text = "ECS — Claude usage",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _latest = accounts;
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) onToggleShow(); };
    }

    private bool _reordering;

    /// <summary>
    /// One entry per account below the first. Clicking one lifts it a place; the numbers
    /// are what claude1..claudeN answer to, so the list doubles as the mapping.
    /// </summary>
    private void BuildOrderMenu(List<(string Id, string Label)> accounts, Action<string> onMoveUp) {
        _order.DropDownItems.Clear();
        if (accounts.Count < 2) {
            _order.DropDownItems.Add(new WinForms.ToolStripMenuItem("(only one account)") { Enabled = false });
            return;
        }
        _order.DropDownItems.Add(new WinForms.ToolStripMenuItem("click to move up") { Enabled = false });
        _order.DropDownItems.Add(new WinForms.ToolStripSeparator());
        for (var i = 0; i < accounts.Count; i++) {
            var (id, label) = accounts[i];
            var text = "  " + (i + 1) + "   " + label;
            var item = new WinForms.ToolStripMenuItem(text) { Enabled = i > 0 };
            if (i > 0) item.Click += (_, _) => {
                _reordering = true;
                onMoveUp(id);
                BuildOrderMenu(_latest(), onMoveUp);
            };
            _order.DropDownItems.Add(item);
        }
    }

    /// <summary>
    /// Deleting throws away a login, so it lives here behind a confirmation rather than
    /// one stray click on the panel. The default profile is not offered: ECS does not own it.
    /// </summary>
    private void BuildDeleteMenu(List<(string Id, string Label)> accounts, Action<string, string> onDelete) {
        _delete.DropDownItems.Clear();
        var any = false;
        foreach (var (id, label) in accounts) {
            if (id == "default") continue;
            any = true;
            var item = new WinForms.ToolStripMenuItem("  " + label);
            item.Click += (_, _) => onDelete(id, label);
            _delete.DropDownItems.Add(item);
        }
        if (!any) _delete.DropDownItems.Add(
            new WinForms.ToolStripMenuItem("(nothing to delete)") { Enabled = false });
    }

    private Func<List<(string Id, string Label)>> _latest;

    private static WinForms.ToolStripMenuItem Item(string text, Action act) {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += (_, _) => act();
        return item;
    }

    /// <summary>Draw the icon rather than ship one: it is a ring, same as the gauges.</summary>
    private Icon BuildIcon() {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var track = new Pen(Color.FromArgb(0x80, 0x4d, 0x48, 0x40), 4f);
            using var arc = new Pen(Color.FromArgb(0xd9, 0x77, 0x57), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var box = new Rectangle(4, 4, 24, 24);
            g.DrawEllipse(track, box);
            g.DrawArc(arc, box, -90, 260);
        }
        _iconHandle = bmp.GetHicon();
        return Icon.FromHandle(_iconHandle);
    }

    public void Dispose() {
        _icon.Visible = false;
        _icon.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
    }
}
