using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// igr-feedback-ux04b item 1, the cheap version: an attached property that
/// renders a short caps header the way the design sheet tracks it. It upper-cases
/// the text and joins the glyphs with U+200A hair spaces, and it keeps the
/// original string in AutomationProperties.Name so a screen reader still hears
/// "IDENTITY" instead of a spaced-out letter run.
///
/// It is applied only to the 11 DIP section headers and the LIBRARY eyebrow, and
/// it is a one-shot transform: a header whose text already carries a hair space
/// is left alone, so a second Loaded pass cannot double-space it.
/// </summary>
public static class Caps
{
    private const char HairSpace = '\u200A';

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.RegisterAttached(
        "Tracking",
        typeof(bool),
        typeof(Caps),
        new PropertyMetadata(false, OnTrackingChanged));

    public static void SetTracking(DependencyObject element, bool value) =>
        element.SetValue(TrackingProperty, value);

    public static bool GetTracking(DependencyObject element) =>
        (bool)element.GetValue(TrackingProperty);

    private static void OnTrackingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        if (e.NewValue is true)
        {
            block.Loaded += OnLoaded;
            // The attribute may already have been set on the element; apply now
            // and again on Loaded for the case where Text arrives later.
            Apply(block);
        }
        else
        {
            block.Loaded -= OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply(sender as TextBlock);

    private static void Apply(TextBlock? block)
    {
        if (block is null)
        {
            return;
        }

        var text = block.Text;
        if (string.IsNullOrEmpty(text) || text.IndexOf(HairSpace) >= 0)
        {
            return;
        }

        var upper = text.ToUpperInvariant();
        var tracked = new StringBuilder(upper.Length * 2);
        for (var index = 0; index < upper.Length; index++)
        {
            var glyph = upper[index];
            if (glyph == ' ')
            {
                // A word break keeps a plain space; a hair space on either side
                // of it would open an uneven gap.
                tracked.Append(glyph);
                continue;
            }

            if (index > 0 && upper[index - 1] != ' ')
            {
                tracked.Append(HairSpace);
            }

            tracked.Append(glyph);
        }

        AutomationProperties.SetName(block, text);
        block.Text = tracked.ToString();
    }
}
