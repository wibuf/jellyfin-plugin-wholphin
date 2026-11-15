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
- **A release is a `v*` tag on `gooseflix`.** `.github/workflows/release.yml` builds the zip and
  publishes a GitHub release with the zip's MD5 in its notes. Tags not on `gooseflix` are refused.
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
  - **No data is an empty result, never an error.** Results are cached per user per day for `CacheHours`.
  - **Headers are percent-encoded UTF-8** (`Uri.EscapeDataString`), since titles can be non-ASCII. Decode them.
  - `because-you-watched`: similar library titles to one the user finished in the last `LookbackDays` (an episode counts as its show). The source title rotates daily. Headers: `X-GooseFlix-Row-Title` ("Because you watched <title>"), `X-GooseFlix-Source-Id` (Jellyfin id, no dashes), `X-GooseFlix-Source-Type` (`Movie`/`Series`), `X-GooseFlix-Source-Tmdb` (when known).
  - `mood?slot=N` (N from 1 to `MoodsPerDay`): today's Nth mood for the user. Titles carry any of its keyword tags, case-insensitively. `TotalRecordCount` is how many the user has ("N in your library"). Headers: `X-GooseFlix-Row-Title` (the mood's name) and `X-GooseFlix-Mood-Id`. A slot with no mood today is empty.
  - `moods`: today's moods for the user, as `[{Slot, Id, Name, Titles}]` (Jellyfin's JSON is PascalCase).
  - `picked-for-you`: unwatched titles in the genres the user watched in the last `LookbackDays`, best match first. Header `X-GooseFlix-Row-Title: Picked for you`.
  - `capabilities`: `{Version, Rows: [...the row types switched on], MoodsPerDay}`. The app uses it to tell this plugin from upstream's, which doesn't have it (404).
- **Settings** (`Rows` in the plugin configuration, on the YAML settings page; no restart):
  - **Switches:** `BecauseYouWatched`, `Moods` and `PickedForYou`, each on or off.
  - **Numbers:** `MoodsPerDay` (3), `CacheHours` (3), `LookbackDays` (60), `MaxItems` (40).
  - **`MoodList`:** each mood has `Id`, `Name`, `Keywords`, `MinTitles` and optional `Months`. It's an array, because Jellyfin's XML settings would add saved list entries to the defaults.
- **Default moods:** 19, tuned on the library's TMDB keyword tags on 2026-11-15, each with 34–192 titles across the library. They're only offered to a user with at least 12 titles. Christmas favourites shows in November and December only, Halloween in October only.
- **Daily rotation** uses FNV-1a over user, date and purpose, not `string.GetHashCode`, so it doesn't change on a Jellyfin restart.
- **Ratings when media is added:** researched, not built (findings on GooseFlix #1635).

## Upstream

- Forked from upstream v0.1.1.0 (`2d40996`) on 2026-11-15.
