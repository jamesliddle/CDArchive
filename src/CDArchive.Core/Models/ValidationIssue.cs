namespace CDArchive.Core.Models;

public class ValidationIssue
{
    public ValidationSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    public ValidationIssue() { }

    public ValidationIssue(ValidationSeverity severity, string message, string path)
    {
        Severity = severity;
        Message = message;
        Path = path;
    }
}
