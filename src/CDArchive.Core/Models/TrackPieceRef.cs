using System.Text.Json.Serialization;

namespace CDArchive.Core.Models;

/// <summary>
/// A reference from an album track to a piece (or subpiece) in the Canon.
/// Identity is path-based: composer name + piece title + optional subpiece path.
/// </summary>
public class TrackPieceRef
{
    /// <summary>Matches <see cref="CanonComposer.Name"/> exactly (case-insensitive lookup).</summary>
    [JsonPropertyName("composer")]
    public string Composer { get; set; } = "";

    /// <summary>Matches <see cref="CanonPiece.Title"/> for the named composer.</summary>
    [JsonPropertyName("piece_title")]
    public string PieceTitle { get; set; } = "";

    /// <summary>
    /// Ordered list of subpiece titles that form a path from the top-level piece
    /// down to the target movement/section.
    /// <list type="bullet">
    ///   <item>null or empty — the whole piece is referenced.</item>
    ///   <item>["Allegro"] — a single movement at the first level.</item>
    ///   <item>["Act I", "No. 3 Aria"] — a nested section.</item>
    /// </list>
    /// </summary>
    [JsonPropertyName("subpiece_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SubpiecePath { get; set; }

    /// <summary>
    /// Optional display label that overrides how the reference is shown in list views,
    /// when the title on the CD differs from the canonical title.
    /// </summary>
    [JsonPropertyName("display_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayLabel { get; set; }

    /// <summary>
    /// When the track corresponds to a specific version/arrangement of the piece,
    /// this holds the version's <see cref="CanonPieceVersion.Description"/> so the
    /// reference can distinguish "Piano Sonata Op. 27 No. 2 (arr. for orchestra)" from
    /// the original.  null means the reference is to the main (unversioned) text.
    /// </summary>
    [JsonPropertyName("version_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VersionDescription { get; set; }

    // ── Computed helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Single-line display, e.g. "Beethoven – Piano Sonata No. 4: Allegro molto e con brio".
    /// When a version is referenced, its description is appended in parentheses before
    /// the subpiece path, e.g. "… (arr. for piano duet): Allegro".
    /// </summary>
    [JsonIgnore]
    public string DisplaySummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayLabel)) return DisplayLabel;
            var sb = new System.Text.StringBuilder(Composer).Append(" – ").Append(PieceTitle);
            if (!string.IsNullOrWhiteSpace(VersionDescription))
                sb.Append(" (").Append(VersionDescription).Append(')');
            if (SubpiecePath is { Count: > 0 })
                sb.Append(": ").Append(string.Join(" › ", SubpiecePath));
            return sb.ToString();
        }
    }

    /// <summary>True if the ref points at the whole piece (no subpiece path).</summary>
    [JsonIgnore]
    public bool IsWholePiece => SubpiecePath is null or { Count: 0 };
}

// ── Referential-integrity support types ─────────────────────────────────────

/// <summary>
/// Describes a title change detected by <see cref="PieceRefPathDiffer"/> after a piece edit.
/// Used by <see cref="AlbumRefUpdater"/> to patch stale <see cref="TrackPieceRef"/>s.
/// </summary>
public record PieceRename(
    /// <summary>Composer name (unchanged by the edit).</summary>
    string Composer,
    /// <summary>The piece's title before the edit.</summary>
    string OldPieceTitle,
    /// <summary>The piece's title after the edit (may equal OldPieceTitle).</summary>
    string NewPieceTitle,
    /// <summary>
    /// Full subpiece path to the renamed node, using pre-edit titles at every level.
    /// null means the rename was of the top-level piece title itself
    /// (OldPieceTitle → NewPieceTitle).
    /// </summary>
    IReadOnlyList<string>? OldSubpiecePath,
    /// <summary>
    /// Full subpiece path after the rename, using post-edit titles at every level
    /// (including any ancestor renames already applied upward in the same edit).
    /// null when OldSubpiecePath is null.
    /// </summary>
    IReadOnlyList<string>? NewSubpiecePath
);

/// <summary>
/// A <see cref="TrackPieceRef"/> whose path could not be resolved against the
/// current Canon, as reported by <see cref="AlbumConsistencyChecker"/>.
/// </summary>
public record BrokenRef(
    CanonAlbum Album,
    AlbumDisc Disc,
    AlbumTrack Track,
    TrackPieceRef Ref,
    /// <summary>Human-readable explanation, e.g. "Composer not found", "Piece not found", "Subpiece path not found: Act I › No. 3 Aria".</summary>
    string Reason
);
