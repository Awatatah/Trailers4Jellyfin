using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public record TmdbVideo(string Key, string Name, bool Official, int Size);

    public record TmdbMovieResult(int Id, string Title, string ReleaseDate)
    {
        public int? Year => DateTime.TryParse(ReleaseDate, out var d) ? d.Year : (int?)null;
    }

    public class TmdbService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TmdbService> _logger;
        private const string BaseUrl = "https://api.themoviedb.org/3";

        public TmdbService(IHttpClientFactory httpClientFactory, ILogger<TmdbService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Fetches candidate movies from the configured TMDB sources, optionally filtered
        /// by a minimum release date. Deduplicates across sources by TMDB ID.
        /// </summary>
        public async Task<List<TmdbMovieResult>> GetCandidateMoviesAsync(
            Configuration.PluginConfiguration config,
            CancellationToken ct)
        {
            DateTime? releasedAfter = config.ReleaseDateRangeMonths > 0
                ? DateTime.UtcNow.AddMonths(-config.ReleaseDateRangeMonths)
                : null;

            var seen = new HashSet<int>();
            var results = new List<TmdbMovieResult>();

            async Task FetchSource(string endpoint)
            {
                var movies = await FetchSourcePagesAsync(endpoint, config.TmdbApiKey, releasedAfter, config.MaxPagesPerSource, ct)
                    .ConfigureAwait(false);

                foreach (var m in movies)
                {
                    if (seen.Add(m.Id))
                        results.Add(m);
                }
            }

            if (config.SourceNowPlaying) await FetchSource("now_playing");
            if (config.SourceUpcoming)   await FetchSource("upcoming");
            if (config.SourcePopular)    await FetchSource("popular");
            if (config.SourceTopRated)   await FetchSource("top_rated");

            return results;
        }

        /// <summary>
        /// Fetches movies from a standard TMDB list endpoint (now_playing, upcoming, popular, top_rated),
        /// applying an optional release-date lower bound.
        /// </summary>
        private async Task<List<TmdbMovieResult>> FetchSourcePagesAsync(
            string endpoint,
            string apiKey,
            DateTime? releasedAfter,
            int maxPages,
            CancellationToken ct)
        {
            var results = new List<TmdbMovieResult>();
            var client = _httpClientFactory.CreateClient();

            for (int page = 1; page <= maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var url = $"{BaseUrl}/movie/{endpoint}?language=en-US&page={page}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);

                    var pageResults = doc.RootElement.GetProperty("results");
                    int totalPages = doc.RootElement.GetProperty("total_pages").GetInt32();
                    bool anyInRange = false;

                    foreach (var movie in pageResults.EnumerateArray())
                    {
                        var releaseDate = movie.TryGetProperty("release_date", out var rd) ? rd.GetString() ?? string.Empty : string.Empty;
                        var title = movie.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        var id = movie.GetProperty("id").GetInt32();

                        // Apply date filter
                        if (releasedAfter.HasValue && DateTime.TryParse(releaseDate, out var parsed))
                        {
                            if (parsed < releasedAfter.Value) continue;
                        }

                        anyInRange = true;
                        results.Add(new TmdbMovieResult(id, title, releaseDate));
                    }

                    // Stop paginating if all results on this page are outside the date range
                    // or if we've reached the last page.
                    if (page >= totalPages || (releasedAfter.HasValue && !anyInRange))
                        break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "|Trailers4Jellyfin| Failed to fetch TMDB source '{Endpoint}' page {Page}", endpoint, page);
                    break;
                }
            }

            return results;
        }

        /// <summary>
        /// Searches TMDB by title + optional year. Used as fallback when a movie lacks a stored TMDB ID.
        /// </summary>
        public async Task<string?> SearchMovieAsync(string title, int? year, string apiKey, CancellationToken ct)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var url = $"{BaseUrl}/search/movie?query={Uri.EscapeDataString(title)}&language=en-US";
                if (year.HasValue) url += $"&year={year.Value}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var res = doc.RootElement.GetProperty("results");
                if (res.GetArrayLength() > 0)
                    return res[0].GetProperty("id").GetInt32().ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| TMDB search failed for '{Title}'", title);
            }
            return null;
        }

        /// <summary>
        /// Returns YouTube trailers for a TMDB movie ID, sorted by official-first then resolution.
        /// </summary>
        public async Task<List<TmdbVideo>> GetTrailersAsync(string tmdbId, string apiKey, CancellationToken ct)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var url = $"{BaseUrl}/movie/{tmdbId}/videos?language=en-US";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var videos = new List<TmdbVideo>();
                foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var type = result.GetProperty("type").GetString();
                    var site = result.GetProperty("site").GetString();
                    if (!string.Equals(type, "Trailer", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(site, "YouTube", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var key = result.GetProperty("key").GetString();
                    if (string.IsNullOrEmpty(key)) continue;

                    videos.Add(new TmdbVideo(
                        key,
                        result.GetProperty("name").GetString() ?? "Trailer",
                        result.GetProperty("official").GetBoolean(),
                        result.GetProperty("size").GetInt32()));
                }

                return videos
                    .OrderByDescending(v => v.Official)
                    .ThenByDescending(v => v.Size)
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "|Trailers4Jellyfin| GetTrailers failed for TMDB ID {Id}", tmdbId);
                return new List<TmdbVideo>();
            }
        }
    }
}
