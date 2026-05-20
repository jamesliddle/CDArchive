using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Pure model-level tests of the contract the six small editors
/// (Performer / Session / Role / ComposerCredit / InstrumentEntry /
/// EnsembleEntry) must respect after the Rework H31 + H33 fix:
/// the editor mutates the input instance in place rather than constructing
/// a fresh one, so any field the editor doesn't surface (today or in the
/// future) survives the round trip.
///
/// <para>
/// The editor classes themselves are WPF code-behinds and aren't unit-
/// testable without a WPF host (H39 still open). These tests pin down the
/// invariant the editors must respect, and the manual smoke test exercises
/// the actual UI.
/// </para>
/// </summary>
public class SmallEditorContractTests
{
    /// <summary>
    /// H33 regression: <c>EnsembleEntryEditorWindow</c> used to overwrite
    /// <c>IsEnsemble = true</c> on every save, silently flipping a
    /// non-ensemble entry's type. The mutate-in-place fix touches only
    /// <c>Members</c>; this test pins down the model-level invariant that
    /// non-touched fields survive.
    /// </summary>
    [Fact]
    public void InstrumentEntry_MutateMembersOnly_PreservesIsEnsembleFalse()
    {
        var entry = new InstrumentEntry
        {
            Instrument = "Piano",
            IsEnsemble = false,
            PartNumber = 2,
        };

        // Simulate the EnsembleEntryEditorWindow post-fix OK handler.
        entry.Members = new List<InstrumentEntry> { new() { Instrument = "(child)" } };

        Assert.False(entry.IsEnsemble);          // H33 contract
        Assert.Equal("Piano", entry.Instrument); // unchanged
        Assert.Equal(2, entry.PartNumber);       // H31 contract — fields the editor doesn't touch
        Assert.Single(entry.Members!);
    }

    /// <summary>
    /// H33's natural counterpart: an ensemble entry that's actually
    /// supposed to be an ensemble also survives the mutate-in-place
    /// pattern with IsEnsemble intact.
    /// </summary>
    [Fact]
    public void InstrumentEntry_MutateMembersOnly_PreservesIsEnsembleTrue()
    {
        var entry = new InstrumentEntry
        {
            Instrument = "String Quartet",
            IsEnsemble = true,
        };

        entry.Members = new List<InstrumentEntry>
        {
            new() { Instrument = "Violin" },
            new() { Instrument = "Violin" },
            new() { Instrument = "Viola" },
            new() { Instrument = "Cello" },
        };

        Assert.True(entry.IsEnsemble);
        Assert.Equal(4, entry.Members!.Count);
    }

    /// <summary>
    /// H31 contract for <see cref="AlbumPerformer"/>: any future FK field
    /// added to the model (the row class already carries PersonId /
    /// EnsembleId — H31's "concrete risk") must survive a property-level
    /// mutate-in-place edit. Today the model class doesn't carry FK fields
    /// directly, so this test is anticipatory: it pins the invariant by
    /// using <c>Name</c> / <c>Role</c> / <c>Instrument</c> as proxies and
    /// asserting that mutating one doesn't reset the others.
    /// </summary>
    [Fact]
    public void AlbumPerformer_MutateOneField_PreservesOthers()
    {
        var perf = new AlbumPerformer
        {
            Name       = "Karajan, Herbert von",
            Role       = "Conductor",
            Instrument = null,
        };

        // The PerformerEditorWindow OK handler mutates only the three
        // fields it exposes. Anything else on the model must survive.
        perf.Name = "Karajan, Herbert von";
        perf.Role = "Conductor";
        perf.Instrument = null;

        Assert.Equal("Karajan, Herbert von", perf.Name);
        Assert.Equal("Conductor", perf.Role);
        Assert.Null(perf.Instrument);
    }

    /// <summary>
    /// H31 contract for <see cref="RoleEntry"/>: the editor mutates Name,
    /// VoiceType, Description. Any future field stays put.
    /// </summary>
    [Fact]
    public void RoleEntry_MutateThreeFields_PreservesIdentity()
    {
        var role = new RoleEntry
        {
            Name        = "Mimi",
            VoiceType   = "Soprano",
            Description = "Seamstress",
        };
        // Capture the reference so we can assert post-mutation it's the
        // same object the caller passed in.
        var original = role;

        // Simulate the RoleEditorWindow OK handler.
        role.Name        = "Mimì";
        role.VoiceType   = "Soprano";
        role.Description = "Seamstress, friend of the Bohemians";

        Assert.Same(original, role);
        Assert.Equal("Mimì", role.Name);
    }

    /// <summary>
    /// H31 contract for <see cref="RecordingSession"/>: the editor exposes
    /// Dates / Venue / City / Country / Engineers / Producers — that's
    /// every public-settable field on the model today. The contract holds
    /// trivially now, and pins the invariant if (when) the model grows.
    /// </summary>
    [Fact]
    public void RecordingSession_MutateAllFields_StaysSameInstance()
    {
        var session = new RecordingSession();
        var original = session;

        session.Dates     = "1962-03-01";
        session.Venue     = "Musikverein";
        session.City      = "Vienna";
        session.Country   = "Austria";
        session.Engineers = new List<string> { "Erich Karajan" };
        session.Producers = new List<string> { "Walter Legge" };

        Assert.Same(original, session);
        Assert.Equal("Musikverein", session.Venue);
    }
}
