// Fork: the plugin's own MDBList ratings, kept per title in the plugin's data folder (GooseFlix #1671).
// In memory for reads; saved as one JSON file after each batch, written to a temporary file first
// so a crash can't leave half a file.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wholphin.Ratings;

/// <summary>Ratings per Jellyfin item id.</summary>
public sealed class TitleRatingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<Guid, TitleRatings> _titles = new();
    private readonly object _saveLock = new();
    private readonly ILogger<TitleRatingsStore> _logger;
    private bool _loaded;

    /// <summary>Initializes a new instance of the <see cref="TitleRatingsStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public TitleRatingsStore(ILogger<TitleRatingsStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets how many titles have an entry.</summary>
    public int Count
    {
        get
        {
            EnsureLoaded();
            return _titles.Count;
        }
    }

    private static string? FilePath =>
        WholphinPlugin.Instance?.DataFolderPath is { } dir ? Path.Combine(dir, "ratings.json") : null;

    /// <summary>A title's ratings, or null if never fetched.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The ratings.</returns>
    public TitleRatings? Get(Guid itemId)
    {
        EnsureLoaded();
        return _titles.TryGetValue(itemId, out var t) ? t : null;
    }

    /// <summary>Stores a title's ratings.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="ratings">The ratings.</param>
    public void Set(Guid itemId, TitleRatings ratings)
    {
        EnsureLoaded();
        _titles[itemId] = ratings;
    }

    /// <summary>Every title's ratings.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyDictionary<Guid, TitleRatings> All()
    {
        EnsureLoaded();
        return new Dictionary<Guid, TitleRatings>(_titles);
    }

    /// <summary>Writes the store to disk.</summary>
    public void Save()
    {
        if (FilePath is not { } path)
        {
            return;
        }

        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Dictionary<Guid, TitleRatings>(_titles), JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
#pragma warning disable CA1031 // A failed save is logged; the next batch saves again
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "GooseFlix: could not save ratings");
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_saveLock)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            if (FilePath is not { } path || !File.Exists(path))
            {
                return;
            }

            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<Guid, TitleRatings>>(File.ReadAllText(path), JsonOptions);
                foreach (var (id, ratings) in saved ?? new Dictionary<Guid, TitleRatings>())
                {
                    _titles[id] = ratings;
                }

                _logger.LogInformation("GooseFlix: loaded ratings for {Count} titles", _titles.Count);
            }
#pragma warning disable CA1031 // An unreadable file means starting over, not failing Jellyfin
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "GooseFlix: could not read saved ratings; fetching them again");
            }
        }
    }
}
