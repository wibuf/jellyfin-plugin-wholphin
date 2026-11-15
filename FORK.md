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

## Upstream

- Forked from upstream v0.1.1.0 (`2d40996`) on 2026-11-15.
