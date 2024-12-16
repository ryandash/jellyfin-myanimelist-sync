<h1>MyAnimeList-Sync Jellyfin Plugin</h1>

## About

MyAnimeList-Sync lets you synchorinze your Jellyfin Anime watch progress to popular services.

This is a fork of https://github.com/vosmiic/jellyfin-ani-sync trimmed down to use the MyAnimeList provider from my other plugin https://github.com/ryandash/jellyfin-plugin-myanimelist.

## Installation

### Automatic (recommended)
1. Navigate to Settings > Admin Dashboard > Plugins > Repositories
2. Add a new repository with a `Repository URL` of `https://raw.githubusercontent.com/vosmiic/jellyfin-ani-sync/master/manifest.json`. The name can be anything you like.
3. Save, and navigate to Catalogue.
4. Ani-Sync should be present. Click on it and install the latest version.

### Manual

[See the official Jellyfin documentation for install instructions](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

1. Download a version from the [releases tab](https://github.com/vosmiic/jellyfin-ani-sync/releases) that matches your Jellyfin version.
2. Copy the `meta.json` and `jellyfin-ani-sync.dll` files into `plugins/ani-sync` (see above official documentation on where to find the `plugins` folder).
3. Restart your Jellyfin instance.
4. Navigate to Plugins in Jellyfin (Settings > Admin Dashboard > Plugins).
5. Adjust the settings accordingly. I would advise following the detailed instructions on the [wiki page](https://github.com/vosmiic/jellyfin-ani-sync/wiki).

#### Docker

There is a Docker script that will pull the last built Docker image and copy the DLL file to the given directory.

```bash
docker run --rm -v "/plugin/dir/Ani-Sync:/out" ghcr.io/vosmiic/jellyfin-ani-sync
```

## Build

1. To build this plugin you will need [.Net 6.x](https://dotnet.microsoft.com/download/dotnet/6.0).

2. Build plugin with following command
  ```
  dotnet publish --configuration Release --output bin
  ```

3. Place the dll-file in the `plugins/ani-sync` folder (you might need to create the folders) of your JF install

## Services/providers
MyAnimeList
