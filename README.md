<h1>MyAnimeList-Sync Jellyfin Plugin</h1>

## About

MyAnimeList-Sync allows you to synchronize your Jellyfin anime watch progress with MyAnimeList.

This plugin is a streamlined fork of vosmiic's Jellyfin Ani-Sync, customized to use the MyAnimeList provider from my other plugin, Jellyfin MyAnimeList Plugin.

## Installation

### Automatic (recommended)

1. Navigate to Settings > Admin Dashboard > Plugins > Repositories
2. Add a new repository with a `Repository URL` of `https://raw.githubusercontent.com/ryandash/jellyfin-myanimelist-sync/refs/heads/master/manifest.json`. The name can be anything you like.
3. Save, and navigate to Catalogue.
4. Ani-Sync should be present. Click on it and install the latest version.

### Manual

[Refer to the official Jellyfin documentation for plugin installation instructions](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

1. Download the latest version from the [releases tab](https://github.com/vosmiic/jellyfin-ani-sync/releases).
2. Copy the `meta.json` and `jellyfin-ani-sync.dll` files into `plugins/ani-sync` (see the official documentation for the `plugin` folder location).
3. Restart your Jellyfin server.
4. Go to Plugins in Jellyfin (Settings > Admin Dashboard > Plugins).
5. Adjust the plugin settings as needed. Detailed instructions are available on the [wiki page](https://github.com/vosmiic/jellyfin-ani-sync/wiki).

## Build

1. To build this plugin you will need [.Net 6.x](https://dotnet.microsoft.com/download/dotnet/6.0).

2. Build plugin with following command

```
dotnet publish --configuration Release --output bin
```

3. Copy the generated .dll file to the `plugins/ani-sync` folder in your Jellyfin installation

## Services/providers

### Currently supported

1. MyAnimeList
2. AniList
3. (Beta) Kitsu
4. (Limited support) Annict
5. Shikimori
6. Simkl

## External tools

### Anime Lists

We use the XML documents in the [anime lists repo](https://github.com/Anime-Lists/anime-lists) to find the anime you are watching on each provider we support.

Please help the project by contributing to the lists of anime, it helps everyone!

### Anime Offline Database/arm server

We use the API offered by the [arm server repo](https://github.com/BeeeQueue/arm-server) which accesses the [~~anime offline database repo~~](https://github.com/manami-project/anime-offline-database) that we use to fetch our providers IDs so we can update your progress.

> **Note:** the original anime offline database repo was archived on 2026-07-04 and no longer accepts issues, PRs, or updates. [cedya77/anime-offline-database](https://github.com/cedya77/anime-offline-database) is an active continuation seeded from its final release (same format, weekly updates), and [Fribb/anime-lists](https://github.com/Fribb/anime-lists) — which arm-server's `/api/v2` endpoints are generated from — has already switched to pulling from it as of 2026-09-03 ([see the handoff thread](https://github.com/Fribb/anime-lists/issues/30)). Anime added since the original database's archival will only get cross-referenced to AniList/MAL through this new source.

Please also help these projects by contributing to the anime database/helping with the API server.

## Development

Beta releases can be installed automatically by replacing the manifest URL with `https://raw.githubusercontent.com/vosmiic/jellyfin-ani-sync/master/beta-manifest.json`. This will replace your version with the latest beta release (if there is a beta release more recent than the latest stable release). It will be replaced with the latest stable release when it is released, so both repos can be used at the same time. Authentication and other settings should be carried over. Use at your own risk.

Unit tests can be found [here](https://github.com/vosmiic/jellyfin-ani-sync-unit-tests).

The `docker.sh` file can be used to build and automatically copy the resulting DLL to the correct place. The first argument should be either the path to the Ani-Sync folder or the plugins folder (where it will attempt to find the latest Ani-Sync plugin folder). The second optional argument can be an image, or left empty to build using the Dockerfile.
