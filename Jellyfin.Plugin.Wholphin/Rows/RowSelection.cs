// Fork: the selection rules behind the personal home rows (GooseFlix #1635). Pure functions on
// plain values, no Jellyfin types, so the test project compiles this file on its own.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Wholphin.Rows;

/// <summary>A mood row: titles carrying any of its keywords as tags.</summary>
/// <param name="Id">Stable id, used in the daily rotation.</param>
/// <param name="Name">Row title.</param>
/// <param name="Keywords">Tags (TMDB keywords), matched case-insensitively.</param>
/// <param name="MinTitles">Fewest titles a user must have for the mood to be offered.</param>
/// <param name="Months">Months (1-12) it may show in; empty means all year.</param>
public sealed record MoodDefinition(string Id, string Name, IReadOnlyList<string> Keywords, int MinTitles, IReadOnlyList<int> Months);

/// <summary>Selection rules for the personal rows.</summary>
public static class RowSelection
{
    /// <summary>
    /// A seed that is the same all day for one user and one purpose, and changes the next day.
    /// FNV-1a over the inputs: string.GetHashCode is randomised per process, so it would rotate on
    /// every Jellyfin restart.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="date">The day.</param>
    /// <param name="purpose">What the seed is for, so rows don't rotate in lockstep.</param>
    /// <returns>The seed.</returns>
    public static ulong DailySeed(Guid userId, DateOnly date, string purpose)
    {
        return Hash(userId.ToString("N") + "|" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + purpose);
    }

    /// <summary>FNV-1a 64-bit.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The hash.</returns>
    public static ulong Hash(string value)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>One of <paramref name="candidates"/>, the same for the same seed.</summary>
    /// <typeparam name="T">Candidate type.</typeparam>
    /// <param name="candidates">Candidates.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The pick, or default when there are none.</returns>
    public static T? Pick<T>(IReadOnlyList<T> candidates, ulong seed)
    {
        return candidates.Count == 0 ? default : candidates[(int)(seed % (ulong)candidates.Count)];
    }

