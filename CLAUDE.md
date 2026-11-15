# GooseFlix plugin (fork of jellyfin-plugin-wholphin)

Read **FORK.md** first: what this fork changes, how releases reach the server, and why restarts
shape the design.

## Starting a session

1. **`gooseflix` is the main branch.** Start your branch from `origin/gooseflix`, not `main`.
2. Plan and issues live in the **GooseFlix** repo: #1633 (plan), #1634 (setup), #1635 (first release).
   GooseFlix's conventions (deployment window, credentials, plan-first) are in its `.claude/GooseFlix.md`.

## Working

- .NET 10, Jellyfin 12.1+. Build: `dotnet build Jellyfin.Plugin.Wholphin`; tests: `dotnet test`.
- Keep upstream's plugin id and `/wholphin/...` routes (see FORK.md, Conventions).
- Anything someone might want to tune goes in the plugin's settings, not in code: a code change
  costs a Jellyfin restart.
- **When a fork feature lands, add it to FORK.md** in the same commit.

## Releasing

- A release is a version bump merged into `gooseflix`: CI publishes `v<version>` on the push
  (FORK.md, Conventions). Pushes that don't change the version release nothing.
- Installing or updating it on the server needs a **Jellyfin restart**, only in the 4–5 AM window,
  and only when the owner has asked for it.
- Never put the Jellyfin API key, MDBList key or any token in a file or commit.
