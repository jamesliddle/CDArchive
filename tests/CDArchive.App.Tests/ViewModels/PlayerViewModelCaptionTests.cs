using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// Covers the two-line player caption composition: line 1 is the track title
/// (with a "No track loaded" fallback); line 2 joins Composer / Performers /
/// Album with " — ", omitting empty parts, and collapses (via
/// <see cref="PlayerViewModel.HasTrackDetail"/>) when there's nothing to show.
/// The marquee scrolling itself is view-layer (needs a realised visual tree)
/// and is covered by manual smoke test.
/// </summary>
public class PlayerViewModelCaptionTests
{
    private static PlayerViewModel Build() => new(
        Substitute.For<IAudioPlayerService>(),
        Substitute.For<IArchiveAudioLocator>(),
        Substitute.For<IArchiveSettings>(),
        Substitute.For<ICanonDataService>());

    [Fact]
    public void TitleLine_FallsBackToNoTrackLoaded_WhenEmpty()
    {
        var vm = Build();
        Assert.Equal("No track loaded", vm.TrackTitleLine);
        Assert.False(vm.HasTrackDetail);
        Assert.Equal("", vm.TrackDetailLine);
    }

    [Fact]
    public void DetailLine_JoinsComposerPerformersAlbum_OmittingEmpties()
    {
        var vm = Build();
        vm.Title = "Symphony No. 5 — I. Allegro con brio";
        vm.Composer = "Beethoven, Ludwig van";
        vm.Performers = "Berliner Philharmoniker +1 more";
        vm.Album = "Beethoven: Complete Symphonies";

        Assert.Equal("Symphony No. 5 — I. Allegro con brio", vm.TrackTitleLine);
        Assert.Equal(
            "Beethoven, Ludwig van — Berliner Philharmoniker +1 more — Beethoven: Complete Symphonies",
            vm.TrackDetailLine);
        Assert.True(vm.HasTrackDetail);
    }

    [Fact]
    public void DetailLine_DropsMissingParts()
    {
        var vm = Build();
        vm.Composer = "Bach, Johann Sebastian";
        vm.Performers = null;
        vm.Album = null;

        Assert.Equal("Bach, Johann Sebastian", vm.TrackDetailLine);
        Assert.True(vm.HasTrackDetail);
    }

    // ── Performer formatting ─────────────────────────────────────────────────

