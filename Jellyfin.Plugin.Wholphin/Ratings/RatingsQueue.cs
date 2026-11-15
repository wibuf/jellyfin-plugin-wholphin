// Fork: the queue, daily limit and dedupe behind ratings-on-add (GooseFlix #1635). Plain values only,
// no Jellyfin types, so the test project compiles this file on its own.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Wholphin.Ratings;

/// <summary>Settings for fetching ratings when media is added. Changing them takes no restart.</summary>
public class RatingsOnAddConfig
{
    /// <summary>Gets or sets a value indicating whether new movies and shows get their ratings fetched straight away.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the most refreshes a day (resets at midnight), leaving MDBList's daily allowance for its own task.</summary>
    public int DailyLimit { get; set; } = 200;

    /// <summary>Gets or sets the minutes to wait after an item is added before the first try (Jellyfin fills metadata after the add).</summary>
    public int DelayMinutes { get; set; } = 10;
}

/// <summary>What to do with a queued item that is due.</summary>
public enum RatingsDecision
{
    /// <summary>Fetch its ratings now.</summary>
    Refresh,

    /// <summary>MDBList already has fresh ratings for it: nothing to do.</summary>
    SkipFresh,

    /// <summary>No TMDB or IMDb id yet: try again later.</summary>
    WaitForIds,

    /// <summary>Today's limit is used up: leave it to the daily task.</summary>
    OutOfBudget,
}

/// <summary>Pure decision rules.</summary>
public static class RatingsRules
{
    /// <summary>
    /// Ratings cached within this long are fresh. MDBList Ratings' shortest cache setting is a day,
    /// so anything newer is fresh by its own rules too.
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);

    /// <summary>Minutes between later tries while waiting for provider ids, after the first delay.</summary>
    public static readonly IReadOnlyList<int> RetryMinutes = new[] { 5, 15, 30, 60 };

    /// <summary>Decides what to do with a due item.</summary>
    /// <param name="hasProviderId">Whether it has a TMDB or IMDb id.</param>
    /// <param name="cachedAtUtc">When MDBList Ratings last cached it, if ever.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="budgetLeft">Whether today's limit has room.</param>
    /// <returns>The decision.</returns>
    public static RatingsDecision Decide(bool hasProviderId, DateTimeOffset? cachedAtUtc, DateTimeOffset nowUtc, bool budgetLeft)
    {
        if (!hasProviderId)
        {
            return RatingsDecision.WaitForIds;
        }

        if (cachedAtUtc is { } cached && nowUtc - cached < FreshFor)
        {
            return RatingsDecision.SkipFresh;
        }

        return budgetLeft ? RatingsDecision.Refresh : RatingsDecision.OutOfBudget;
    }
}

/// <summary>A count that allows <c>limit</c> uses a day and starts again at midnight (local date).</summary>
public sealed class DailyBudget
{
    private readonly object _lock = new();
    private DateOnly _day;
    private int _used;

    /// <summary>How many were used on <paramref name="today"/>.</summary>
    /// <param name="today">Today.</param>
    /// <returns>Uses so far today.</returns>
    public int Used(DateOnly today)
    {
        lock (_lock)
        {
            return _day == today ? _used : 0;
        }
    }

    /// <summary>Whether one more fits today without using it.</summary>
    /// <param name="today">Today.</param>
    /// <param name="limit">The daily limit.</param>
    /// <returns>True if there's room.</returns>
    public bool HasRoom(DateOnly today, int limit) => Used(today) < Math.Max(0, limit);

    /// <summary>Uses one if today has room.</summary>
    /// <param name="today">Today.</param>
    /// <param name="limit">The daily limit.</param>
    /// <returns>True if it was used.</returns>
    public bool TryUse(DateOnly today, int limit)
    {
        lock (_lock)
        {
            if (_day != today)
            {
                _day = today;
                _used = 0;
            }

            if (_used >= Math.Max(0, limit))
            {
                return false;
            }

            _used++;
            return true;
        }
    }
}

/// <summary>
/// Items waiting for their ratings. An item is queued at most once a day; while it waits for provider
/// ids it is retried on a schedule and then dropped (the daily task picks it up later).
/// </summary>
public sealed class RatingsQueue
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Pending> _pending = new();
    private readonly Dictionary<Guid, DateOnly> _queuedOn = new();

    /// <summary>Gets how many items are waiting.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Queues an item for its first try after <paramref name="delayMinutes"/>.</summary>
    /// <param name="id">The item.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="today">Today (local date), for the once-a-day rule.</param>
    /// <param name="delayMinutes">Minutes before the first try.</param>
    /// <returns>False if it's already waiting or was queued today.</returns>
    public bool Enqueue(Guid id, DateTime nowUtc, DateOnly today, int delayMinutes)
    {
        lock (_lock)
        {
            if (_pending.ContainsKey(id) || (_queuedOn.TryGetValue(id, out var day) && day == today))
            {
                return false;
            }

            _queuedOn[id] = today;
            _pending[id] = new Pending(nowUtc.AddMinutes(Math.Max(0, delayMinutes)), 0);

            // Forget earlier days, so the record can't grow without end
            if (_queuedOn.Count > 5000)
            {
                foreach (var old in _queuedOn.Where(e => e.Value != today).Select(e => e.Key).ToList())
                {
                    _queuedOn.Remove(old);
                }
            }

            return true;
        }
    }

    /// <summary>The items whose next try is due, oldest first, at most <paramref name="max"/>.</summary>
    /// <param name="nowUtc">Now.</param>
    /// <param name="max">Most items to hand out.</param>
    /// <returns>The due items.</returns>
    public IReadOnlyList<Guid> Due(DateTime nowUtc, int max)
    {
        lock (_lock)
        {
            return _pending.Where(p => p.Value.NextTryUtc <= nowUtc)
                .OrderBy(p => p.Value.NextTryUtc)
                .Take(Math.Max(0, max))
                .Select(p => p.Key)
                .ToList();
        }
    }

    /// <summary>Schedules the next try for an item still waiting for its ids.</summary>
    /// <param name="id">The item.</param>
    /// <param name="nowUtc">Now.</param>
    /// <returns>False if it has used its tries and was dropped.</returns>
    public bool Retry(Guid id, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out var pending))
            {
                return false;
            }

            if (pending.Retries >= RatingsRules.RetryMinutes.Count)
            {
                _pending.Remove(id);
                return false;
            }

            _pending[id] = new Pending(nowUtc.AddMinutes(RatingsRules.RetryMinutes[pending.Retries]), pending.Retries + 1);
            return true;
        }
    }

    /// <summary>Removes an item: done, skipped or dropped.</summary>
    /// <param name="id">The item.</param>
    public void Done(Guid id)
    {
        lock (_lock)
        {
            _pending.Remove(id);
        }
    }

    private readonly record struct Pending(DateTime NextTryUtc, int Retries);
}
