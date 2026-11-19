// Fork: builds the personal home rows (GooseFlix #1635). Written for speed on a cold cache:
// every query is capped before any sorting, played state comes from one batched lookup, a page of
// items is one query, mood counts are shared by users with the same library access and warmed in
// the background, and concurrent requests for the same row share one build.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wholphin.Rows;

/// <summary>A built row: item ids in order, the total for "N in your library", and its headers.</summary>
/// <param name="ItemIds">Item ids, in row order.</param>
/// <param name="TotalCount">Total count.</param>
/// <param name="Headers">Response headers (unencoded).</param>
public sealed record RowResult(IReadOnlyList<Guid> ItemIds, int TotalCount, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Gets an empty row.</summary>
    public static RowResult Nothing { get; } = new(Array.Empty<Guid>(), 0, new Dictionary<string, string>());
}

/// <summary>Builds and caches the personal rows.</summary>
public sealed class RowsService
{
    /// <summary>Header carrying the row's title.</summary>
    public const string TitleHeader = "X-GooseFlix-Row-Title";

    private const int MoodCandidates = 150;
    private const int SimilarCandidates = 60;
    private const int PickedCandidates = 300;

    private static readonly BaseItemKind[] TitleKinds = { BaseItemKind.Movie, BaseItemKind.Series };

    private static readonly PreferenceKind[] AccessPreferences =
    {
        PreferenceKind.EnabledFolders,
        PreferenceKind.MyMediaExcludes,
        PreferenceKind.BlockedTags,
        PreferenceKind.AllowedTags,
        PreferenceKind.BlockUnratedItems,
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IDtoService _dtoService;
    private readonly ISimilarItemsManager _similarItemsManager;
    private readonly ILogger<RowsService> _logger;
    private readonly ConcurrentDictionary<string, Entry<RowResult>> _rows = new();
    private readonly ConcurrentDictionary<string, Entry<List<(MoodDefinition Mood, int Count)>>> _moodCounts = new();

    /// <summary>Initializes a new instance of the <see cref="RowsService"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="dtoService">DTO service.</param>
    /// <param name="similarItemsManager">Similar items manager.</param>
    /// <param name="logger">Logger.</param>
    public RowsService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IDtoService dtoService,
        ISimilarItemsManager similarItemsManager,
        ILogger<RowsService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _dtoService = dtoService;
        _similarItemsManager = similarItemsManager;
        _logger = logger;
    }

    /// <summary>Gets the current settings.</summary>
    public static RowsConfig Config => WholphinPlugin.Instance?.Configuration.Rows ?? new RowsConfig();

    /// <summary>Gets the row day: it starts at <see cref="RowsConfig.WarmHour"/>, when the rows are rebuilt.</summary>
    public static DateOnly RowDay => DateOnly.FromDateTime(DateTime.Now.AddHours(-Math.Clamp(Config.WarmHour, 0, 23)));

    private static DateOnly Today => RowDay;

    /// <summary>A user by id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The user, or null.</returns>
    public User? GetUser(Guid id) => _userManager.GetUserById(id);

    /// <summary>
    /// A "Because you watched" row: from a movie the user finished ("movie"), a show ("series"), or
    /// anything (null), which then avoids the other two rows' titles.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="kind">"movie", "series" or null (see <see cref="RowSelection.WatchedKind"/>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row.</returns>
    public Task<RowResult> BecauseYouWatched(User user, string? kind, CancellationToken cancellationToken) =>
        Cached(user, "because-you-watched" + (kind is null ? string.Empty : ":" + kind), () => BuildBecauseYouWatched(user, kind, cancellationToken));

    /// <summary>Today's mood row in a slot.</summary>
    /// <param name="user">The user.</param>
    /// <param name="slot">The slot, from 1.</param>
    /// <returns>The row.</returns>
    public Task<RowResult> Mood(User user, int slot) =>
        Cached(user, "mood" + slot.ToString(CultureInfo.InvariantCulture), () => Task.FromResult(BuildMood(user, slot)));

    /// <summary>The "Picked for you" row.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The row.</returns>
    public Task<RowResult> PickedForYou(User user) =>
        Cached(user, "picked-for-you", () => Task.FromResult(BuildPickedForYou(user)));

