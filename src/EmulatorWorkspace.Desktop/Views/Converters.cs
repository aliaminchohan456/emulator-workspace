using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EmulatorWorkspace.Desktop.Views;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// UX-03: a sentence is shown only when it has text, so an optional hint never
/// leaves a gap in the stage card. UX-04 adds <see cref="Invert"/> for the
/// search placeholder, which is visible only while the field is empty.
/// </summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        if (Invert)
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// UX-04: shows an element only when the value equals the converter parameter.
/// Used to pick the single primary action a library card offers.
/// </summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// UX-04: the soft tint behind a state chip or a card thumbnail. State is never
/// carried by this colour alone; every chip prints its word.
/// </summary>
public sealed class StateToSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var chip = string.Equals(parameter?.ToString(), "chip", StringComparison.OrdinalIgnoreCase);
        var key = (value?.ToString() ?? "") switch
        {
            "Running" => "SuccessBg",
            "Starting" or "Stopping" => "WarningBg",
            "Failed" => "DangerBg",
            _ => chip ? "ChipBg" : "PanelAlt",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// UX-04: tone ("info" | "warning" | "error" | "ok") to the brush a chip or a
/// banner needs. The parameter selects the role: "bg" (soft tint), "border",
/// "fg" (text/icon) or "dot".
/// </summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = (value?.ToString() ?? "info").ToLowerInvariant();
        var role = (parameter?.ToString() ?? "bg").ToLowerInvariant();
        var key = (tone, role) switch
        {
            ("warning", "bg") => "WarningBg",
            ("warning", "border") => "Warning",
            ("warning", _) => "Warning",
            ("error", "bg") => "DangerBg",
            ("error", "border") => "DangerBorder",
            ("error", _) => "Danger",
            ("ok", "bg") => "SuccessBg",
            ("ok", "border") => "Success",
            ("ok", _) => "Success",
            (_, "bg") => "PanelAlt",
            (_, "border") => "BorderBrush",
            _ => "TextSecondary",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "") switch
        {
            "Running" => "Success",
            "Starting" or "Stopping" => "Warning",
            "Failed" or "Unknown" => "Danger",
            _ => "TextSecondary",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// UX-03: the viewport stage tone ("info" | "warning" | "error" | "success") to
/// a themed brush. The tone never carries the state on its own; every stage
/// also shows a title and a sentence.
/// </summary>
public sealed class StageToneToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "info") switch
        {
            "error" => "Danger",
            "warning" => "Warning",
            "success" => "Success",
            _ => "TextSecondary",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// UX-03: one boot-stepper item ("done" | "active" | "pending" | "unknown") to a
/// themed brush. The item always prints its state word beside the dot.
/// </summary>
public sealed class StepStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "pending") switch
        {
            "done" => "Success",
            "active" => "Accent",
            "unknown" => "TextSecondary",
            _ => "TextTertiary",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "I") switch
        {
            "E" or "F" or "A" => "Danger",
            "W" => "Warning",
            "D" => "TextSecondary",
            _ => "TextPrimary",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
