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
    }

    /// <summary>
    /// Populate from a multi-track selection (multi-edit mode). Each field
    /// inspects the distinct values across <paramref name="tracks"/>: when
    /// all share one value it loads Unanimous; otherwise it loads Mixed with
    /// the supplied placeholder string.
    /// </summary>
    public void LoadMulti(IReadOnlyList<AlbumTrack> tracks, string mixedPlaceholder)
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
    }

    private static void Init(MixedField<string> field, IEnumerable<string> values, string mixedPlaceholder)
    {
        var distinct = values.Distinct().ToList();
        if (distinct.Count == 1)
            field.InitUnanimous(distinct[0]);
        else
            field.InitMixed(mixedPlaceholder);
    }
}
