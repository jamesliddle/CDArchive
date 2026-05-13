using CDArchive.Core.Models;

namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>piece_markers</c>. A musical marker — tempo, first line,
/// rehearsal mark, bar number, or section label — anchored to a piece, a
/// version, or another marker (nesting).
/// <para>
/// Exactly one of <see cref="PieceId"/> / <see cref="VersionId"/> /
/// <see cref="ParentMarkerId"/> is non-null (enforced by CHECK constraint).
/// The <see cref="Id"/> field is the stable identity that
/// <c>album_track_piece_refs.start_marker_id</c> / <c>end_marker_id</c>
/// reference, so a track-anchor survives renames and edits.
/// </para>
/// </summary>
public class PieceMarkerRow
{
    public long Id { get; set; }

    public long? PieceId { get; set; }
    public PieceRow? Piece { get; set; }

    public long? VersionId { get; set; }
    public PieceVersionRow? Version { get; set; }

    public long? ParentMarkerId { get; set; }
    public PieceMarkerRow? ParentMarker { get; set; }

    /// <summary>Order in the marker list — authoritative for "musical order".</summary>
    public int Position { get; set; }

    /// <summary>Marker discriminator (tempo / first_line / rehearsal_mark / bar_number / section).</summary>
    public MarkerKind Kind { get; set; }

    /// <summary>Display value (tempo phrase, first-line text, rehearsal label, …).</summary>
    public string? Value { get; set; }

    /// <summary>Absolute bar number when known.</summary>
    public int? BarNumber { get; set; }

    /// <summary>Movement-relative ordinal — preserved from legacy <c>TempoInfo.Number</c>.</summary>
    public int? Number { get; set; }

    /// <summary>Freeform supplementary text.</summary>
    public string? Description { get; set; }

    /// <summary>Nested sub-markers (e.g. tempo with internal changes).</summary>
    public List<PieceMarkerRow> SubMarkers { get; set; } = [];
}
