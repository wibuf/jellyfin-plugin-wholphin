// Fork: tests for the personal home rows' selection rules (GooseFlix #1635).

using System.Xml.Serialization;
using Jellyfin.Plugin.Wholphin.Rows;
using Xunit;

namespace Jellyfin.Plugin.Wholphin.Tests;

public class RowSelectionTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly Day = new(2026, 11, 15);

    private static MoodDefinition Mood(string id, int minTitles = 12, params int[] months) =>
        new(id, id.ToUpperInvariant(), new[] { id }, minTitles, months);

    [Fact]
    public void DailySeed_IsStableForADay_AndChangesWithDayUserAndPurpose()
    {
        var seed = RowSelection.DailySeed(Alice, Day, "byw");
        Assert.Equal(seed, RowSelection.DailySeed(Alice, Day, "byw"));
        Assert.NotEqual(seed, RowSelection.DailySeed(Alice, Day.AddDays(1), "byw"));
        Assert.NotEqual(seed, RowSelection.DailySeed(Bob, Day, "byw"));
        Assert.NotEqual(seed, RowSelection.DailySeed(Alice, Day, "moods"));
    }

    [Fact]
    public void Hash_IsFnv1a_SoItSurvivesRestarts()
    {
        // Known FNV-1a 64 values: string.GetHashCode would differ per process
        Assert.Equal(14695981039346656037UL, RowSelection.Hash(string.Empty));
        Assert.Equal(0xaf63dc4c8601ec8cUL, RowSelection.Hash("a"));
    }

    [Fact]
    public void Pick_IsDeterministic_AndRotatesAcrossDays()
    {
        var candidates = new[] { "a", "b", "c", "d", "e" };
        var today = RowSelection.Pick(candidates, RowSelection.DailySeed(Alice, Day, "byw"));
        Assert.Equal(today, RowSelection.Pick(candidates, RowSelection.DailySeed(Alice, Day, "byw")));

        var month = Enumerable.Range(0, 30)
            .Select(d => RowSelection.Pick(candidates, RowSelection.DailySeed(Alice, Day.AddDays(d), "byw")))
            .Distinct()
            .Count();
        Assert.True(month >= 3, $"only {month} different sources in 30 days");
    }

    [Fact]
    public void Pick_FromNothing_IsNull()
    {
        Assert.Null(RowSelection.Pick(Array.Empty<string>(), 42));
    }

    [Fact]
    public void QualifyingMoods_NeedTheirMinimumTitles_AndTheirMonths()
    {
        var moods = new[]
        {
            Mood("heists", 12),
            Mood("spies", 12),
            Mood("christmas", 12, 11, 12),
            Mood("halloween", 12, 10),
            new MoodDefinition("empty", "Empty", Array.Empty<string>(), 1, Array.Empty<int>()),
        };
        var counts = new Dictionary<string, int> { ["heists"] = 12, ["spies"] = 11, ["christmas"] = 40, ["halloween"] = 58, ["empty"] = 99 };

        var november = RowSelection.QualifyingMoods(moods, m => counts[m.Id], 11).Select(m => m.Mood.Id).ToList();
        Assert.Equal(new[] { "heists", "christmas" }, november);

        var october = RowSelection.QualifyingMoods(moods, m => counts[m.Id], 10).Select(m => m.Mood.Id).ToList();
        Assert.Equal(new[] { "heists", "halloween" }, october);
    }

    [Fact]
    public void TodaysMoods_TakesPerDay_Deterministically_AndSlotsAreDistinct()
    {
        var qualifying = Enumerable.Range(1, 15).Select(i => (Mood("m" + i), 20 + i)).ToList();
        var seed = RowSelection.DailySeed(Alice, Day, "moods");

        var today = RowSelection.TodaysMoods(qualifying, seed, 3);
        Assert.Equal(3, today.Count);
        Assert.Equal(3, today.Select(m => m.Mood.Id).Distinct().Count());
        Assert.Equal(today, RowSelection.TodaysMoods(qualifying, seed, 3));

        // The input order doesn't matter, only the seed and ids
        var reversed = RowSelection.TodaysMoods(qualifying.AsEnumerable().Reverse().ToList(), seed, 3);
        Assert.Equal(today.Select(m => m.Mood.Id), reversed.Select(m => m.Mood.Id));

        var tomorrow = RowSelection.TodaysMoods(qualifying, RowSelection.DailySeed(Alice, Day.AddDays(1), "moods"), 3);
        Assert.NotEqual(today.Select(m => m.Mood.Id), tomorrow.Select(m => m.Mood.Id));
    }

    [Fact]
    public void TodaysMoods_WithFewerQualifyingThanPerDay_ReturnsWhatThereIs()
    {
        var qualifying = new List<(MoodDefinition, int)> { (Mood("only"), 30) };
        Assert.Single(RowSelection.TodaysMoods(qualifying, 7, 3));
    }

    [Fact]
    public void MoodForSlot_IsOneBased_AndNullOutOfRange()
    {
        var today = new List<(MoodDefinition, int)> { (Mood("a"), 20), (Mood("b"), 30) };
        Assert.Equal("a", RowSelection.MoodForSlot(today, 1)!.Value.Mood.Id);
        Assert.Equal("b", RowSelection.MoodForSlot(today, 2)!.Value.Mood.Id);
        Assert.Null(RowSelection.MoodForSlot(today, 0));
        Assert.Null(RowSelection.MoodForSlot(today, 3));
    }

    [Fact]
    public void MatchesMood_IgnoresCase()
    {
        Assert.True(RowSelection.MatchesMood(new[] { "Bank Robbery", "new york city" }, new[] { "heist", "bank robbery" }));
        Assert.False(RowSelection.MatchesMood(new[] { "romance" }, new[] { "heist" }));
    }

    [Theory]
    [InlineData(true, false, true)] // own rows
    [InlineData(false, false, false)] // someone else's
    [InlineData(false, true, true)] // admin or API key
    public void MayActFor_OwnRowsOrPrivileged(bool own, bool privileged, bool allowed)
    {
        Assert.Equal(allowed, RowSelection.MayActFor(Alice, own ? Alice : Bob, privileged));
    }

    [Fact]
    public void MayActFor_NeverAnEmptyUser()
    {
        Assert.False(RowSelection.MayActFor(Guid.Empty, Guid.Empty, true));
    }

    [Fact]
    public void UnwatchedFirst_KeepsOrderWithinGroups()
    {
        var items = new[] { "w1", "u1", "w2", "u2" };
        Assert.Equal(new[] { "u1", "u2", "w1", "w2" }, RowSelection.UnwatchedFirst(items, i => i.StartsWith('w')));
    }

    [Fact]
    public void ByGenreMatch_PrefersTheMostWatchedGenres_AndDropsNoMatch()
    {
        var weights = RowSelection.GenreWeights(new[]
        {
            new[] { "Action", "Comedy" },
            new[] { "action" },
            new[] { "Drama" },
        });
        Assert.Equal(2, weights["ACTION"]);

        var candidates = new[]
        {
            ("drama", new[] { "Drama" }),
            ("action-comedy", new[] { "Action", "Comedy" }),
            ("horror", new[] { "Horror" }),
            ("action", new[] { "Action" }),
        };
        var ordered = RowSelection.ByGenreMatch(candidates, weights, c => c.Item2, c => c.Item1, 99).Select(c => c.Item1).ToList();
        Assert.Equal(new[] { "action-comedy", "action", "drama" }, ordered);
    }

    [Fact]
    public void HeaderValue_IsAsciiSafe()
    {
        var value = RowSelection.HeaderValue("Because you watched 鬼滅の刃 & Amélie");
        Assert.All(value, c => Assert.True(c < 128));
        Assert.Equal("Because you watched 鬼滅の刃 & Amélie", Uri.UnescapeDataString(value));
    }

    [Fact]
    public void Page_ClampsAndLimits()
    {
        var items = Enumerable.Range(0, 10).ToList();
        Assert.Equal(new[] { 2, 3, 4 }, RowSelection.Page(items, 2, 3));
        Assert.Empty(RowSelection.Page(items, 50, 3));
        Assert.Equal(10, RowSelection.Page(items, null, null).Count);
    }

    [Fact]
    public void DefaultMoods_HaveUniqueIdsAndKeywords()
    {
        var moods = RowsConfig.DefaultMoods();
        Assert.True(moods.Length >= 15);
        Assert.Equal(moods.Length, moods.Select(m => m.Id).Distinct().Count());
        Assert.All(moods, m => Assert.NotEmpty(m.Keywords!));
        Assert.Equal(moods.Length, new RowsConfig().Definitions().Length);
    }

    [Fact]
    public void SavedMoods_ReplaceTheDefaults_WhenJellyfinReadsTheSettings()
    {
        // Jellyfin stores plugin settings with XmlSerializer; a list property would get the saved
        // moods added to the defaults on every load
        var config = new RowsConfig { MoodList = new[] { new MoodConfig { Id = "only", Name = "Only", Keywords = new[] { "x" } } } };
        var serializer = new XmlSerializer(typeof(RowsConfig));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var loaded = (RowsConfig)serializer.Deserialize(reader)!;
        Assert.Single(loaded.MoodList);
        Assert.Equal("only", loaded.MoodList[0].Id);
    }
}
