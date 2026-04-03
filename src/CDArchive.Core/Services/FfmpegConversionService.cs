using System.Diagnostics;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public class FfmpegConversionService : IConversionService
{
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
                var mp3Path = track.FullPath
                    .Replace(Path.DirectorySeparatorChar + "FLAC" + Path.DirectorySeparatorChar,
                             Path.DirectorySeparatorChar + "MP3" + Path.DirectorySeparatorChar)
                    .Replace("/FLAC/", "/MP3/");

                mp3Path = Path.ChangeExtension(mp3Path, ".mp3");

                jobs.Add(new ConversionJob
                {
                    SourceFlacPath = track.FullPath,
                    TargetMp3Path = mp3Path
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

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _settings.FfmpegPath,
                Arguments = $"-i \"{flacPath}\" -ab {_settings.Mp3Bitrate}k -map_metadata 0 -id3v2_version 3 \"{mp3Path}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            var stderr = new List<string>();

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    stderr.Add(e.Data);
            };

            process.Start();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0)
            {
                job.Status = ConversionStatus.Completed;
                job.ProgressPercent = 100;
            }
            else
            {
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = string.Join(Environment.NewLine, stderr);
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = ConversionStatus.Failed;
            job.ErrorMessage = "Conversion was cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            job.Status = ConversionStatus.Failed;
            job.ErrorMessage = ex.Message;
        }

        return job;
    }
}
