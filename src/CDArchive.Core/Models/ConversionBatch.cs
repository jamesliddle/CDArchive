namespace CDArchive.Core.Models;

public class ConversionBatch
{
    public string AlbumName { get; set; } = string.Empty;
    public List<ConversionJob> Jobs { get; set; } = new();

    public int TotalCount => Jobs.Count;
    public int CompletedCount => Jobs.Count(j => j.Status == ConversionStatus.Completed);
    public int FailedCount => Jobs.Count(j => j.Status == ConversionStatus.Failed);

    public double OverallProgress =>
        TotalCount == 0 ? 0 : Jobs.Sum(j => j.ProgressPercent) / TotalCount;
}
