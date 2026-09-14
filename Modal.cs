using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ECS;

/// <summary>
/// Small confirm and alert windows drawn in the panel's own language, so a destructive
/// step does not hand the user off to a stock system dialog that looks nothing like ECS.
/// </summary>
internal sealed class Modal : Window {
    private static SolidColorBrush B(string hex) {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static readonly Brush Fg = B("#ece7df");
    private static readonly Brush Dim = B("#8d8578");
    private static readonly Brush Danger = B("#f38a68");
    private static readonly Brush Accent = B("#f4c071");

    private Modal(string title, string body, string confirmText, bool destructive, Window owner) {
        Title = title;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Pretendard, Segoe UI, Malgun Gothic");
        FontSize = 12;
        Owner = owner is { IsVisible: true } ? owner : null;
        WindowStartupLocation = Owner == null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock {
            Text = title, Foreground = destructive ? Danger : Fg,
            FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock {
            Text = body, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0), LineHeight = 17,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        });

        var buttons = new StackPanel {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        if (confirmText != null) {
            buttons.Children.Add(Link(confirmText, destructive ? Danger : Accent, () => {
                DialogResult = true;
            }));
            buttons.Children.Add(Link("Cancel", Dim, Close));
        } else {
            buttons.Children.Add(Link("OK", Accent, Close));
        }
        stack.Children.Add(buttons);

        Content = new Border {
            Background = B("#1c1a18"), BorderBrush = B("#38342d"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Child = stack,
        };

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) => {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter && confirmText == null) Close();
        };
    }

    private TextBlock Link(string text, Brush colour, Action act) {
        var t = new TextBlock {
            Text = text, Foreground = colour, FontSize = 11, Cursor = Cursors.Hand,
            Margin = new Thickness(14, 0, 0, 0), FontWeight = FontWeights.SemiBold,
        };
        t.MouseLeftButtonDown += (_, e) => { e.Handled = true; act(); };
        return t;
    }

    /// <summary>Ask before something irreversible. Cancel is the default: nothing is preselected.</summary>
    public static bool Confirm(Window owner, string title, string body, string confirmText) =>
        new Modal(title, body, confirmText, true, owner).ShowDialog() == true;

    public static void Alert(Window owner, string title, string body) =>
        new Modal(title, body, null, false, owner).ShowDialog();
}
