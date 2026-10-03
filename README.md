# Silkroad Atlas

A native Windows app for discovering Silkroad Online private servers and their public Discord communities.

## Run

THIS IS THE SOURCE CODE. COMPILE IT

The initial catalog contains **1,661 listings and 448 distinct Discord invite links**, collected on **30 September–1 October 2026**. These are directory listings, including older advertisements; they are **not confirmed active game servers**.

## Use

- **Server directory:** search names, caps, rates and source titles; filter by level cap, race or Discord availability. Select a result for its website, community links and source evidence.
- **Saved servers:** click a star to save a listing locally.
- **Discord communities:** search communities, then select **Check invites & load icons**. This checks the current filtered results, loads available community icons, and shows approximate Discord member counts. A blank search checks every discovered invite. You can cancel.
- **Find Discord on website:** scans the selected server's public homepage for additional invites.
- **Refresh sources:** collects current public pages. The default is five directory pages per source. Choose 40 pages for a broader sweep, or up to 100 in Sources & settings.
- **Sources & settings:** search sources by name or language, view imported advertisements, refresh individual sources, and enable, remove or add sources. Supported types include XenForo, MyBB, vBulletin and custom forum advertisements, community references, server websites, Arena Top100 and Nostalgic.
- **Automatic refresh:** optionally runs hourly while the app is open. Disabled by default.
- **Export results:** saves the current server results as CSV, including Discord and source links. **Export full catalog backup** saves all data as JSON.

## Coverage and freshness

Version 1.1 adds 36 source sections/references spanning 25 additional community websites. See **Forum-sources.md** for all 26 forum/community websites and their collection status, and **Forum-advertisements.csv** for the imported advertisements. New sources are added automatically when opening an existing saved catalog; favorites and source preferences are preserved.

The original directory snapshot was collected from [Nostalgic.gg](https://nostalgic.gg/en/silkroad-online) and all 38 available pages of [Arena Top100](https://www.arena-top100.com/silkroad-private-servers/). The [elitepvpers advertising forum](https://www.elitepvpers.com/forum/sro-pserver-advertising/) connector is included, but live access returned HTTP 403 during testing. Its source card reports this instead of claiming success. You can open blocked pages in your browser.

There is no complete public registry of all private servers or all related Discord channels. Unlisted servers, login-only pages and private Discord channels cannot be discovered by this app. Invites may expire. Discord counts describe Discord membership, **not in-game player population**.

Listed dates mean the listing was observed, not that the server launched or was reachable. Missing metadata stays unknown. Conflicting reported caps/rates remain visible with their sources. Facts are extracted automatically and may need verification. Records with a matching website or Discord invite are combined; multiple worlds under one community may consequently appear together. Old entries are retained when a refresh does not rediscover them.

Forum refresh reads up to 12 announcement first posts for additional website/invite links. Website scanning reads a public homepage, not an entire site. JavaScript-only links, shortened invites and links inside images may be missed. Sources can change their page formats; an unrecognized or blocked source reports an error; recognized empty sections report zero listings and preserves previous data.

## Local data

Catalog, favorites, sources and settings are stored in `%LOCALAPPDATA%\SilkroadAtlas\catalog.json`; community icons are cached in the sibling `icons` folder. No account, Discord token, or backend service is required. Refresh sends ordinary public web requests; invite checks use Discord's public invite endpoint. Opening a community link hands it to your browser and does not automatically join it.

For a separate portable data location, launch with `SilkroadAtlas.exe --data "C:\YourFolder\catalog.json"`. To restore a JSON backup, close the app and replace its catalog file with that backup. Keep a copy of the previous file.

## Build and test

Source is included under `Source/` and in **SilkroadAtlas-Source.zip**. Install the .NET 10 SDK on a Windows development machine, then run from `Source/`:

```powershell
dotnet run --project Tests/Tests.csproj -c Release
dotnet run --project App/App.csproj -c Release
```

Build the portable executable with `Source/publish.ps1`, or:

```powershell
dotnet publish App/App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o ../Windows
```

Implementation: C# / WPF, HtmlAgilityPack 1.13.0, local JSON storage. The app uses paced requests, robots.txt rules, cancellation, response size/time limits, validated HTTP(S) URLs and public-address checks. External HTML is parsed as data and never executed in the app.

Validation: **41 automated tests pass**. Live directory collection, Discord invite lookup, and icon retrieval passed. Windows UI checks covered search, selection, saving a favorite, saved-server navigation and Discord verification/icon display. Version 1.1 UI checks also passed for source filtering by language and navigation from a source card to its imported advertisements. Forum tests cover topic indexes, first-post scope, quote exclusion, pagination, duplicate consolidation and source migration. Live collection passed for multiple added forums; blocked and partial sources are documented in Forum-sources.md.

Silkroad Atlas is independent of Silkroad Online, its publisher, the directories and Discord. Source names and community artwork remain the property of their owners. Third-party runtime and parser notices are included with the Windows package.
