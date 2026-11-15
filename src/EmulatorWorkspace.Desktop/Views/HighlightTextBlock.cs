using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// UX-07: a single-line text run that paints the characters a fuzzy query
/// matched in the accent colour. The palette's rows are one 13 DIP title and one
/// 12 DIP description; both need the same treatment, and WPF's TextBlock has no
/// way to bind an InlineCollection, so this subclass rebuilds its inlines when
/// the text or the matched positions change.
///
/// Positions are character offsets into <see cref="TextValue"/>. A position that
/// falls outside the string is ignored rather than throwing, so a stale index
/// can never break a render pass.
/// </summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty TextValueProperty = DependencyProperty.Register(
        nameof(TextValue),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty HighlightIndexesProperty = DependencyProperty.Register(
        nameof(HighlightIndexes),
        typeof(IEnumerable<int>),
        typeof(HighlightTextBlock),
        new PropertyMetadata(null, OnContentChanged));

    /// <summary>
    /// UX-08: paints the matched runs on the accent-soft surface as well as in the
    /// accent colour. The Logcat page needs that step because the Graphite accent
    /// sits very close to text1 in luminance; the palette keeps its plain accent
    /// runs (the default), so its look is unchanged.
    /// </summary>
    public static readonly DependencyProperty MarkMatchesProperty = DependencyProperty.Register(
        nameof(MarkMatches),
        typeof(bool),
        typeof(HighlightTextBlock),
        new PropertyMetadata(false, OnContentChanged));

    /// <summary>The text to render. Named to avoid hiding TextBlock.Text.</summary>
    public string TextValue
    {
        get => (string)GetValue(TextValueProperty);
        set => SetValue(TextValueProperty, value);
    }

    /// <summary>Character offsets that render in the theme accent colour.</summary>
    public IEnumerable<int>? HighlightIndexes
    {
        get => (IEnumerable<int>?)GetValue(HighlightIndexesProperty);
        set => SetValue(HighlightIndexesProperty, value);
    }

    /// <summary>Also paint the matched runs on the accent-soft surface.</summary>
    public bool MarkMatches
    {
        get => (bool)GetValue(MarkMatchesProperty);
        set => SetValue(MarkMatchesProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HighlightTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();

        var text = TextValue;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        HashSet<int>? matches = null;
        if (HighlightIndexes is { } indexes)
        {
            foreach (var index in indexes)
            {
                if (index >= 0 && index < text.Length)
                {
                    (matches ??= new HashSet<int>()).Add(index);
                }
            }
        }

        if (matches is null || matches.Count == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var runStart = 0;
        var highlighted = matches.Contains(0);
        for (var i = 1; i <= text.Length; i++)
        {
            var current = i < text.Length && matches.Contains(i);
            if (i == text.Length || current != highlighted)
            {
                var run = new Run(text.Substring(runStart, i - runStart));
                if (highlighted)
                {
                    // DynamicResource so a live theme switch repaints the accent
                    // the way every other accent surface does.
                    run.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                    if (MarkMatches)
                    {
                        run.SetResourceReference(TextElement.BackgroundProperty, "AccentSoftTint");
                    }
                }

                Inlines.Add(run);
                runStart = i;
                highlighted = current;
            }
        }
    }
}
