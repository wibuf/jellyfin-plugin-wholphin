// Fork: settings for the plugin's own MDBList ratings (GooseFlix #1671). They live in the plugin
// configuration (the YAML settings page), so changing them takes effect without a restart.

using System;

namespace Jellyfin.Plugin.Wholphin.Ratings;

/// <summary>The plugin's own MDBList ratings: fetching, refreshing and (optionally) filling Jellyfin's fields.</summary>
public class TitleRatingsConfig
{
    /// <summary>Gets or sets a value indicating whether ratings are fetched. Needs <see cref="MdbListApiKey"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the MDBList API key. Server-side only; never sent to clients.</summary>
    public string? MdbListApiKey { get; set; }

    /// <summary>Gets or sets how old a title's ratings may get before they're fetched again.</summary>
    public int RefreshHours { get; set; } = 24;

    /// <summary>Gets or sets how long after a title is added before its ratings are fetched (its ids arrive with its metadata).</summary>
    public int NewTitleDelayMinutes { get; set; } = 10;

    /// <summary>Gets or sets the most MDBList requests per day (the account allows 25,000).</summary>
    public int DailyRequestLimit { get; set; } = 5000;

    /// <summary>Gets or sets the titles per request.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Gets or sets the most requests in one five-minute run (MDBList allows 1,000 reads per 5 minutes).</summary>
    public int MaxRequestsPerRun { get; set; } = 60;

    /// <summary>Gets or sets which ratings fill Jellyfin's Community and Critic ratings.</summary>
    public FieldFillConfig FieldFill { get; set; } = new();
}

/// <summary>
/// Which rating fills Jellyfin's own Community rating (0–10) and Critic rating (0–100), which
/// Jellyfin's sort and other clients use. Off by default: it replaces the values already there.
/// Source names are MDBList's: imdb, tomatoes (RT critics), popcorn (RT audience), metacritic,
/// metacriticuser, myanimelist, letterboxd, tmdb, trakt, rogerebert, mdblist. Empty means leave it.
/// </summary>
public class FieldFillConfig
{
    /// <summary>Gets or sets a value indicating whether the fields are filled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the movies' Community rating source.</summary>
    public string MovieCommunity { get; set; } = "imdb";

    /// <summary>Gets or sets the movies' Critic rating source.</summary>
    public string MovieCritic { get; set; } = "tomatoes";

    /// <summary>Gets or sets the shows' Community rating source.</summary>
    public string ShowCommunity { get; set; } = "imdb";

    /// <summary>Gets or sets the shows' Critic rating source.</summary>
    public string ShowCritic { get; set; } = string.Empty;

    /// <summary>Gets or sets per-library sources, by library name. An array: Jellyfin's XML settings would add saved list entries to the defaults.</summary>
    public LibraryRatingOverride[] Overrides { get; set; } =
    {
        new() { Library = "Anime", MovieCommunity = "myanimelist", ShowCommunity = "myanimelist" },
        new() { Library = "Anime (Streaming)", MovieCommunity = "myanimelist", ShowCommunity = "myanimelist" },
    };
}

/// <summary>A library's own sources; empty fields use the defaults.</summary>
public class LibraryRatingOverride
{
    /// <summary>Gets or sets the library's name (case-insensitive).</summary>
    public string? Library { get; set; }

    /// <summary>Gets or sets the movies' Community rating source.</summary>
    public string? MovieCommunity { get; set; }

    /// <summary>Gets or sets the movies' Critic rating source.</summary>
    public string? MovieCritic { get; set; }

    /// <summary>Gets or sets the shows' Community rating source.</summary>
    public string? ShowCommunity { get; set; }

    /// <summary>Gets or sets the shows' Critic rating source.</summary>
    public string? ShowCritic { get; set; }
}
