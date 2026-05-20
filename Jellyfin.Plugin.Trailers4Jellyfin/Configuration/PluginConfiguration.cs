using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ── TMDB ──────────────────────────────────────────────────────────────

        /// <summary>TMDB v3 API key from https://www.themoviedb.org/settings/api</summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        // ── Sources ───────────────────────────────────────────────────────────

        /// <summary>Fetch from TMDB "Now Playing" (currently in theatres).</summary>
        public bool SourceNowPlaying { get; set; } = true;

        /// <summary>Fetch from TMDB "Upcoming" (coming soon to theatres).</summary>
        public bool SourceUpcoming { get; set; } = true;

        /// <summary>Fetch from TMDB "Popular" filtered by the release date range.</summary>
        public bool SourcePopular { get; set; } = false;

        /// <summary>Fetch from TMDB "Top Rated" filtered by the release date range.</summary>
        public bool SourceTopRated { get; set; } = false;

        // ── Date Range ────────────────────────────────────────────────────────

        /// <summary>
        /// Only include movies released within this many months.
        /// Applies to all sources. 0 = no limit (all time).
        /// Typical values: 3, 6, 12, 24.
        /// </summary>
        public int ReleaseDateRangeMonths { get; set; } = 6;

        // ── Download Settings ─────────────────────────────────────────────────

        /// <summary>Folder where trailers are saved. Required.</summary>
        public string DownloadFolder { get; set; } = string.Empty;

        /// <summary>Maximum number of trailer files to download per scheduled task run.</summary>
        public int MaxTrailersToDownload { get; set; } = 20;

        /// <summary>How many pages to fetch from each TMDB source (each page = 20 movies).</summary>
        public int MaxPagesPerSource { get; set; } = 3;

        /// <summary>Maximum video height. 720 = built-in downloader. 1080 requires yt-dlp.</summary>
        public int PreferredVideoHeight { get; set; } = 720;

        /// <summary>Skip downloading if a file for this movie already exists in the download folder.</summary>
        public bool SkipAlreadyDownloaded { get; set; } = true;

        /// <summary>Skip movies that are already in your Jellyfin library.</summary>
        public bool SkipMoviesInLibrary { get; set; } = true;

        /// <summary>
        /// Optional path to yt-dlp executable for 1080p support.
        /// Leave blank to use the built-in downloader (max 720p, no extra tools).
        /// </summary>
        public string YtDlpPath { get; set; } = string.Empty;
    }
}
