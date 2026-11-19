// Fork: the rules behind the plugin's own MDBList ratings (GooseFlix #1671): which titles to fetch,
// how requests are grouped, reading MDBList's answer, sorting by a rating and the values written
// to Jellyfin's fields. Plain values only, no Jellyfin types, so the test project compiles this file
// on its own.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Wholphin.Ratings;

/// <summary>One rating, as MDBList gives it.</summary>
/// <param name="Source">MDBList's source name, lower case (imdb, tomatoes, popcorn, myanimelist, ...).</param>
/// <param name="Value">The source's own scale (IMDb 7.2, RT 68).</param>
/// <param name="Score">Normalised to 0–100.</param>
/// <param name="Votes">How many votes, when known.</param>
/// <param name="Url">The source's page, or part of it, when known.</param>
public sealed record RatingValue(string Source, double? Value, double? Score, int? Votes, string? Url);

/// <summary>A title's ratings and when they were fetched.</summary>
public sealed class TitleRatings
{
    /// <summary>Gets or sets when the ratings were fetched (UTC).</summary>
    public DateTime FetchedAtUtc { get; set; }

    /// <summary>Gets or sets the ids MDBList gave (imdb, tmdb, tvdb, trakt, mal, ...).</summary>
    public Dictionary<string, string> Ids { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the ratings; empty when MDBList had none or didn't know the title.</summary>
    public List<RatingValue> Ratings { get; set; } = new();

    /// <summary>The rating from <paramref name="source"/>, if there's a value.</summary>
    /// <param name="source">The source name.</param>
    /// <returns>The rating, or null.</returns>
    public RatingValue? For(string source) =>
        Ratings.FirstOrDefault(r => string.Equals(r.Source, source, StringComparison.OrdinalIgnoreCase) && (r.Value is not null || r.Score is not null));
}

/// <summary>A movie or show in the library, with the ids MDBList can look it up by.</summary>
/// <param name="ItemId">Jellyfin's id.</param>
/// <param name="IsMovie">A movie (else a show).</param>
/// <param name="Tmdb">TMDB id, if known.</param>
/// <param name="Imdb">IMDb id, if known.</param>
/// <param name="Tvdb">TVDB id, if known.</param>
/// <param name="CreatedUtc">When it was added.</param>
public sealed record TitleRef(Guid ItemId, bool IsMovie, string? Tmdb, string? Imdb, string? Tvdb, DateTime CreatedUtc);

/// <summary>One MDBList request: titles of one kind, looked up by one kind of id.</summary>
/// <param name="Provider">tmdb, imdb or tvdb.</param>
/// <param name="MediaType">movie or show.</param>
/// <param name="Titles">The titles, in request order.</param>
public sealed record FetchBatch(string Provider, string MediaType, IReadOnlyList<TitleRef> Titles)
{
    /// <summary>Gets the ids sent, in order.</summary>
    public IReadOnlyList<string> Ids => Titles.Select(t => TitleRatingsLogic.IdFor(t, Provider)!).ToList();
}

/// <summary>A title in MDBList's answer.</summary>
/// <param name="Ids">Its ids.</param>
/// <param name="Ratings">Its ratings.</param>
public sealed record FetchedTitle(Dictionary<string, string> Ids, List<RatingValue> Ratings);

/// <summary>The rules.</summary>
public static class TitleRatingsLogic
{
    /// <summary>
    /// Titles to fetch now, in order: never fetched first (newest added first), then the stalest.
    /// A title is only fetched <paramref name="newDelayMinutes"/> after it was added, when its ids have
    /// arrived; one with no TMDB, IMDb or TVDB id is skipped.
    /// </summary>
    /// <param name="titles">The library's movies and shows.</param>
    /// <param name="fetchedAt">When a title was last fetched, or null.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="refreshHours">How old ratings may get.</param>
    /// <param name="newDelayMinutes">The wait after a title is added.</param>
    /// <returns>The titles due, in order.</returns>
    public static List<TitleRef> Due(IEnumerable<TitleRef> titles, Func<Guid, DateTime?> fetchedAt, DateTime nowUtc, int refreshHours, int newDelayMinutes)
    {
        var settled = nowUtc.AddMinutes(-Math.Max(0, newDelayMinutes));
        var stale = nowUtc.AddHours(-Math.Max(1, refreshHours));
        var never = new List<TitleRef>();
        var old = new List<(TitleRef Title, DateTime At)>();
        foreach (var title in titles)
        {
            if (BestProvider(title) is null)
            {
                continue;
            }

            var at = fetchedAt(title.ItemId);
            if (at is null)
            {
                if (title.CreatedUtc <= settled)
                {
                    never.Add(title);
                }
            }
            else if (at.Value <= stale)
            {
                old.Add((title, at.Value));
            }
        }

        return never.OrderByDescending(t => t.CreatedUtc)
            .Concat(old.OrderBy(o => o.At).Select(o => o.Title))
            .ToList();
    }

