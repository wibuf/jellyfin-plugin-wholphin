# GooseFlix plugin: what this fork changes

A fork of [jellyfin-plugin-wholphin](https://github.com/damontecres/jellyfin-plugin-wholphin), the
server companion plugin for Wholphin, for the GooseFlix Jellyfin server (jelly.grit.bot) and the
GooseFlix TV app (a Wholphin fork, [wibuf/Wholphin](https://github.com/wibuf/Wholphin)). This file is
the record of what the fork adds or changes. Keep it current: add a line whenever a fork feature lands.

Tracking lives in the **GooseFlix** repo's issues ([wibuf/GooseFlix](https://github.com/wibuf/GooseFlix/issues)):
the plan is #1633, the setup #1634, the first release #1635.

## Where the work lives

- **`gooseflix` is the main branch.** `main` is upstream's code. Work on a `claude/...` branch from
  `gooseflix` and merge into it.
- **A release is a version bump on `gooseflix`.** Every push to `gooseflix` runs
  `.github/workflows/release.yml`: if the version in `Directory.Build.props` isn't released yet, it
  builds the zip and publishes release `v<version>` (making the tag) with the zip's MD5 in its notes;
  otherwise it does nothing. A `v*` tag pushed by hand still works. Commits not on `gooseflix` are refused.
- **Jellyfin installs it from Scuffed**, which serves a plugin repository manifest built from these
  releases (#1634).

## Restarts: why the plugin is built the way it is

New plugin code only takes effect after a **Jellyfin restart**, which interrupts everyone watching
and may only happen in the 4–5 AM window (GooseFlix `.claude/GooseFlix.md`). Changing the plugin's
**settings** takes effect immediately. So:

- The plugin is a general-purpose engine: which rows exist, mood definitions, titles, limits and
  switches live in its settings (the YAML page), or are read live from Scuffed. Tuning never needs
  a restart.
- Releases are rare and batched. Auto-update is off for this plugin on the server, so a release
  never leaves Jellyfin waiting on a restart nobody planned.
- What changes often belongs in the app (ships in ~15 minutes) or Scuffed (restarts any time).

## Conventions

- **Keep upstream's plugin id** (`00E68C90-8840-48D0-AD65-0A5D58249652`) and the `/wholphin/...`
  routes: the app is Wholphin's code and looks for the plugin there. This plugin replaces upstream's
  on the server; the two can't be installed together.
- **Fork code goes in its own files** where it can, marked `Fork` in a comment; changes inside
  upstream files are small. That keeps syncing with upstream manageable.
- **Versions** are upstream's plus 500 in the last part (`0.1.1.500`, `0.1.1.501`, ...), in
  `Directory.Build.props` and the `.csproj`, always higher than what's installed.

## What the fork adds

### Identity
- Shown as **GooseFlix** on the Jellyfin plugins page, with its own description and settings page
  title (#1634).

### Personal home rows (#1635)
Row endpoints for the app's "custom endpoint" home rows (`Api/RowsController.cs`; the selection rules are
pure functions in `Rows/RowSelection.cs`, tested in `Jellyfin.Plugin.Wholphin.Tests`).

- **Contract.** `GET /wholphin/rows/<row>?userId=<guid>&startIndex=&limit=`, signed in (`[Authorize]`).
  - **Response:** a `QueryResult<BaseItemDto>` (Movies and Series) built with the user's own access, so library access, hidden or out-of-season libraries and parental limits all apply. Unwatched titles come first.
  - **Who may ask:** the caller's own `userId`, or anyone's for an admin or API key; anyone else gets 403. With no `userId`, the caller is used.
  - **No data is an empty result, never an error.** Rows are cached per user for the row day (see Speed).
  - **"Don't recommend":** titles the user disliked (Jellyfin's own per-user like/dislike, which the app sets) never show in any of these rows. They're dropped when a page is served, so a dislike counts at once although rows are cached for the day, and the row tops up from further down its list (0.1.1.501).
  - **Headers are percent-encoded UTF-8** (`Uri.EscapeDataString`), since titles can be non-ASCII. Decode them.
  - `because-you-watched[?kind=movie|series]`: similar library titles to one the user finished in the last `LookbackDays` (an episode counts as its show). The source title rotates daily. With `kind=movie` the source and the suggestions are movies; with `kind=series` they're shows; with no kind (the "anything" row) the source is neither of those two rows' sources, and their suggestions are left out (0.1.1.501). Header `X-GooseFlix-Row-Kind` (`movie`, `series`, `any`) lets the app tell this version from 0.1.1.500, which ignores `kind`. Headers: `X-GooseFlix-Row-Title` ("Because you watched <title>"), `X-GooseFlix-Source-Id` (Jellyfin id, no dashes), `X-GooseFlix-Source-Type` (`Movie`/`Series`), `X-GooseFlix-Source-Tmdb` (when known).
  - `mood?slot=N` (N from 1 to `MoodsPerDay`): today's Nth mood for the user. Titles carry any of its keyword tags, case-insensitively. `TotalRecordCount` is how many the user has ("N in your library"). Headers: `X-GooseFlix-Row-Title` (the mood's name) and `X-GooseFlix-Mood-Id`. A slot with no mood today is empty.
  - `moods`: today's moods for the user, as `[{Slot, Id, Name, Titles}]` (Jellyfin's JSON is PascalCase).
  - `picked-for-you`: unwatched titles in the genres the user watched in the last `LookbackDays`. Each genre the user marked "Don't recommend" on counts one less per disliked title. The order takes turns across the user's genres, heaviest first (so one genre, eg anime, can't fill the row), then alternates movies and shows (`RowSelection.Diverse`, 0.1.1.501). Header `X-GooseFlix-Row-Title: Picked for you`.
  - `capabilities`: `{Version, Rows: [...the row types switched on], MoodsPerDay, Features}`; `Features` (0.1.1.501) is `because-you-watched-kinds`, `dislikes`, `varied-picks`. The app uses it to tell this plugin from upstream's, which doesn't have it (404).
- **Settings** (`Rows` in the plugin configuration, on the YAML settings page; no restart):
  - **Switches:** `BecauseYouWatched`, `Moods` and `PickedForYou`, each on or off.
  - **Numbers:** `MoodsPerDay` (6; 3 before 0.1.1.501), `CacheHours` (24), `LookbackDays` (60), `MaxItems` (40), `WarmHour` (5), `WarmActiveDays` (14).
  - **`MoodList`:** each mood has `Id`, `Name`, `Keywords`, `MinTitles` and optional `Months`. It's an array, because Jellyfin's XML settings would add saved list entries to the defaults.
- **Default moods:** 36: 19 tuned on the library's TMDB keyword tags on 2026-11-15 and 17 more on 2026-11-16 (ghost stories, assassins, robots & AI, swords & sorcery, buddy cops, mobsters, on the run, aliens, mind-benders, war, revenge, family movie night, tearjerkers, creature features, conspiracies, game worlds, disasters), each with 34–192 titles across the library. They're only offered to a user with at least 12 titles. Christmas favourites shows in November and December only, Halloween in October only.
- **Daily rotation** uses FNV-1a over user, date and purpose, not `string.GetHashCode`, so it doesn't change on a Jellyfin restart.
- **Speed** (`Rows/RowsService.cs`). On the GooseFlix server (Jellyfin 12.3, timed through its REST API on 2026-11-15), any user-scoped query over shows or episodes takes about 2–4 s, whatever the filters. Jellyfin's own Resume endpoint took 8 s. Movie-only queries and a 40-card page by ids are fast (~0.25 s). So no query design builds these rows under 1 s on demand. Instead:
  - **Built ahead:** rows for users active in the last `WarmActiveDays` are built in the background (`RowsWarmupService`) when the row day starts. The row day begins at `WarmHour` local time (05:00, after the 4–5 AM window), which is also when the daily rotation changes. A home page request then only pays for the page: one `ItemIds` query plus DTOs, about 0.25–0.3 s server-side for 40 cards.
  - **On demand:** a user who isn't built ahead (inactive for 14+ days, or the first minutes after a restart) pays the build once: about 2–4 s per row on this server, then it's cached for the day. Concurrent requests for the same row share one build.
  - **Inside a build:** every candidate query is capped before sorting (history 80–400, similar 60, mood 150, picked 300). Played state comes from one batched user-data lookup, not one per item. Shows for watched episodes are fetched in one query.
  - **Mood counts** (19 counts) are computed once per row day per distinct set of library access (most accounts share one) and reused by everyone in it.
  - **Debug log:** each request logs its time ("GooseFlix: <row> for <user>: N ms"), and each build logs its own.

### Ratings when media is added (#1635)
New movies and shows get their ratings fetched within minutes, not at the MDBList Ratings plugin's next daily task (`Ratings/`).
- **The queue:** `ILibraryManager.ItemAdded` for a Movie or Series (not virtual) only queues the item, so library scans are never held up. A new show arrives as a Series; new episodes of a show already in the library don't queue it, and an item is queued at most once a day.
- **The timer:** every minute it handles up to 10 due items. The first try is `DelayMinutes` after the add (Jellyfin fills metadata after adding). Without a TMDB or IMDb id yet, it retries after 5, 15, 30 and 60 minutes, then drops the item for the daily task.
- **Skips and the limit:** items MDBList cached within 24 h are skipped (its shortest cache setting is a day). A daily limit (`DailyLimit`, reset at local midnight) leaves MDBList's allowance for its own task.
- **The call:** the refresh runs **in-process** through the MDBList Ratings plugin, the same code path as its admin-only `POST /Plugins/MdbListRatings/DebugRefreshItem`, so no API key is stored. `MdbListBridge` finds the plugin by id (`ab96f8b5-45ef-44be-81d6-99bc01e26b9d`) through `IPluginManager`, then by reflection its `internal Updater` property and the updater's `UpdateItemRatingsAsync(BaseItem, CancellationToken)` and `TryGetCacheEnvelopeByItemAsync(BaseItem, CancellationToken)` (whose result's `CachedAtUtc` is the cache time).
- **Versions checked:** MDBList Ratings **1.0.1.0** (running on the server on 2026-11-15) and **1.0.1.1** (installed, waiting on a restart). Both have exactly those members. Another version without them (or no plugin) is logged once ("ratings on add is idle: …") and nothing else happens. Nothing here throws into Jellyfin.
- **Settings** (`RatingsOnAdd`, YAML page, no restart): `Enabled` (false), `DailyLimit` (200), `DelayMinutes` (10).
- **Tests:** the queue, dedupe, retry schedule, daily limit and decision rules are pure functions in `Ratings/RatingsQueue.cs`, tested in `RatingsQueueTests`.

## Upstream

- Forked from upstream v0.1.1.0 (`2d40996`) on 2026-11-15.
