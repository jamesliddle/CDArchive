namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>ensemble_memberships</c>. A person's tenure in an ensemble
/// in a given role, with optional temporal bounds. A member replacement is
/// modelled as two rows: the old member's row with end_date set, and a new
/// row for the replacement with start_date set.
/// </summary>
public class EnsembleMembershipRow
{
    public long Id { get; set; }

    public long EnsembleId { get; set; }
    public EnsembleRow Ensemble { get; set; } = null!;

    public long PersonId { get; set; }
    public PersonRow Person { get; set; } = null!;

    public int Position { get; set; }

    /// <summary>Freeform role, e.g. "First Violin", "Principal Conductor", "Cello".</summary>
    public string? Role { get; set; }

    /// <summary>Freeform date string. Null = unknown / since the ensemble's founding.</summary>
    public string? StartDate { get; set; }

    /// <summary>Freeform date string. Null = still a member / open-ended.</summary>
    public string? EndDate { get; set; }

    public string? Notes { get; set; }
}
