// Fork: fetches every movie's and show's ratings from MDBList itself, in batches (GooseFlix #1671),
// instead of one title at a time through the MDBList Ratings plugin. Every five minutes it fetches
// what's due: new titles (once their ids have arrived) and anything older than RefreshHours, within
// a daily request limit and a per-run cap, with a pause between requests. It can also fill
// Jellyfin's Community and Critic ratings from chosen sources (off by default).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wholphin.Ratings;

/// <summary>Keeps the ratings store current.</summary>
public sealed class TitleRatingsService : IHostedService, IDisposable
{
    private const string ApiBase = "https://api.mdblist.com";
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(1500);

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TitleRatingsStore _store;
    private readonly ILogger<TitleRatingsService> _logger;
    private static readonly DailyBudget Budget = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private int _working;
    private string? _rejectedKey;
    private DateTime _pausedUntilUtc;

    /// <summary>Initializes a new instance of the <see cref="TitleRatingsService"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="httpClientFactory">HTTP clients.</param>
    /// <param name="store">The ratings store.</param>
    /// <param name="logger">Logger.</param>
    public TitleRatingsService(ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, TitleRatingsStore store, ILogger<TitleRatingsService> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    /// <summary>Gets the settings.</summary>
    public static TitleRatingsConfig Config => WholphinPlugin.Instance?.Configuration.TitleRatings ?? new TitleRatingsConfig();

    /// <summary>Gets a value indicating whether ratings are being fetched (switched on, with a key).</summary>
    public static bool Active => Config is { Enabled: true } c && !string.IsNullOrWhiteSpace(c.MdbListApiKey);

    /// <summary>Gets requests used today.</summary>
    public static int UsedToday => Budget.Used(DateOnly.FromDateTime(DateTime.Now));

    /// <summary>Gets the running service, once Jellyfin has started it (null before, or if it never did).</summary>
    public static TitleRatingsService? Current { get; private set; }

    /// <summary>Gets the last run's outcome, for the status endpoint.</summary>
    public static RunReport? LastRun { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Current = this;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        _logger.LogInformation("GooseFlix: ratings service started (fetching {State})", Active ? "on" : "off until switched on with a key");
        return Task.CompletedTask;
    }

    /// <summary>Runs now instead of at the next five-minute tick (the status endpoint's "run now").</summary>
    /// <returns>False if a run is already going.</returns>
    public bool RunNow()
    {
        if (Volatile.Read(ref _working) == 1)
        {
            return false;
        }

        _ = TickAsync();
        return true;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _working, 1) == 1)
        {
            return;
        }

