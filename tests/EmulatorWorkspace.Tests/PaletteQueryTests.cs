using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-07: the command palette's grouping and fuzzy search. The palette lists only
/// the commands the build really has, so these tests use the same shape: a label,
/// a description, a group and a stable source order.
/// </summary>
public sealed class PaletteQueryTests
{
    private sealed record Row(string Group, string Label, string Hint);

    private static readonly Row[] Rows =
    [
        new(PaletteGroups.Device, "Create device", "Create a new managed guest"),
        new(PaletteGroups.Device, "Device library", "Browse, search and manage the device library"),
        new(PaletteGroups.Device, "Start device", "Start the selected guest"),
        new(PaletteGroups.Device, "Stop device", "Stop the selected guest"),
        new(PaletteGroups.Device, "Cold boot", "Cold boot the selected guest"),
        new(PaletteGroups.AppsAndCapture, "Install APK", "Install an APK on the selected guest"),
        new(PaletteGroups.AppsAndCapture, "Screenshot", "Capture a PNG of the selected guest"),
        new(PaletteGroups.Diagnostics, "Refresh ADB state", "Re-read the ADB daemon identity"),
        new(PaletteGroups.Privileged, "ADB root", "Run the guest ADB daemon as root"),
        new(PaletteGroups.Privileged, "ADB unroot", "Return the guest ADB daemon to the shell user"),
    ];

    private static IReadOnlyList<PaletteSection<Row>> Group() =>
        PaletteQuery.Group(Rows, row => row.Group);

    private static IReadOnlyList<PaletteHit<Row>> Rank(string? query) =>
        PaletteQuery.Rank(Rows, query, row => row.Label, row => row.Hint);

    private static string[] Labels(IEnumerable<PaletteHit<Row>> hits) =>
        hits.Select(hit => hit.Item.Label).ToArray();

    [Fact]
    public void EmptyQueryKeepsEveryRowInOneSectionPerGroup()
    {
        var sections = Group();

        Assert.Equal(4, sections.Count);
        Assert.Equal(
            new[]
            {
                PaletteGroups.Device,
                PaletteGroups.AppsAndCapture,
                PaletteGroups.Diagnostics,
                PaletteGroups.Privileged,
            },
            sections.Select(section => section.Group).ToArray());
        Assert.Equal(Rows.Select(row => row.Label).ToArray(), sections.SelectMany(s => s.Items).Select(r => r.Label).ToArray());
    }

    [Fact]
    public void NonContiguousGroupsMergeIntoOneSectionAtFirstAppearance()
    {
        var scattered = new[]
        {
            new Row("B", "one", ""),
            new Row("A", "two", ""),
            new Row("B", "three", ""),
        };

        var sections = PaletteQuery.Group(scattered, row => row.Group);

        Assert.Equal(2, sections.Count);
        Assert.Equal("B", sections[0].Group);
        Assert.Equal(new[] { "one", "three" }, sections[0].Items.Select(row => row.Label).ToArray());
        Assert.Equal("A", sections[1].Group);
    }

    [Fact]
    public void RankWithNoQueryReturnsEveryRowInSourceOrderWithoutHighlights()
    {
        var hits = Rank("");

        Assert.Equal(Rows.Select(row => row.Label).ToArray(), Labels(hits));
        Assert.All(hits, hit => Assert.Empty(hit.LabelHits));
        Assert.All(hits, hit => Assert.Empty(hit.HintHits));
    }

    [Fact]
    public void WhitespaceQueryRanksAsEmpty()
    {
        Assert.Equal(Rows.Select(row => row.Label).ToArray(), Labels(Rank("   ")));
    }

    [Fact]
    public void TitleMatchesOutrankSubtitleMatches()
    {
        // "capture" appears only in Screenshot's subtitle; "stop" in a title and
        // in no subtitle.
        var hits = Rank("stop");
        Assert.Equal("Stop device", hits[0].Item.Label);

        var hintHits = Rank("capture");
        var single = Assert.Single(hintHits);
        Assert.Equal("Screenshot", single.Item.Label);
        Assert.Empty(single.LabelHits);
        Assert.NotEmpty(single.HintHits);
    }

    [Fact]
    public void EarlyWordStartMatchesRankFirst()
    {
        // "device" is a word-start match at offset 0 for "Device library" and at
        // offset 7 for "Create device"/"Start device"/"Stop device".
        var hits = Rank("device");
        Assert.Equal("Device library", hits[0].Item.Label);
        Assert.Equal(4, hits.Count);
    }

    [Fact]
    public void ContiguousMatchesReportEveryCharacterPosition()
    {
        var hit = Assert.Single(Rank("screenshot"));
        Assert.Equal("Screenshot", hit.Item.Label);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, hit.LabelHits);
    }

    [Fact]
    public void FuzzySubsequenceMatchesScatteredCharacters()
    {
        // s-h-t in "Screenshot": 0, 7, 9. Other rows match the same letters
        // somewhere in their description, so the title match is the one on top.
        var hits = Rank("sht");
        var hit = hits[0];
        Assert.Equal("Screenshot", hit.Item.Label);
        Assert.Equal(new[] { 0, 7, 9 }, hit.LabelHits);
    }

    [Fact]
    public void QueryIsCaseInsensitiveAndTrimmed()
    {
        Assert.Equal(new[] { "Screenshot" }, Labels(Rank("  SCREENshot  ")));
    }

    [Fact]
    public void NoMatchReturnsNoHits()
    {
        Assert.Empty(Rank("no such command"));
    }

    [Fact]
    public void EqualScoresKeepSourceOrder()
    {
        var rows = new[]
        {
            new Row("X", "probe alpha", ""),
            new Row("X", "probe beta", ""),
        };

        var hits = PaletteQuery.Rank(rows, "probe", row => row.Label, row => row.Hint);

        Assert.Equal(new[] { "probe alpha", "probe beta" }, hits.Select(hit => hit.Item.Label).ToArray());
    }

    [Fact]
    public void EmptyQueryMatchesEveryFieldWithNoPositions()
    {
        var result = PaletteQuery.FuzzyMatch("ADB root", "");

        Assert.True(result.Matched);
        Assert.Empty(result.Indexes);
        Assert.False(PaletteQuery.FuzzyMatch("", "adb").Matched);
        Assert.False(PaletteQuery.FuzzyMatch(null, "adb").Matched);
        Assert.True(PaletteQuery.FuzzyMatch(null, "").Matched);
        Assert.Equal("adb", PaletteQuery.Normalize("  adb "));
        Assert.Equal("", PaletteQuery.Normalize(null));
    }
}
