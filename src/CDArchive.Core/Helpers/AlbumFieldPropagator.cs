using System.Text.Json;
using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Propagates album-level inheritable fields down to every track on the
/// album. Extracted from <c>AlbumEditorWindow</c> so the propagation rules
/// can be unit-tested without spinning up the WPF dialog.
///
/// <para>Fields propagated:</para>
/// <list type="bullet">
///   <item><c>SparsCode</c></item>
///   <item><c>IsStereo</c></item>
///   <item><c>Performers</c></item>
///   <item>Session fields: <c>SessionDates</c>, <c>SessionVenue</c>,
///     <c>SessionCity</c>, <c>SessionState</c>, <c>SessionCountry</c>,
///     <c>SessionEngineers</c>, <c>SessionProducers</c> (post the
///     sessions-as-fields refactor — these used to live as a separate
///     <c>RecordingSession</c> list referenced by track via
///     <c>SessionId</c>; they're now flat columns and propagate like
///     SparsCode does).</item>
/// </list>
///
/// <para>The semantic is documented in CLAUDE.md's <i>Inheritable album-level
/// fields</i> section. Two rules per field:</para>
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
/// <para>Tracks whose value is non-null and whose album-level value was not
/// changed are left alone — preserving prior track-level overrides.</para>
/// </summary>
public static class AlbumFieldPropagator
{
    /// <summary>
    /// Snapshot of the album's inheritable fields, captured when the
    /// editor opens. Compared against the album's current state at save
    /// time to decide push-vs-backfill per field. Lists are stored as JSON
    /// fingerprints (cheap order-sensitive equality check).
    /// </summary>
    public readonly record struct InheritableSnapshot(
        string? SparsCode,
        bool?   IsStereo,
        string  PerformersFingerprint,
        // Session-as-fields snapshot members default to null/"" so existing
        // call sites (the older SparsCode/IsStereo/Performers tests +
        // anything constructing a snapshot manually) still compile.
        string? SessionDates   = null,
        string? SessionVenue   = null,
        string? SessionCity    = null,
        string? SessionState   = null,
        string? SessionCountry = null,
        string  SessionEngineersFingerprint = "",
        string  SessionProducersFingerprint = "");

    public static InheritableSnapshot Snapshot(CanonAlbum album) =>
        new(album.SparsCode, album.IsStereo,
            FingerprintPerformers(album.Performers),
            album.SessionDates, album.SessionVenue, album.SessionCity,
            album.SessionState, album.SessionCountry,
            FingerprintStringList(album.SessionEngineers),
            FingerprintStringList(album.SessionProducers));

    /// <summary>
    /// Applies the propagation rules described above to every track on
    /// <paramref name="album"/>. Mutates the tracks in place.
    /// </summary>
    public static void Propagate(CanonAlbum album, InheritableSnapshot original)
    {
        var sparsChanged    = !string.Equals(original.SparsCode, album.SparsCode, StringComparison.Ordinal);
        var stereoChanged   = original.IsStereo != album.IsStereo;
        var perfsChanged    = original.PerformersFingerprint != FingerprintPerformers(album.Performers);

        var datesChanged    = !string.Equals(original.SessionDates,   album.SessionDates,   StringComparison.Ordinal);
        var venueChanged    = !string.Equals(original.SessionVenue,   album.SessionVenue,   StringComparison.Ordinal);
        var cityChanged     = !string.Equals(original.SessionCity,    album.SessionCity,    StringComparison.Ordinal);
        var stateChanged    = !string.Equals(original.SessionState,   album.SessionState,   StringComparison.Ordinal);
        var countryChanged  = !string.Equals(original.SessionCountry, album.SessionCountry, StringComparison.Ordinal);
        var engsChanged     = original.SessionEngineersFingerprint != FingerprintStringList(album.SessionEngineers);
        var prodsChanged    = original.SessionProducersFingerprint != FingerprintStringList(album.SessionProducers);

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

                // Session text fields: push-changed or backfill-null.
                if (datesChanged   || track.SessionDates   is null) track.SessionDates   = album.SessionDates;
                if (venueChanged   || track.SessionVenue   is null) track.SessionVenue   = album.SessionVenue;
                if (cityChanged    || track.SessionCity    is null) track.SessionCity    = album.SessionCity;
                if (stateChanged   || track.SessionState   is null) track.SessionState   = album.SessionState;
                if (countryChanged || track.SessionCountry is null) track.SessionCountry = album.SessionCountry;

                if (engsChanged  || track.SessionEngineers is null)
                    track.SessionEngineers = CloneStringList(album.SessionEngineers);
                if (prodsChanged || track.SessionProducers is null)
                    track.SessionProducers = CloneStringList(album.SessionProducers);
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

    /// <summary>
    /// Defensive copy of a string-name list (Engineers / Producers) so each
    /// track owns its own list. Returns null for null/empty input.
    /// </summary>
    public static List<string>? CloneStringList(List<string>? src) =>
        src is { Count: > 0 } ? new List<string>(src) : null;

    private static string FingerprintPerformers(List<AlbumPerformer>? src) =>
        src is null || src.Count == 0 ? "" : JsonSerializer.Serialize(src);

    private static string FingerprintStringList(List<string>? src) =>
        src is null || src.Count == 0 ? "" : JsonSerializer.Serialize(src);
}