    /// <summary>The id a title is looked up by: TMDB, then IMDb, then TVDB.</summary>
    /// <param name="title">The title.</param>
    /// <returns>tmdb, imdb, tvdb, or null without any.</returns>
    public static string? BestProvider(TitleRef title) =>
        !string.IsNullOrWhiteSpace(title.Tmdb) ? "tmdb"
        : !string.IsNullOrWhiteSpace(title.Imdb) ? "imdb"
        : !string.IsNullOrWhiteSpace(title.Tvdb) ? "tvdb"
        : null;

    /// <summary>A title's id for <paramref name="provider"/>.</summary>
    /// <param name="title">The title.</param>
    /// <param name="provider">tmdb, imdb or tvdb.</param>
    /// <returns>The id, or null.</returns>
    public static string? IdFor(TitleRef title, string provider) => provider switch
    {
        "tmdb" => title.Tmdb?.Trim(),
        "imdb" => title.Imdb?.Trim(),
        "tvdb" => title.Tvdb?.Trim(),
        _ => null,
    };

    /// <summary>Groups titles into requests by kind and id provider, at most <paramref name="batchSize"/> each, keeping their order.</summary>
    /// <param name="due">The titles, in order.</param>
    /// <param name="batchSize">Titles per request.</param>
    /// <returns>The requests.</returns>
    public static List<FetchBatch> Batches(IReadOnlyList<TitleRef> due, int batchSize)
    {
        var size = Math.Clamp(batchSize, 1, 200);
        return due
            .GroupBy(t => (Provider: BestProvider(t)!, MediaType: t.IsMovie ? "movie" : "show"))
            .OrderBy(g => due.IndexOf(g.First()))
            .SelectMany(g => g.Chunk(size).Select(chunk => new FetchBatch(g.Key.Provider, g.Key.MediaType, chunk)))
            .ToList();
    }

