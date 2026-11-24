using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Gestaobim.Ai.Revit;

/// <summary>Componentes visuais compartilhados pelas janelas de IA (Configurar IA, GB Cotas e GB Render).</summary>
public static class AiUi
{
    public static readonly SolidColorBrush WindowBg = Freeze(Color.FromRgb(0xF1, 0xF5, 0xF9));
    public static readonly SolidColorBrush HeaderBg = Freeze(Color.FromRgb(0x12, 0x26, 0x3A));
    public static readonly SolidColorBrush HeaderSub = Freeze(Color.FromRgb(0xC4, 0xD6, 0xE8));
    public static readonly SolidColorBrush CardBorder = Freeze(Color.FromRgb(0xE2, 0xE8, 0xF0));
    public static readonly SolidColorBrush TitleFg = Freeze(Color.FromRgb(0x1E, 0x29, 0x3B));
    public static readonly SolidColorBrush MutedFg = Freeze(Color.FromRgb(0x64, 0x74, 0x8B));
    public static readonly SolidColorBrush PrimaryBg = Freeze(Color.FromRgb(0x16, 0x77, 0xFF));
    public static readonly SolidColorBrush SecondaryBorder = Freeze(Color.FromRgb(0xCB, 0xD5, 0xE1));
    public static readonly SolidColorBrush OkBg = Freeze(Color.FromRgb(0xEC, 0xFD, 0xF5));
    public static readonly SolidColorBrush OkFg = Freeze(Color.FromRgb(0x15, 0x80, 0x3D));
    public static readonly SolidColorBrush WarnBg = Freeze(Color.FromRgb(0xFE, 0xF3, 0xC7));
    public static readonly SolidColorBrush WarnFg = Freeze(Color.FromRgb(0x92, 0x40, 0x0E));
    public static readonly SolidColorBrush ErrBg = Freeze(Color.FromRgb(0xFE, 0xF2, 0xF2));
    public static readonly SolidColorBrush ErrFg = Freeze(Color.FromRgb(0xB9, 0x1C, 0x1C));
    public static readonly SolidColorBrush InfoBg = Freeze(Color.FromRgb(0xEF, 0xF6, 0xFF));
    public static readonly SolidColorBrush InfoFg = Freeze(Color.FromRgb(0x1D, 0x4E, 0xD8));

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Border Header(string title, string subtitle)
    {
        var stack = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
        stack.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = subtitle, Foreground = HeaderSub, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        return new Border { Background = HeaderBg, Child = stack };
    }

    public static Border Card(string? title, UIElement content, Thickness? margin = null)
    {
        var stack = new StackPanel();
        if (!string.IsNullOrEmpty(title))
            stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = TitleFg, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(content);
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = margin ?? new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    public static TextBlock Note(string text, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = MutedFg,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0, 4, 0, 0),
    };

    public static Button PrimaryButton(string text, bool isDefault = false) => new()
    {
        Content = text,
        IsDefault = isDefault,
        Background = PrimaryBg,
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        FontWeight = FontWeights.SemiBold,
        Padding = new Thickness(16, 7, 16, 7),
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    public static Button SecondaryButton(string text, bool isCancel = false) => new()
    {
        Content = text,
        IsCancel = isCancel,
        Background = Brushes.White,
        Foreground = TitleFg,
        BorderBrush = SecondaryBorder,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 6, 12, 6),
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    public static (Border Box, TextBlock Text, ProgressBar Bar) StatusBanner()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = TitleFg };
        var bar = new ProgressBar { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(text);
        stack.Children.Add(bar);
        var box = new Border
        {
            Background = InfoBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = stack,
        };
        return (box, text, bar);
    }

    public static void SetStatus(Border box, TextBlock text, ProgressBar bar, string message, StatusKind kind, bool busy = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            box.Visibility = Visibility.Collapsed;
            bar.Visibility = Visibility.Collapsed;
            return;
        }
        box.Visibility = Visibility.Visible;
        text.Text = message;
        bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        (box.Background, text.Foreground) = kind switch
        {
            StatusKind.Ok => (OkBg, OkFg),
            StatusKind.Warn => (WarnBg, WarnFg),
            StatusKind.Error => (ErrBg, ErrFg),
            _ => (InfoBg, InfoFg),
        };
    }

    public static bool LooksLikeImageModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true; // vazio = automatico
        return model.Contains("image", StringComparison.OrdinalIgnoreCase)
               || model.Contains("imagen", StringComparison.OrdinalIgnoreCase)
               || model.Contains("banana", StringComparison.OrdinalIgnoreCase)
               || model.Contains("gpt-image", StringComparison.OrdinalIgnoreCase);
    }
}

public enum StatusKind { Info, Ok, Warn, Error }
