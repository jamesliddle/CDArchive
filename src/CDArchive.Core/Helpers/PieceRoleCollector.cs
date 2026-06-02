using System.Text.Json;
using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Collects character / cast role names from the canon pieces an album or
/// track references — used to populate the Role dropdown in the Performer
/// editor with suggestions drawn from the actual operas / oratorios on the
/// recording, rather than from the (always-empty) PerformerRoles pick list.
/// <para>
/// Cast definition lives on the <em>top-level</em> piece's
/// <see cref="CanonPiece.Roles"/> as an array of
/// <c>{ name, voice_type, description }</c> objects. Subpieces reference
/// those characters as plain strings in their own Roles array (e.g.
/// <c>["Florestan", "Leonore"]</c>). Both shapes are honoured —
/// <see cref="RoleEntry.ParseRoles"/> already accepts either at one level;
/// this helper recurses into subpieces and versions so a referenced
/// movement contributes its specific characters even if its parent doesn't
/// enumerate them.
/// </para>
/// <para>
/// Order: cast members in their declared sequence on the first piece
/// encountered, then any extras introduced by subsequent pieces in
/// encounter order. Deduplication is case-insensitive on first occurrence
/// — so "Don Giovanni" and "DON GIOVANNI" collapse to whichever spelling
/// the data uses first.
/// </para>
/// </summary>
/// <summary>
/// A character / cast role harvested from a canon piece's <c>roles</c>
/// definition. <see cref="VoiceType"/> is the voice fach (Soprano, Tenor,
/// Baritone, …) when the piece declared it as an object-form role; null
/// for subpiece string references that name a character without restating
/// the voice type.
/// </summary>
public sealed record CastRole(string Name, string? VoiceType);

public static class PieceRoleCollector
{
    /// <summary>
    /// Collect cast roles referenced by every track on <paramref name="album"/>.
    /// Tracks with no piece-refs contribute nothing.
    /// </summary>
    public static IReadOnlyList<CastRole> CollectFromAlbum(
        CanonAlbum album, IReadOnlyList<CanonPiece> allPieces)
    {
        if (album.Discs is null or { Count: 0 }) return Array.Empty<CastRole>();
        var tracks = album.Discs.SelectMany(d => d.Tracks);
        return CollectFromTracks(tracks, allPieces);
    }

    /// <summary>
    /// Collect cast roles referenced by <paramref name="tracks"/>.
    /// </summary>
    public static IReadOnlyList<CastRole> CollectFromTracks(
        IEnumerable<AlbumTrack> tracks, IReadOnlyList<CanonPiece> allPieces)
    {
        var refs = tracks
            .Where(t => t.PieceRefs is { Count: > 0 })
            .SelectMany(t => t.PieceRefs!);
        return CollectFromPieceRefs(refs, allPieces);
    }

    /// <summary>
    /// Collect cast roles referenced by a single track.
    /// </summary>
    public static IReadOnlyList<CastRole> CollectFromTrack(
        AlbumTrack track, IReadOnlyList<CanonPiece> allPieces) =>
        CollectFromPieceRefs(track.PieceRefs, allPieces);

    /// <summary>
    /// Collect cast roles from the top-level piece named by each
    /// <see cref="TrackPieceRef"/>. The ref's <see cref="TrackPieceRef.PieceTitle"/>
    /// always names a top-level <see cref="CanonPiece"/> for the same
    /// composer; we look that up in <paramref name="allPieces"/> and harvest
    /// its full cast (recursing into subpieces and versions).
    /// <para>
    /// First-occurrence wins on duplicate names — so a subpiece's string-only
    /// "Chorus" reference doesn't overwrite an earlier object-form definition
    /// that carries a voice type.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CastRole> CollectFromPieceRefs(
        IEnumerable<TrackPieceRef>? pieceRefs,
        IReadOnlyList<CanonPiece> allPieces)
    {
        if (pieceRefs is null) return Array.Empty<CastRole>();

        var result = new List<CastRole>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<CanonPiece>();

        foreach (var pr in pieceRefs)
        {
            if (string.IsNullOrWhiteSpace(pr.Composer) ||
                string.IsNullOrWhiteSpace(pr.PieceTitle))
                continue;

            var top = FindTopPiece(pr.Composer, pr.PieceTitle, allPieces);
            if (top is null) continue;
            if (!visited.Add(top)) continue;

            CollectFromPieceRecursive(top, result, seen);
        }

        return result;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static CanonPiece? FindTopPiece(
        string composer, string pieceTitle, IReadOnlyList<CanonPiece> allPieces)
    {
        foreach (var p in allPieces)
        {
            if (string.Equals(p.Composer, composer, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Title,    pieceTitle, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    private static void CollectFromPieceRecursive(
        CanonPiece piece, List<CastRole> result, HashSet<string> seen)
    {
        AddRoleNames(piece.Roles, result, seen);

        if (piece.Subpieces is { Count: > 0 })
            foreach (var sp in piece.Subpieces)
                CollectFromPieceRecursive(sp, result, seen);

        if (piece.Versions is { Count: > 0 })
        {
            foreach (var v in piece.Versions)
            {
                AddRoleNames(v.Roles, result, seen);
                if (v.Subpieces is { Count: > 0 })
                    foreach (var sp in v.Subpieces)
                        CollectFromPieceRecursive(sp, result, seen);
            }
        }
    }

    private static void AddRoleNames(
        JsonElement? rolesEl, List<CastRole> result, HashSet<string> seen)
    {
        if (rolesEl is null || rolesEl.Value.ValueKind != JsonValueKind.Array) return;

        // RoleEntry.ParseRoles handles both element shapes (object with
        // {name, voice_type, …}, and bare string). Object form carries the
        // voice fach so the Performer editor can prefill Instrument when
        // the user picks the role; string form leaves VoiceType null.
        foreach (var entry in RoleEntry.ParseRoles(rolesEl.Value))
        {
            var name = entry.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (seen.Add(name))
                result.Add(new CastRole(name, NullIfEmpty(entry.VoiceType)));
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
