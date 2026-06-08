using System.Collections.ObjectModel;
using CDArchive.App.Helpers;
using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>TrackEditorWindow</c>.
///
/// <para>The TrackEditor has two single-track modes plus multi-edit:</para>
/// <list type="bullet">
///   <item><b>Single album-bound</b>: editing one track within a disc.
///     <see cref="LoadSingle"/> handles both edit-existing and add-new
///     (with album defaults supplied via <paramref name="defaultsFromAlbum"/>).</item>
///   <item><b>Loose track</b>: singleton with no owning album. Callers pass
///     the track instance to <see cref="LoadSingle"/> with
///     <c>defaultsFromAlbum: null</c> — the loose track's TrackNumber=0
///     sentinel flows through as a "0" string the editor hides. Save goes
///     through <see cref="SaveLoose"/> so the sentinel is preserved.</item>
///   <item><b>Multi-edit</b>: editing several tracks at once. <see cref="LoadMulti"/>
///     loads each field as Unanimous (all share a value) or Mixed (values
///     differ).</item>
/// </list>
///
/// <para>Recording-session fields (Dates / Venue / City / State / Country /
/// Engineers / Producers): the parent album carries one set of these; each
/// track has its own copies. The Load methods accept an optional
/// <c>defaultsFromAlbum</c> parameter and eagerly copy non-null album values
/// into blank track fields on open — the user sees the album defaults
/// pre-filled and can override per track. Pre-refactor this was a
/// SessionId reference into <c>CanonAlbum.Sessions[]</c>; the whole list +
/// SessionId machinery is gone.</para>
/// </summary>
public partial class TrackEditorViewModel : ObservableObject
{
    public MixedField<string> TrackNumber { get; } = new();
    public MixedField<string> Duration    { get; } = new();
    public MixedField<string> Description { get; } = new();
    public MixedField<string> FlacPath    { get; } = new();
    public MixedField<string> Mp3Path     { get; } = new();

    public MixedField<string> SparsCode { get; } = new();
    public MixedField<string> IsStereo  { get; } = new();

    public const string SparsCodeMixedSentinel = "Mixed";
    public const string IsStereoMixedSentinel  = "Mixed";

    public static string IsStereoToString(bool? v) => v switch
    {
        true  => "Stereo",
        false => "Mono",
        null  => "Unknown",
    };

    public static bool? IsStereoFromString(string? v) => v switch
    {
        "Stereo" => true,
        "Mono"   => false,
        _        => null,
    };

    public static string SparsCodeToString(string? v) =>
        string.IsNullOrEmpty(v) ? "Unknown" : v;

    public static string? SparsCodeFromString(string? v) =>
        string.IsNullOrEmpty(v) ? null : v;

    // ── Recording-session fields ──────────────────────────────────────────────
    // Per-track copies of the same fields the album owns. The Load methods
    // eagerly copy non-null album defaults into blank track fields on open.
    public MixedField<string> SessionDates    { get; } = new();
    public MixedField<string> SessionVenue    { get; } = new();
    public MixedField<string> SessionCity     { get; } = new();
    public MixedField<string> SessionState    { get; } = new();
    public MixedField<string> SessionCountry  { get; } = new();

    /// <summary>Engineer names — per-track list, edited via Add/Edit/Remove/Up/Down.</summary>
    public ObservableCollection<string> SessionEngineers { get; } = new();

    /// <summary>Producer names — same shape as <see cref="SessionEngineers"/>.</summary>
    public ObservableCollection<string> SessionProducers { get; } = new();

    // ── List-shaped fields (PieceRefs, Performers) — multi-edit aware. ───────

    public MixedCollection<TrackPieceRef> PieceRefs  { get; } = new();
    public MixedCollection<AlbumPerformer> Performers { get; } = new();

