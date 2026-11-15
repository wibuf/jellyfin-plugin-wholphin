// Fork: tests for ratings-on-add's queue, daily limit and dedupe (GooseFlix #1635).

using Jellyfin.Plugin.Wholphin.Ratings;
using Xunit;

namespace Jellyfin.Plugin.Wholphin.Tests;

public class RatingsQueueTests
{
    private static readonly DateTime Noon = new(2026, 11, 15, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 11, 15);

    [Fact]
    public void Decide_WaitsForIds_ThenSkipsFresh_ThenRespectsTheBudget()
    {
        var now = new DateTimeOffset(Noon);
        Assert.Equal(RatingsDecision.WaitForIds, RatingsRules.Decide(false, null, now, true));
        Assert.Equal(RatingsDecision.SkipFresh, RatingsRules.Decide(true, now.AddHours(-23), now, true));
        Assert.Equal(RatingsDecision.SkipFresh, RatingsRules.Decide(true, now.AddHours(-1), now, false));
        Assert.Equal(RatingsDecision.Refresh, RatingsRules.Decide(true, now.AddHours(-25), now, true));
        Assert.Equal(RatingsDecision.Refresh, RatingsRules.Decide(true, null, now, true));
        Assert.Equal(RatingsDecision.OutOfBudget, RatingsRules.Decide(true, null, now, false));
    }

    [Fact]
    public void DailyBudget_StopsAtTheLimit_AndResetsTheNextDay()
    {
        var budget = new DailyBudget();
        Assert.True(budget.TryUse(Day, 2));
        Assert.True(budget.HasRoom(Day, 2));
        Assert.True(budget.TryUse(Day, 2));
        Assert.False(budget.HasRoom(Day, 2));
        Assert.False(budget.TryUse(Day, 2));
        Assert.Equal(2, budget.Used(Day));

        Assert.Equal(0, budget.Used(Day.AddDays(1)));
        Assert.True(budget.TryUse(Day.AddDays(1), 2));
        Assert.False(new DailyBudget().TryUse(Day, 0));
    }

    [Fact]
    public void Enqueue_OncePerItemPerDay()
    {
        var queue = new RatingsQueue();
        var item = Guid.NewGuid();
        Assert.True(queue.Enqueue(item, Noon, Day, 10));
        Assert.False(queue.Enqueue(item, Noon, Day, 10));

        queue.Done(item);
        Assert.False(queue.Enqueue(item, Noon.AddHours(2), Day, 10));
        Assert.True(queue.Enqueue(item, Noon.AddDays(1), Day.AddDays(1), 10));
    }

    [Fact]
    public void Due_WaitsForTheDelay_OldestFirst_AndCapped()
    {
        var queue = new RatingsQueue();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        queue.Enqueue(second, Noon.AddMinutes(1), Day, 10);
        queue.Enqueue(first, Noon, Day, 10);
        queue.Enqueue(third, Noon.AddMinutes(2), Day, 10);

        Assert.Empty(queue.Due(Noon.AddMinutes(9), 10));
        Assert.Equal(new[] { first, second }, queue.Due(Noon.AddMinutes(11), 2));
        Assert.Equal(new[] { first, second, third }, queue.Due(Noon.AddMinutes(12), 10));
    }

    [Fact]
    public void Retry_FollowsTheSchedule_ThenDropsTheItem()
    {
        var queue = new RatingsQueue();
        var item = Guid.NewGuid();
        queue.Enqueue(item, Noon, Day, 10);
        var now = Noon.AddMinutes(10);
        foreach (var minutes in RatingsRules.RetryMinutes)
        {
            Assert.True(queue.Retry(item, now));
            Assert.Empty(queue.Due(now.AddMinutes(minutes - 1), 10));
            now = now.AddMinutes(minutes);
            Assert.Equal(new[] { item }, queue.Due(now, 10));
        }

        Assert.False(queue.Retry(item, now));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Defaults_AreOffWithALimitAndADelay()
    {
        var config = new RatingsOnAddConfig();
        Assert.False(config.Enabled);
        Assert.Equal(200, config.DailyLimit);
        Assert.Equal(10, config.DelayMinutes);
    }
}
