namespace CDArchive.Core.Models;

public class ConversionJob
{
    public string SourceFlacPath { get; set; } = string.Empty;
    public string TargetMp3Path { get; set; } = string.Empty;
    public ConversionStatus Status { get; set; } = ConversionStatus.Pending;
    public string? ErrorMessage { get; set; }
    public double ProgressPercent { get; set; }
}