    /// <summary>
    /// Populate from a single track. The optional <paramref name="defaultsFromAlbum"/>
    /// supplies album-level session field values; for any track field that's
    /// null/empty, the VM displays the album's value as the default (the user
    /// can override). On save, whatever's in the VM is written verbatim.
    /// </summary>
    public void LoadSingle(AlbumTrack track, CanonAlbum? defaultsFromAlbum = null)
    {
        TrackNumber.InitUnanimous(track.TrackNumber.ToString());
        Duration.InitUnanimous(track.Duration       ?? "");
        Description.InitUnanimous(track.Description ?? "");
        FlacPath.InitUnanimous(track.FlacPath       ?? "");
        Mp3Path.InitUnanimous(track.Mp3Path         ?? "");
        SparsCode.InitUnanimous(SparsCodeToString(track.SparsCode));
        IsStereo.InitUnanimous(IsStereoToString(track.IsStereo));

        SessionDates.InitUnanimous(DefaultFromAlbum(track.SessionDates, defaultsFromAlbum?.SessionDates));
        SessionVenue.InitUnanimous(DefaultFromAlbum(track.SessionVenue, defaultsFromAlbum?.SessionVenue));
        SessionCity.InitUnanimous(DefaultFromAlbum(track.SessionCity, defaultsFromAlbum?.SessionCity));
        SessionState.InitUnanimous(DefaultFromAlbum(track.SessionState, defaultsFromAlbum?.SessionState));
        SessionCountry.InitUnanimous(DefaultFromAlbum(track.SessionCountry, defaultsFromAlbum?.SessionCountry));

        SetCollection(SessionEngineers, track.SessionEngineers ?? defaultsFromAlbum?.SessionEngineers);
        SetCollection(SessionProducers, track.SessionProducers ?? defaultsFromAlbum?.SessionProducers);

        PieceRefs.InitUnanimous(track.PieceRefs   ?? []);
        Performers.InitUnanimous(track.Performers ?? []);
    }

    /// <summary>
    /// Populate for a brand-new track within a disc. TrackNumber gets the
    /// next-available position; every other field starts blank, with session
    /// fields pre-filled from <paramref name="defaultsFromAlbum"/> when supplied.
    /// </summary>
    public void LoadNew(AlbumDisc disc, CanonAlbum? defaultsFromAlbum = null)
    {
        var next = (disc.Tracks.Count > 0 ? disc.Tracks.Max(t => t.TrackNumber) : 0) + 1;
        TrackNumber.InitUnanimous(next.ToString());
        Duration.InitUnanimous("");
        Description.InitUnanimous("");
        FlacPath.InitUnanimous("");
        Mp3Path.InitUnanimous("");
        // Default to DDD-stereo digital — the dominant convention for the
        // user's modern-era acquisitions. Matches the new-album defaults so
        // a freshly-created album + track align without per-row editing.
        SparsCode.InitUnanimous(SparsCodeToString("DDD"));       // "DDD"
        IsStereo.InitUnanimous(IsStereoToString(true));          // "Stereo"

        SessionDates.InitUnanimous(defaultsFromAlbum?.SessionDates ?? "");
        SessionVenue.InitUnanimous(defaultsFromAlbum?.SessionVenue ?? "");
        SessionCity.InitUnanimous(defaultsFromAlbum?.SessionCity ?? "");
        SessionState.InitUnanimous(defaultsFromAlbum?.SessionState ?? "");
        SessionCountry.InitUnanimous(defaultsFromAlbum?.SessionCountry ?? "");

        SetCollection(SessionEngineers, defaultsFromAlbum?.SessionEngineers);
        SetCollection(SessionProducers, defaultsFromAlbum?.SessionProducers);

        PieceRefs.InitUnanimous([]);
        Performers.InitUnanimous([]);
    }

    /// <summary>
    /// Populate from a multi-track selection. Each field inspects the
    /// distinct values across <paramref name="tracks"/>: all-share-one →
    /// Unanimous; differ → Mixed. The eager album-default copy is skipped
    /// in multi-edit (it would mask genuine differences and confuse the
    /// Mixed sentinel UX).
    /// </summary>
    public void LoadMulti(IReadOnlyList<AlbumTrack> tracks, string mixedPlaceholder)
    {
        Init(TrackNumber, tracks.Select(t => t.TrackNumber.ToString()), mixedPlaceholder);
        Init(Duration,    tracks.Select(t => t.Duration    ?? ""), mixedPlaceholder);
        Init(Description, tracks.Select(t => t.Description ?? ""), mixedPlaceholder);

        // Audio-file overrides are disabled in multi-edit; init from first
        // track to keep bindings sane.
        FlacPath.InitUnanimous(tracks.Count > 0 ? tracks[0].FlacPath ?? "" : "");
        Mp3Path.InitUnanimous (tracks.Count > 0 ? tracks[0].Mp3Path  ?? "" : "");

        Init(SparsCode, tracks.Select(t => SparsCodeToString(t.SparsCode)), SparsCodeMixedSentinel);
        Init(IsStereo,  tracks.Select(t => IsStereoToString(t.IsStereo)),  IsStereoMixedSentinel);

        // Session text fields share the standard multi-edit shape.
        Init(SessionDates,   tracks.Select(t => t.SessionDates   ?? ""), mixedPlaceholder);
        Init(SessionVenue,   tracks.Select(t => t.SessionVenue   ?? ""), mixedPlaceholder);
        Init(SessionCity,    tracks.Select(t => t.SessionCity    ?? ""), mixedPlaceholder);
        Init(SessionState,   tracks.Select(t => t.SessionState   ?? ""), mixedPlaceholder);
        Init(SessionCountry, tracks.Select(t => t.SessionCountry ?? ""), mixedPlaceholder);

        // Engineers / Producers are list-shaped — leave empty in multi-edit
        // (the lists are hidden in that mode, same as the per-track audio
        // overrides). Multi-edit save doesn't read them.
        SessionEngineers.Clear();
        SessionProducers.Clear();

        InitListMixed(PieceRefs,  tracks.Select(t => t.PieceRefs   as IEnumerable<TrackPieceRef>  ?? []));
        InitListMixed(Performers, tracks.Select(t => t.Performers as IEnumerable<AlbumPerformer> ?? []));
    }

