// Fork: builds the day's rows ahead (GooseFlix #1635): shortly after Jellyfin starts, then each time
// the row day turns over (RowsConfig.WarmHour), so the home page finds them cached.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wholphin.Rows;

/// <summary>Warms the mood counts.</summary>
public sealed class RowsWarmupService : IHostedService, IDisposable
{
    private readonly RowsService _rows;
    private readonly ILogger<RowsWarmupService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private int _working;
    private DateOnly _warmedFor;

    /// <summary>Initializes a new instance of the <see cref="RowsWarmupService"/> class.</summary>
    /// <param name="rows">Rows service.</param>
    /// <param name="logger">Logger.</param>
    public RowsWarmupService(RowsService rows, ILogger<RowsWarmupService> logger)
    {
        _rows = rows;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Out of the way of startup work, then a check every 10 minutes for a new row day
        _timer = new Timer(_ => _ = Warm(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));
        return Task.CompletedTask;
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

    private async Task Warm()
    {
        var day = RowsService.RowDay;
        if (day == _warmedFor || Interlocked.Exchange(ref _working, 1) == 1)
        {
            return;
        }

        try
        {
            await _rows.Warm(_stopping.Token).ConfigureAwait(false);
            _warmedFor = day;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A failed warm-up just means the first request computes the counts
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "GooseFlix: mood warm-up failed");
        }
        finally
        {
            Interlocked.Exchange(ref _working, 0);
        }
    }
}
