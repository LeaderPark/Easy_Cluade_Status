using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ECS;

/// <summary>
/// Asks for an account name and nothing else. The folder is ECS's job, not the user's:
/// a picker cannot even open the accounts root before the first account exists.
/// </summary>
internal sealed class NameDialog : Window {
    private readonly TextBox _box;
    private readonly TextBlock _hint;
    private readonly Func<string, string> _validate;

    public string Value { get; private set; }

    public NameDialog(Func<string, string> validate) {
        _validate = validate;

        Title = "New account";
        Width = 300;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Pretendard, Segoe UI, Malgun Gothic");
        FontSize = 12;

        var fg = Brush("#ece7df");
        var dim = Brush("#8d8578");

        _box = new TextBox {
            Background = Brush("#26241f"), Foreground = fg, CaretBrush = fg, FontSize = 13,
            BorderBrush = Brush("#4d4840"), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 6, 0, 0),
        };
        _box.KeyDown += (_, e) => {
            if (e.Key == Key.Enter) Submit();
            else if (e.Key == Key.Escape) Close();
        };

        _hint = new TextBlock {
            Text = "letters, digits, . _ - only", Foreground = dim, FontSize = 10,
            Margin = new Thickness(2, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
        };

        var ok = Link("Create", Brush("#f4c071"), Submit);
        var cancel = Link("Cancel", dim, Close);
        var buttons = new StackPanel {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel();
        body.Children.Add(new TextBlock {
            Text = "Account name", Foreground = fg, FontWeight = FontWeights.SemiBold,
        });
        body.Children.Add(_box);
        body.Children.Add(_hint);
        body.Children.Add(buttons);

        Content = new Border {
            Background = Brush("#1c1a18"), BorderBrush = Brush("#38342d"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = body,
        };

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => _box.Focus();
    }

    private static SolidColorBrush Brush(string hex) {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private TextBlock Link(string text, Brush colour, Action act) {
        var t = new TextBlock {
            Text = text, Foreground = colour, FontSize = 11, Cursor = Cursors.Hand,
            Margin = new Thickness(12, 0, 0, 0),
        };
        t.MouseLeftButtonDown += (_, e) => { e.Handled = true; act(); };
        return t;
    }

    private void Submit() {
        var name = _box.Text.Trim();
        var err = _validate(name);
        if (err != null) {
            _hint.Text = err;
            _hint.Foreground = Brush("#e0806a");
            return;
        }
        Value = name;
        DialogResult = true;
    }
}
