using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

public class ConversionStatusService : IConversionStatusService
{
    private readonly IArchiveScannerService _scanner;

    public ConversionStatusService(IArchiveScannerService scanner)
    {
        _scanner = scanner;
    }

    public async Task<List<(AlbumInfo Album, DiscInfo Disc, List<string> MissingMp3s)>> GetMissingConversionsAsync(
        CancellationToken ct = default)
    {
        var results = new List<(AlbumInfo Album, DiscInfo Disc, List<string> MissingMp3s)>();
        var albums = await _scanner.ScanArchiveAsync(ct);

        foreach (var album in albums)
        {
            foreach (var disc in album.Discs)
            {
                var mp3Names = new HashSet<string>(
                    disc.Mp3Tracks.Select(t => Path.GetFileNameWithoutExtension(t.FileName)),
                    StringComparer.OrdinalIgnoreCase);

                var missing = disc.FlacTracks
                    .Where(t => !mp3Names.Contains(Path.GetFileNameWithoutExtension(t.FileName)))
                    .Select(t => t.FileName)
                    .ToList();

                if (missing.Count > 0)
                    results.Add((album, disc, missing));
            }
        }

        return results;
    }
}
