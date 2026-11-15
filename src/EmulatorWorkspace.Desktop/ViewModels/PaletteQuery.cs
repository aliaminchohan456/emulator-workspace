using System;
using System.Collections.Generic;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// UX-07: the group headers the command palette prints above its rows. They
/// describe what the commands do, not a product area, because every entry is
/// still the same command the toolbar submits. "ROOT LAB · PRIVILEGED" keeps the
/// design sheet's rule that amber means privileged.
/// </summary>
public static class PaletteGroups
{
    public const string Device = "DEVICE";

    public const string AppsAndCapture = "APPS & CAPTURE";

    public const string Diagnostics = "DIAGNOSTICS";

    public const string Privileged = "ROOT LAB · PRIVILEGED";
}

/// <summary>One group header plus the entries that belong to it.</summary>
public sealed class PaletteSection<T>
{
    public PaletteSection(string group, IReadOnlyList<T> items)
    {
        Group = group;
        Items = items;
    }

    public string Group { get; }

    public IReadOnlyList<T> Items { get; }
}

/// <summary>
/// One ranked search hit: the entry, its score, and the character positions a
/// fuzzy query matched in the title and the subtitle. The palette paints those
/// positions in the accent colour.
/// </summary>
public sealed class PaletteHit<T>
{
    public PaletteHit(T item, int score, IReadOnlyList<int> labelHits, IReadOnlyList<int> hintHits)
    {
        Item = item;
        Score = score;
        LabelHits = labelHits;
        HintHits = hintHits;
    }

    public T Item { get; }

    public int Score { get; }

    public IReadOnlyList<int> LabelHits { get; }

    public IReadOnlyList<int> HintHits { get; }
}

/// <summary>
/// UX-07: the palette's grouping and fuzzy search, kept free of WPF so it can be
/// tested without a window.
///
/// With no query the list keeps the mockup's sections: one header per group, the
/// order the groups first appear in, and the source order inside a group. With a
/// query the list becomes one flat ranked list (the mockup collapses its groups
/// while searching): a title match outranks a subtitle match, an early match
/// outranks a late one, and a contiguous match outranks scattered characters.
/// Characters matched by a subsequence are reported so the row can colour them.
/// </summary>
public static class PaletteQuery
{
    private const int LabelBaseScore = 1000;

    private const int HintBaseScore = 400;

    private const int WordStartBonus = 120;

    /// <summary>The text a query is compared with: trimmed, never null.</summary>
    public static string Normalize(string? query) => (query ?? "").Trim();

    /// <summary>
    /// Buckets <paramref name="entries"/> by group, keeping both the group order
    /// and the order inside a group exactly as the caller supplied them. A group
    /// is emitted once even if its entries are not contiguous, so the header list
    /// never repeats. No filtering happens here: this is the empty-query view.
    /// </summary>
    public static IReadOnlyList<PaletteSection<T>> Group<T>(
        IReadOnlyList<T> entries,
        Func<T, string> group)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(group);

        var sections = new List<PaletteSection<T>>();
        var index = new Dictionary<string, List<T>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var name = group(entry) ?? "";
            if (!index.TryGetValue(name, out var bucket))
            {
                bucket = new List<T>();
                index.Add(name, bucket);
                sections.Add(new PaletteSection<T>(name, bucket));
            }

            bucket.Add(entry);
        }

        return sections;
    }

    /// <summary>
    /// Ranks <paramref name="entries"/> against <paramref name="query"/>. The
    /// result is a flat list ordered by score (highest first) and, for equal
    /// scores, by the caller's source order, so a query never shuffles rows
    /// behind the operator's back. An empty query returns every entry in source
    /// order with no highlighted characters.
    /// </summary>
    public static IReadOnlyList<PaletteHit<T>> Rank<T>(
        IReadOnlyList<T> entries,
        string? query,
        Func<T, string> label,
        Func<T, string> hint)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(hint);

        var text = Normalize(query);
        var hits = new List<PaletteHit<T>>(entries.Count);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (text.Length == 0)
            {
                hits.Add(new PaletteHit<T>(entry, 0, Array.Empty<int>(), Array.Empty<int>()));
                continue;
            }

            var labelMatch = FuzzyMatch(label(entry), text);
            var hintMatch = FuzzyMatch(hint(entry), text);
            if (!labelMatch.Matched && !hintMatch.Matched)
            {
                continue;
            }

            var score = (labelMatch.Matched ? LabelBaseScore + labelMatch.Score : 0)
                + (hintMatch.Matched ? HintBaseScore + hintMatch.Score : 0);

            // Stable: the original index breaks a tie, and the list is already
            // in source order, so OrderByDescending alone keeps it.
            hits.Add(new PaletteHit<T>(entry, score, labelMatch.Indexes, hintMatch.Indexes));
        }

        if (text.Length == 0)
        {
            return hits;
        }

        var ranked = new List<(int Order, PaletteHit<T> Hit)>(hits.Count);
        for (var i = 0; i < hits.Count; i++)
        {
            ranked.Add((i, hits[i]));
        }

        ranked.Sort((a, b) =>
        {
            var byScore = b.Hit.Score.CompareTo(a.Hit.Score);
            return byScore != 0 ? byScore : a.Order.CompareTo(b.Order);
        });

        var result = new PaletteHit<T>[ranked.Count];
        for (var i = 0; i < ranked.Count; i++)
        {
            result[i] = ranked[i].Hit;
        }

        return result;
    }

    /// <summary>A fuzzy match plus the character positions it consumed.</summary>
    public readonly record struct FuzzyResult(bool Matched, int Score, IReadOnlyList<int> Indexes);

    /// <summary>
    /// Matches <paramref name="query"/> against one field. A contiguous
    /// case-insensitive substring wins and scores by how early it starts (a
    /// word-start match scores higher still). Otherwise every query character
    /// must appear in order; that scores lower and penalises the span and the
    /// start offset, so "ad" prefers "ADB root" over a scattered match deep in a
    /// sentence. An empty query matches with no positions.
    /// </summary>
    public static FuzzyResult FuzzyMatch(string? value, string query)
    {
        var text = value ?? "";
        if (query.Length == 0)
        {
            return new FuzzyResult(true, 0, Array.Empty<int>());
        }

        if (text.Length == 0)
        {
            return new FuzzyResult(false, 0, Array.Empty<int>());
        }

        var start = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var indexes = new int[query.Length];
            for (var i = 0; i < query.Length; i++)
            {
                indexes[i] = start + i;
            }

            var score = 400 - (start * 4);
            if (start == 0 || text[start - 1] == ' ' || text[start - 1] == '·')
            {
                score += WordStartBonus;
            }

            return new FuzzyResult(true, score, indexes);
        }

        var positions = new List<int>(query.Length);
        var cursor = 0;
        foreach (var wanted in query)
        {
            var found = -1;
            for (var i = cursor; i < text.Length; i++)
            {
                if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(wanted))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return new FuzzyResult(false, 0, Array.Empty<int>());
            }

            positions.Add(found);
            cursor = found + 1;
        }

        var span = positions[^1] - positions[0] + 1;
        var scattered = 150 - span - (positions[0] * 2);
        if (positions[0] == 0)
        {
            scattered += 40;
        }

        return new FuzzyResult(true, Math.Max(1, scattered), positions);
    }
}
