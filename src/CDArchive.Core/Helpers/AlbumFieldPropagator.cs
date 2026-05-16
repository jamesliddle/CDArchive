using System.Text.Json;
using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Propagates album-level inheritable fields (<c>SparsCode</c>,
/// <c>IsStereo</c>, <c>Performers</c>) down to every track on the album.
/// Extracted from <c>AlbumEditorWindow</c> so the propagation rules can be
/// unit-tested without spinning up the WPF dialog.
///
/// <para>
/// The semantic is documented in CLAUDE.md's <i>Inheritable album-level
/// fields</i> section. Two rules per field:
/// <list type="number">
///   <item><b>Push</b>: when the album-level value changed since the
///     snapshot, push the new value to every track, overwriting any prior
///     track-level value. The user's clear intent: "set at album →
///     propagate to all."</item>
///   <item><b>Backfill</b>: when the album-level value didn't change,
///     write the album's value into any track whose value is still null.
///     Keeps the "no Inherit" UI contract for new tracks and legacy null
///     tracks.</item>
/// </list>
/// Tracks whose value is non-null and whose album-level value was not
/// changed are left alone — preserving prior track-level overrides.
/// </para>
/// </summary>
public static class AlbumFieldPropagator
{
    /// <summary>
    /// Snapshot of the album's inheritable fields, captured when the
    /// editor opens. Compared against the album's current state at save
    /// time to decide push-vs-backfill per field.
    /// </summary>
    public readonly record struct InheritableSnapshot(
        string? SparsCode,
        bool?   IsStereo,
        string  PerformersFingerprint);

    public static InheritableSnapshot Snapshot(CanonAlbum album) =>
        new(album.SparsCode, album.IsStereo, FingerprintPerformers(album.Performers));

    /// <summary>
    /// Applies the propagation rules described above to every track on
    /// <paramref name="album"/>. Mutates the tracks in place.
    /// </summary>
    public static void Propagate(CanonAlbum album, InheritableSnapshot original)
    {
        var sparsChanged  = !string.Equals(original.SparsCode, album.SparsCode, StringComparison.Ordinal);
        var stereoChanged = original.IsStereo != album.IsStereo;
        var perfsChanged  = original.PerformersFingerprint != FingerprintPerformers(album.Performers);

        foreach (var disc in album.Discs)
        {
            foreach (var track in disc.Tracks)
            {
                if (sparsChanged || track.SparsCode is null)
                    track.SparsCode = album.SparsCode;

                if (stereoChanged || track.IsStereo is null)
                    track.IsStereo = album.IsStereo;

                if (perfsChanged || track.Performers is null)
                    track.Performers = ClonePerformers(album.Performers);
            }
        }
    }

    /// <summary>
    /// JSON-roundtrip clone so each track owns an independent performer list.
    /// Returns null for null/empty input so the round-trip preserves the
    /// "no performers" state.
    /// </summary>
    public static List<AlbumPerformer>? ClonePerformers(List<AlbumPerformer>? src)
    {
        if (src is null || src.Count == 0) return null;
        var json = JsonSerializer.Serialize(src);
        return JsonSerializer.Deserialize<List<AlbumPerformer>>(json);
    }

    private static string FingerprintPerformers(List<AlbumPerformer>? src) =>
        src is null || src.Count == 0 ? "" : JsonSerializer.Serialize(src);
}