    /// <summary>A shuffle that is the same for the same seed and keys, whatever the input order.</summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="items">Items.</param>
    /// <param name="seed">The seed.</param>
    /// <param name="key">A stable key per item (eg its id).</param>
    /// <returns>The shuffled items.</returns>
    public static List<T> SeededOrder<T>(IEnumerable<T> items, ulong seed, Func<T, string> key)
    {
        var seedText = seed.ToString(CultureInfo.InvariantCulture);
        return items.OrderBy(i => Hash(seedText + "|" + key(i))).ThenBy(key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Unwatched items first, keeping the order within each group.</summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="items">Items.</param>
    /// <param name="played">Whether an item has been watched.</param>
    /// <returns>The reordered items.</returns>
    public static List<T> UnwatchedFirst<T>(IEnumerable<T> items, Func<T, bool> played)
    {
        var list = items.ToList();
        return list.Where(i => !played(i)).Concat(list.Where(played)).ToList();
    }

    /// <summary>The moods a user may get today: in season, with at least their minimum titles.</summary>
    /// <param name="moods">All moods.</param>
    /// <param name="titlesFor">How many titles the user has for a mood.</param>
    /// <param name="month">This month, 1-12.</param>
    /// <returns>The moods that qualify, with their counts, in the order given.</returns>
    public static List<(MoodDefinition Mood, int Count)> QualifyingMoods(
        IEnumerable<MoodDefinition> moods,
        Func<MoodDefinition, int> titlesFor,
        int month)
    {
        var result = new List<(MoodDefinition Mood, int Count)>();
        foreach (var mood in moods)
        {
            if (mood.Keywords.Count == 0 || (mood.Months.Count > 0 && !mood.Months.Contains(month)))
            {
                continue;
            }

            var count = titlesFor(mood);
            if (count >= Math.Max(1, mood.MinTitles))
            {
                result.Add((mood, count));
            }
        }

        return result;
    }

    /// <summary>Today's moods for a user: <paramref name="perDay"/> of the qualifying ones, rotating daily.</summary>
    /// <param name="qualifying">Qualifying moods (see <see cref="QualifyingMoods"/>).</param>
    /// <param name="seed">The user's daily seed.</param>
    /// <param name="perDay">How many moods show a day.</param>
    /// <returns>Today's moods, slot 1 first.</returns>
    public static List<(MoodDefinition Mood, int Count)> TodaysMoods(
        IReadOnlyList<(MoodDefinition Mood, int Count)> qualifying,
        ulong seed,
        int perDay)
    {
        return SeededOrder(qualifying, seed, m => m.Mood.Id).Take(Math.Max(0, perDay)).ToList();
    }

    /// <summary>The mood in a 1-based slot, or null when there isn't one.</summary>
    /// <param name="todays">Today's moods.</param>
    /// <param name="slot">The slot, from 1.</param>
    /// <returns>The mood and its count, or null.</returns>
    public static (MoodDefinition Mood, int Count)? MoodForSlot(IReadOnlyList<(MoodDefinition Mood, int Count)> todays, int slot)
    {
        return slot >= 1 && slot <= todays.Count ? todays[slot - 1] : null;
    }

    /// <summary>Whether a title carries any of a mood's keywords, ignoring case.</summary>
    /// <param name="tags">The title's tags.</param>
    /// <param name="keywords">The mood's keywords.</param>
    /// <returns>True on a match.</returns>
    public static bool MatchesMood(IEnumerable<string> tags, IEnumerable<string> keywords)
    {
        var set = new HashSet<string>(keywords.Select(k => k.Trim()), StringComparer.OrdinalIgnoreCase);
        return tags.Any(t => set.Contains(t.Trim()));
    }

    /// <summary>
    /// Genre weights from what the user watched: each watched title adds 1 to each of its genres.
    /// </summary>
    /// <param name="watchedGenres">The genres of each watched title.</param>
    /// <returns>Weight per genre, case-insensitive.</returns>
    public static Dictionary<string, int> GenreWeights(IEnumerable<IEnumerable<string>> watchedGenres)
    {
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var genres in watchedGenres)
        {
            foreach (var genre in genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                weights[genre] = weights.GetValueOrDefault(genre) + 1;
            }
        }

        return weights;
    }

    /// <summary>Candidates ordered by how well their genres match the weights, ties broken by the seed.</summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="candidates">Candidates.</param>
    /// <param name="weights">Genre weights.</param>
    /// <param name="genres">A candidate's genres.</param>
    /// <param name="key">A stable key per candidate.</param>
    /// <param name="seed">The daily seed.</param>
    /// <returns>Best match first.</returns>
    public static List<T> ByGenreMatch<T>(
        IEnumerable<T> candidates,
        IReadOnlyDictionary<string, int> weights,
        Func<T, IEnumerable<string>> genres,
        Func<T, string> key,
        ulong seed)
    {
        var seedText = seed.ToString(CultureInfo.InvariantCulture);
        return candidates
            .Select(c => (Item: c, Score: genres(c).Distinct(StringComparer.OrdinalIgnoreCase).Sum(g => weights.GetValueOrDefault(g))))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => Hash(seedText + "|" + key(c.Item)))
            .Select(c => c.Item)
            .ToList();
    }

    /// <summary>
    /// <paramref name="weights"/> less one per genre for each title the user marked "Don't recommend" (disliked):
    /// genres the user keeps turning down count for less. Genres left at zero or below are dropped.
    /// </summary>
    /// <param name="weights">Genre weights from what the user watched.</param>
    /// <param name="dislikedGenres">The genres of each disliked title.</param>
    /// <returns>The adjusted weights, case-insensitive.</returns>
    public static Dictionary<string, int> LessDisliked(IReadOnlyDictionary<string, int> weights, IEnumerable<IEnumerable<string>> dislikedGenres)
    {
        var result = new Dictionary<string, int>(weights, StringComparer.OrdinalIgnoreCase);
        foreach (var genres in dislikedGenres)
        {
            foreach (var genre in genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (result.TryGetValue(genre, out var weight))
                {
                    result[genre] = weight - 1;
                }
            }
        }

        foreach (var genre in result.Where(w => w.Value <= 0).Select(w => w.Key).ToList())
        {
            result.Remove(genre);
        }

        return result;
    }

