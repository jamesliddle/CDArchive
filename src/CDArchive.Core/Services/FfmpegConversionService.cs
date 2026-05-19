using System.ComponentModel;
using System.Diagnostics;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public class FfmpegConversionService : IConversionService
{
    /// <summary>
    /// Maximum wall-clock time allowed for a single FLAC → MP3 conversion before
    /// the ffmpeg process is killed and the job marked failed. A 320 kbps
    /// conversion of a typical CD track runs in seconds; 10 minutes covers
    /// pathological inputs (very long live recordings) without leaving a hung
    /// ffmpeg forever.
    /// </summary>
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromMinutes(10);

    private readonly IArchiveSettings _settings;
    private readonly IFileSystemService _fs;

    public FfmpegConversionService(IArchiveSettings settings, IFileSystemService fs)
    {
        _settings = settings;
        _fs = fs;
    }

    public async Task<ConversionBatch> ConvertAlbumAsync(
        AlbumInfo album,
        IProgress<ConversionJob>? progress = null,
        CancellationToken ct = default)
    {
        var batch = new ConversionBatch { AlbumName = album.Name };
        var jobs = new List<ConversionJob>();

        foreach (var disc in album.Discs)
        {
            foreach (var track in disc.FlacTracks)
            {
                jobs.Add(new ConversionJob
                {
                    SourceFlacPath = track.FullPath,
                    TargetMp3Path = DeriveMp3Path(track.FullPath)
                });
            }
        }

        batch.Jobs = jobs;

        int maxConcurrency = Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 4));
        var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = jobs.Select(async job =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var mp3Dir = _fs.GetDirectoryName(job.TargetMp3Path);
                if (!_fs.DirectoryExists(mp3Dir))
                    _fs.CreateDirectory(mp3Dir);

                job.Status = ConversionStatus.InProgress;
                progress?.Report(job);

                var result = await ConvertFileAsync(job.SourceFlacPath, job.TargetMp3Path, ct);
                job.Status = result.Status;
                job.ErrorMessage = result.ErrorMessage;
                job.ProgressPercent = result.ProgressPercent;
            }
            catch (OperationCanceledException)
            {
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = "Conversion was cancelled.";
            }
            catch (Exception ex)
            {
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = ex.Message;
            }
            finally
            {
                semaphore.Release();
                progress?.Report(job);
            }
        });

        await Task.WhenAll(tasks);

        return batch;
    }

    public async Task<ConversionJob> ConvertFileAsync(string flacPath, string mp3Path, CancellationToken ct = default)
    {
        var job = new ConversionJob
        {
            SourceFlacPath = flacPath,
            TargetMp3Path = mp3Path,
            Status = ConversionStatus.InProgress
        };

        if (!File.Exists(flacPath))
        {
            job.Status = ConversionStatus.Failed;
            job.ErrorMessage = $"Source file not found: {flacPath}";
            return job;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _settings.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Per-argument escaping — each path/value is passed as a separate
        // argument, so quote or backslash characters in filenames can't break
        // out of the command line (C8). Order is the same as the previous
        // single-string form.
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(flacPath);
        psi.ArgumentList.Add("-ab");
        psi.ArgumentList.Add($"{_settings.Mp3Bitrate}k");
        psi.ArgumentList.Add("-map_metadata");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-id3v2_version");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add(mp3Path);

        using var process = new Process { StartInfo = psi };
        var stderr = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                stderr.Add(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            job.Status = ConversionStatus.Failed;
            job.ErrorMessage = $"Could not launch ffmpeg ('{_settings.FfmpegPath}'): {ex.Message}";
            TryDeletePartial(mp3Path);
            return job;
        }

        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(PerFileTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDeletePartial(mp3Path);

            if (ct.IsCancellationRequested)
            {
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = "Conversion was cancelled.";
                throw;
            }

            job.Status = ConversionStatus.Failed;
            job.ErrorMessage = $"ffmpeg timed out after {PerFileTimeout.TotalMinutes:0} minutes.";
            return job;
        }

        if (process.ExitCode == 0)
        {
            job.Status = ConversionStatus.Completed;
            job.ProgressPercent = 100;
        }
        else
        {
            job.Status = ConversionStatus.Failed;
            job.ErrorMessage = string.Join(Environment.NewLine, stderr);
            TryDeletePartial(mp3Path);
        }

        return job;
    }

    /// <summary>
    /// Derive the MP3 target path from a FLAC source path. Replaces only the
    /// immediate parent directory's name when it is "FLAC" — never a global
    /// path-string substitution. This means a root like <c>D:\FLAC\Music\</c>
    /// (or any filename containing "FLAC") survives the rewrite unchanged.
    /// (H16)
    /// </summary>
    internal static string DeriveMp3Path(string flacPath)
    {
        var dir = Path.GetDirectoryName(flacPath);
        var fileName = Path.GetFileName(flacPath);

        string mp3Dir;
        if (dir is null)
        {
            mp3Dir = string.Empty;
        }
        else
        {
            var parent = Path.GetDirectoryName(dir);
            var leaf = Path.GetFileName(dir);
            mp3Dir = string.Equals(leaf, "FLAC", StringComparison.OrdinalIgnoreCase) && parent is not null
                ? Path.Combine(parent, "MP3")
                : dir;
        }

        var mp3FileName = Path.ChangeExtension(fileName, ".mp3");
        return string.IsNullOrEmpty(mp3Dir) ? mp3FileName : Path.Combine(mp3Dir, mp3FileName);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
        catch (Win32Exception)            { /* race with natural exit */ }
        catch (NotSupportedException)     { /* not local process */ }
    }

    private static void TryDeletePartial(string mp3Path)
    {
        try
        {
            if (File.Exists(mp3Path))
                File.Delete(mp3Path);
        }
        catch (IOException)            { /* locked — best-effort */ }
        catch (UnauthorizedAccessException) { /* readonly — best-effort */ }
    }
}
