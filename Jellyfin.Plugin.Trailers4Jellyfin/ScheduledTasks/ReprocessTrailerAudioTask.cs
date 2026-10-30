using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.ScheduledTasks
{
    public sealed class ReprocessTrailerAudioTask : IScheduledTask
    {
        private readonly TrailerAudioService _audio;
        private readonly ILogger<ReprocessTrailerAudioTask> _logger;
        public ReprocessTrailerAudioTask(TrailerAudioService audio, ILogger<ReprocessTrailerAudioTask> logger)
        {
            _audio = audio;
            _logger = logger;
        }
        public string Name => "Reprocess Trailer Audio";
        public string Key => "Trailers4JellyfinReprocessAudio";
        public string Description => "Applies saved trailer volume settings to existing trailers, using their original audio.";
        public string Category => "Trailers4Jellyfin";
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await _audio.PipelineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var config = Plugin.Instance.Configuration;
                if (string.IsNullOrWhiteSpace(config.DownloadFolder) || !Directory.Exists(config.DownloadFolder))
                    throw new DirectoryNotFoundException("Configure an existing trailer download folder before reprocessing.");
                var enabled = config.EnableTrailerVolumeReduction;
                var volume = config.TrailerVolumePercent;
                var encoder = config.FfmpegPath;
                var mode = config.TrailerVolumeMode;
                var targetLufs = config.TrailerTargetLufs;
                var adjustmentDb = config.TrailerVolumeAdjustmentDb;
                var files = Directory.EnumerateFiles(config.DownloadFolder)
                    .Where(p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal)).ToList();
                var failures = 0;
                progress.Report(0);
                for (var i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await _audio.ProcessAsync(files[i], enabled, volume, encoder, cancellationToken, mode, targetLufs, adjustmentDb).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures++;
                        _logger.LogError(ex, "|Trailers4Jellyfin| Could not process trailer audio: {File}", files[i]);
                    }
                    progress.Report(100.0 * (i + 1) / files.Count);
                }
                if (failures > 0)
                    throw new IOException($"Audio processing failed for {failures} trailer(s). See server logs; original files were preserved.");
                progress.Report(100);
            }
            finally
            {
                _audio.PipelineLock.Release();
            }
        }
    }
}
