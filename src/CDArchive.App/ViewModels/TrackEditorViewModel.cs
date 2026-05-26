using CDArchive.App.Helpers;
using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>TrackEditorWindow</c> (H13 TrackEditor extraction, slice 1).
///
/// <para>This is the first slice of the multi-PR TrackEditor extraction — modelled
/// on the completed AlbumEditor extraction. Owns the simple text fields:
/// TrackNumber (string for binding; parsed to int at save time), Duration,
/// Description, FlacPath, Mp3Path. The combobox-driven fields (SparsCode,
/// IsStereo), the session combo, and the two list-shaped fields (PieceRefs,
/// Performers) stay in code-behind for now — they'll migrate in subsequent
/// slices.</para>
///
/// <para>The TrackEditor has three modes:
/// <list type="bullet">
///   <item><b>Single album-bound</b>: editing one track within a disc.
///     <see cref="LoadSingle"/> handles both edit-existing and add-new.</item>
///   <item><b>Multi-edit</b>: editing several tracks at once. <see cref="LoadMulti"/>
///     loads each field as Unanimous (all share a value) or Mixed (values
///     differ), mirroring the AlbumEditor pattern.</item>
///   <item><b>Loose track</b>: singleton with no owning album. <see cref="LoadLoose"/>
///     loads the displayed fields; the hidden ones (TrackNumber, Session) get
///     defaults on save.</item>
/// </list>
/// </para>
///
/// <para>Multi-edit semantics are modelled via <see cref="MixedField{T}"/>: each
/// text field carries its own Value / IsMixed / WasEdited / StartedMixed
/// tri-state. XAML TextBoxes TwoWay-bind to <c>FieldName.Value</c>; the
/// editor's code-behind reads <see cref="MixedField{T}.StartedMixed"/> +
/// <see cref="MixedField{T}.IsMixed"/> on multi-edit save to decide whether to
/// commit (same contract as AlbumEditor's slice-4 SaveMulti).</para>
/// </summary>
public partial class TrackEditorViewModel : ObservableObject
{
    /// <summary>
    /// Track number. Stored as string in the VM because the TextBox is
    /// untyped and multi-edit's "Mixed" placeholder is also a string; parsed
    /// to int at save time. The model field is <c>int</c> (not nullable) —
    /// loose tracks use the sentinel value 0.
    /// </summary>
    public MixedField<string> TrackNumber { get; } = new();

    /// <summary>Duration string (e.g. "5:32" or "1:02:15"). Free text.</summary>
    public MixedField<string> Duration    { get; } = new();

    /// <summary>Track description — used for non-Canon tracks (interviews etc.).</summary>
    public MixedField<string> Description { get; } = new();

    /// <summary>Per-track FLAC override (absolute path). Disabled in multi-edit.</summary>
    public MixedField<string> FlacPath    { get; } = new();

    /// <summary>Per-track MP3 override (absolute path). Disabled in multi-edit.</summary>
    public MixedField<string> Mp3Path     { get; } = new();

    // ── Combobox fields (slice 2) ─────────────────────────────────────────────
    // SparsCode + IsStereo use a stable string vocabulary on the VM side; the
    // editor's code-behind syncs the (non-editable) ComboBox SelectedItems
    // imperatively because they use a "Mixed" sentinel ComboBoxItem rather
    // than a placeholder text — different shape from text fields. Same
    // hybrid pattern AlbumEditor's slice 2 established.

    /// <summary>
    /// SPARS code. Values: "DDD" / "ADD" / "AAD" / "Unknown" (canonical set
    /// from the dropdown), or a legacy non-standard code string from existing
    /// data, or <see cref="SparsCodeMixedSentinel"/> when a multi-edit
    /// selection has differing values and the user hasn't picked one yet.
    /// Null / empty maps to "Unknown" on load.
    /// </summary>
    public MixedField<string> SparsCode { get; } = new();

    /// <summary>
    /// Stereo flag, represented as a string for the ComboBox sync. Values:
    /// "Unknown" / "Stereo" / "Mono" (the dropdown items), or
    /// <see cref="IsStereoMixedSentinel"/> when multi-edit values differ.
    /// </summary>
    public MixedField<string> IsStereo { get; } = new();

    /// <summary>Constant used on the VM side for the multi-edit "Mixed" SparsCode sentinel.</summary>
    public const string SparsCodeMixedSentinel = "Mixed";
    /// <summary>Constant used on the VM side for the multi-edit "Mixed" IsStereo sentinel.</summary>
    public const string IsStereoMixedSentinel = "Mixed";

