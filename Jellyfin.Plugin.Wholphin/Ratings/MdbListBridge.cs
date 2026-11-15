// Fork: calls the MDBList Ratings plugin in-process (GooseFlix #1635), the same code path as its
// POST /Plugins/MdbListRatings/DebugRefreshItem, so no admin API key is needed.
//
// Checked against MDBList Ratings 1.0.1.0 and 1.0.1.1 (Druidblack/Jellyfin.Plugin.MDBList_Ratings,
// tags Release-1.0.1.0 and Release-1.0.1.1). Both have, on the plugin instance (id ab96f8b5-…):
//   internal RatingsUpdater Updater { get; }
//   public  Task<UpdateOutcome> RatingsUpdater.UpdateItemRatingsAsync(BaseItem, CancellationToken)
//   internal Task<CacheEnvelope?> RatingsUpdater.TryGetCacheEnvelopeByItemAsync(BaseItem, CancellationToken)
//   public  DateTimeOffset CacheEnvelope.CachedAtUtc
// Everything is looked up by reflection at run time. Anything missing (plugin not installed, another
// version) is logged once and the bridge reports itself unavailable: it never throws.

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wholphin.Ratings;

/// <summary>Reaches the MDBList Ratings plugin's per-item refresh and cache.</summary>
public sealed class MdbListBridge
{
    /// <summary>The MDBList Ratings plugin's id.</summary>
    public static readonly Guid MdbListPluginId = Guid.Parse("ab96f8b5-45ef-44be-81d6-99bc01e26b9d");

    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IPluginManager _pluginManager;
    private readonly ILogger<MdbListBridge> _logger;
    private readonly object _lock = new();
    private Resolved? _resolved;
    private string? _lastProblem;

    /// <summary>Initializes a new instance of the <see cref="MdbListBridge"/> class.</summary>
    /// <param name="pluginManager">Plugin manager.</param>
    /// <param name="logger">Logger.</param>
    public MdbListBridge(IPluginManager pluginManager, ILogger<MdbListBridge> logger)
    {
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <summary>When MDBList Ratings last cached the item's ratings, or null (none, or the plugin can't be reached).</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The cache time, or null.</returns>
    public async Task<DateTimeOffset?> CachedAtAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var resolved = Resolve();
        if (resolved is null)
        {
            return null;
        }

        try
        {
            var envelope = await InvokeAsync(resolved.CacheMethod, resolved.Updater, item, cancellationToken).ConfigureAwait(false);
            return envelope?.GetType().GetProperty("CachedAtUtc", AnyInstance)?.GetValue(envelope) as DateTimeOffset?;
        }
#pragma warning disable CA1031 // Never let another plugin's failure escape
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "GooseFlix: MDBList cache lookup failed for {Item}", item.Name);
            return null;
        }
    }

    /// <summary>Asks MDBList Ratings to fetch and apply the item's ratings now.</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>MDBList's outcome (eg "Updated", "Skipped"), or null when it couldn't be asked.</returns>
    public async Task<string?> RefreshAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var resolved = Resolve();
        if (resolved is null)
        {
            return null;
        }

        try
        {
            var outcome = await InvokeAsync(resolved.RefreshMethod, resolved.Updater, item, cancellationToken).ConfigureAwait(false);
            return outcome?.ToString() ?? "Done";
        }
#pragma warning disable CA1031 // Never let another plugin's failure escape
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "GooseFlix: MDBList refresh failed for {Item}", item.Name);
            return null;
        }
    }

    /// <summary>Whether the MDBList plugin can be reached right now.</summary>
    /// <returns>True if available.</returns>
    public bool IsAvailable() => Resolve() is not null;

    private static async Task<object?> InvokeAsync(MethodInfo method, object target, BaseItem item, CancellationToken cancellationToken)
    {
        if (method.Invoke(target, new object[] { item, cancellationToken }) is not Task task)
        {
            return null;
        }

        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private Resolved? Resolve()
    {
        lock (_lock)
        {
            LocalPlugin? local;
            try
            {
                local = _pluginManager.GetPlugin(MdbListPluginId, null!);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return Unavailable("the plugin list could not be read: " + ex.Message);
            }

            var instance = local?.Instance;
            if (instance is null)
            {
                return Unavailable("MDBList Ratings is not installed or not loaded");
            }

            if (_resolved is not null && ReferenceEquals(_resolved.Plugin, instance))
            {
                return _resolved;
            }

            var version = local!.Version?.ToString() ?? "?";
            var updater = instance.GetType().GetProperty("Updater", AnyInstance)?.GetValue(instance);
            if (updater is null)
            {
                return Unavailable($"MDBList Ratings {version} has no Updater");
            }

            var signature = new[] { typeof(BaseItem), typeof(CancellationToken) };
            var refresh = updater.GetType().GetMethod("UpdateItemRatingsAsync", AnyInstance, null, signature, null);
            var cache = updater.GetType().GetMethod("TryGetCacheEnvelopeByItemAsync", AnyInstance, null, signature, null);
            if (refresh is null || cache is null || !typeof(Task).IsAssignableFrom(refresh.ReturnType) || !typeof(Task).IsAssignableFrom(cache.ReturnType))
            {
                return Unavailable($"MDBList Ratings {version} has a different refresh or cache method");
            }

            _resolved = new Resolved(instance, updater, refresh, cache);
            if (_lastProblem is not null || _resolved is not null)
            {
                _logger.LogInformation("GooseFlix: ratings on add uses MDBList Ratings {Version}", version);
            }

            _lastProblem = null;
            return _resolved;
        }
    }

    private Resolved? Unavailable(string problem)
    {
        _resolved = null;
        if (problem != _lastProblem)
        {
            _lastProblem = problem;
            _logger.LogWarning("GooseFlix: ratings on add is idle: {Problem}", problem);
        }

        return null;
    }

    private sealed record Resolved(object Plugin, object Updater, MethodInfo RefreshMethod, MethodInfo CacheMethod);
}
