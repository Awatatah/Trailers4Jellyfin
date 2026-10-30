using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using Microsoft.Extensions.Logging.Abstractions;

var encoder = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
var root = Path.Combine(Path.GetTempPath(), "t4j-audio-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
    async Task<(string output, string error)> Run(params string[] args)
    {
        var start = new ProcessStartInfo(encoder) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        return (await output, await error);
    }
    async Task<double> Mean(string file)
    {
        var (_, error) = await Run("-i", file, "-vn", "-af", "volumedetect", "-f", "null", "-");
        return double.Parse(Regex.Match(error, @"mean_volume: (-?[\d.]+) dB").Groups[1].Value, CultureInfo.InvariantCulture);
    }
    async Task<double> Loudness(string file)
    {
        var (_, error) = await Run("-i", file, "-map", "0:a:0", "-af", "loudnorm=print_format=json", "-f", "null", "-");
        using var json = System.Text.Json.JsonDocument.Parse(error.Substring(error.LastIndexOf('{'), error.LastIndexOf('}') - error.LastIndexOf('{') + 1));
        return double.Parse(json.RootElement.GetProperty("input_i").GetString()!, CultureInfo.InvariantCulture);
    }
    string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    var config = new PluginConfiguration();
    Check(!config.EnableTrailerVolumeReduction && config.TrailerVolumePercent == 80,
        "Default is unchanged audio; enabling starts at 80%");
    Check(config.KeepOriginalTrailers, "Original retention defaults to on for existing configurations");
    config.TrailerVolumePercent = 40;
    Check(config.TrailerVolumePercent == 50, "Server clamps values below 50%");
    config.TrailerVolumePercent = 100;
    Check(config.TrailerVolumePercent == 90, "Server clamps values above 90%");

    var file = Path.Combine(root, "Trailer with spaces.mp4");
    await Run("-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:d=2",
        "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000:duration=2",
        "-c:v", "libx264", "-c:a", "aac", "-b:a", "192k", "-shortest", file);
    var originalHash = Hash(file);
    var originalMean = await Mean(file);
    var originalVideo = (await Run("-v", "error", "-i", file, "-map", "0:v:0", "-c", "copy", "-f", "hash", "-")).output;
    var originals = Path.Combine(root, "originals");
    var service = new TrailerAudioService(NullLogger<TrailerAudioService>.Instance, encoder, originals);
    service.ForgetOriginal(file);
    await service.ProcessAsync(file, false, 80, "", CancellationToken.None);
    Check(Hash(file) == originalHash && !Directory.Exists(originals), "Disabled processing leaves file untouched");

    await service.ProcessAsync(file, true, 80, "", CancellationToken.None);
    var firstMean = await Mean(file);
    Check(Math.Abs((firstMean - originalMean) - 20 * Math.Log10(0.8)) < 0.25,
        "80% produces the expected approximately -1.94 dB adjustment");
    var video = (await Run("-v", "error", "-i", file, "-map", "0:v:0", "-c", "copy", "-f", "hash", "-")).output;
    Check(video == originalVideo, "Video stream is unchanged");
    Check(Hash(Directory.GetFiles(originals).Single()) == originalHash, "Original is preserved outside the trailer library");
    await service.ProcessAsync(file, true, 80, "", CancellationToken.None);
    Check(Math.Abs(await Mean(file) - firstMean) < 0.1, "Repeat processing does not compound reduction");
    await service.ProcessAsync(file, true, 50, "", CancellationToken.None);
    Check(Math.Abs((await Mean(file) - originalMean) - 20 * Math.Log10(0.5)) < 0.25,
        "Changing to 50% uses original audio");
    await service.ProcessAsync(file, true, 50, "", CancellationToken.None, "Decibels", -23, -2);
    Check(Math.Abs((await Mean(file) - originalMean) + 2) < 0.25,
        "dB mode applies only -2 dB, without the percentage reduction");
    config.TrailerTargetLufs = double.NaN;
    Check(config.TrailerTargetLufs == -23, "Non-finite normalization target falls back to a safe default");
    config.TrailerTargetLufs = -100;
    Check(config.TrailerTargetLufs == -30, "Normalization target is limited to -30 through -14 LUFS");
    config.TrailerVolumeAdjustmentDb = double.PositiveInfinity;
    Check(config.TrailerVolumeAdjustmentDb == -2, "Non-finite dB adjustment falls back to -2 dB");
    await service.ProcessAsync(file, true, 50, "", CancellationToken.None, "Normalize", -23);
    Check(Math.Abs(await Loudness(file) + 23) < 0.3, "Two-pass normalization reaches -23 LUFS without stacking percentage reduction");
    await service.ProcessAsync(file, true, 90, "", CancellationToken.None, "Normalize", -29);
    var normalizedMean = await Loudness(file);
    Check(Math.Abs(normalizedMean + 29) < 0.3, "Changing the target reaches -29 LUFS from the untouched original");
    await service.ProcessAsync(file, true, 80, "", CancellationToken.None, "Normalize", -29);
    Check(Math.Abs(await Loudness(file) - normalizedMean) < 0.1, "Repeat normalization does not compound processing");
    var reducedHash = Hash(file);
    var broken = new TrailerAudioService(NullLogger<TrailerAudioService>.Instance, "/missing/ffmpeg", originals);
    try
    {
        await broken.ProcessAsync(file, true, 80, "", CancellationToken.None);
        throw new Exception("Missing encoder unexpectedly succeeded");
    }
    catch (System.ComponentModel.Win32Exception) { }
    Check(Hash(file) == reducedHash && !Directory.GetFiles(root, ".t4j-audio-*").Any(),
        "Encoder failure preserves current trailer and removes temporary output");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            await service.ProcessAsync(file, true, 80, "", cancelled.Token);
            throw new Exception("Cancellation unexpectedly succeeded");
        }
        catch (OperationCanceledException) { }
    }
    Check(Hash(file) == reducedHash, "Cancellation preserves current trailer");
    if (!OperatingSystem.IsWindows())
    {
        var slowEncoder = Path.Combine(root, "slow-encoder");
        var pidPath = Path.Combine(root, "encoder.pid");
        File.WriteAllText(slowEncoder, "#!/bin/sh\necho $$ > '" + pidPath + "'\nexec sleep 30\n");
        File.SetUnixFileMode(slowEncoder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var slow = new TrailerAudioService(NullLogger<TrailerAudioService>.Instance, slowEncoder, originals);
        using var cancelRunning = new CancellationTokenSource();
        var processing = slow.ProcessAsync(file, true, 80, "", cancelRunning.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(pidPath) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(File.Exists(pidPath), "Test encoder started for in-flight cancellation check");
        var pid = int.Parse(File.ReadAllText(pidPath).Trim());
        cancelRunning.Cancel();
        try { await processing; throw new Exception("Active encoding did not cancel"); }
        catch (OperationCanceledException) { }
        var alive = false;
        try { using var child = Process.GetProcessById(pid); alive = !child.HasExited; }
        catch (ArgumentException) { }
        Check(!alive && Hash(file) == reducedHash && !Directory.GetFiles(root, ".t4j-audio-*").Any(),
            "Cancellation stops the active encoder, preserves trailer, and removes temporary output");
        File.Delete(pidPath);
        using var downloader = new TrailerDownloadService(NullLogger<TrailerDownloadService>.Instance);
        using var cancelDownload = new CancellationTokenSource();
        var download = downloader.DownloadAsync("test-key", Path.Combine(root, "download.mp4"), 720,
            slowEncoder, "", "", cancelDownload.Token);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(pidPath) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(File.Exists(pidPath), "Test downloader started for cancellation check");
        pid = int.Parse(File.ReadAllText(pidPath).Trim());
        cancelDownload.Cancel();
        try { await download; throw new Exception("Active download did not cancel"); }
        catch (OperationCanceledException) { }
        alive = false;
        try { using var child = Process.GetProcessById(pid); alive = !child.HasExited; }
        catch (ArgumentException) { }
        Check(!alive, "Cancelled yt-dlp stops before audio reprocessing can acquire the shared lock");
    }
    var badFile = Path.Combine(root, "invalid.mp4");
    File.WriteAllText(badFile, "invalid media");
    var badHash = Hash(badFile);
    try
    {
        await service.ProcessAsync(badFile, true, 80, "", CancellationToken.None);
        throw new Exception("Invalid media unexpectedly processed");
    }
    catch (IOException) { }
    Check(Hash(badFile) == badHash && !Directory.GetFiles(root, ".t4j-audio-*").Any(),
        "FFmpeg decoding errors preserve the input and clean temporary output");
    service.ForgetOriginal(badFile);
    await service.ProcessAsync(file, false, 80, "", CancellationToken.None);
    Check(Hash(file) == originalHash, "Disabling and reprocessing restores original bytes");
    service.ForgetOriginal(file);
    Check(!Directory.GetFiles(originals).Any(), "Trailer cleanup removes its original backup");
    using (var missingDownloader = new TrailerDownloadService(NullLogger<TrailerDownloadService>.Instance, () => null))
    {
        var missingOutput = Path.Combine(root, "missing-1080.mp4");
        try
        {
            await missingDownloader.DownloadAsync("test-key", missingOutput, 1080, "/missing/yt-dlp", "", "", CancellationToken.None);
            throw new Exception("1080p silently fell back to the built-in downloader");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("1080p downloads require yt-dlp")) { }
        Check(!File.Exists(missingOutput), "Missing yt-dlp produces an explicit 1080p setup error without a low-quality fallback");
    }
    var retentionFolder = Path.Combine(root, "retained-originals");
    var retention = new TrailerAudioService(NullLogger<TrailerAudioService>.Instance, encoder, retentionFolder);
    var noEncoder = new TrailerAudioService(NullLogger<TrailerAudioService>.Instance, "/missing/ffmpeg", retentionFolder);
    var retainedFile = Path.Combine(root, "retention-test.mp4");
    File.Copy(file, retainedFile);
    await retention.ProcessAsync(retainedFile, true, 80, "", CancellationToken.None);
    var retainedHash = Hash(retainedFile);
    try
    {
        await noEncoder.ProcessAsync(retainedFile, true, 50, "", CancellationToken.None, keepOriginal: false);
        throw new Exception("Failed processing unexpectedly succeeded");
    }
    catch (System.ComponentModel.Win32Exception) { }
    Check(Hash(retainedFile) == retainedHash && Directory.GetFiles(retentionFolder).Length == 1,
        "Processing failure with retention off keeps both the current trailer and saved original");
    await noEncoder.ProcessAsync(retainedFile, true, 80, "", CancellationToken.None, keepOriginal: false);
    Check(Hash(retainedFile) == retainedHash && !Directory.GetFiles(retentionFolder).Any(),
        "Turning retention off deletes only the backup when settings match, without invoking FFmpeg");
    // Recreate the service to prove history survives server restarts.
    var restarted = new TrailerAudioService(NullLogger<TrailerAudioService>.Instance, "/missing/ffmpeg", retentionFolder);
    await restarted.ProcessAsync(retainedFile, true, 80, "", CancellationToken.None, keepOriginal: false);
    Check(Hash(retainedFile) == retainedHash, "Repeat processing without an original is a no-op after restart");
    await restarted.ProcessAsync(retainedFile, false, 80, "", CancellationToken.None, keepOriginal: false);
    Check(Hash(retainedFile) == retainedHash, "Disabling adjustment cannot restore a discarded original");
    await restarted.ProcessAsync(retainedFile, true, 80, "", CancellationToken.None, keepOriginal: true);
    Check(!Directory.GetFiles(retentionFolder).Any(), "Re-enabling retention does not save adjusted audio as a fake original");
    try
    {
        await retention.ProcessAsync(retainedFile, true, 50, "", CancellationToken.None, keepOriginal: false);
        throw new Exception("Changed settings were applied without an original");
    }
    catch (IOException ex) when (ex.Message.Contains("Download the trailer again")) { }
    Check(Hash(retainedFile) == retainedHash, "Changing settings without an original requires a fresh download");
    retention.ForgetOriginal(retainedFile);
    File.Copy(file, retainedFile, overwrite: true);
    await retention.ProcessAsync(retainedFile, true, 80, "", CancellationToken.None, keepOriginal: false);
    Check(!Directory.GetFiles(retentionFolder).Any() && Hash(retainedFile) != originalHash,
        "Fresh downloads with retention off are processed without creating an original backup");
    var freshHash = Hash(retainedFile);
    var history = Directory.GetFiles(retentionFolder + "-state").Single();
    File.WriteAllText(history, "invalid json");
    try
    {
        await retention.ProcessAsync(retainedFile, true, 80, "", CancellationToken.None, keepOriginal: false);
        throw new Exception("Corrupt history was ignored without an original");
    }
    catch (IOException ex) when (ex.Message.Contains("history is unreadable")) { }
    Check(Hash(retainedFile) == freshHash, "Corrupt processing history fails safely instead of compounding reduction");
    retention.ForgetOriginal(retainedFile);
    Check(!Directory.GetFiles(retentionFolder + "-state").Any(), "Fresh-download reset and cleanup remove processing history");

    foreach (var mode in new[] { "Percentage", "Normalize" })
    {
        var legacyFolder = Path.Combine(root, "legacy-" + mode);
        var legacy = new TrailerAudioService(NullLogger<TrailerAudioService>.Instance, encoder, legacyFolder);
        var legacyFile = Path.Combine(root, "legacy-" + mode + ".mp4");
        File.Copy(file, legacyFile);
        await legacy.ProcessAsync(legacyFile, true, 80, "", CancellationToken.None, mode, -29);
        var legacyHash = Hash(legacyFile);
        Directory.Delete(legacyFolder + "-state", recursive: true); // Simulate a 2.1 processed trailer.
        await legacy.ProcessAsync(legacyFile, true, 80, "", CancellationToken.None, mode, -29, keepOriginal: false);
        Check(Hash(legacyFile) == legacyHash && !Directory.GetFiles(legacyFolder).Any(),
            $"Legacy 2.1 {mode} processing is detected: backup removed and adjusted bytes unchanged");
    }
    var changedFolder = Path.Combine(root, "changed-originals");
    var changed = new TrailerAudioService(NullLogger<TrailerAudioService>.Instance, encoder, changedFolder);
    var changedFile = Path.Combine(root, "changed-settings.mp4");
    File.Copy(file, changedFile);
    await changed.ProcessAsync(changedFile, true, 80, "", CancellationToken.None);
    await changed.ProcessAsync(changedFile, true, 50, "", CancellationToken.None, keepOriginal: false);
    Check(Math.Abs((await Mean(changedFile) - originalMean) - 20 * Math.Log10(0.5)) < 0.25
        && !Directory.GetFiles(changedFolder).Any(),
        "Changed settings use the saved original before discarding it after success");
    var restoreFile = Path.Combine(root, "restore-and-discard.mp4");
    File.Copy(file, restoreFile);
    await changed.ProcessAsync(restoreFile, true, 80, "", CancellationToken.None);
    await changed.ProcessAsync(restoreFile, false, 80, "", CancellationToken.None, keepOriginal: false);
    Check(Hash(restoreFile) == originalHash && !Directory.GetFiles(changedFolder).Any(),
        "Turning adjustment off restores available original audio before deleting its backup");

    var sample = Environment.GetEnvironmentVariable("T4J_SAMPLE_TRAILER");
    if (!string.IsNullOrWhiteSpace(sample))
    {
        var localCopy = Path.Combine(root, "sample-trailer.mp4");
        File.Copy(sample, localCopy);
        var before = await Loudness(localCopy);
        var sourceVideo = (await Run("-v", "error", "-i", localCopy, "-map", "0:v:0", "-c", "copy", "-f", "hash", "-")).output;
        await service.ProcessAsync(localCopy, true, 80, "", CancellationToken.None, "Normalize", -29);
        var after = await Loudness(localCopy);
        var resultVideo = (await Run("-v", "error", "-i", localCopy, "-map", "0:v:0", "-c", "copy", "-f", "hash", "-")).output;
        Check(Math.Abs(after + 29) < 0.5 && sourceVideo == resultVideo,
            $"Real trailer reaches -29 LUFS with unchanged video ({before:F1} -> {after:F1} LUFS)");
    }

}
finally
{
    Directory.Delete(root, recursive: true);
}