    private static int IndexOf(this IReadOnlyList<TitleRef> list, TitleRef title)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], title))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>The JSON body for a request: TMDB and TVDB ids as numbers, IMDb ids as strings.</summary>
    /// <param name="batch">The request.</param>
    /// <returns>The body.</returns>
    public static string RequestBody(FetchBatch batch)
    {
        var ids = batch.Ids
            .Select(id => batch.Provider != "imdb" && long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (object)n : id)
            .ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["ids"] = ids });
    }

    /// <summary>
    /// Reads MDBList's batch answer: an array of titles, each with its ids (an <c>ids</c> object, or
    /// <c>imdb_id</c>/<c>tmdb_id</c>/<c>tvdb_id</c>) and a <c>ratings</c> array of
    /// <c>{source, value, score, votes, url}</c>. Anything it can't read is skipped.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <returns>The titles.</returns>
    public static List<FetchedTitle> Parse(string json)
    {
        var result = new List<FetchedTitle>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items))
        {
            root = items;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var title in root.EnumerateArray())
        {
            if (title.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (title.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var id in idsElement.EnumerateObject())
                {
                    if (Text(id.Value) is { } text)
                    {
                        ids[id.Name] = text;
                    }
                }
            }

            foreach (var (field, key) in new[] { ("imdb_id", "imdb"), ("tmdb_id", "tmdb"), ("tvdb_id", "tvdb"), ("mal_id", "mal"), ("trakt_id", "trakt") })
            {
                if (!ids.ContainsKey(key) && title.TryGetProperty(field, out var value) && Text(value) is { } text)
                {
                    ids[key] = text;
                }
            }

            var ratings = new List<RatingValue>();
            if (title.TryGetProperty("ratings", out var ratingsElement) && ratingsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rating in ratingsElement.EnumerateArray())
                {
                    if (rating.ValueKind != JsonValueKind.Object || !rating.TryGetProperty("source", out var source) || Text(source) is not { } sourceName)
                    {
                        continue;
                    }

                    ratings.Add(new RatingValue(
                        sourceName.ToLowerInvariant(),
                        Number(rating, "value"),
                        Number(rating, "score"),
                        Number(rating, "votes") is { } votes ? (int)Math.Min(votes, int.MaxValue) : null,
                        rating.TryGetProperty("url", out var url) ? Text(url) : null));
                }
            }

            result.Add(new FetchedTitle(ids, ratings));
        }

        return result;
    }

    private static string? Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null,
    };

    private static double? Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    /// <summary>
    /// Which answer belongs to which title: by the id the request used. If no answer carries ids at
    /// all and there's one per title, by position.
    /// </summary>
    /// <param name="batch">The request.</param>
    /// <param name="fetched">The answer.</param>
    /// <returns>Each matched title's answer.</returns>
    public static Dictionary<Guid, FetchedTitle> Match(FetchBatch batch, IReadOnlyList<FetchedTitle> fetched)
    {
        var result = new Dictionary<Guid, FetchedTitle>();
        var byId = new Dictionary<string, FetchedTitle>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in fetched)
        {
            if (title.Ids.TryGetValue(batch.Provider, out var id))
            {
                byId.TryAdd(id.Trim(), title);
            }
        }

        if (byId.Count == 0 && fetched.Count == batch.Titles.Count)
        {
            for (var i = 0; i < fetched.Count; i++)
            {
                result[batch.Titles[i].ItemId] = fetched[i];
            }

            return result;
        }

        foreach (var title in batch.Titles)
        {
            if (IdFor(title, batch.Provider) is { } id && byId.TryGetValue(id, out var match))
            {
                result[title.ItemId] = match;
            }
        }

        return result;
    }

    /// <summary>
    /// Items ordered by a rating: highest first (or lowest with <paramref name="descending"/> false),
    /// titles without that rating last whichever way, keeping their order among themselves. Ties keep
    /// the incoming order (eg by name).
    /// </summary>
    /// <param name="ids">The items, in their tie-break order.</param>
    /// <param name="score">An item's score, or null.</param>
    /// <param name="descending">Highest first.</param>
    /// <returns>The items, in order.</returns>
    public static List<Guid> OrderByRating(IReadOnlyList<Guid> ids, Func<Guid, double?> score, bool descending)
    {
        var rated = new List<(Guid Id, double Score, int Index)>();
        var unrated = new List<Guid>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (score(ids[i]) is { } s)
            {
                rated.Add((ids[i], s, i));
            }
            else
            {
                unrated.Add(ids[i]);
            }
        }

        var ordered = descending
            ? rated.OrderByDescending(r => r.Score).ThenBy(r => r.Index)
            : rated.OrderBy(r => r.Score).ThenBy(r => r.Index);
        return ordered.Select(r => r.Id).Concat(unrated).ToList();
    }

    /// <summary>The score to sort by: MDBList's 0–100 score, else the value scaled to 0–100 when the scale is known.</summary>
    /// <param name="rating">The rating.</param>
    /// <returns>0–100, or null.</returns>
    public static double? SortScore(RatingValue? rating) => rating?.Score ?? rating?.Value switch
    {
        null => null,
        <= 10 and var v => v * 10,
        var v => v,
    };

    /// <summary>Jellyfin's Community rating (0–10, one decimal) from a rating.</summary>
    /// <param name="rating">The rating.</param>
    /// <returns>The value, or null.</returns>
    public static float? Community(RatingValue? rating) =>
        SortScore(rating) is { } s ? (float)Math.Round(s / 10, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>Jellyfin's Critic rating (0–100, whole) from a rating.</summary>
    /// <param name="rating">The rating.</param>
    /// <returns>The value, or null.</returns>
    public static float? Critic(RatingValue? rating) =>
        SortScore(rating) is { } s ? (float)Math.Round(s, MidpointRounding.AwayFromZero) : null;

    /// <summary>The sources that fill a title's Community and Critic ratings: its library's override, else the defaults.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="isMovie">A movie (else a show).</param>
    /// <param name="libraryNames">The libraries the title is in.</param>
    /// <returns>The Community and Critic sources; empty means leave that field alone.</returns>
    public static (string Community, string Critic) SourcesFor(FieldFillConfig config, bool isMovie, IEnumerable<string> libraryNames)
    {
        var names = libraryNames.ToList();
        var library = (config.Overrides ?? Array.Empty<LibraryRatingOverride>())
            .FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Library) && names.Any(n => string.Equals(n, o.Library!.Trim(), StringComparison.OrdinalIgnoreCase)));
        string Pick(string? overridden, string fallback) => (string.IsNullOrWhiteSpace(overridden) ? fallback : overridden).Trim().ToLowerInvariant();
        return isMovie
            ? (Pick(library?.MovieCommunity, config.MovieCommunity), Pick(library?.MovieCritic, config.MovieCritic))
            : (Pick(library?.ShowCommunity, config.ShowCommunity), Pick(library?.ShowCritic, config.ShowCritic));
    }
}
