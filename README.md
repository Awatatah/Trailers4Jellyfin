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

## Trailer volume (2.1.0.0)

**Adjust trailer volume** is off by default: new trailers retain their original audio (100%). Enable it and choose one of two methods:

| Method | What it does | Starting point |
|---|---|---|
| **Lower each trailer by a set amount** | Apply the same reduction to every trailer, using either percentage or dB. Existing loudness differences remain. | **80%** of original volume; allowed range **50–90%**. Alternatively **−2 dB**, allowed range **−6 to −1 dB**. |
| **Set all trailers to the same volume** | Measure each trailer and adjust its average loudness toward a common level. Individual scenes still vary. | **−23 LUFS**, adjustable from **−30 to −14 LUFS**. Try **−29 LUFS** for especially quiet movie files. Lower numbers mean quieter audio. |

Only the selected method applies; percentage, dB reduction, and target loudness never stack. Percentage describes audio amplitude, not perceived loudness: 80% is approximately −1.94 dB, while 50% is approximately −6.02 dB. An 80% setting may be too small a reduction when trailers are much louder than movies. Normalization can also increase quiet trailers to reach the chosen target, subject to peak limits. One target cannot match every movie mix.

New downloads from either downloader are processed automatically when enabled. Click **Save and reprocess current trailers** to save settings and apply them to existing `.mp4` files in the download folder. The button shows progress; you can also monitor or cancel **Scheduled Tasks → Reprocess Trailer Audio**. Downloads/cleanup and reprocessing are serialized. Avoid reprocessing during trailer playback. Scan the library afterward if a client retains old media information.

Turn adjustment off and click the same button to restore originals for previously processed trailers **where a saved original is available**. Turning it off alone only changes future downloads; it does not undo existing files.

Both methods use **server CPU** to process audio and require extra disk space. The common-volume option uses more CPU time and takes longer because it measures each trailer before encoding. This work runs during downloads or reprocessing, not each playback; the feature does not use GPU video encoding.

The plugin uses Jellyfin's FFmpeg automatically, or your explicit **ffmpeg path** override. No extra .NET libraries or separate FFmpeg installation are normally required. The chosen FFmpeg must support AAC encoding and, for normalization, the `loudnorm` filter. Two-pass normalization measures the original first, then encodes audio toward the target with a −2 dB true-peak target. Video is copied without re-encoding. Final measurements can differ slightly after AAC encoding.

### Keeping original trailers (2.2.0.0)

**Keep original trailers** defaults to on. Unmodified copies are retained under the plugin data directory in `trailer-audio-originals`, outside the media library. This uses roughly one additional trailer file's worth of disk space per processed trailer, plus temporary working space.

Turn it **off** to avoid retaining original copies after successful processing. **Save alone** changes future processing and does not delete existing backups. **Save and reprocess current trailers** applies the choice to existing trailers:

- If the trailer already matches the selected volume settings, its playable file stays unchanged; only its saved original is deleted when retention is off.
- If settings changed and an original exists, the original is used to create the adjusted file. It is discarded only after successful replacement when retention is off. Errors/cancellation retain the current playable file and any existing original.
- If adjustment is off and an original exists, original audio is restored before discarding the backup when retention is off.
- Without an original, disabling adjustment cannot restore audio, and changing a processed trailer's settings requires downloading that trailer again. Re-enabling retention cannot recover an original already discarded.

The plugin stores small processing-history files in `trailer-audio-originals-state` to identify unchanged files and avoid applying a reduction twice. Keep that history even if original retention is off. Trailers processed by 2.1 without history are measured against their originals before deciding whether encoding can be skipped; very small measurement differences are tolerated.

When retention is off from the start, processing reads the downloaded trailer directly and creates temporary output; it does not create a second permanent original. The plugin removes both original backups and processing history when its rotation task deletes the corresponding trailer or replaces it with a fresh download. Manually deleted/moved trailers can leave unused backups/history. Temporary output only replaces the playable file after successful processing; errors and cancellation are reported in Scheduled Tasks/logs.


## Download quality

The built-in YoutubeExplode downloader uses combined audio/video streams, generally up to 720p. yt-dlp with ffmpeg can merge separate streams for 1080p or higher. Configure executable paths if auto-detection cannot find them. A Netscape-format cookies file can be configured when YouTube requires authentication.

## Synology DSM 7: 1080p downloads

