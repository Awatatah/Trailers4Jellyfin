# Trailers4Jellyfin

Downloads trailers for upcoming and recently released movies from TMDB/YouTube and plays them before movies through the plugin's built-in Cinema Mode intro provider. No separate Cinema Mode plugin is required.

## Compatibility

- Plugin **2.0.0.0 and later** targets **Jellyfin Server 12.x** (including 12.2) and **.NET 10**.
- Plugin **1.0.18.6** remains in the catalogue for Jellyfin 10.11. Do not install the 12.x build on 10.11.
- A free [TMDB API key or Read Access Token](https://www.themoviedb.org/settings/api) is required for downloads.
- Optional: [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [ffmpeg](https://ffmpeg.org/) for higher quality downloads and an alternative to the built-in YouTube downloader.

## Installation

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   ```text
   https://raw.githubusercontent.com/Awatatah/Trailers4Jellyfin/main/manifest.json
   ```
2. Install or update **Trailers4Jellyfin** from the catalogue and restart Jellyfin.
3. For a manual installation, extract **all DLLs** from the release ZIP into a dedicated folder under Jellyfin's `plugins/` directory, then restart. The plugin DLL alone is insufficient.

## Setup and playback

1. Open **Dashboard → Plugins → Trailers4Jellyfin**. Set your TMDB credentials and a dedicated **Download Folder** writable by Jellyfin. In Docker, use the path visible inside the container.
2. Add that same folder as a Jellyfin **Movies** library. Trailer files must be indexed as library items before the intro provider can play them.
3. Choose TMDB sources (Now Playing, Upcoming, Popular, Top Rated), release date range, languages, quality, and download limits. Movies already in your library can be excluded.
4. Run **Dashboard → Scheduled Tasks → Trailers4Jellyfin → Download TMDB Trailers**, then run a **Library Scan**. Downloads also run daily.
5. Enable the plugin's **Cinema Mode** and set **Trailers per movie** above zero. Enable **Cinema Mode** in the playback settings of each client that supports it. The client must request intros; client support varies.
6. Start a movie from the beginning to check playback.

The plugin selects random trailers from its download library, optionally matching genres. Sidecar JSON files preserve TMDB IDs and genres. It can skip trailers watched by the current user, skip resumed movies, and show trailers at a configurable per-user movie-start interval. Rotation options can delete watched trailers, trailers for movies now in your library, or the oldest trailers above the disk limit.

## If trailers stop playing after a server upgrade

- Check **Dashboard → Plugins** for an incompatible/failed plugin. On Jellyfin 12, install version 2.0.0.0 or later and restart the server.
- Confirm the download folder still exists at the configured path and its Movies library contains scanned `.mp4` files. Downloads alone do not index trailers.
- Check both the plugin and client's Cinema Mode settings. Test with a fresh movie start; resumed movies are skipped by default.
- If all trailers have been watched, temporarily disable **Skip watched trailers**, and set the trailer interval to 1.
- Check server logs for `Trailers4Jellyfin`. `Queuing ... intro trailer(s)` confirms selection. `No trailer items found` means the folder/library scan needs attention. Download errors point to TMDB/YouTube or downloader configuration instead.

## Download quality

The built-in YoutubeExplode downloader uses combined audio/video streams, generally up to 720p. yt-dlp with ffmpeg can merge separate streams for 1080p or higher. Configure executable paths if auto-detection cannot find them. A Netscape-format cookies file can be configured when YouTube requires authentication.

## Building from source

Install the .NET 10 SDK, then run:

```sh
dotnet publish Jellyfin.Plugin.Trailers4Jellyfin/Jellyfin.Plugin.Trailers4Jellyfin.csproj --configuration Release --output bin
```

Install the plugin DLL and its third-party dependencies. Do not bundle Jellyfin, MediaBrowser, Microsoft, or System framework DLLs from the build output; the server supplies them.

## Releasing from a fork

1. Keep `version` in `build.yaml`, `AssemblyVersion`, and `FileVersion` in the project file identical (four components). Update the changelog and `targetAbi` for the minimum supported server API. The 12.x release uses `12.0.0.0`.
2. Push the changes to `main`, then push a matching tag, for example `v2.0.0.0`. A branch push alone does not publish a release.
3. The **Publish Release** workflow validates versions, builds with .NET 10, packages the plugin and dependencies, computes the ZIP's MD5, and uploads the release before committing its manifest entry to `main`.
4. Enable Actions and allow the workflow token to write repository contents. Branch protection must permit the manifest commit. If that final push fails, rerun the workflow after correcting permissions.
5. Use your fork's raw `main/manifest.json` URL in Jellyfin. Generated release links use `GITHUB_REPOSITORY`, so they point to the fork running the workflow.

`update_manifest.py` reads the release's `build.yaml`, preserves older releases, and sorts versions newest first. The workflow saves the tagged metadata before switching to the latest `main`, so later changes cannot alter the ABI/changelog of the artifact being released. Do not add a placeholder checksum or catalogue entry before a real release ZIP exists.

For a manual manifest update after uploading a ZIP:

```sh
python3 -m pip install pyyaml
python3 update_manifest.py 2.0.0.0 <zip-md5> --repository YOUR-NAME/Trailers4Jellyfin
```

## Licence

MIT
