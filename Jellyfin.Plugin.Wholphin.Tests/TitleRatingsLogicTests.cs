// Fork: tests for the plugin's own MDBList ratings rules (GooseFlix #1671).

using Jellyfin.Plugin.Wholphin.Ratings;
using Xunit;

namespace Jellyfin.Plugin.Wholphin.Tests;

public class TitleRatingsLogicTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static TitleRef Title(string? tmdb = null, string? imdb = null, string? tvdb = null, bool movie = true, int addedDaysAgo = 30) =>
        new(Guid.NewGuid(), movie, tmdb, imdb, tvdb, Now.AddDays(-addedDaysAgo));

    [Fact]
    public void Due_NeverFetchedFirstNewestFirst_ThenStalest_SkippingFreshAndIdless()
    {
        var oldNew = Title("1", addedDaysAgo: 5);
        var newest = Title("2", addedDaysAgo: 1);
        var justAdded = Title("3", addedDaysAgo: 0); // added now: ids may not be there yet
        var stale = Title("4");
        var staler = Title("5");
        var fresh = Title("6");
        var noIds = Title();
        var fetched = new Dictionary<Guid, DateTime>
        {
            [stale.ItemId] = Now.AddHours(-30),
            [staler.ItemId] = Now.AddHours(-50),
            [fresh.ItemId] = Now.AddHours(-2),
        };

        var due = TitleRatingsLogic.Due(
            new[] { oldNew, newest, justAdded, stale, staler, fresh, noIds },
            id => fetched.TryGetValue(id, out var at) ? at : null,
            Now,
            refreshHours: 24,
            newDelayMinutes: 10);

        Assert.Equal(new[] { newest, oldNew, staler, stale }, due);
    }

    [Fact]
    public void Batches_GroupByKindAndBestId_AtMostBatchSize()
    {
        var movies = Enumerable.Range(1, 5).Select(i => Title(tmdb: i.ToString())).ToList();
        var imdbShow = Title(imdb: "tt1", movie: false);
        var tvdbShow = Title(tvdb: "99", movie: false);
        var batches = TitleRatingsLogic.Batches(movies.Append(imdbShow).Append(tvdbShow).ToList(), batchSize: 2);

        Assert.Equal(
            new[] { ("tmdb", "movie", 2), ("tmdb", "movie", 2), ("tmdb", "movie", 1), ("imdb", "show", 1), ("tvdb", "show", 1) },
            batches.Select(b => (b.Provider, b.MediaType, b.Titles.Count)));
        Assert.Equal("{\"ids\":[1,2]}", TitleRatingsLogic.RequestBody(batches[0]));
        Assert.Equal("{\"ids\":[\"tt1\"]}", TitleRatingsLogic.RequestBody(batches[3]));
    }

    [Fact]
    public void Parse_ReadsIdsAndRatings_AndSkipsWhatItCant()
    {
        const string json = """
            [
              {"title": "Frieren", "ids": {"imdb": "tt22248376", "tmdb": 209867, "mal": 52991},
               "ratings": [
                 {"source": "imdb", "value": 9.0, "score": 90, "votes": 55000},
                 {"source": "MyAnimeList", "value": 9.3, "score": 93, "votes": 1000000, "url": "52991"},
                 {"source": "tomatoes"},
                 {"value": 5}
               ]},
              {"title": "Old shape", "imdb_id": "tt0111161", "tmdb_id": 278, "ratings": []},
              "not an object"
            ]
            """;
        var titles = TitleRatingsLogic.Parse(json);

        Assert.Equal(2, titles.Count);
        Assert.Equal("209867", titles[0].Ids["tmdb"]);
        Assert.Equal("52991", titles[0].Ids["mal"]);
        Assert.Equal(new[] { "imdb", "myanimelist", "tomatoes" }, titles[0].Ratings.Select(r => r.Source));
        Assert.Equal(9.3, titles[0].Ratings[1].Value);
        Assert.Equal(1000000, titles[0].Ratings[1].Votes);
        Assert.Null(titles[0].Ratings[2].Score);
        Assert.Equal("278", titles[1].Ids["tmdb"]);
    }

    [Fact]
    public void Match_ByTheIdUsed_OrByPositionWhenNoIds()
    {
        var a = Title("10");
        var b = Title("20");
        var batch = new FetchBatch("tmdb", "movie", new[] { a, b });
        var withIds = TitleRatingsLogic.Parse("""[{"ids":{"tmdb":20},"ratings":[]},{"ids":{"tmdb":10},"ratings":[]}]""");
        var matched = TitleRatingsLogic.Match(batch, withIds);
        Assert.Same(withIds[1], matched[a.ItemId]);
        Assert.Same(withIds[0], matched[b.ItemId]);

        var noIds = TitleRatingsLogic.Parse("""[{"ratings":[]},{"ratings":[]}]""");
        var byPosition = TitleRatingsLogic.Match(batch, noIds);
        Assert.Same(noIds[0], byPosition[a.ItemId]);

        // One missing from the answer: the other still matches, the missing one doesn't
        var partial = TitleRatingsLogic.Match(batch, TitleRatingsLogic.Parse("""[{"ids":{"tmdb":"20"},"ratings":[]}]"""));
        Assert.False(partial.ContainsKey(a.ItemId));
        Assert.True(partial.ContainsKey(b.ItemId));
    }

    [Fact]
    public void OrderByRating_HighestFirst_UnratedLast_TiesKeepOrder()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var scores = new Dictionary<Guid, double> { [ids[0]] = 70, [ids[1]] = 90, [ids[3]] = 90 };
        double? Score(Guid id) => scores.TryGetValue(id, out var s) ? s : null;

        Assert.Equal(new[] { ids[1], ids[3], ids[0], ids[2], ids[4] }, TitleRatingsLogic.OrderByRating(ids, Score, descending: true));
        Assert.Equal(new[] { ids[0], ids[1], ids[3], ids[2], ids[4] }, TitleRatingsLogic.OrderByRating(ids, Score, descending: false));
    }

    [Fact]
    public void Scores_FromScoreOrValue_AndJellyfinFields()
    {
        Assert.Equal(92, TitleRatingsLogic.SortScore(new RatingValue("myanimelist", 9.2, 92, null, null)));
        Assert.Equal(84, TitleRatingsLogic.SortScore(new RatingValue("imdb", 8.4, null, null, null)));
        Assert.Equal(68, TitleRatingsLogic.SortScore(new RatingValue("tomatoes", 68, null, null, null)));
        Assert.Null(TitleRatingsLogic.SortScore(new RatingValue("tomatoes", null, null, null, null)));
        Assert.Equal(9.2f, TitleRatingsLogic.Community(new RatingValue("myanimelist", 9.2, 92, null, null)));
        Assert.Equal(68f, TitleRatingsLogic.Critic(new RatingValue("tomatoes", 68, 68, null, null)));
    }

    [Fact]
    public void SourcesFor_LibraryOverride_ElseDefaults()
    {
        var fill = new FieldFillConfig();
        Assert.Equal(("myanimelist", string.Empty), TitleRatingsLogic.SourcesFor(fill, isMovie: false, new[] { "anime" }));
        Assert.Equal(("myanimelist", "tomatoes"), TitleRatingsLogic.SourcesFor(fill, isMovie: true, new[] { "Anime" }));
        Assert.Equal(("imdb", string.Empty), TitleRatingsLogic.SourcesFor(fill, isMovie: false, new[] { "TV" }));
        Assert.Equal(("imdb", "tomatoes"), TitleRatingsLogic.SourcesFor(fill, isMovie: true, new[] { "Movies", "4K" }));
    }

    [Fact]
    public void TitleRatings_For_IgnoresSourcesWithoutAValue()
    {
        var t = new TitleRatings { Ratings = { new RatingValue("tomatoes", null, null, null, "/m/x"), new RatingValue("IMDB", 7.1, 71, 10, null) } };
        Assert.Null(t.For("tomatoes"));
        Assert.Equal(7.1, t.For("imdb")!.Value);
    }
}
