using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services
{
    public sealed class TrailerAudioService
    {
        // Download/cleanup and manual processing must never modify the same files concurrently.
        public SemaphoreSlim PipelineLock { get; } = new(1, 1);
        private readonly ILogger<TrailerAudioService> _logger;
        private readonly string _encoderPath;
        private readonly string _originalsFolder;

        public TrailerAudioService(ILogger<TrailerAudioService> logger, string encoderPath, string originalsFolder)
        {
            _logger = logger;
            _encoderPath = encoderPath;
            _originalsFolder = originalsFolder;
        }

        public static int ClampVolume(int percentage) => Math.Clamp(percentage, 50, 90);

        public static double ClampTarget(double target) => double.IsFinite(target) ? Math.Clamp(target, -30, -14) : -23;

        private static async Task CopyAsync(string source, string destination, CancellationToken ct)
        {
            using var input = File.OpenRead(source);
            using var output = File.Create(destination);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }

        private static async Task<string> RunEncoderAsync(string encoder, IEnumerable<string> arguments, CancellationToken ct)
        {
            var start = new ProcessStartInfo(encoder)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start FFmpeg.");
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                throw;
            }
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException("FFmpeg audio processing failed: " + error.Substring(Math.Max(0, error.Length - 2000)));
            return error;
        }

        private static async Task<string> NormalizationFilterAsync(string encoder, string original, double target, CancellationToken ct)
        {
            var filter = "loudnorm=I=" + ClampTarget(target).ToString(CultureInfo.InvariantCulture) + ":TP=-2:LRA=50";
            var analysis = await RunEncoderAsync(encoder, new[] {
                "-hide_banner", "-nostdin", "-nostats", "-i", original, "-map", "0:a:0",
                "-af", filter + ":print_format=json", "-f", "null", "-"
            }, ct).ConfigureAwait(false);
            var jsonStart = analysis.LastIndexOf('{');
            var jsonEnd = analysis.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < jsonStart)
                throw new InvalidDataException("FFmpeg did not return loudness measurements. Check loudnorm support.");
            using var measurements = JsonDocument.Parse(analysis.Substring(jsonStart, jsonEnd - jsonStart + 1));
            string Measurement(string name)
            {
                var text = measurements.RootElement.GetProperty(name).GetString();
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                    throw new InvalidDataException("Cannot normalize audio with no measurable loudness (for example, a silent trailer).");
                return value.ToString(CultureInfo.InvariantCulture);
            }
            return filter + ":measured_I=" + Measurement("input_i") + ":measured_TP=" + Measurement("input_tp")
                + ":measured_LRA=" + Measurement("input_lra") + ":measured_thresh=" + Measurement("input_thresh")
                + ":offset=" + Measurement("target_offset") + ":linear=true";
        }

        private string OriginalPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()) fullPath = fullPath.ToUpperInvariant();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
            return Path.Combine(_originalsFolder, key + ".mp4");
        }

        private string StatePath(string path) => Path.Combine(_originalsFolder + "-state",
            Path.GetFileNameWithoutExtension(OriginalPath(path)) + ".json");

        private sealed record AudioState(string Settings, string Hash);

        private static async Task<string> FileHashAsync(string path, CancellationToken ct)
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
        }

        private static string SettingsKey(bool enabled, int percentage, string mode, double target, double db)
        {
            if (!enabled) return "original";
            if (mode == "Normalize") return "normalize:" + ClampTarget(target).ToString(CultureInfo.InvariantCulture);
            if (mode == "Decibels") return "db:" + (double.IsFinite(db) ? Math.Clamp(db, -6, -1) : -2).ToString(CultureInfo.InvariantCulture);
            return "percentage:" + ClampVolume(percentage);
        }

        private async Task SaveStateAsync(string path, AudioState state, CancellationToken ct)
        {
            var statePath = StatePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var temp = statePath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state), ct).ConfigureAwait(false);
                File.Move(temp, statePath, overwrite: true);
            }
            finally { File.Delete(temp); }
        }

        private void DiscardOriginal(string path)
        {
            var original = OriginalPath(path);
            if (File.Exists(original)) File.Delete(original);
        }

        // Fresh downloads and trailer deletion also reset processing history.
        public void ForgetOriginal(string path)
        {
            DiscardOriginal(path);
            var state = StatePath(path);
            if (File.Exists(state)) File.Delete(state);
        }

        // Version 2.1 did not record processing settings. Measure existing audio before
        // discarding a legacy backup, avoiding an unnecessary encode when it already matches.
        private static async Task<bool> LegacySettingsMatchAsync(string encoder, string path, string original,
            int percentage, string mode, double target, double db, CancellationToken ct)
        {
            if (mode == "Normalize")
            {
                var analysis = await RunEncoderAsync(encoder, new[] { "-hide_banner", "-nostdin", "-nostats", "-i", path,
                    "-map", "0:a:0", "-af", "loudnorm=print_format=json", "-f", "null", "-" }, ct).ConfigureAwait(false);
                var start = analysis.LastIndexOf('{');
                var end = analysis.LastIndexOf('}');
                if (start < 0 || end < start) return false;
                using var result = JsonDocument.Parse(analysis.Substring(start, end - start + 1));
                return double.TryParse(result.RootElement.GetProperty("input_i").GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var measured) && Math.Abs(measured - ClampTarget(target)) <= 0.3;
            }
            async Task<double> Mean(string input)
            {
                var analysis = await RunEncoderAsync(encoder, new[] { "-hide_banner", "-nostdin", "-nostats", "-i", input,
                    "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-" }, ct).ConfigureAwait(false);
                var match = Regex.Match(analysis, @"mean_volume: (-?[\d.]+) dB");
                return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
            }
            var expected = mode == "Decibels" ? (double.IsFinite(db) ? Math.Clamp(db, -6, -1) : -2)
                : 20 * Math.Log10(ClampVolume(percentage) / 100.0);
            return Math.Abs(await Mean(path).ConfigureAwait(false) - await Mean(original).ConfigureAwait(false) - expected) <= 0.2;
        }

        // Caller holds PipelineLock. Never apply a new reduction to a known processed file without its original.
        public async Task ProcessAsync(string path, bool enabled, int percentage, string ffmpegPath, CancellationToken ct,
            string mode = "Percentage", double targetLufs = -23, double adjustmentDb = -2, bool keepOriginal = true)
        {
            var original = OriginalPath(path);
            var hasOriginal = File.Exists(original);
            ct.ThrowIfCancellationRequested();
            var settings = SettingsKey(enabled, percentage, mode, targetLufs, adjustmentDb);
            AudioState? state = null;
            if (File.Exists(StatePath(path)))
            {
                try
                {
                    state = JsonSerializer.Deserialize<AudioState>(await File.ReadAllTextAsync(StatePath(path), ct).ConfigureAwait(false))
                        ?? throw new InvalidDataException("Empty trailer audio history.");
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidDataException)
                {
                    if (!hasOriginal) throw new IOException("Trailer audio history is unreadable and no original is available. Download the trailer again.", ex);
                    _logger.LogWarning(ex, "|Trailers4Jellyfin| Rebuilding trailer audio history from saved original: {File}", path);
                }
            }
            if (state?.Settings == settings && state.Hash == await FileHashAsync(path, ct).ConfigureAwait(false))
            {
                if (!keepOriginal) DiscardOriginal(path);
                return;
            }
            if (!enabled && !hasOriginal)
            {
                if (state != null && state.Settings != "original")
                    _logger.LogWarning("|Trailers4Jellyfin| Cannot restore {File}: original was not kept. Download it again to restore original audio.", path);
                return;
            }
            if (enabled && !hasOriginal && state != null && state.Settings != "original")
                throw new IOException($"Cannot change audio settings for '{Path.GetFileName(path)}': the original was not kept. Download the trailer again.");
            var encoder = string.IsNullOrWhiteSpace(ffmpegPath) ? _encoderPath : ffmpegPath;
            if (enabled && hasOriginal && state == null && await LegacySettingsMatchAsync(encoder, path, original,
                percentage, mode, targetLufs, adjustmentDb, ct).ConfigureAwait(false))
            {
                await SaveStateAsync(path, new AudioState(settings, await FileHashAsync(path, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
                if (!keepOriginal) DiscardOriginal(path);
                return;
            }
            var temp = Path.Combine(Path.GetDirectoryName(path)!, ".t4j-audio-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (enabled)
                {
                    if (!hasOriginal && keepOriginal)
                    {
                        Directory.CreateDirectory(_originalsFolder);
                        var backupTemp = original + ".tmp";
                        try
                        {
                            await CopyAsync(path, backupTemp, ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            File.Move(backupTemp, original);
                        }
                        finally
                        {
                            File.Delete(backupTemp);
                        }
                    }

                    var source = File.Exists(original) ? original : path;
                    var filter = mode == "Normalize"
                        ? await NormalizationFilterAsync(encoder, source, targetLufs, ct).ConfigureAwait(false)
                        : mode == "Decibels"
                            ? "volume=" + (double.IsFinite(adjustmentDb) ? Math.Clamp(adjustmentDb, -6, -1) : -2).ToString(CultureInfo.InvariantCulture) + "dB"
                            : "volume=" + (ClampVolume(percentage) / 100.0).ToString(CultureInfo.InvariantCulture);
                    await RunEncoderAsync(encoder, new[] {
                        "-hide_banner", "-nostdin", "-nostats", "-y", "-i", source,
                        "-map", "0:v:0", "-map", "0:a:0", "-map_metadata", "0",
                        "-c:v", "copy", "-af", filter,
                        "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", "-f", "mp4", temp
                    }, ct).ConfigureAwait(false);
                    if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                        throw new IOException("FFmpeg produced no audio processing output.");
                }
                else
                {
                    await CopyAsync(original, temp, ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                // Temporary output is on the same filesystem; replace only after successful processing.
                // Record the output fingerprint before replacing the playable file. If a crash
                // interrupts replacement, a mismatch fails safely rather than compounding volume.
                await SaveStateAsync(path, new AudioState(settings, await FileHashAsync(temp, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
                if (!keepOriginal) DiscardOriginal(path);
                _logger.LogInformation("|Trailers4Jellyfin| Trailer audio updated: {File}, {Adjustment}",
                    Path.GetFileName(path), enabled ? (mode == "Normalize" ? $"target {ClampTarget(targetLufs)} LUFS"
                        : mode == "Decibels" ? $"{Math.Clamp(adjustmentDb, -6, -1)} dB" : $"{ClampVolume(percentage)}% of original") : "original volume");
            }
            finally
            {
                File.Delete(temp);
            }
        }
    }
}