    [Fact]
    public void FormatPerformers_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(PlayerViewModel.FormatPerformers(null));
        Assert.Null(PlayerViewModel.FormatPerformers(new List<AlbumPerformer>()));
    }

    [Fact]
    public void FormatPerformers_ShowsFirstTwo_WithRolePreferred_AndPlusNMore()
    {
        var performers = new List<AlbumPerformer>
        {
            new() { Name = "Karajan, Herbert von", Role = "Conductor" },
            new() { Name = "Argerich, Martha", Instrument = "fortepiano" },
            new() { Name = "Berliner Philharmoniker", Role = "Orchestra" },
        };

        // First two only; #1 uses its role, #2 falls back to its instrument;
        // a "+1 more" suffix accounts for the third.
        Assert.Equal(
            "Karajan, Herbert von (Conductor), Argerich, Martha (fortepiano) +1 more",
            PlayerViewModel.FormatPerformers(performers));
    }

    [Fact]
    public void FormatPerformers_TwoOrFewer_HasNoMoreSuffix()
    {
        var performers = new List<AlbumPerformer>
        {
            new() { Name = "Gould, Glenn", Role = "Piano" },
            new() { Name = "Bernstein, Leonard", Role = "Conductor" },
        };

        Assert.Equal(
            "Gould, Glenn (Piano), Bernstein, Leonard (Conductor)",
            PlayerViewModel.FormatPerformers(performers));
    }

    [Fact]
    public void FormatPerformerDetailed_ShowsBothRoleAndInstrument_WhenPresent()
    {
        Assert.Equal(
            "Argerich, Martha (Soloist, fortepiano)",
            PlayerViewModel.FormatPerformerDetailed(
                new AlbumPerformer { Name = "Argerich, Martha", Role = "Soloist", Instrument = "fortepiano" }));

        Assert.Equal(
            "Gould, Glenn (Piano)",
            PlayerViewModel.FormatPerformerDetailed(
                new AlbumPerformer { Name = "Gould, Glenn", Role = "Piano" }));

        Assert.Equal(
            "Berliner Philharmoniker",
            PlayerViewModel.FormatPerformerDetailed(
                new AlbumPerformer { Name = "Berliner Philharmoniker" }));
    }

    // ── Full track tooltip ───────────────────────────────────────────────────

    [Fact]
    public void BuildTrackTooltip_FullSummary_TopToBottom_WithIndentedSubpieces()
    {
        var refs = new List<TrackPieceRef>
        {
            new()
            {
                Composer = "Beethoven, Ludwig van",
                PieceTitle = "Symphony No. 9 \"Choral\"",
                SubpiecePath = new List<string> { "IV. Finale", "Presto" },
            },
        };
        var performers = new List<AlbumPerformer>
        {
            new() { Name = "Karajan, Herbert von", Role = "Conductor" },
            new() { Name = "Berliner Philharmoniker" },
        };

        var expected = string.Join(Environment.NewLine,
            "Beethoven: Complete Symphonies",
            "Beethoven, Ludwig van",
            "Symphony No. 9 \"Choral\"",
            "    IV. Finale",
            "        Presto",
            "Karajan, Herbert von (Conductor)",
            "Berliner Philharmoniker");

        Assert.Equal(expected, PlayerViewModel.BuildTrackTooltip(
            "Beethoven: Complete Symphonies", refs, fallbackDescription: null, performers));
    }

    [Fact]
    public void BuildTrackTooltip_OmitsAlbum_AndRepeatsComposerOnlyWhenItChanges()
    {
        var refs = new List<TrackPieceRef>
        {
            new() { Composer = "Mozart, Wolfgang Amadeus", PieceTitle = "Overture" },
            new() { Composer = "Mozart, Wolfgang Amadeus", PieceTitle = "Aria" },
            new() { Composer = "Salieri, Antonio", PieceTitle = "Finale" },
        };

        // No album line; composer appears once for the Mozart run, again at the
        // Salieri switch.
        var expected = string.Join(Environment.NewLine,
            "Mozart, Wolfgang Amadeus",
            "Overture",
            "Aria",
            "Salieri, Antonio",
            "Finale");

        Assert.Equal(expected, PlayerViewModel.BuildTrackTooltip(
            albumTitle: null, refs, fallbackDescription: null, performers: null));
    }

    [Fact]
    public void BuildTrackTooltip_Uncatalogued_FallsBackToDescription()
    {
        var expected = string.Join(Environment.NewLine,
            "Live at the Proms",
            "Applause");

        Assert.Equal(expected, PlayerViewModel.BuildTrackTooltip(
            "Live at the Proms",
            pieceRefs: null,
            fallbackDescription: "Applause",
            performers: null));
    }

    [Fact]
    public void BuildTrackTooltip_NothingToShow_ReturnsNull()
    {
        Assert.Null(PlayerViewModel.BuildTrackTooltip(null, null, null, null));
    }

    [Fact]
    public void FormatPerformer_RolePreferredOverInstrument()
    {
        var p = new AlbumPerformer { Name = "X", Role = "Piano", Instrument = "fortepiano" };
        Assert.Equal("X (Piano)", PlayerViewModel.FormatPerformer(p));
    }

    [Fact]
    public void FormatPerformer_NoRoleOrInstrument_IsBareName()
    {
        var p = new AlbumPerformer { Name = "Berliner Philharmoniker" };
        Assert.Equal("Berliner Philharmoniker", PlayerViewModel.FormatPerformer(p));
    }

    [Fact]
    public void BuildTrackTooltip_ComposerLine_IncludesLifespan_WhenLookupProvides()
    {
        var refs = new List<TrackPieceRef>
        {
            new() { Composer = "Puccini, Giacomo", PieceTitle = "Turandot" },
        };

        var tip = PlayerViewModel.BuildTrackTooltip(
            albumTitle: null, refs, fallbackDescription: null, performers: null,
            composerLifespan: name => name == "Puccini, Giacomo" ? "(1858–1924)" : null);

        Assert.Equal(string.Join(Environment.NewLine,
            "Puccini, Giacomo (1858–1924)",
            "Turandot"), tip);
    }

    // ── Caption line 1: shared-hierarchy collapse across leaf pieces ──────────

    [Fact]
    public void BuildCaptionTitle_SingleRef_UsesNormalSummary()
    {
        var refs = new List<TrackPieceRef>
        {
            new()
            {
                Composer = "Beethoven, Ludwig van",
                PieceTitle = "Piano Sonata No. 14",
                SubpiecePath = new List<string> { "Adagio sostenuto" },
            },
        };

        Assert.Equal(
            "Piano Sonata No. 14: Adagio sostenuto",
            PlayerViewModel.BuildCaptionTitle(refs, fallbackDescription: null));
    }

    [Fact]
    public void BuildCaptionTitle_Uncatalogued_UsesDescription()
    {
        Assert.Equal("Applause",
            PlayerViewModel.BuildCaptionTitle(pieceRefs: null, fallbackDescription: "Applause"));
        Assert.Equal("(no description)",
            PlayerViewModel.BuildCaptionTitle(pieceRefs: null, fallbackDescription: null));
    }

    [Fact]
    public void BuildCaptionTitle_MultipleLeaves_CollapsesSharedHierarchy_Turandot()
    {
        TrackPieceRef Leaf(params string[] sub) => new()
        {
            Composer = "Puccini, Giacomo",
            PieceTitle = "Turandot",
            SubpiecePath = sub.ToList(),
        };

        var refs = new List<TrackPieceRef>
        {
            Leaf("Act II", "Scene 1", "Ho una casa nell'Honan"),
            Leaf("Act II", "Scene 1", "O mondo, o mondo"),
            Leaf("Act II", "Scene 2", "Introduction"),
            Leaf("Act II", "Scene 2", "Gravi, enormi ed imponenti"),
        };

        Assert.Equal(
            "Turandot > Act II > Scene 1 > Ho una casa nell'Honan - O mondo, o mondo - "
            + "Scene 2 > Introduction - Gravi, enormi ed imponenti",
            PlayerViewModel.BuildCaptionTitle(refs, fallbackDescription: null));
    }

    [Fact]
    public void BuildCaptionTitle_MultipleLeaves_DistinctTopPieces_ShowEachFully()
    {
        var refs = new List<TrackPieceRef>
        {
            new() { PieceTitle = "Prelude" },
            new() { PieceTitle = "Fugue" },
        };

        // No shared hierarchy → each piece shows in full (its own single leaf).
        Assert.Equal("Prelude - Fugue",
            PlayerViewModel.BuildCaptionTitle(refs, fallbackDescription: null));
    }

    // ── Caption line 3: file path visibility ─────────────────────────────────

    [Fact]
    public void RefreshCaptionOptions_ShowsFilePathLine_OnlyWhenSettingOn_AndPathPresent()
    {
        var settings = Substitute.For<IArchiveSettings>();
        var vm = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            settings,
            Substitute.For<ICanonDataService>());

        // Setting on but no file loaded → still hidden.
        settings.ShowPlayingFilePath.Returns(true);
        vm.RefreshCaptionOptions();
        Assert.False(vm.ShowFilePath);

        // Setting on + a file loaded → shown.
        vm.PlayingFilePath = @"D:\CD archive\Album\FLAC\01 Track.flac";
        vm.RefreshCaptionOptions();
        Assert.True(vm.ShowFilePath);

        // Setting off → hidden again even with a file loaded.
        settings.ShowPlayingFilePath.Returns(false);
        vm.RefreshCaptionOptions();
        Assert.False(vm.ShowFilePath);
    }
}