    private static void InitListMixed<T>(MixedCollection<T> field, IEnumerable<IEnumerable<T>> trackLists)
    {
        var fingerprints = trackLists.Select(l => System.Text.Json.JsonSerializer.Serialize(l.ToList())).Distinct().ToList();
        if (fingerprints.Count == 1)
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize<List<T>>(fingerprints[0]) ?? [];
            field.InitUnanimous(copy);
        }
        else
        {
            field.InitMixed();
        }
    }

    private static void Init(MixedField<string> field, IEnumerable<string> values, string mixedPlaceholder)
    {
        var distinct = values.Distinct().ToList();
        if (distinct.Count == 1)
            field.InitUnanimous(distinct[0]);
        else
            field.InitMixed(mixedPlaceholder);
    }

    /// <summary>
    /// Returns the track value when non-empty, falling back to the album
    /// default. Empty string is returned when both are null/empty.
    /// </summary>
    private static string DefaultFromAlbum(string? trackValue, string? albumDefault)
    {
        if (!string.IsNullOrEmpty(trackValue)) return trackValue;
        return albumDefault ?? "";
    }

    private static void SetCollection(ObservableCollection<string> target, List<string>? source)
    {
        target.Clear();
        if (source is { Count: > 0 })
            foreach (var s in source) target.Add(s);
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    public enum SaveValidationError
    {
        None,
        InvalidTrackNumber,
    }

    public SaveValidationError SaveSingle(AlbumDisc disc, int trackIndex)
    {
        if (!int.TryParse((TrackNumber.Value ?? "").Trim(), out var num) || num <= 0)
            return SaveValidationError.InvalidTrackNumber;

        if (trackIndex >= disc.Tracks.Count)
        {
            var newTrack = new AlbumTrack();
            ApplyToTrack(newTrack, num);
            disc.Tracks.Add(newTrack);
        }
        else
        {
            ApplyToTrack(disc.Tracks[trackIndex], num);
        }
        return SaveValidationError.None;
    }

    public void SaveLoose(AlbumTrack track)
    {
        track.TrackNumber      = 0;
        track.Duration         = NullIfEmpty(Duration.Value);
        track.SparsCode        = SparsCodeFromString(SparsCode.Value);
        track.IsStereo         = IsStereoFromString(IsStereo.Value);
        track.Description      = NullIfEmpty(Description.Value);
        track.FlacPath         = NullIfEmpty(FlacPath.Value);
        track.Mp3Path          = NullIfEmpty(Mp3Path.Value);
        track.PieceRefs        = PieceRefs.Items.Count   > 0 ? PieceRefs.Items.ToList()   : null;
        track.Performers       = Performers.Items.Count > 0 ? Performers.Items.ToList() : null;
        track.SessionDates     = NullIfEmpty(SessionDates.Value);
        track.SessionVenue     = NullIfEmpty(SessionVenue.Value);
        track.SessionCity      = NullIfEmpty(SessionCity.Value);
        track.SessionState     = NullIfEmpty(SessionState.Value);
        track.SessionCountry   = NullIfEmpty(SessionCountry.Value);
        track.SessionEngineers = SessionEngineers.Count > 0 ? SessionEngineers.ToList() : null;
        track.SessionProducers = SessionProducers.Count > 0 ? SessionProducers.ToList() : null;
    }

    public SaveValidationError SaveMulti(IReadOnlyList<AlbumTrack> tracks, bool allLoose)
    {
        int? trackNum = null;
        if (!allLoose && !SkipMixedTextWrite(TrackNumber))
        {
            var trackNumText = (TrackNumber.Value ?? "").Trim();
            if (!int.TryParse(trackNumText, out var n) || n <= 0)
                return SaveValidationError.InvalidTrackNumber;
            trackNum = n;
        }
        if (trackNum is { } resolvedTrackNum)
            foreach (var t in tracks) t.TrackNumber = resolvedTrackNum;

        ApplyMixedFieldText(Duration,    v => { foreach (var t in tracks) t.Duration    = v; });
        ApplyMixedFieldText(Description, v => { foreach (var t in tracks) t.Description = v; });

        // Session text fields share the multi-edit contract.
        ApplyMixedFieldText(SessionDates,   v => { foreach (var t in tracks) t.SessionDates   = v; });
        ApplyMixedFieldText(SessionVenue,   v => { foreach (var t in tracks) t.SessionVenue   = v; });
        ApplyMixedFieldText(SessionCity,    v => { foreach (var t in tracks) t.SessionCity    = v; });
        ApplyMixedFieldText(SessionState,   v => { foreach (var t in tracks) t.SessionState   = v; });
        ApplyMixedFieldText(SessionCountry, v => { foreach (var t in tracks) t.SessionCountry = v; });

        if (!(SparsCode.StartedMixed && SparsCode.IsMixed))
        {
            var spars = SparsCodeFromString(SparsCode.Value);
            foreach (var t in tracks) t.SparsCode = spars;
        }
        if (!(IsStereo.StartedMixed && IsStereo.IsMixed))
        {
            var stereo = IsStereoFromString(IsStereo.Value);
            foreach (var t in tracks) t.IsStereo = stereo;
        }

        ApplyListMulti(PieceRefs, tracks,
            t => t.PieceRefs,
            (t, v) => t.PieceRefs = v,
            () => new List<TrackPieceRef>());

        ApplyListMulti(Performers, tracks,
            t => t.Performers,
            (t, v) => t.Performers = v,
            () => new List<AlbumPerformer>());

        return SaveValidationError.None;
    }

    private void ApplyToTrack(AlbumTrack target, int trackNumber)
    {
        target.TrackNumber      = trackNumber;
        target.Duration         = NullIfEmpty(Duration.Value);
        target.SparsCode        = SparsCodeFromString(SparsCode.Value);
        target.IsStereo         = IsStereoFromString(IsStereo.Value);
        target.Description      = NullIfEmpty(Description.Value);
        target.FlacPath         = NullIfEmpty(FlacPath.Value);
        target.Mp3Path          = NullIfEmpty(Mp3Path.Value);
        target.PieceRefs        = PieceRefs.Items.Count   > 0 ? PieceRefs.Items.ToList()   : null;
        target.Performers       = Performers.Items.Count > 0 ? Performers.Items.ToList() : null;
        target.SessionDates     = NullIfEmpty(SessionDates.Value);
        target.SessionVenue     = NullIfEmpty(SessionVenue.Value);
        target.SessionCity      = NullIfEmpty(SessionCity.Value);
        target.SessionState     = NullIfEmpty(SessionState.Value);
        target.SessionCountry   = NullIfEmpty(SessionCountry.Value);
        target.SessionEngineers = SessionEngineers.Count > 0 ? SessionEngineers.ToList() : null;
        target.SessionProducers = SessionProducers.Count > 0 ? SessionProducers.ToList() : null;
    }

    private static bool SkipMixedTextWrite(MixedField<string> field) =>
        field.StartedMixed && (field.IsMixed || string.IsNullOrEmpty(field.Value));

    private static void ApplyMixedFieldText(MixedField<string> field, Action<string?> setter)
    {
        if (SkipMixedTextWrite(field)) return;
        setter(NullIfEmpty(field.Value));
    }

    private static void ApplyListMulti<T>(
        MixedCollection<T>           field,
        IReadOnlyList<AlbumTrack>    tracks,
        Func<AlbumTrack, List<T>?>   getter,
        Action<AlbumTrack, List<T>?> setter,
        Func<List<T>>                newEmptyList)
    {
        if (field.StartedMixed)
        {
            if (!field.WasEdited || field.Items.Count == 0) return;
            foreach (var t in tracks)
            {
                var existing = getter(t) ?? newEmptyList();
                foreach (var item in field.Items) existing.Add(item);
                setter(t, existing);
            }
        }
        else
        {
            foreach (var t in tracks)
            {
                var copy = field.Items.Count > 0 ? field.Items.ToList() : null;
                setter(t, copy);
            }
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
