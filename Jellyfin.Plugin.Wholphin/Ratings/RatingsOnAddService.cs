// Fork: fetches ratings for movies and shows as soon as they're added (GooseFlix #1635), rather than
// waiting for MDBList Ratings' daily task. The library event only queues the item; a timer does the
// work, so library scans are never held up.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wholphin.Ratings;

/// <summary>Queues new movies and shows and asks MDBList Ratings to refresh them.</summary>
public sealed class RatingsOnAddService : IHostedService, IDisposable
{
    private const int PerTick = 10;

    private readonly ILibraryManager _libraryManager;
    private readonly MdbListBridge _bridge;
    private readonly ILogger<RatingsOnAddService> _logger;
    private readonly RatingsQueue _queue = new();
    private readonly DailyBudget _budget = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private int _working;

    /// <summary>Initializes a new instance of the <see cref="RatingsOnAddService"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="bridge">MDBList bridge.</param>
    /// <param name="logger">Logger.</param>
    public RatingsOnAddService(ILibraryManager libraryManager, MdbListBridge bridge, ILogger<RatingsOnAddService> logger)
    {
        _libraryManager = libraryManager;
        _bridge = bridge;
        _logger = logger;
    }

    private static RatingsOnAddConfig Config => WholphinPlugin.Instance?.Configuration.RatingsOnAdd ?? new RatingsOnAddConfig();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
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

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var config = Config;
            // Movies and shows only: a new show arrives as a Series, and new episodes of an existing
            // show don't change its ratings (the daily task covers them)
            if (!config.Enabled || e.Item is not (Movie or Series) || e.Item.IsVirtualItem)
            {
                return;
            }

            if (_queue.Enqueue(e.Item.Id, DateTime.UtcNow, Today, config.DelayMinutes))
            {
                _logger.LogDebug("GooseFlix: queued {Item} for ratings", e.Item.Name);
            }
        }
#pragma warning disable CA1031 // Never disturb a library scan
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "GooseFlix: could not queue an added item for ratings");
        }
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
            if (!config.Enabled || _queue.Count == 0)
            {
                return;
            }

            foreach (var id in _queue.Due(DateTime.UtcNow, PerTick))
            {
                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                await HandleAsync(id, config).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A failed tick is logged and the next one tries again
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "GooseFlix: ratings on add tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _working, 0);
        }
    }

    private async Task HandleAsync(Guid id, RatingsOnAddConfig config)
    {
        var item = _libraryManager.GetItemById(id);
        if (item is null)
        {
            _queue.Done(id);
            return;
        }

        if (!_bridge.IsAvailable())
        {
            // Logged once by the bridge; keep nothing waiting on a plugin that isn't there
            _queue.Done(id);
            return;
        }

        var hasIds = item.HasProviderId(MetadataProvider.Tmdb) || item.HasProviderId(MetadataProvider.Imdb);
        var cachedAt = hasIds ? await _bridge.CachedAtAsync(item, _stopping.Token).ConfigureAwait(false) : null;
        switch (RatingsRules.Decide(hasIds, cachedAt, DateTimeOffset.UtcNow, _budget.HasRoom(Today, config.DailyLimit)))
        {
            case RatingsDecision.WaitForIds:
                if (!_queue.Retry(id, DateTime.UtcNow))
                {
                    _logger.LogDebug("GooseFlix: {Item} still has no TMDB/IMDb id, leaving it to the daily task", item.Name);
                }

                return;

            case RatingsDecision.SkipFresh:
                _queue.Done(id);
                return;

            case RatingsDecision.OutOfBudget:
                _queue.Done(id);
                _logger.LogInformation("GooseFlix: today's ratings limit ({Limit}) is used, {Item} waits for the daily task", config.DailyLimit, item.Name);
                return;

            default:
                _queue.Done(id);
                if (!_budget.TryUse(Today, config.DailyLimit))
                {
                    return;
                }

                var outcome = await _bridge.RefreshAsync(item, _stopping.Token).ConfigureAwait(false);
                _logger.LogInformation("GooseFlix: ratings for new {Item}: {Outcome}", item.Name, outcome ?? "not reached");
                return;
        }
    }
}
