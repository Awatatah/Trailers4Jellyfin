using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        public void ForgetOriginal(string path)
        {
            var original = OriginalPath(path);
            if (File.Exists(original)) File.Delete(original);
        }

        // Caller holds PipelineLock. Always process the untouched original, never an already reduced file.
        public async Task ProcessAsync(string path, bool enabled, int percentage, string ffmpegPath, CancellationToken ct,
            string mode = "Percentage", double targetLufs = -23, double adjustmentDb = -2)
        {
            var original = OriginalPath(path);
            if (!enabled && !File.Exists(original)) return;
            ct.ThrowIfCancellationRequested();
            var temp = Path.Combine(Path.GetDirectoryName(path)!, ".t4j-audio-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (enabled)
                {
                    if (!File.Exists(original))
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

                    var encoder = string.IsNullOrWhiteSpace(ffmpegPath) ? _encoderPath : ffmpegPath;
                    var filter = mode == "Normalize"
                        ? await NormalizationFilterAsync(encoder, original, targetLufs, ct).ConfigureAwait(false)
                        : mode == "Decibels"
                            ? "volume=" + (double.IsFinite(adjustmentDb) ? Math.Clamp(adjustmentDb, -6, -1) : -2).ToString(CultureInfo.InvariantCulture) + "dB"
                            : "volume=" + (ClampVolume(percentage) / 100.0).ToString(CultureInfo.InvariantCulture);
                    await RunEncoderAsync(encoder, new[] {
                        "-hide_banner", "-nostdin", "-nostats", "-y", "-i", original,
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
                File.Move(temp, path, overwrite: true);
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