    /// <summary>Today's moods for the user, slot 1 first.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The moods and the user's title counts.</returns>
    public List<(MoodDefinition Mood, int Count)> TodaysMoods(User user)
    {
        var qualifying = QualifyingMoods(user);
        return RowSelection.TodaysMoods(qualifying, RowSelection.DailySeed(user.Id, Today, "moods"), Config.MoodsPerDay);
    }

    /// <summary>
    /// Builds today's rows ahead for users active in the last <see cref="RowsConfig.WarmActiveDays"/>,
    /// so the home page finds them cached. Mood counts are shared by users with the same library
    /// access. One user at a time, with a pause between, to keep the load gentle.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task Warm(CancellationToken cancellationToken)
    {
        var config = Config;
        var watch = Stopwatch.StartNew();
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, config.WarmActiveDays));
        var users = _userManager.GetUsers().Where(u => u.LastActivityDate is { } last && last >= since).ToList();
        foreach (var user in users)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (config.BecauseYouWatched)
            {
                // The movie and show rows first: the "anything" row avoids their titles
                await BecauseYouWatched(user, "movie", cancellationToken).ConfigureAwait(false);
                await BecauseYouWatched(user, "series", cancellationToken).ConfigureAwait(false);
                await BecauseYouWatched(user, null, cancellationToken).ConfigureAwait(false);
            }

            if (config.PickedForYou)
            {
                await PickedForYou(user).ConfigureAwait(false);
            }

            if (config.Moods)
            {
                for (var slot = 1; slot <= Math.Max(0, config.MoodsPerDay); slot++)
                {
                    await Mood(user, slot).ConfigureAwait(false);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("GooseFlix: rows for {Day} built ahead for {Count} active users in {Seconds} s", RowDay, users.Count, watch.Elapsed.TotalSeconds);
    }

    /// <summary>A page of a row as cards.</summary>
    /// <param name="user">The user.</param>
    /// <param name="row">The row.</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Most items.</param>
    /// <returns>The page.</returns>
    public QueryResult<BaseItemDto> Page(User user, RowResult row, int? startIndex, int? limit)
    {
        var size = limit is > 0 ? limit.Value : Math.Max(1, Config.MaxItems);
        var next = Math.Clamp(startIndex ?? 0, 0, row.ItemIds.Count);
        var ordered = new List<BaseItem>(size);

        // Titles the user marked "Don't recommend" (disliked) drop out as soon as they're marked, and
        // the row tops up from further down its list. A few rounds at most; each is one item query
        // and one batched user-data lookup.
        for (var round = 0; round < 3 && ordered.Count < size && next < row.ItemIds.Count; round++)
        {
            var chunk = RowSelection.Page(row.ItemIds, next, size - ordered.Count + (round == 0 ? 0 : 5));
            next += chunk.Count;

            // One query for the chunk, with the user's access applied again
            var found = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = chunk.ToArray(),
                DtoOptions = CardOptions(),
            }).ToDictionary(i => i.Id);
            var items = chunk.Where(found.ContainsKey).Select(id => found[id]).ToList();
            var disliked = DislikedIds(user, items);
            ordered.AddRange(items.Where(i => !disliked.Contains(i.Id)).Take(size - ordered.Count));
        }

        if (ordered.Count == 0)
        {
            return new QueryResult<BaseItemDto>(startIndex ?? 0, row.TotalCount, Array.Empty<BaseItemDto>());
        }

        var dtos = _dtoService.GetBaseItemDtos(ordered, CardOptions(), user);
        return new QueryResult<BaseItemDto>(startIndex ?? 0, row.TotalCount, dtos);
    }

    private static DtoOptions Minimal() => new(false) { EnableImages = false, EnableUserData = false };

    private static DtoOptions CardOptions() => new(false)
    {
        Fields = new[]
        {
            ItemFields.PrimaryImageAspectRatio,
            ItemFields.Overview,
            ItemFields.Genres,
            ItemFields.Taglines,
            ItemFields.ChildCount,
            ItemFields.MediaSourceCount,
            ItemFields.ProviderIds,
        },
        EnableImages = true,
        ImageTypeLimit = 1,
        ImageTypes = new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Thumb, ImageType.Logo },
        EnableUserData = true,
    };

    /// <summary>
    /// What decides which titles a user can see; users with equal signatures get equal mood counts.
    /// </summary>
    private static string AccessSignature(User user)
    {
        var parts = user.Preferences
            .Where(p => AccessPreferences.Contains(p.Kind))
            .OrderBy(p => p.Kind)
            .Select(p => ((int)p.Kind).ToString(CultureInfo.InvariantCulture) + "=" + p.Value);
        return RowSelection.Hash(string.Join(";", parts)
            + "|all=" + user.HasPermission(PermissionKind.EnableAllFolders).ToString(CultureInfo.InvariantCulture)
            + "|r=" + user.MaxParentalRatingScore?.ToString(CultureInfo.InvariantCulture)
            + "/" + user.MaxParentalRatingSubScore?.ToString(CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
    }

    private static string Key(User user, string kind) =>
        user.Id.ToString("N") + "|" + Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" + kind;

    private async Task<RowResult> BuildBecauseYouWatched(User user, string? kind, CancellationToken cancellationToken)
    {
        // The "anything" row stays clear of the movie and show rows: another source title, and
        // none of their suggestions
        var otherSources = new HashSet<Guid>();
        var otherItems = new HashSet<Guid>();
        if (kind is null)
        {
            foreach (var other in new[] { "movie", "series" })
            {
                var row = await BecauseYouWatched(user, other, cancellationToken).ConfigureAwait(false);
                otherItems.UnionWith(row.ItemIds);
                if (row.Headers.TryGetValue("X-GooseFlix-Source-Id", out var id) && Guid.TryParse(id, out var sourceId))
                {
                    otherSources.Add(sourceId);
                }
            }
        }

        var candidates = RecentlyFinished(user, 30)
            .Where(i => MatchesKind(i, kind) && !otherSources.Contains(i.Id))
            .Take(10)
            .ToList();
        var source = RowSelection.Pick(candidates, RowSelection.DailySeed(user.Id, Today, "because-you-watched" + (kind is null ? string.Empty : ":" + kind)));
        if (source is null)
        {
            return RowResult.Nothing;
        }

        var similar = await _similarItemsManager.GetSimilarItemsAsync(
            source,
            Array.Empty<Guid>(),
            user,
            Minimal(),
            kind is null ? SimilarCandidates : SimilarCandidates * 2,
            _libraryManager.GetLibraryOptions(source),
            cancellationToken).ConfigureAwait(false);
        var titles = similar
            .Where(i => i.Id != source.Id && i is Movie or Series && MatchesKind(i, kind) && !otherItems.Contains(i.Id))
            .ToList();
        var played = PlayedIds(user, titles);
        var items = RowSelection.UnwatchedFirst(titles, i => played.Contains(i.Id));

        var headers = new Dictionary<string, string>
        {
            [TitleHeader] = "Because you watched " + source.Name,
            ["X-GooseFlix-Source-Id"] = source.Id.ToString("N"),
            ["X-GooseFlix-Source-Type"] = source is Series ? "Series" : "Movie",

            // Which row this is; an app that asks for the movie or show row can tell this plugin
            // (which has them) from an older one that ignores the kind
            ["X-GooseFlix-Row-Kind"] = kind ?? "any",
        };
        // The source came from a query without provider ids (Jellyfin only loads them when asked);
        // the item itself has them
        if ((_libraryManager.GetItemById(source.Id) ?? source).TryGetProviderId(MetadataProvider.Tmdb, out var tmdb))
        {
            headers["X-GooseFlix-Source-Tmdb"] = tmdb;
        }

        return new RowResult(items.Select(i => i.Id).ToList(), items.Count, headers);
    }

    private RowResult BuildMood(User user, int slot)
    {
        var picked = RowSelection.MoodForSlot(TodaysMoods(user), slot);
        if (picked is null)
        {
            return RowResult.Nothing;
        }

        var (mood, count) = picked.Value;
        var limit = Math.Max(1, Config.MaxItems);

        // The best-rated unwatched titles in the mood, shuffled for the day; capped before sorting
        var unwatched = _libraryManager.GetItemList(MoodQuery(user, mood, played: false, MoodCandidates));
        var items = RowSelection.SeededOrder(unwatched, RowSelection.DailySeed(user.Id, Today, "mood-items:" + mood.Id), i => i.Id.ToString("N"));
        if (items.Count < limit)
        {
            items.AddRange(_libraryManager.GetItemList(MoodQuery(user, mood, played: true, limit - items.Count)));
        }

        var headers = new Dictionary<string, string> { [TitleHeader] = mood.Name, ["X-GooseFlix-Mood-Id"] = mood.Id };
        return new RowResult(items.Select(i => i.Id).ToList(), count, headers);
    }

    private static InternalItemsQuery MoodQuery(User user, MoodDefinition mood, bool played, int limit) => new(user)
    {
        IncludeItemTypes = TitleKinds,
        Recursive = true,
        IsVirtualItem = false,
        IsPlayed = played,
        Tags = mood.Keywords.ToArray(),
        OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
        Limit = limit,
        DtoOptions = Minimal(),
    };

    private RowResult BuildPickedForYou(User user)
    {
        var watched = RecentlyFinished(user, 50);
        var disliked = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = TitleKinds,
            Recursive = true,
            IsLiked = false,
            Limit = 100,
            DtoOptions = Minimal(),
        });
        var weights = RowSelection.LessDisliked(
            RowSelection.GenreWeights(watched.Select(i => (IEnumerable<string>)i.Genres)),
            disliked.Select(i => (IEnumerable<string>)i.Genres));
        if (weights.Count == 0)
        {
            return RowResult.Nothing;
        }

        var top = weights.OrderByDescending(w => w.Value).Take(6).Select(w => w.Key).ToArray();
        var watchedIds = watched.Select(i => i.Id).Concat(disliked.Select(i => i.Id)).ToHashSet();
        var candidates = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = TitleKinds,
            Recursive = true,
            IsVirtualItem = false,
            IsPlayed = false,
            Genres = top,
            OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
            Limit = PickedCandidates,
            DtoOptions = Minimal(),
        }).Where(i => !watchedIds.Contains(i.Id));
        var items = RowSelection.Diverse(candidates, weights, i => i.Genres, i => i is Movie, i => i.Id.ToString("N"), RowSelection.DailySeed(user.Id, Today, "picked-for-you"));
        var headers = new Dictionary<string, string> { [TitleHeader] = "Picked for you" };
        return new RowResult(items.Select(i => i.Id).ToList(), items.Count, headers);
    }

    /// <summary>
    /// Movies and shows the user watched in the last <see cref="RowsConfig.LookbackDays"/>, newest first
    /// (an episode counts as its show). Three queries whatever the size of the history.
    /// </summary>
    private List<BaseItem> RecentlyFinished(User user, int maxTitles)
    {
        var played = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
            IsPlayed = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = Math.Min(400, maxTitles * 8),
            DtoOptions = Minimal(),
        });
        if (played.Count == 0)
        {
            return new List<BaseItem>();
        }

        var since = DateTime.UtcNow.AddDays(-Math.Max(1, Config.LookbackDays));
        var userData = _userDataManager.GetUserDataBatch(played, user);
        var order = new List<Guid>();
        var movies = new Dictionary<Guid, BaseItem>();
        foreach (var item in played)
        {
            if (userData.TryGetValue(item.Id, out var data) && data.LastPlayedDate is { } last && last < since)
            {
                break;
            }

            var titleId = item is Episode episode ? episode.SeriesId : item.Id;
            if (titleId == Guid.Empty || order.Contains(titleId))
            {
                continue;
            }

            order.Add(titleId);
            if (item is not Episode)
            {
                movies[titleId] = item;
            }

            if (order.Count >= maxTitles)
            {
                break;
            }
        }

        var seriesIds = order.Where(id => !movies.ContainsKey(id)).ToArray();
        var series = seriesIds.Length == 0
            ? new Dictionary<Guid, BaseItem>()
            : _libraryManager.GetItemList(new InternalItemsQuery(user) { ItemIds = seriesIds, DtoOptions = Minimal() }).ToDictionary(i => i.Id);
        return order
            .Select(id => movies.TryGetValue(id, out var m) ? m : series.GetValueOrDefault(id))
            .OfType<BaseItem>()
            .ToList();
    }

    private static bool MatchesKind(BaseItem item, string? kind) => kind switch
    {
        "movie" => item is Movie,
        "series" => item is Series,
        _ => true,
    };

    /// <summary>Which of these items the user marked "Don't recommend" (disliked).</summary>
    private HashSet<Guid> DislikedIds(User user, IReadOnlyList<BaseItem> items)
    {
        if (items.Count == 0)
        {
            return new HashSet<Guid>();
        }

        return _userDataManager.GetUserDataBatch(items, user)
            .Where(e => e.Value?.Likes == false)
            .Select(e => e.Key)
            .ToHashSet();
    }

    private HashSet<Guid> PlayedIds(User user, IReadOnlyList<BaseItem> items)
    {
        if (items.Count == 0)
        {
            return new HashSet<Guid>();
        }

        return _userDataManager.GetUserDataBatch(items, user)
            .Where(e => e.Value?.Played == true)
            .Select(e => e.Key)
            .ToHashSet();
    }

    private List<(MoodDefinition Mood, int Count)> QualifyingMoods(User user)
    {
        var key = AccessSignature(user) + "|" + Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" + Config.MoodsVersion();
        var entry = _moodCounts.GetOrAdd(key, _ => new Entry<List<(MoodDefinition Mood, int Count)>>(
            new Lazy<Task<List<(MoodDefinition Mood, int Count)>>>(() => Task.FromResult(CountMoods(user))),
            DateTime.UtcNow.AddDays(1)));
        if (_moodCounts.Count > 200)
        {
            foreach (var old in _moodCounts.Where(e => e.Value.Expires <= DateTime.UtcNow).Select(e => e.Key).ToList())
            {
                _moodCounts.TryRemove(old, out _);
            }
        }

        try
        {
            return entry.Value.Value.GetAwaiter().GetResult();
        }
        catch
        {
            _moodCounts.TryRemove(key, out _);
            throw;
        }
    }

    private List<(MoodDefinition Mood, int Count)> CountMoods(User user)
    {
        var watch = Stopwatch.StartNew();
        var result = RowSelection.QualifyingMoods(
            Config.Definitions(),
            mood => _libraryManager.GetCount(new InternalItemsQuery(user)
            {
                IncludeItemTypes = TitleKinds,
                Recursive = true,
                IsVirtualItem = false,
                Tags = mood.Keywords.ToArray(),
            }),
            Today.Month);
        _logger.LogDebug("GooseFlix: mood counts for {User}'s access group took {Ms} ms", user.Username, watch.ElapsedMilliseconds);
        return result;
    }

    private async Task<RowResult> Cached(User user, string kind, Func<Task<RowResult>> build)
    {
        var key = Key(user, kind);
        var now = DateTime.UtcNow;
        if (_rows.TryGetValue(key, out var hit) && hit.Expires > now)
        {
            return await hit.Value.Value.ConfigureAwait(false);
        }

        var fresh = new Entry<RowResult>(new Lazy<Task<RowResult>>(() => Timed(user, kind, build)), now.AddHours(Math.Max(0, Config.CacheHours)));
        var entry = _rows.AddOrUpdate(key, fresh, (_, old) => old.Expires > now ? old : fresh);
        if (_rows.Count > 5000)
        {
            foreach (var old in _rows.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
            {
                _rows.TryRemove(old, out _);
            }
        }

        return await entry.Value.Value.ConfigureAwait(false);
    }

    private async Task<RowResult> Timed(User user, string kind, Func<Task<RowResult>> build)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var row = await build().ConfigureAwait(false);
            _logger.LogDebug("GooseFlix: built {Row} for {User} in {Ms} ms ({Count} titles)", kind, user.Username, watch.ElapsedMilliseconds, row.ItemIds.Count);
            return row;
        }
#pragma warning disable CA1031 // A row that can't be built is an empty row, not an error page
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "GooseFlix: row {Row} failed for {User} after {Ms} ms", kind, user.Username, watch.ElapsedMilliseconds);
            return RowResult.Nothing;
        }
    }

    private sealed record Entry<T>(Lazy<Task<T>> Value, DateTime Expires);
}
