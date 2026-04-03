namespace CDArchive.Core.Models;

public class ValidationResult
{
    public string AlbumPath { get; set; } = string.Empty;
    public string AlbumName { get; set; } = string.Empty;
    public List<ValidationIssue> Issues { get; set; } = new();
}
