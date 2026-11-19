// Fork: the plugin's own MDBList ratings for the GooseFlix app (GooseFlix #1671).
// - /wholphin/ratings/item: one title's ratings, in the same JSON shape as the MDBList Ratings
//   plugin's CachedByItemId, so the app's ratings row reads either.
// - /wholphin/ratings/values: one source's score for every title in a library, for the app's
//   "Rating" sort. The app asks Jellyfin which titles match its filters (with the user's own
//   access), then orders them by these scores.
// - /wholphin/ratings/status: what's stored and requests used today (admins).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Wholphin.Ratings;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Wholphin.Api;

/// <summary>Ratings endpoints.</summary>
[ApiController]
[Authorize]
[Route("wholphin/ratings")]
public class RatingsController : ControllerBase
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TitleRatingsStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IAuthorizationContext _authContext;

    /// <summary>Initializes a new instance of the <see cref="RatingsController"/> class.</summary>
    /// <param name="store">Ratings store.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="authContext">Authorization context.</param>
    public RatingsController(TitleRatingsStore store, ILibraryManager libraryManager, IUserManager userManager, IAuthorizationContext authContext)
    {
        _store = store;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _authContext = authContext;
    }

    /// <summary>One title's ratings, shaped like the MDBList Ratings plugin's CachedByItemId.</summary>
    /// <param name="itemId">The movie or show (an episode or season answers for its show).</param>
    /// <returns>The ratings; <c>hasCache</c> false when there are none yet.</returns>
    [HttpGet("item")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Item([FromQuery] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        var titleId = item switch
        {
            MediaBrowser.Controller.Entities.TV.Episode episode => episode.SeriesId,
            MediaBrowser.Controller.Entities.TV.Season season => season.SeriesId,
            _ => itemId,
        };
        var ratings = _store.Get(titleId);
        var body = new
        {
            hasCache = ratings is { Ratings.Count: > 0 },
            cachedAtUtc = ratings?.FetchedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            ids = ratings?.Ids ?? new Dictionary<string, string>(),
            ratings = (ratings?.Ratings ?? new List<RatingValue>())
                .Select(r => new { source = r.Source, value = r.Value, score = r.Score, votes = r.Votes, url = r.Url }),
        };
        return Content(JsonSerializer.Serialize(body, CamelCase), "application/json");
    }

    /// <summary>
    /// One source's rating for every movie and show in a library the caller can see, by item id
    /// (no dashes). Titles without that rating are left out.
    /// </summary>
    /// <param name="parentId">The library.</param>
    /// <param name="source">MDBList's source name (imdb, tomatoes, popcorn, myanimelist, ...).</param>
    /// <returns><c>{source, values: {id: {value, score, votes}}}</c>.</returns>
    [HttpGet("values")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Values([FromQuery] Guid parentId, [FromQuery] string source)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.UserId == Guid.Empty ? null : _userManager.GetUserById(auth.UserId);
        var parent = parentId == Guid.Empty ? null : _libraryManager.GetItemById(parentId);
        if (parent is null || (user is not null && !parent.IsVisible(user) && !auth.IsApiKey))
        {
            return Forbid();
        }

        var ids = _libraryManager.GetItemIds(new InternalItemsQuery
        {
            AncestorIds = new[] { parentId },
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false),
        });
        var name = (source ?? string.Empty).Trim().ToLowerInvariant();
        var values = new Dictionary<string, object>();
        foreach (var id in ids)
        {
            if (_store.Get(id)?.For(name) is { } rating && TitleRatingsLogic.SortScore(rating) is { } score)
            {
                values[id.ToString("N")] = new { value = rating.Value, score, votes = rating.Votes };
            }
        }

        return Content(JsonSerializer.Serialize(new { source = name, values }, CamelCase), "application/json");
    }

    /// <summary>How many titles have ratings and how many requests were used today. Admins only.</summary>
    /// <returns>The status.</returns>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Status()
    {
        if (!await IsAdmin().ConfigureAwait(false))
        {
            return Forbid();
        }

        var all = _store.All();
        var now = DateTime.UtcNow;
        var body = new
        {
            active = TitleRatingsService.Active,
            titles = all.Count,
            withRatings = all.Values.Count(t => t.Ratings.Count > 0),
            fetchedLastDay = all.Values.Count(t => now - t.FetchedAtUtc < TimeSpan.FromDays(1)),
            requestsToday = TitleRatingsService.UsedToday,
            dailyRequestLimit = TitleRatingsService.Config.DailyRequestLimit,
            serviceRunning = TitleRatingsService.Current is not null,
            lastRun = TitleRatingsService.LastRun,
        };
        return Content(JsonSerializer.Serialize(body, CamelCase), "application/json");
    }

    /// <summary>Starts a ratings run now instead of at the next five-minute tick. Admins only.</summary>
    /// <returns>202 when started, 409 if one is already running, 503 if the service isn't running.</returns>
    [HttpPost("refresh")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Refresh()
    {
        if (!await IsAdmin().ConfigureAwait(false))
        {
            return Forbid();
        }

        if (TitleRatingsService.Current is not { } service)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return service.RunNow() ? Accepted() : Conflict();
    }

    private async Task<bool> IsAdmin()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return auth.IsApiKey || (auth.User?.HasPermission(PermissionKind.IsAdministrator) ?? false);
    }
}
