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
    /// When <see cref="EndSubpiecePath"/> is non-null, this is the <em>start</em>
    /// of an inclusive range over sibling subpieces.
    /// </summary>
    [JsonPropertyName("subpiece_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SubpiecePath { get; set; }

    /// <summary>
    /// Optional end path for range-spanning track refs (collaborative-work
    /// scenarios where one track spans several adjacent subpieces).
    /// <para>
    /// Must be a sibling of <see cref="SubpiecePath"/> at the same depth and
    /// under the same parent. Resolution credits every leaf subpiece in
    /// <c>[SubpiecePath..EndSubpiecePath]</c> inclusive, so a track that
    /// covers Act III sections 3j through 3l ends up crediting 3j, 3k, 3l.
    /// </para>
    /// </summary>
    [JsonPropertyName("end_subpiece_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? EndSubpiecePath { get; set; }

    /// <summary>
    /// Optional marker pinning the start of the track inside the referenced
    /// subpiece. Resolves through <see cref="MarkerReference.Id"/> first,
    /// falling back to kind+value/bar-number matching against the subpiece's
    /// <see cref="CanonPiece.Markers"/> list.
    /// </summary>
    [JsonPropertyName("start_marker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MarkerReference? StartMarker { get; set; }

    /// <summary>
    /// Optional marker pinning the end of the track. When set without
    /// <see cref="EndSubpiecePath"/>, the end marker lives in the same
    /// subpiece as <see cref="StartMarker"/>; with <see cref="EndSubpiecePath"/>
    /// set, it lives in the end subpiece.
    /// </summary>
    [JsonPropertyName("end_marker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MarkerReference? EndMarker { get; set; }

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
    /// <para>
    /// Kept as a human-readable fallback / display aid alongside
    /// <see cref="VersionId"/>; resolution prefers the stable id when present.
    /// </para>
    /// </summary>
    [JsonPropertyName("version_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VersionDescription { get; set; }

    /// <summary>
    /// Stable id of the referenced <see cref="CanonPieceVersion"/>. Zero means
    /// the ref is to the main (unversioned) text, or that resolution should fall
    /// back to <see cref="VersionDescription"/> (e.g. after a fresh reseed
    /// reassigns row ids). Preferred over <see cref="VersionDescription"/> when
    /// non-zero. Mirrors the <see cref="MusicalMarker.Id"/> + description-fallback
    /// pattern.
    /// </summary>
    [JsonPropertyName("version_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long VersionId { get; set; }

    /// <summary>
    /// Which variant(s) of the referenced piece / version / movement this
    /// recording uses. Null or empty means no variant has been identified —
    /// a valid-but-findable state (the recording may use a variant the user
    /// hasn't pinned down yet). A ref can carry several variants at once
    /// (e.g. a cadenza choice plus an ending choice). Each entry resolves by
    /// <see cref="VariantReference.Id"/> first, falling back to
    /// <see cref="VariantReference.Description"/>.
    /// </summary>
    [JsonPropertyName("variants")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VariantReference>? Variants { get; set; }

    // ── Computed helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Single-line display, e.g. "Beethoven – Piano Sonata No. 4: Allegro molto e con brio".
    /// When a version is referenced, its description is appended in parentheses before
    /// the subpiece path, e.g. "… (arr. for piano duet): Allegro".
    /// Marker anchors and end-paths/markers are appended when present.
    /// </summary>
    [JsonIgnore]
    public string DisplaySummary => BuildSummary(includeComposer: true);

    /// <summary>
    /// Like <see cref="DisplaySummary"/> but omits the leading "Composer – "
    /// prefix. Used where the composer is shown separately (e.g. the player
    /// caption, whose second line already carries the composer). When a
    /// <see cref="DisplayLabel"/> override is set it is returned verbatim —
    /// the label is the user's own wording and isn't decomposed.
    /// </summary>
    [JsonIgnore]
    public string DisplaySummaryWithoutComposer => BuildSummary(includeComposer: false);

    private string BuildSummary(bool includeComposer)
    {
        if (!string.IsNullOrWhiteSpace(DisplayLabel)) return DisplayLabel;
        var sb = new System.Text.StringBuilder();
        if (includeComposer) sb.Append(Composer).Append(" – ");
        sb.Append(PieceTitle);
        if (!string.IsNullOrWhiteSpace(VersionDescription))
            sb.Append(" (").Append(VersionDescription).Append(')');
        if (SubpiecePath is { Count: > 0 })
            sb.Append(": ").Append(string.Join(" › ", SubpiecePath));
        if (StartMarker is not null)
            sb.Append(" [from ").Append(StartMarker).Append(']');
        if (EndSubpiecePath is { Count: > 0 })
            sb.Append(" through ").Append(string.Join(" › ", EndSubpiecePath));
        if (EndMarker is not null)
            sb.Append(" [to ").Append(EndMarker).Append(']');
        // Chosen variant(s) — e.g. "… [Autograph ending]" or
        // "… [Kreisler cadenza; shortened ending]". A user-supplied DisplayLabel
        // (handled above) is returned verbatim, so this only decorates the
        // decomposed summary.
        if (Variants is { Count: > 0 })
        {
            var labels = Variants
                .Select(v => v.Description ?? v.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var joined = string.Join("; ", labels);
            if (joined.Length > 0)
                sb.Append(" [").Append(joined).Append(']');
        }
        return sb.ToString();
    }

    /// <summary>True if the ref points at the whole piece (no subpiece path).</summary>
    [JsonIgnore]
    public bool IsWholePiece => SubpiecePath is null or { Count: 0 };

    /// <summary>True if the ref describes a range across sibling subpieces.</summary>
    [JsonIgnore]
    public bool IsRange => EndSubpiecePath is { Count: > 0 };

    /// <summary>True if the ref pins a specific marker as its start.</summary>
    [JsonIgnore]
    public bool HasMarkerAnchor => StartMarker is not null || EndMarker is not null;

    /// <summary>True if at least one variant has been identified on this ref.</summary>
    [JsonIgnore]
    public bool HasVariantSelection => Variants is { Count: > 0 };
}

/// <summary>
/// A reference from a <see cref="TrackPieceRef"/> to a specific
/// <see cref="VariantInfo"/> on the resolved piece / version / movement.
/// <para>
/// Resolution order mirrors <see cref="MarkerReference"/>: <see cref="Id"/>
/// first (the only thing the runtime resolver needs once the canon is loaded);
/// <see cref="Description"/> is the fallback matcher used during JSON import or
/// after a fresh reseed reassigns variant row ids.
/// </para>
/// </summary>
public class VariantReference
{
    /// <summary>Stable variant id, the primary lookup key at runtime.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Id { get; set; }

    /// <summary>Variant description to match if <see cref="Id"/> can't be resolved.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    public override string ToString()
        => !string.IsNullOrEmpty(Description) ? Description! : (Id != 0 ? $"#{Id}" : "");
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