    /// <summary>
    /// "Picked for you" order: the best genre matches (<see cref="ByGenreMatch{T}"/>), but taken in turn from each
    /// of the user's genres, heaviest first, so one genre can't fill the row; then movies and shows
    /// alternate, each keeping that order.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="candidates">Unwatched candidates.</param>
    /// <param name="weights">Genre weights.</param>
    /// <param name="genres">A candidate's genres.</param>
    /// <param name="isMovie">Whether a candidate is a movie (else a show).</param>
    /// <param name="key">A stable key for the candidate.</param>
    /// <param name="seed">Daily seed for ties.</param>
    /// <returns>The row, in order.</returns>
    public static List<T> Diverse<T>(
        IEnumerable<T> candidates,
        IReadOnlyDictionary<string, int> weights,
        Func<T, IEnumerable<string>> genres,
        Func<T, bool> isMovie,
        Func<T, string> key,
        ulong seed)
    {
        var ranked = ByGenreMatch(candidates, weights, genres, key, seed);
        var queues = weights
            .Where(w => w.Value > 0)
            .OrderByDescending(w => w.Value)
            .ThenBy(w => w.Key, StringComparer.OrdinalIgnoreCase)
            .Select(w => new Queue<T>(ranked.Where(i => genres(i).Contains(w.Key, StringComparer.OrdinalIgnoreCase))))
            .ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var turns = new List<T>();
        var took = true;
        while (took)
        {
            took = false;
            foreach (var queue in queues)
            {
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    if (used.Add(key(item)))
                    {
                        turns.Add(item);
                        took = true;
                        break;
                    }
                }
            }
        }

        turns.AddRange(ranked.Where(i => used.Add(key(i))));

        var movies = new Queue<T>(turns.Where(isMovie));
        var shows = new Queue<T>(turns.Where(i => !isMovie(i)));
        var result = new List<T>(turns.Count);
        var movieNext = turns.Count > 0 && isMovie(turns[0]);
        while (movies.Count > 0 || shows.Count > 0)
        {
            var from = (movieNext && movies.Count > 0) || shows.Count == 0 ? movies : shows;
            result.Add(from.Dequeue());
            movieNext = !movieNext;
        }

        return result;
    }

    /// <summary>
    /// The "Because you watched" kind from a request: "movie", "series" ("show" and "tv" too), or
    /// null for the row that draws on anything.
    /// </summary>
    /// <param name="kind">The query value.</param>
    /// <returns>"movie", "series" or null.</returns>
    public static string? WatchedKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => "movie",
        "series" or "show" or "shows" or "tv" => "series",
        _ => null,
    };

    /// <summary>
    /// Whether a caller may get rows for <paramref name="requested"/>: their own, or anyone's for an
    /// admin or an API key.
    /// </summary>
    /// <param name="caller">The signed-in user (empty for an API key).</param>
    /// <param name="requested">The user asked for.</param>
    /// <param name="isAdminOrApiKey">Whether the caller is an admin or uses an API key.</param>
    /// <returns>True if allowed.</returns>
    public static bool MayActFor(Guid caller, Guid requested, bool isAdminOrApiKey)
    {
        if (requested == Guid.Empty)
        {
            return false;
        }

        return isAdminOrApiKey || caller == requested;
    }

    /// <summary>
    /// A header value safe for HTTP: titles can hold non-ASCII text (eg anime), which Kestrel refuses
    /// in headers, so values are percent-encoded UTF-8. The app decodes them.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The encoded value.</returns>
    public static string HeaderValue(string value)
    {
        return Uri.EscapeDataString(value);
    }

    /// <summary>A page of a list.</summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="items">All items.</param>
    /// <param name="startIndex">First index, from 0.</param>
    /// <param name="limit">Most items, or null for all.</param>
    /// <returns>The page.</returns>
    public static List<T> Page<T>(IReadOnlyList<T> items, int? startIndex, int? limit)
    {
        var start = Math.Clamp(startIndex ?? 0, 0, items.Count);
        var take = limit is > 0 ? limit.Value : items.Count;
        return items.Skip(start).Take(take).ToList();
    }
}
