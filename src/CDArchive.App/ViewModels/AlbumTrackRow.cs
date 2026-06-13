using CDArchive.Core.Models;

namespace CDArchive.App.ViewModels;

/// <summary>
/// Row projection for the cross-album Tracks list. Holds direct references
/// back to the owning album, disc, and track so a double-click can open the
/// parent album editor and bulk edits can mutate the underlying instances in
/// place.
/// <para>
/// The <see cref="Piece"/> string is precomputed at construction time using
/// the live <see cref="Core.Services.PieceReferenceIndex"/>, so the display
/// can include catalogue numbers / nicknames that live on the resolved
/// <see cref="CanonPiece"/> rather than on the lighter-weight
/// <see cref="TrackPieceRef"/>. Filter / sort runs read the stored string —
/// no per-row re-resolution.
/// </para>
/// </summary>
public sealed class AlbumTrackRow
{
    /// <summary>Owning album, or null for loose tracks (singletons).</summary>
    public CanonAlbum? Album { get; }

    /// <summary>Owning disc, or null for loose tracks.</summary>
    public AlbumDisc?  Disc  { get; }

    public AlbumTrack Track { get; }

    /// <summary>
    /// Row constructor.  Pass null for <paramref name="album"/> and
    /// <paramref name="disc"/> when projecting a loose track — the row's
    /// display fields render empty for Album / Disc / Track in that case.
    /// </summary>
    public AlbumTrackRow(CanonAlbum? album, AlbumDisc? disc, AlbumTrack track, string piece,
                         bool needsVariantIdentification = false)
    {
        Album = album;
        Disc  = disc;
        Track = track;
        Piece = piece;
        NeedsVariantIdentification = needsVariantIdentification;
    }

    /// <summary>True when this row projects a loose track.</summary>
    public bool IsLooseTrack => Album is null;

    public string AlbumTitle => Album?.DisplayTitle ?? "";

    /// <summary>Numeric track number for sort. 0 for loose tracks (sort first).</summary>
    public int TrackNumber => Track.TrackNumber;

    /// <summary>
    /// Display value for the Disc column: empty for loose tracks AND for
    /// single-disc albums (consolidation rule from Step 5). Multi-disc albums
    /// show the disc number, with volume prefix when present.
    /// </summary>
    public string DiscDisplay
    {
        get
        {
            if (Album is null || Disc is null) return "";
            if (Album.Discs.Count <= 1)        return "";
            return Disc.VolumeNumber.HasValue
                ? $"V{Disc.VolumeNumber} D{Disc.DiscNumber}"
                : Disc.DiscNumber.ToString();
        }
    }

    /// <summary>Display value for the Track column: empty for loose tracks.</summary>
    public string TrackDisplay => Album is null ? "" : Track.TrackNumber.ToString();

    /// <summary>Numeric sort key for disc that respects volume grouping.</summary>
    public int DiscSort => Disc is null
        ? 0
        : ((Disc.VolumeNumber ?? 0) << 16) | (Disc.DiscNumber & 0xFFFF);

    public string Duration => Track.Duration ?? "";

    /// <summary>
    /// Single-column piece label: the resolved top-level piece's
    /// <see cref="CanonPiece.DisplayTitle"/> (which includes catalogue numbers
    /// and nickname) followed by <c>" › Movement › Section"</c> when the ref
    /// has a subpiece path. Multi-ref tracks join per-ref labels with
    /// <c>" / "</c>. Uncatalogued tracks fall back to
    /// <see cref="AlbumTrack.Description"/>.
    /// </summary>
    public string Piece { get; }

    /// <summary>Comma-joined distinct composer names from the track's refs.</summary>
    public string Composer
    {
        get
        {
            if (!Track.IsCatalogued) return "";
            return string.Join(", ",
                Track.PieceRefs!.Select(r => r.Composer).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct());
        }
    }

    /// <summary>
    /// Performer summary mirroring <see cref="CanonAlbum.PerformerSummary"/>.
    /// Track-level performers win over album-level when both are present.
    /// </summary>
    public string PerformerSummary
    {
        get
        {
            var list = Track.Performers is { Count: > 0 } ? Track.Performers : Album?.Performers;
            if (list is null or { Count: 0 }) return "";
            return list.Count == 1
                ? list[0].DisplayName
                : $"{list[0].DisplayName} +{list.Count - 1} more";
        }
    }

    public bool IsProvisional => Track.IsProvisional;

    /// <summary>
    /// True when at least one of the track's refs points at a piece / version /
    /// movement that defines variants but identifies none. Computed once at
    /// row-build time against the live <see cref="Core.Services.PieceReferenceIndex"/>
    /// (same no-re-resolution-on-filter contract as <see cref="Piece"/>). Drives
    /// the unchosen-variant indicator and the "needs variant" filter.
    /// </summary>
    public bool NeedsVariantIdentification { get; }
}
