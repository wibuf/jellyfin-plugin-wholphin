// Fork: settings for the personal home rows (GooseFlix #1635). They live in the plugin
// configuration, so changing them on the plugin's settings page takes effect without a restart.

using System;
using System.Linq;

namespace Jellyfin.Plugin.Wholphin.Rows;

/// <summary>Settings for the personal home rows.</summary>
public class RowsConfig
{
    /// <summary>Gets or sets a value indicating whether the "Because you watched" row is served.</summary>
    public bool BecauseYouWatched { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether mood rows are served.</summary>
    public bool Moods { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Picked for you" row is served.</summary>
    public bool PickedForYou { get; set; } = true;

    /// <summary>Gets or sets how many mood rows show a day.</summary>
    public int MoodsPerDay { get; set; } = 3;

    /// <summary>Gets or sets how long a row is cached per user, in hours.</summary>
    public int CacheHours { get; set; } = 3;

    /// <summary>Gets or sets how far back watched titles count, in days.</summary>
    public int LookbackDays { get; set; } = 60;

    /// <summary>Gets or sets the most titles a row returns when the app asks for no limit.</summary>
    public int MaxItems { get; set; } = 40;

    /// <summary>
    /// Gets or sets the moods. An array, not a list: Jellyfin reads settings with XmlSerializer,
    /// which would add saved entries to a list's defaults instead of replacing them.
    /// </summary>
    public MoodConfig[] MoodList { get; set; } = DefaultMoods();

    /// <summary>The moods as selection definitions.</summary>
    /// <returns>Definitions.</returns>
    public MoodDefinition[] Definitions()
    {
        return (MoodList ?? Array.Empty<MoodConfig>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Id) && !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => new MoodDefinition(
                m.Id!,
                m.Name!,
                (m.Keywords ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray(),
                m.MinTitles,
                m.Months ?? Array.Empty<int>()))
            .ToArray();
    }

    /// <summary>
    /// Defaults tuned on the GooseFlix library's TMDB keyword tags (2026-11-15: 2,816 movies and
    /// 1,143 shows; each mood had 34-192 titles).
    /// </summary>
    /// <returns>The default moods.</returns>
    public static MoodConfig[] DefaultMoods() => new[]
    {
        Mood("heists", "Heists & cons", "heist", "con artist", "bank robbery", "robbery", "con man", "thief", "casino"),
        Mood("whodunits", "Whodunits", "whodunit", "murder mystery", "detective", "private detective", "investigation", "mystery"),
        Mood("coming-of-age", "Coming of age", "coming of age", "teenager", "high school", "first love", "growing up", "teenage romance"),
        Mood("road-trips", "Road trips", "road trip", "road movie"),
        Mood("true-stories", "Based on a true story", "based on true story", "biography", "based on real person", "historical figure"),
        Mood("feel-good", "Feel-good", "friendship", "comforting", "heartwarming", "feel-good"),
        Mood("space", "Lost in space", "space", "astronaut", "spacecraft", "space travel", "space opera", "spaceship"),
        Mood("time-travel", "Time travel", "time travel", "time loop", "time machine", "time paradox"),
        Mood("superheroes", "Superheroes", "superhero", "based on comic", "super power", "marvel cinematic universe (mcu)"),
        Mood("monsters", "Zombies & monsters", "zombie", "monster", "creature", "kaiju", "vampire", "werewolf", "zombie apocalypse"),
        Mood("sports", "Sports stories", "sports", "boxing", "american football", "basketball", "baseball"),
        Mood("spies", "Spies", "spy", "espionage", "secret agent", "mi6"),
        Mood("survival", "Survival", "survival", "stranded", "wilderness", "plane crash", "shipwreck"),
        Mood("isekai", "Isekai & other worlds", "isekai", "fantasy world", "reincarnation", "another world"),
        Mood("dystopias", "Dystopias", "dystopia", "post-apocalyptic future", "post-apocalyptic"),
        Mood("serial-killers", "Serial killers", "serial killer"),
        Mood("martial-arts", "Martial arts", "martial arts", "kung fu", "karate"),
        Seasonal(Mood("christmas", "Christmas favourites", "christmas", "santa claus", "christmas party"), 11, 12),
        Seasonal(Mood("halloween", "Halloween", "halloween"), 10),
    };

    private static MoodConfig Mood(string id, string name, params string[] keywords) =>
        new() { Id = id, Name = name, Keywords = keywords, MinTitles = 12 };

    private static MoodConfig Seasonal(MoodConfig mood, params int[] months)
    {
        mood.Months = months;
        return mood;
    }
}

/// <summary>One mood in the settings.</summary>
public class MoodConfig
{
    /// <summary>Gets or sets the stable id.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the row title.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the keywords (tags), matched case-insensitively.</summary>
    public string[]? Keywords { get; set; }

    /// <summary>Gets or sets the fewest titles a user must have for the mood to show.</summary>
    public int MinTitles { get; set; } = 12;

    /// <summary>Gets or sets the months (1-12) it may show in; empty for all year.</summary>
    public int[]? Months { get; set; }
}
