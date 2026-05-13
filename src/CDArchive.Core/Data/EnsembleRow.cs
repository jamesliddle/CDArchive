namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for the <c>ensembles</c> table. A named performer ensemble
/// (orchestra, string quartet, chorus, etc). Distinct from <c>EnsembleDefinition</c>
/// in the pick lists, which describes instrumentation templates used by pieces.
/// Name history lives in <see cref="EnsembleNameRow"/>; current/historical
/// member rosters live in <see cref="EnsembleMembershipRow"/>.
/// </summary>
public class EnsembleRow
{
    public long Id { get; set; }

    /// <summary>Freeform kind, e.g. "Orchestra", "String Quartet", "Chorus".</summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Derived from the current name for alphabetical listing; updated at rename time
    /// so the ensemble doesn't re-sort when a historical name is added.
    /// </summary>
    public string SortName { get; set; } = "";

    public int? FoundedYear { get; set; }
    public int? DisbandedYear { get; set; }
    public string? Notes { get; set; }

    public List<EnsembleNameRow> Names { get; set; } = [];
    public List<EnsembleMembershipRow> Memberships { get; set; } = [];
    public List<AlbumPerformerRow> PerformerCredits { get; set; } = [];
}
