// Fork: the personal home row endpoints (GooseFlix #1635). Each answers with QueryResult<BaseItemDto>
// for one user, so the app's "custom endpoint" home row shows it as it is. No data is an empty
// result, never an error: the app hides empty rows.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Wholphin.Rows;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wholphin.Api;

/// <summary>Personal home rows for the GooseFlix app.</summary>
[ApiController]
[Authorize]
[Route("wholphin/rows")]
public class RowsController : ControllerBase
{
    /// <summary>Header carrying the row's title, percent-encoded UTF-8.</summary>
    public const string TitleHeader = "X-GooseFlix-Row-Title";

    private const string SourceIdHeader = "X-GooseFlix-Source-Id";
    private const string SourceTmdbHeader = "X-GooseFlix-Source-Tmdb";
    private const string SourceTypeHeader = "X-GooseFlix-Source-Type";

    private static readonly ConcurrentDictionary<string, CachedRow> Cache = new();

    private static readonly BaseItemKind[] TitleKinds = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IDtoService _dtoService;
    private readonly ISimilarItemsManager _similarItemsManager;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<RowsController> _logger;

    /// <summary>Initializes a new instance of the <see cref="RowsController"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="dtoService">DTO service.</param>
    /// <param name="similarItemsManager">Similar items manager.</param>
    /// <param name="authContext">Authorization context.</param>
    /// <param name="logger">Logger.</param>
    public RowsController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IDtoService dtoService,
        ISimilarItemsManager similarItemsManager,
        IAuthorizationContext authContext,
        ILogger<RowsController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _dtoService = dtoService;
        _similarItemsManager = similarItemsManager;
        _authContext = authContext;
        _logger = logger;
    }

    private static RowsConfig Config => WholphinPlugin.Instance?.Configuration.Rows ?? new RowsConfig();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>What this plugin serves, so the app can tell it from upstream's plugin.</summary>
    /// <returns>The plugin version and row types.</returns>
    [HttpGet("capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RowsCapabilities> Capabilities()
    {
        var config = Config;
        var rows = new List<string>();
        if (config.BecauseYouWatched)
        {
            rows.Add("because-you-watched");
        }

        if (config.Moods)
        {
            rows.Add("mood");
        }

        if (config.PickedForYou)
        {
            rows.Add("picked-for-you");
        }

        return new RowsCapabilities
        {
            Version = WholphinPlugin.Instance?.Version.ToString() ?? string.Empty,
            Rows = rows,
            MoodsPerDay = config.Moods ? Math.Max(0, config.MoodsPerDay) : 0,
        };
    }

    /// <summary>Similar titles to one the user finished recently; the source rotates daily.</summary>
    /// <param name="userId">The user (the app sends it).</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Most items.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row.</returns>
    [HttpGet("because-you-watched")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> BecauseYouWatched(
        [FromQuery] Guid? userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var (user, denied) = await ResolveUser(userId).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        if (user is null || !Config.BecauseYouWatched)
        {
            return Empty();
        }

        var row = await Cached(user, "byw", async () =>
        {
            var candidates = RecentlyFinished(user).Take(10).ToList();
            var source = RowSelection.Pick(candidates, RowSelection.DailySeed(user.Id, Today, "byw"));
            if (source is null)
            {
                return CachedRow.Nothing;
            }

            var options = SelectionOptions();
            var similar = await _similarItemsManager.GetSimilarItemsAsync(
                source,
                Array.Empty<Guid>(),
                user,
                options,
                100,
                _libraryManager.GetLibraryOptions(source),
                cancellationToken).ConfigureAwait(false);
            var items = RowSelection.UnwatchedFirst(
                similar.Where(i => i.Id != source.Id && TitleKinds.Contains(i.GetBaseItemKind())),
                i => IsPlayed(user, i));
            var headers = new Dictionary<string, string>
            {
                [TitleHeader] = "Because you watched " + source.Name,
                [SourceIdHeader] = source.Id.ToString("N"),
                [SourceTypeHeader] = source is Series ? "Series" : "Movie",
            };
            if (source.TryGetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb, out var tmdb))
            {
                headers[SourceTmdbHeader] = tmdb;
            }

            return new CachedRow(items.Select(i => i.Id).ToList(), items.Count, headers);
        }).ConfigureAwait(false);
        return Answer(user, row, startIndex, limit);
    }

    /// <summary>Today's mood in a slot: titles with any of the mood's keyword tags.</summary>
    /// <param name="userId">The user (the app sends it).</param>
    /// <param name="slot">Which of today's moods, from 1.</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Most items.</param>
    /// <returns>The row; TotalRecordCount is how many titles the user has for the mood.</returns>
    [HttpGet("mood")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> Mood(
        [FromQuery] Guid? userId,
        [FromQuery] int slot,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit)
    {
        var (user, denied) = await ResolveUser(userId).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        if (user is null || !Config.Moods)
        {
            return Empty();
        }

        var row = await Cached(user, "mood" + slot, () =>
        {
            var picked = RowSelection.MoodForSlot(TodaysMoods(user), slot);
            if (picked is null)
            {
                return Task.FromResult(CachedRow.Nothing);
            }

            var (mood, count) = picked.Value;
            var found = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = TitleKinds,
                Recursive = true,
                IsVirtualItem = false,
                Tags = mood.Keywords.ToArray(),
                Limit = 500,
                DtoOptions = SelectionOptions(),
            });
            var shuffled = RowSelection.SeededOrder(found, RowSelection.DailySeed(user.Id, Today, "mood-items:" + mood.Id), i => i.Id.ToString("N"));
            var items = RowSelection.UnwatchedFirst(shuffled, i => IsPlayed(user, i));
            var headers = new Dictionary<string, string> { [TitleHeader] = mood.Name, ["X-GooseFlix-Mood-Id"] = mood.Id };
            return Task.FromResult(new CachedRow(items.Select(i => i.Id).ToList(), count, headers));
        }).ConfigureAwait(false);
        return Answer(user, row, startIndex, limit);
    }

    /// <summary>Today's moods for the user, for the app and for checking.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The moods, slot 1 first.</returns>
    [HttpGet("moods")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<TodaysMood>>> Moods([FromQuery] Guid? userId)
    {
        var (user, denied) = await ResolveUser(userId).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        if (user is null || !Config.Moods)
        {
            return Array.Empty<TodaysMood>();
        }

        try
        {
            return TodaysMoods(user)
                .Select((m, i) => new TodaysMood { Slot = i + 1, Id = m.Mood.Id, Name = m.Mood.Name, Titles = m.Count })
                .ToList();
        }
#pragma warning disable CA1031 // No moods is an empty list, not an error page
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "GooseFlix moods failed for {User}", user.Username);
            return Array.Empty<TodaysMood>();
        }
    }

    /// <summary>Unwatched titles in the genres the user watches most.</summary>
    /// <param name="userId">The user (the app sends it).</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Most items.</param>
    /// <returns>The row.</returns>
    [HttpGet("picked-for-you")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> PickedForYou(
        [FromQuery] Guid? userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit)
    {
        var (user, denied) = await ResolveUser(userId).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        if (user is null || !Config.PickedForYou)
        {
            return Empty();
        }

        var row = await Cached(user, "pfy", () =>
        {
            var watched = RecentlyFinished(user).Take(50).ToList();
            var weights = RowSelection.GenreWeights(watched.Select(i => (IEnumerable<string>)i.Genres));
            if (weights.Count == 0)
            {
                return Task.FromResult(CachedRow.Nothing);
            }

            var top = weights.OrderByDescending(w => w.Value).Take(6).Select(w => w.Key).ToArray();
            var watchedIds = watched.Select(i => i.Id).ToHashSet();
            var candidates = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = TitleKinds,
                Recursive = true,
                IsVirtualItem = false,
                IsPlayed = false,
                Genres = top,
                Limit = 400,
                DtoOptions = SelectionOptions(),
            }).Where(i => !watchedIds.Contains(i.Id));
            var items = RowSelection.ByGenreMatch(candidates, weights, i => i.Genres, i => i.Id.ToString("N"), RowSelection.DailySeed(user.Id, Today, "pfy"));
            var headers = new Dictionary<string, string> { [TitleHeader] = "Picked for you" };
            return Task.FromResult(new CachedRow(items.Select(i => i.Id).ToList(), items.Count, headers));
        }).ConfigureAwait(false);
        return Answer(user, row, startIndex, limit);
    }

    private static QueryResult<BaseItemDto> Empty() => new(Array.Empty<BaseItemDto>());

    private static DtoOptions SelectionOptions() => new(false) { EnableImages = false, EnableUserData = false };

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

    private async Task<(User? User, ActionResult? Denied)> ResolveUser(Guid? userId)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var caller = auth.UserId;
        var privileged = auth.IsApiKey || (auth.User?.HasPermission(PermissionKind.IsAdministrator) ?? false);
        var requested = userId is { } id && id != Guid.Empty ? id : caller;
        if (!RowSelection.MayActFor(caller, requested, privileged))
        {
            return (null, Forbid());
        }

        return (_userManager.GetUserById(requested), null);
    }

    private bool IsPlayed(User user, BaseItem item) => _userDataManager.GetUserData(user, item)?.Played ?? false;

    /// <summary>Movies and shows the user finished (or watched episodes of) recently, newest first.</summary>
    private IEnumerable<BaseItem> RecentlyFinished(User user)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, Config.LookbackDays));
        var played = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
            IsPlayed = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = 200,
            DtoOptions = SelectionOptions(),
        });
        var seen = new HashSet<Guid>();
        foreach (var item in played)
        {
            var last = _userDataManager.GetUserData(user, item)?.LastPlayedDate;
            if (last is { } date && date < since)
            {
                break;
            }

            BaseItem? title = item is Episode episode ? episode.Series : item;
            if (title is not null && seen.Add(title.Id))
            {
                yield return title;
            }
        }
    }

    private List<(MoodDefinition Mood, int Count)> TodaysMoods(User user)
    {
        var config = Config;
        var key = Key(user, "moods");
        if (Cache.TryGetValue(key, out var hit) && hit.Moods is not null && hit.Expires > DateTime.UtcNow)
        {
            return hit.Moods;
        }

        var qualifying = RowSelection.QualifyingMoods(
            config.Definitions(),
            mood => _libraryManager.GetCount(new InternalItemsQuery(user)
            {
                IncludeItemTypes = TitleKinds,
                Recursive = true,
                IsVirtualItem = false,
                Tags = mood.Keywords.ToArray(),
            }),
            Today.Month);
        var todays = RowSelection.TodaysMoods(qualifying, RowSelection.DailySeed(user.Id, Today, "moods"), config.MoodsPerDay);
        Cache[key] = CachedRow.ForMoods(todays, Expiry());
        return todays;
    }

    private async Task<CachedRow> Cached(User user, string kind, Func<Task<CachedRow>> build)
    {
        var key = Key(user, kind);
        if (Cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow)
        {
            return hit;
        }

        CachedRow row;
        try
        {
            row = await build().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A row that can't be built is an empty row, not an error page
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "GooseFlix row {Kind} failed for {User}", kind, user.Username);
            return CachedRow.Nothing;
        }

        row = row with { Expires = Expiry() };
        Cache[key] = row;
        PruneCache();
        return row;
    }

    private ActionResult<QueryResult<BaseItemDto>> Answer(User user, CachedRow row, int? startIndex, int? limit)
    {
        foreach (var (name, value) in row.Headers)
        {
            Response.Headers[name] = RowSelection.HeaderValue(value);
        }

        var page = RowSelection.Page(row.ItemIds, startIndex, limit ?? Config.MaxItems);
        var items = page.Select(_libraryManager.GetItemById).OfType<BaseItem>().ToList();
        var dtos = _dtoService.GetBaseItemDtos(items, CardOptions(), user);
        return new QueryResult<BaseItemDto>(startIndex ?? 0, row.TotalCount, dtos);
    }

    private static string Key(User user, string kind) => user.Id.ToString("N") + "|" + Today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + "|" + kind;

    private static DateTime Expiry() => DateTime.UtcNow.AddHours(Math.Max(0, Config.CacheHours));

    private static void PruneCache()
    {
        if (Cache.Count < 2000)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var entry in Cache.Where(e => e.Value.Expires <= now).ToList())
        {
            Cache.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>A built row, kept for a few hours.</summary>
    private sealed record CachedRow(IReadOnlyList<Guid> ItemIds, int TotalCount, IReadOnlyDictionary<string, string> Headers)
    {
        public static CachedRow Nothing => new(Array.Empty<Guid>(), 0, new Dictionary<string, string>());

        public DateTime Expires { get; init; }

        public List<(MoodDefinition Mood, int Count)>? Moods { get; init; }

        public static CachedRow ForMoods(List<(MoodDefinition Mood, int Count)> moods, DateTime expires) =>
            Nothing with { Moods = moods, Expires = expires };
    }
}

/// <summary>What the plugin serves.</summary>
public class RowsCapabilities
{
    /// <summary>Gets or sets the plugin version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the row types that are on.</summary>
    public IReadOnlyList<string> Rows { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets how many mood slots there are today.</summary>
    public int MoodsPerDay { get; set; }
}

/// <summary>One of today's moods.</summary>
public class TodaysMood
{
    /// <summary>Gets or sets the slot, from 1.</summary>
    public int Slot { get; set; }

    /// <summary>Gets or sets the mood id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the row title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets how many titles the user has for it.</summary>
    public int Titles { get; set; }
}
