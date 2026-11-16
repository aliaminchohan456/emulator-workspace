using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EmulatorWorkspace.Desktop.ViewModels;

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
///
/// UX-18: the tone comes from the one lifecycle, so a chip cannot disagree
/// with the word printed beside it.
/// </summary>
public sealed class StateToSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var chip = string.Equals(parameter?.ToString(), "chip", StringComparison.OrdinalIgnoreCase);
        var lifecycle = DeviceLifecycle.From(value?.ToString(), null, false, false);
        var key = lifecycle.Tone switch
        {
            "ok" => "SuccessBg",
            "warn" => "WarningBg",
            "err" => "DangerBg",
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
            ("warning", "border") => "WarningBorder",
            ("warning", _) => "Warning",
            ("error", "bg") => "DangerBg",
            ("error", "border") => "DangerBorder",
            ("error", _) => "Danger",
            ("ok", "bg") => "SuccessBg",
            ("ok", "border") => "SuccessBorder",
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
        // UX-18: the same lifecycle the surfaces read, so a coloured state dot
        // can never contradict the word next to it.
        var lifecycle = DeviceLifecycle.From(value?.ToString(), null, false, false);
        var key = lifecycle.Tone switch
        {
            "ok" => "Success",
            "warn" => "Warning",
            "err" => "Danger",
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

/// <summary>
/// A log level letter to the brush a chip or a marker needs. UX-08 kept this
/// converter out of the Logcat drawer on purpose: a converter hands back the brush
/// it resolved at conversion time, so a live theme switch would leave the old
/// theme's colour behind. The drawer's level glyph and stream chip use
/// DataTriggers over DynamicResource brushes instead; the converter stays for any
/// surface that is rebuilt on a theme change.
/// </summary>
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

/// <summary>
/// UX-04b: a count badge is shown only when there is something to count, so the
/// rail never carries a "0" bubble. UX-09 adds <see cref="Invert"/> for the empty
/// state of a table, which is visible only while the same count is zero.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int number => number,
            System.Collections.ICollection collection => collection.Count,
            _ => 0,
        };

        var visible = count > 0;
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
/// UX-07: the command palette stores the resource key of its row icon
/// ("IconStop") instead of a brush or a geometry, so the same entry list can be
/// filtered and tested without WPF. This resolves that key against the
/// application resources; a key that no longer exists draws nothing, which is
/// visible in a screenshot rather than silently replaced by a stand-in glyph.
/// </summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString();
        if (string.IsNullOrEmpty(key))
        {
            return Geometry.Empty;
        }

        return Application.Current?.TryFindResource(key) as Geometry ?? Geometry.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// UX-06: a long filesystem path keeps its beginning and its tail with an
/// ellipsis in the middle, because the last folder is the part a reader needs.
/// The full string always stays in the row's tooltip. The limit is a character
/// count (a path row is a fixed 460 DIP drawer minus its padding and buttons, so
/// the mono fit is known); a value already within the limit is returned as is.
/// </summary>
public sealed class MiddleEllipsisConverter : IValueConverter
{
    // A path row in the 460 DIP drawer leaves about 300 DIP for the value after
    // the label column and the two 28 DIP actions; 38 mono characters at 12 DIP
    // stay inside that at every theme and DPI, so the middle ellipsis is the one
    // the reader sees rather than a second tail trim.
    public int MaxLength { get; set; } = 38;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        var limit = MaxLength;
        if (parameter is string raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            limit = parsed;
        }

        if (limit < 8 || text.Length <= limit)
        {
            return text;
        }

        // 60 / 40: enough head to recognise the workspace, enough tail to read
        // the leaf folder, and one character for the ellipsis itself.
        var head = (int)((limit - 1) * 0.6);
        var tail = limit - 1 - head;
        return string.Concat(text.AsSpan(0, head), "\u2026", text.AsSpan(text.Length - tail));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