        try
        {
            var config = Config;
            if (!Active)
            {
                return;
            }

            if (DateTime.UtcNow < _pausedUntilUtc || config.MdbListApiKey == _rejectedKey)
            {
                LastRun = new RunReport(DateTime.UtcNow, 0, 0, 0, _rejectedKey is not null && config.MdbListApiKey == _rejectedKey ? "MDBList refused the key" : "paused after a rate limit");
                return;
            }

            var titles = LibraryTitles();
            var due = TitleRatingsLogic.Due(titles, id => _store.Get(id)?.FetchedAtUtc, DateTime.UtcNow, config.RefreshHours, config.NewTitleDelayMinutes);
            if (due.Count == 0)
            {
                LastRun = new RunReport(DateTime.UtcNow, titles.Count, 0, 0, null);
                return;
            }

            var batches = TitleRatingsLogic.Batches(due, config.BatchSize).Take(Math.Max(1, config.MaxRequestsPerRun)).ToList();
            var fetched = 0;
            foreach (var batch in batches)
            {
                if (_stopping.IsCancellationRequested || !Budget.TryUse(DateOnly.FromDateTime(DateTime.Now), config.DailyRequestLimit))
                {
                    break;
                }

                if (!await FetchAsync(batch, config).ConfigureAwait(false))
                {
                    break;
                }

                fetched += batch.Titles.Count;
                await Task.Delay(Pause, _stopping.Token).ConfigureAwait(false);
            }

            _store.Save();
            LastRun = new RunReport(DateTime.UtcNow, titles.Count, due.Count, fetched, null);
            _logger.LogInformation(
                "GooseFlix: ratings fetched for {Fetched} titles ({Due} of {Titles} were due, {Used} requests today)",
                fetched,
                due.Count,
                titles.Count,
                UsedToday);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Jellyfin is stopping
        }
#pragma warning disable CA1031 // A failed run is logged and the next one tries again
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Timeouts included: only a shutdown is quiet
            LastRun = new RunReport(DateTime.UtcNow, 0, 0, 0, ex.GetType().Name + ": " + ex.Message);
            _logger.LogWarning(ex, "GooseFlix: ratings run failed");
        }
        finally
        {
            Interlocked.Exchange(ref _working, 0);
        }
    }

    /// <summary>Every movie and show (not virtual) with the ids MDBList can look it up by.</summary>
    private List<TitleRef> LibraryTitles() =>
        _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false },
        })
        .Select(i => new TitleRef(
            i.Id,
            i is Movie,
            i.GetProviderId(MetadataProvider.Tmdb),
            i.GetProviderId(MetadataProvider.Imdb),
            i.GetProviderId(MetadataProvider.Tvdb),
            i.DateCreated))
        .ToList();

    /// <summary>One request. False to stop this run (rate limited, rejected key).</summary>
    private async Task<bool> FetchAsync(FetchBatch batch, TitleRatingsConfig config)
    {
        var url = $"{ApiBase}/{batch.Provider}/{batch.MediaType}/?apikey={Uri.EscapeDataString(config.MdbListApiKey!)}";
        using var content = new StringContent(TitleRatingsLogic.RequestBody(batch), Encoding.UTF8, "application/json");
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.PostAsync(new Uri(url), content, _stopping.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(_stopping.Token).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                // Don't keep sending a key MDBList refuses; a new key in the settings starts again
                _rejectedKey = config.MdbListApiKey;
                _logger.LogWarning("GooseFlix: MDBList refused the API key (HTTP {Status}); ratings paused until the key changes", (int)response.StatusCode);
                return false;
            case HttpStatusCode.TooManyRequests:
                _pausedUntilUtc = DateTime.UtcNow.AddMinutes(15);
                _logger.LogInformation("GooseFlix: MDBList rate limit reached; ratings paused for 15 minutes");
                return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GooseFlix: MDBList answered HTTP {Status} for a {Type} batch", (int)response.StatusCode, batch.MediaType);
            return true;
        }

        var matched = TitleRatingsLogic.Match(batch, TitleRatingsLogic.Parse(body));
        var now = DateTime.UtcNow;
        foreach (var title in batch.Titles)
        {
            // A title MDBList didn't know still gets an (empty) entry, so it's tried again after
            // RefreshHours instead of every five minutes
            var ratings = matched.TryGetValue(title.ItemId, out var match)
                ? new TitleRatings { FetchedAtUtc = now, Ids = match.Ids, Ratings = match.Ratings }
                : new TitleRatings { FetchedAtUtc = now };
            _store.Set(title.ItemId, ratings);
            if (config.FieldFill.Enabled && ratings.Ratings.Count > 0)
            {
                await FillFieldsAsync(title, ratings, config.FieldFill).ConfigureAwait(false);
            }
        }

        return true;
    }

    /// <summary>Writes the chosen sources into Jellyfin's Community and Critic ratings when they differ.</summary>
    private async Task FillFieldsAsync(TitleRef title, TitleRatings ratings, FieldFillConfig fill)
    {
        try
        {
            var item = _libraryManager.GetItemById(title.ItemId);
            if (item is null)
            {
                return;
            }

            var libraries = _libraryManager.GetCollectionFolders(item).Select(f => f.Name ?? string.Empty);
            var (communitySource, criticSource) = TitleRatingsLogic.SourcesFor(fill, title.IsMovie, libraries);
            var changed = false;
            if (communitySource.Length > 0 && TitleRatingsLogic.Community(ratings.For(communitySource)) is { } community
                && (item.CommunityRating is not { } oldCommunity || Math.Abs(oldCommunity - community) > 0.05f))
            {
                item.CommunityRating = community;
                changed = true;
            }

            if (criticSource.Length > 0 && TitleRatingsLogic.Critic(ratings.For(criticSource)) is { } critic
                && (item.CriticRating is not { } oldCritic || Math.Abs(oldCritic - critic) > 0.5f))
            {
                item.CriticRating = critic;
                changed = true;
            }

            if (changed)
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, _stopping.Token).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // One title's fields failing doesn't stop the batch
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "GooseFlix: could not fill ratings fields for {Item}", title.ItemId);
        }
    }
}

/// <summary>What a ratings run did.</summary>
/// <param name="AtUtc">When it ran.</param>
/// <param name="Titles">Movies and shows in the library.</param>
/// <param name="Due">How many were due.</param>
/// <param name="Fetched">How many were fetched.</param>
/// <param name="Error">What went wrong, if anything.</param>
public sealed record RunReport(DateTime AtUtc, int Titles, int Due, int Fetched, string? Error);
