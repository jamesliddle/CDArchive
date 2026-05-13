namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>ensemble_names</c>. Each ensemble has one or more names
/// with optional validity dates — a never-renamed ensemble has one row with
/// both dates null; a renamed ensemble (e.g. Leningrad → St. Petersburg Phil.)
/// has multiple rows, with old entries carrying end_date and new entries
/// carrying start_date.
/// </summary>
public class EnsembleNameRow
{
    public long Id { get; set; }

    public long EnsembleId { get; set; }
    public EnsembleRow Ensemble { get; set; } = null!;

    public int Position { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Freeform date string. Null = "since founding / unknown".</summary>
    public string? StartDate { get; set; }

    /// <summary>Freeform date string. Null = "still in use".</summary>
    public string? EndDate { get; set; }

    public string? Notes { get; set; }
}