    // String ↔ bool? translation for IsStereo. Keeps the VM string-typed
    // (matches the ComboBox vocabulary) and confines the conversion to a
    // single pair of helpers used at load and save time. Same shape as
    // AlbumEditorViewModel.
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
        _        => null,   // includes "Unknown", "" / null, AND the Mixed sentinel
    };

    public static string SparsCodeToString(string? v) =>
        string.IsNullOrEmpty(v) ? "Unknown" : v;

    public static string? SparsCodeFromString(string? v) =>
        string.IsNullOrEmpty(v) ? null : v;

    // ── Session field (slice 3) ───────────────────────────────────────────────
    // SessionIndex is int? on the model — a positional FK into the album's
    // session list, or null = "(no session)". The TrackEditor's session
    // ComboBox builds items dynamically (real sessions + "(no session)"
    // pseudo-item ± Mixed sentinel ± "(multiple albums)" disabled state),
    // so we use the VM as the canonical source of truth for selection.
    //
    // <para>
    // Contract: <see cref="MixedField{T}.IsMixed"/> = true means either the
    // multi-edit "Mixed" sentinel OR the "(multiple albums — cannot edit)"
    // disabled state is selected. Save MUST skip writing when IsMixed is
    // still true. <see cref="MixedField{T}.Value"/> is meaningful only when
    // IsMixed is false: int? where null is the "(no session)" pseudo-item
    // and a non-null int is the position into the album's session list.
    // </para>

    /// <summary>
    /// Session index. <c>Value</c> = real session index OR null ("(no
    /// session)"). <c>IsMixed</c> = true when the multi-edit "Mixed" or
    /// "(multiple albums)" sentinel is selected — save skips the write.
    /// </summary>
    public MixedField<int?> Session { get; } = new();

    /// <summary>
    /// Out-of-band placeholder value for <see cref="Session"/> when loaded as
    /// Mixed. Distinct from any plausible real session index (positions are
    /// 0..N) AND from null ("(no session)"), so picking the "(no session)"
    /// pseudo-item in the UI trips the property-changed setter (value-equality
    /// check in CommunityToolkit's <c>[ObservableProperty]</c>) and clears
    /// <see cref="MixedField{T}.IsMixed"/>. Using null as the placeholder
    /// would silently collapse "user picked (no session)" with "Mixed sentinel
    /// still selected" — the bug a slice-3 test caught.
    /// </summary>
    internal const int SessionMixedPlaceholder = int.MinValue;

    // ── List-shaped fields (slice 4) ──────────────────────────────────────────
    // PieceRefs and Performers are observable collections with Mixed/Unanimous
    // state. Unlike AlbumEditor's Performers + Sessions (slice 3), these lists
    // ARE editable in the TrackEditor's multi-edit mode — so we need the
    // StartedMixed + WasEdited contract on the list level too.
    //
    // Pre-slice the code-behind tracked each list's state via two parallel
    // booleans (_pieceRefsUntouched / _performersUntouched) + an entry in the
    // _mixedFields HashSet. MixedCollection<T> centralises the contract:
    //   • Unanimous load + user edits → save writes (idempotent rewrite).
    //   • Mixed load + no user edit → save skips (preserves each track's list).
    //   • Mixed load + user Add/Remove → save writes (replaces each track's list).

    /// <summary>
    /// Track-level piece references. <c>Items</c> is the observable list bound
    /// to the editor's <c>PieceRefList</c>. <see cref="MixedCollection{T}.StartedMixed"/>
    /// is true when the multi-edit selection's PieceRefs differed;
    /// <see cref="MixedCollection{T}.WasEdited"/> flips true on the user's
    /// first Add/Remove. Save writes IFF <c>ShouldWriteOnSave</c>.
    /// </summary>
    public MixedCollection<TrackPieceRef> PieceRefs { get; } = new();

    /// <summary>
    /// Track-level performers. Same shape as <see cref="PieceRefs"/>.
    /// </summary>
    public MixedCollection<AlbumPerformer> Performers { get; } = new();

    /// <summary>
    /// Populate from a single track (single-edit mode). Every field becomes
    /// Unanimous with the track's current value; <see cref="MixedField{T}.WasEdited"/>
    /// resets to false.
    /// </summary>
    public void LoadSingle(AlbumTrack track)
    {
        TrackNumber.InitUnanimous(track.TrackNumber.ToString());
        Duration.InitUnanimous(track.Duration       ?? "");
        Description.InitUnanimous(track.Description ?? "");
        FlacPath.InitUnanimous(track.FlacPath       ?? "");
        Mp3Path.InitUnanimous(track.Mp3Path         ?? "");
        SparsCode.InitUnanimous(SparsCodeToString(track.SparsCode));
        IsStereo.InitUnanimous(IsStereoToString(track.IsStereo));
        Session.InitUnanimous(track.SessionIndex);
        PieceRefs.InitUnanimous(track.PieceRefs   ?? []);
        Performers.InitUnanimous(track.Performers ?? []);
    }

    /// <summary>
    /// Populate for adding a new track within a disc. TrackNumber gets the
    /// next-available position (max + 1); the rest are blank.
    /// </summary>
    public void LoadNew(AlbumDisc disc)
    {
        var next = (disc.Tracks.Count > 0 ? disc.Tracks.Max(t => t.TrackNumber) : 0) + 1;
        TrackNumber.InitUnanimous(next.ToString());
        Duration.InitUnanimous("");
        Description.InitUnanimous("");
        FlacPath.InitUnanimous("");
        Mp3Path.InitUnanimous("");
        SparsCode.InitUnanimous(SparsCodeToString(null));   // "Unknown"
        IsStereo.InitUnanimous(IsStereoToString(null));     // "Unknown"
        Session.InitUnanimous(null);                         // "(no session)"
        PieceRefs.InitUnanimous([]);
        Performers.InitUnanimous([]);
    }

    /// <summary>
    /// Populate from a multi-track selection (multi-edit mode). Each field
    /// inspects the distinct values across <paramref name="tracks"/>: when
    /// all share one value it loads Unanimous; otherwise it loads Mixed with
    /// the supplied placeholder string.
    /// </summary>
    public void LoadMulti(IReadOnlyList<AlbumTrack> tracks, string mixedPlaceholder, bool hasSharedSessions = true)
    {
        Init(TrackNumber, tracks.Select(t => t.TrackNumber.ToString()), mixedPlaceholder);
        Init(Duration,    tracks.Select(t => t.Duration    ?? ""), mixedPlaceholder);
        Init(Description, tracks.Select(t => t.Description ?? ""), mixedPlaceholder);

        // Audio-file overrides are disabled in multi-edit (per-track absolute
        // paths don't bulk-edit meaningfully), but we still initialise the VM
        // values to keep the binding in sync. Init from the first track's
        // values; the editor disables the group, so user edits can't reach
        // these properties anyway.
        FlacPath.InitUnanimous(tracks.Count > 0 ? tracks[0].FlacPath ?? "" : "");
        Mp3Path.InitUnanimous (tracks.Count > 0 ? tracks[0].Mp3Path  ?? "" : "");

        // Comboboxes use their own sentinel constants (the ComboBoxItem
        // sentinel rendering is view-side; the VM just tracks the value
        // string the user would see selected).
        Init(SparsCode, tracks.Select(t => SparsCodeToString(t.SparsCode)), SparsCodeMixedSentinel);
        Init(IsStereo,  tracks.Select(t => IsStereoToString(t.IsStereo)),  IsStereoMixedSentinel);

        // Session: three cases:
        //   • !hasSharedSessions → "(multiple albums — cannot edit)" disabled
        //     state in the UI; treat as Mixed so save skips.
        //   • All tracks share one SessionIndex → Unanimous; that value loads.
        //   • Differing SessionIndex → "Mixed" sentinel; save skips until user
        //     picks a real value.
        if (!hasSharedSessions)
        {
            Session.InitMixed(SessionMixedPlaceholder);
        }
        else
        {
            var distinct = tracks.Select(t => t.SessionIndex).Distinct().ToList();
            if (distinct.Count == 1) Session.InitUnanimous(distinct[0]);
            else                     Session.InitMixed(SessionMixedPlaceholder);
        }

        // PieceRefs + Performers: compare lists by JSON fingerprint (order
        // matters; the model's lists are positional). All-equal → load the
        // shared list as Unanimous; differing → load empty as Mixed and
        // surface the "Mixed" banner in the View. User's first Add/Remove
        // flips WasEdited → save writes the new list to every track.
        InitListMixed(PieceRefs,  tracks.Select(t => t.PieceRefs   as IEnumerable<TrackPieceRef>  ?? []));
        InitListMixed(Performers, tracks.Select(t => t.Performers as IEnumerable<AlbumPerformer> ?? []));
    }

    private static void InitListMixed<T>(MixedCollection<T> field, IEnumerable<IEnumerable<T>> trackLists)
    {
        var fingerprints = trackLists.Select(l => System.Text.Json.JsonSerializer.Serialize(l.ToList())).Distinct().ToList();
        if (fingerprints.Count == 1)
        {
            // All tracks share the same list — deserialize the single
            // fingerprint to get an independent copy (the VM owns its own
            // collection; user Add/Remove should NOT mutate the source).
            var copy = System.Text.Json.JsonSerializer.Deserialize<List<T>>(fingerprints[0]) ?? [];
            field.InitUnanimous(copy);
        }
        else
        {
            field.InitMixed();
        }
    }

    /// <summary>
    /// Populate from a loose track (singleton with no owning album). TrackNumber
    /// is hidden in the UI but the VM holds the sentinel value 0 anyway for
    /// completeness; the editor forces TrackNumber=0 on save regardless.
    /// </summary>
    public void LoadLoose(AlbumTrack track)
    {
        TrackNumber.InitUnanimous("0");   // sentinel — UI hidden, save forces 0
        Duration.InitUnanimous(track.Duration       ?? "");
        Description.InitUnanimous(track.Description ?? "");
        FlacPath.InitUnanimous(track.FlacPath       ?? "");
        Mp3Path.InitUnanimous(track.Mp3Path         ?? "");
        SparsCode.InitUnanimous(SparsCodeToString(track.SparsCode));
        IsStereo.InitUnanimous(IsStereoToString(track.IsStereo));
        Session.InitUnanimous(null);   // loose tracks have no session; UI hidden
        PieceRefs.InitUnanimous(track.PieceRefs   ?? []);
        Performers.InitUnanimous(track.Performers ?? []);
    }

    private static void Init(MixedField<string> field, IEnumerable<string> values, string mixedPlaceholder)
    {
        var distinct = values.Distinct().ToList();
        if (distinct.Count == 1)
            field.InitUnanimous(distinct[0]);
        else
            field.InitMixed(mixedPlaceholder);
    }

    // ── Save (slice 5) ────────────────────────────────────────────────────────
    // The CommitCurrentTrack / CommitLooseTrack / SaveMulti orchestration
    // previously lived on TrackEditorWindow's code-behind, reading from VM
    // properties and writing to AlbumTrack / disc.Tracks / loose AlbumTrack
    // instances. Slice 5 moves the data-mutation pass into the VM. The
    // editor's code-behind retains only the UI-bound bits — validation
    // feedback (MessageBox + Focus on the InvalidTrackNumber case) and
    // DialogResult = true.
    //
    // Mirrors the AlbumEditor slice 4 pattern.

    /// <summary>
    /// Validation result returned by the Save methods. <see cref="None"/>
    /// means the data-mutation succeeded and the caller can close the dialog
    /// (or proceed to navigate, for Prev/Next). Other values indicate a
    /// validation failure; the caller is expected to surface a user-visible
    /// message and focus the relevant control.
    /// </summary>
    public enum SaveValidationError
    {
        None,
        /// <summary>TrackNumber was missing or didn't parse to a positive integer.</summary>
        InvalidTrackNumber,
    }

    /// <summary>
    /// H13 TrackEditor slice 5: single-edit save. Validates TrackNumber, then
    /// writes the VM state into the disc's track list. When
    /// <paramref name="trackIndex"/> is past the end of <c>disc.Tracks</c>,
    /// adds a fresh <see cref="AlbumTrack"/> (the "add-new" path); otherwise
    /// updates the track at that index in place.
    /// </summary>
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

    /// <summary>
    /// H13 TrackEditor slice 5: loose-track save. No validation (TrackNumber +
    /// Session UI are hidden in loose mode). Forces TrackNumber=0 and
    /// SessionIndex=null sentinels. Mutates the supplied track in place.
    /// </summary>
    public void SaveLoose(AlbumTrack track)
    {
        track.TrackNumber  = 0;
        track.Duration     = NullIfEmpty(Duration.Value);
        track.SparsCode    = SparsCodeFromString(SparsCode.Value);
        track.IsStereo     = IsStereoFromString(IsStereo.Value);
        track.Description  = NullIfEmpty(Description.Value);
        track.FlacPath     = NullIfEmpty(FlacPath.Value);
        track.Mp3Path      = NullIfEmpty(Mp3Path.Value);
        track.PieceRefs    = PieceRefs.Items.Count   > 0 ? PieceRefs.Items.ToList()   : null;
        track.Performers   = Performers.Items.Count > 0 ? Performers.Items.ToList() : null;
        track.SessionIndex = null;
    }

    /// <summary>
    /// H13 TrackEditor slice 5: multi-edit save. Applies per-field skip/append
    /// semantics across every track in <paramref name="tracks"/>:
    /// <list type="bullet">
    ///   <item>Text fields: write only when StartedMixed=false OR user typed
    ///     a non-empty value (preserves the "don't wipe on backspace" safety).</item>
    ///   <item>Combobox fields (SparsCode, IsStereo, Session): write IFF
    ///     !(StartedMixed && IsMixed) — i.e. unanimous OR user picked a value.</item>
    ///   <item>List fields (PieceRefs, Performers): Unanimous → replace each
    ///     track's list with a fresh copy; Mixed + WasEdited → append each
    ///     editor entry to each track's existing list (additive semantic).</item>
    ///   <item>TrackNumber: validated only when !allLoose AND user touched it
    ///     (must parse to positive integer). Returns
    ///     <see cref="SaveValidationError.InvalidTrackNumber"/> if validation
    ///     fails; the caller surfaces the message and focuses the control.</item>
    /// </list>
    /// </summary>
    /// <param name="tracks">The tracks being bulk-edited.</param>
    /// <param name="allLoose">True when every track is a loose track. Hides
    /// the TrackNumber UI in the View and skips TrackNumber + Session writes
    /// here (loose tracks stay at TrackNumber=0, SessionIndex=null sentinels).</param>
    public SaveValidationError SaveMulti(IReadOnlyList<AlbumTrack> tracks, bool allLoose)
    {
        // Track # — skip when allLoose batch OR Mixed-but-untouched.
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

        // Text fields.
        ApplyMixedFieldText(Duration,    v => { foreach (var t in tracks) t.Duration    = v; });
        ApplyMixedFieldText(Description, v => { foreach (var t in tracks) t.Description = v; });

        // Combobox fields (SparsCode, IsStereo): write IFF !(StartedMixed && IsMixed).
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

        // Session — same contract. The "(multiple albums — cannot edit)" case
        // loads as IsMixed=true so this skips; the !allLoose check above
        // doesn't apply because for allLoose Session also loads as Mixed.
        if (!(Session.StartedMixed && Session.IsMixed))
        {
            foreach (var t in tracks) t.SessionIndex = Session.Value;
        }

        // Lists — branch on Unanimous (replace) vs Mixed (append).
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

    /// <summary>Internal: write every scalar + list field on <paramref name="target"/>.</summary>
    private void ApplyToTrack(AlbumTrack target, int trackNumber)
    {
        target.TrackNumber  = trackNumber;
        target.Duration     = NullIfEmpty(Duration.Value);
        target.SparsCode    = SparsCodeFromString(SparsCode.Value);
        target.IsStereo     = IsStereoFromString(IsStereo.Value);
        target.Description  = NullIfEmpty(Description.Value);
        target.FlacPath     = NullIfEmpty(FlacPath.Value);
        target.Mp3Path      = NullIfEmpty(Mp3Path.Value);
        target.PieceRefs    = PieceRefs.Items.Count   > 0 ? PieceRefs.Items.ToList()   : null;
        target.SessionIndex = Session.Value;
        target.Performers   = Performers.Items.Count > 0 ? Performers.Items.ToList() : null;
    }

    /// <summary>True when a multi-edit save should skip this text field.</summary>
    private static bool SkipMixedTextWrite(MixedField<string> field) =>
        field.StartedMixed && (field.IsMixed || string.IsNullOrEmpty(field.Value));

    private static void ApplyMixedFieldText(MixedField<string> field, Action<string?> setter)
    {
        if (SkipMixedTextWrite(field)) return;
        setter(NullIfEmpty(field.Value));
    }

    /// <summary>
    /// Apply a multi-edit list to every track. Unanimous mode = replace each
    /// track's list with a fresh copy. Mixed mode = append additions to each
    /// track's existing list, preserving prior entries.
    /// </summary>
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
