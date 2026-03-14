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
MyAnimeList
