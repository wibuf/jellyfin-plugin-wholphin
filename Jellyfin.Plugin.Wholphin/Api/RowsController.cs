// Fork: the personal home row endpoints (GooseFlix #1635). Each answers with QueryResult<BaseItemDto>
// for one user, so the app's "custom endpoint" home row shows it as it is. No data is an empty
// result, never an error: the app hides empty rows. The rows are built by RowsService.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Wholphin.Rows;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
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
    private readonly RowsService _rows;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<RowsController> _logger;

    /// <summary>Initializes a new instance of the <see cref="RowsController"/> class.</summary>
    /// <param name="rows">Rows service.</param>
    /// <param name="authContext">Authorization context.</param>
    /// <param name="logger">Logger.</param>
    public RowsController(RowsService rows, IAuthorizationContext authContext, ILogger<RowsController> logger)
    {
        _rows = rows;
        _authContext = authContext;
        _logger = logger;
    }

    /// <summary>What this plugin serves, so the app can tell it from upstream's plugin.</summary>
    /// <returns>The plugin version and row types.</returns>
    [HttpGet("capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RowsCapabilities> Capabilities()
    {
        var config = RowsService.Config;
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
            Features = new[] { "because-you-watched-kinds", "dislikes", "varied-picks" },
            MoodsPerDay = config.Moods ? Math.Max(0, config.MoodsPerDay) : 0,
        };
    }

    /// <summary>Similar titles to one the user finished recently; the source rotates daily.</summary>
    /// <param name="userId">The user (the app sends it).</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Most items.</param>
    /// <param name="kind">"movie" or "series" for the movie or show row; none for the row from anything.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row.</returns>
    [HttpGet("because-you-watched")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<QueryResult<BaseItemDto>>> BecauseYouWatched(
        [FromQuery] Guid? userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] string? kind,
        CancellationToken cancellationToken)
    {
        var watchedKind = RowSelection.WatchedKind(kind);
        return Row("because-you-watched" + (watchedKind is null ? string.Empty : ":" + watchedKind), userId, startIndex, limit, RowsService.Config.BecauseYouWatched, user => _rows.BecauseYouWatched(user, watchedKind, cancellationToken));
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
    public Task<ActionResult<QueryResult<BaseItemDto>>> Mood(
        [FromQuery] Guid? userId,
        [FromQuery] int slot,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit) =>
        Row("mood" + slot, userId, startIndex, limit, RowsService.Config.Moods, user => _rows.Mood(user, slot));

    /// <summary>Unwatched titles in the genres the user watches most.</summary>
    /// <param name="userId">The user (the app sends it).</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Most items.</param>
    /// <returns>The row.</returns>
    [HttpGet("picked-for-you")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<QueryResult<BaseItemDto>>> PickedForYou(
        [FromQuery] Guid? userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit) =>
        Row("picked-for-you", userId, startIndex, limit, RowsService.Config.PickedForYou, _rows.PickedForYou);

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

        if (user is null || !RowsService.Config.Moods)
        {
            return Array.Empty<TodaysMood>();
        }

        try
        {
            return _rows.TodaysMoods(user)
                .Select((m, i) => new TodaysMood { Slot = i + 1, Id = m.Mood.Id, Name = m.Mood.Name, Titles = m.Count })
                .ToList();
        }
#pragma warning disable CA1031 // No moods is an empty list, not an error page
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "GooseFlix: moods failed for {User}", user.Username);
            return Array.Empty<TodaysMood>();
        }
    }

    private async Task<ActionResult<QueryResult<BaseItemDto>>> Row(
        string name,
        Guid? userId,
        int? startIndex,
        int? limit,
        bool enabled,
        Func<User, Task<RowResult>> build)
    {
        var watch = Stopwatch.StartNew();
        var (user, denied) = await ResolveUser(userId).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        if (user is null || !enabled)
        {
            return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
        }

        try
        {
            var row = await build(user).ConfigureAwait(false);
            var built = watch.ElapsedMilliseconds;
            foreach (var (header, value) in row.Headers)
            {
                Response.Headers[header] = RowSelection.HeaderValue(value);
            }

            var page = _rows.Page(user, row, startIndex, limit);
            _logger.LogDebug(
                "GooseFlix: {Row} for {User}: {Total} ms ({Built} ms building or from cache, {Count} cards)",
                name,
                user.Username,
                watch.ElapsedMilliseconds,
                built,
                page.Items.Count);
            return page;
        }
#pragma warning disable CA1031 // A row that fails is an empty row, not an error page
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "GooseFlix: {Row} failed for {User}", name, user.Username);
            return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
        }
    }

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

        return (_rows.GetUser(requested), null);
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

    /// <summary>Gets or sets what this version can do beyond the first release (GooseFlix #1636).</summary>
    public IReadOnlyList<string> Features { get; set; } = Array.Empty<string>();
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