Jellyfin's FFmpeg and the plugin's built-in YouTube downloader are separate from yt-dlp. For 1080p, configure an executable **yt-dlp path** visible to the native Jellyfin package, not a path that exists only inside a container or another computer. The plugin now stops 1080p downloads with a clear error if yt-dlp is missing. Quality is a maximum, not a guarantee that every YouTube source offers that resolution.

A verified setup on this NAS uses `/volume1/ServerVideos/Tools/Trailers4Jellyfin/yt-dlp` as the wrapper, the official standalone `yt-dlp_linux` executable as `yt-dlp.bin` beside it, and the installed Synology Node.js 22 runtime. The wrapper is:

```sh
#!/bin/sh
export TMPDIR=/volume1/ServerVideos/Tools/Trailers4Jellyfin/tmp
exec /volume1/ServerVideos/Tools/Trailers4Jellyfin/yt-dlp.bin --js-runtimes node:/var/packages/Node.js_v22/target/usr/local/bin/node "$@"
```

The dedicated temp directory must be writable by Jellyfin and allow executable mappings; this NAS's system temp directory prevented the bundled executable's libraries from loading. The tools and wrapper must also be readable/executable by Jellyfin. Download the official binary from [yt-dlp releases](https://github.com/yt-dlp/yt-dlp/releases), verify it against the release's SHA2-256SUMS, and keep it current. The official bundled executable includes the JavaScript challenge solver; see [yt-dlp's runtime guidance](https://github.com/yt-dlp/yt-dlp/wiki/EJS).

Set the wrapper path in the plugin, keep a valid FFmpeg path, and save. Updating these settings does not replace existing low-resolution files. Stop trailer download/reprocess tasks before removing old files, then run the download task and scan the library afterward.

### Preview and reset downloaded trailers

The helper [scripts/reset_trailers.py](scripts/reset_trailers.py) previews files by default. It selects only direct `.mp4` files in the specified dedicated trailer folder, their matching `.json` sidecars, and matching original backups/history when `--plugin-data` is provided. It skips symlink files, subfolders, and unrelated JSON files.

For this native Synology installation (after copying the helper onto the NAS):

```sh
python3 /volume1/ServerVideos/Tools/Trailers4Jellyfin/reset_trailers.py \
  --folder "/volume1/ServerVideos/Movie Trailers" \
  --plugin-data /var/packages/jellyfin/var/data/plugins/Jellyfin.Plugin.Trailers4Jellyfin
```

Review the preview, stop trailer playback and plugin tasks, then rerun with `--delete` to remove those files permanently. Deleting plugin-owned backups may require `sudo`. The helper never deletes by default. Scan the trailer library to clear removed entries, run **Download TMDB Trailers**, and scan again to index new downloads. Increase **Max trailers per run** or run the task several times if you want more than the default 20; the refreshed TMDB selection may differ from the deleted trailers.

## Building from source

Install the .NET 10 SDK, then run:

```sh
dotnet publish Jellyfin.Plugin.Trailers4Jellyfin/Jellyfin.Plugin.Trailers4Jellyfin.csproj --configuration Release --output bin
```

Install the plugin DLL and its third-party dependencies. Do not bundle Jellyfin, MediaBrowser, Microsoft, or System framework DLLs from the build output; the server supplies them.

## Releasing from a fork

1. Keep `version` in `build.yaml`, `AssemblyVersion`, and `FileVersion` in the project file identical (four components). Update the changelog and `targetAbi` for the minimum supported server API. The 12.x release uses `12.0.0.0`.
2. Push the changes to `main`, then push a matching tag, for example `v2.2.0.0`. A branch push alone does not publish a release.
3. The **Publish Release** workflow validates versions, builds with .NET 10, packages the plugin and dependencies, computes the ZIP's MD5, and uploads the release before committing its manifest entry to `main`.
4. Enable Actions and allow the workflow token to write repository contents. Branch protection must permit the manifest commit. If that final push fails, rerun the workflow after correcting permissions.
5. Use your fork's raw `main/manifest.json` URL in Jellyfin. Generated release links use `GITHUB_REPOSITORY`, so they point to the fork running the workflow.

`update_manifest.py` reads the release's `build.yaml`, preserves older releases, and sorts versions newest first. The workflow saves the tagged metadata before switching to the latest `main`, so later changes cannot alter the ABI/changelog of the artifact being released. Do not add a placeholder checksum or catalogue entry before a real release ZIP exists.

For a manual manifest update after uploading a ZIP:

```sh
python3 -m pip install pyyaml
python3 update_manifest.py 2.2.0.0 <zip-md5> --repository YOUR-NAME/Trailers4Jellyfin
```

## Licence

MIT
