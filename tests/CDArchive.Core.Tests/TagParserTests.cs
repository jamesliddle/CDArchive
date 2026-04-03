using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

public class TagParserTests
{
    // --- Parse (from TIT2 tags) ---

    [Fact]
    public void Parse_ComposerWorkMovement_ExtractsAllParts()
    {
        var result = TagParser.Parse("Chausson, Ernest: Sym in B flat, Op 20 / 001 Lent: Allegro vivo");

        Assert.Equal("Chausson, Ernest", result.RawComposer);
        Assert.Equal("Sym in B flat, Op 20", result.RawWork);
        Assert.Equal("Lent: Allegro vivo", result.RawMovement);
        Assert.Equal(1, result.MovementNumber);
    }

    [Fact]
    public void Parse_EmptyWork_StandaloneTitle_TreatedAsWork()
    {
        var result = TagParser.Parse("Barraud, Henry:  / Offrande a une ombre");

        Assert.Equal("Barraud, Henry", result.RawComposer);
        Assert.Equal("Offrande a une ombre", result.RawWork);
        Assert.Null(result.RawMovement);
    }

    [Fact]
    public void Parse_EmptyWork_WithMovementNumber_StaysAsMovement()
    {
        var result = TagParser.Parse("Lalo, Edouard:  / 001 Le Roi d'Ys Ov");

        Assert.Equal("Lalo, Edouard", result.RawComposer);
        Assert.Equal("", result.RawWork);
        Assert.Equal("Le Roi d'Ys Ov", result.RawMovement);
        Assert.Equal(1, result.MovementNumber);
    }

    // --- ParseFilename ---

    [Fact]
    public void ParseFilename_ChaussonSymphony()
    {
        var result = TagParser.ParseFilename(
            "08 Chausson, Ernest- Sym in B flat, Op 20 , 001 Lent- Allegro vivo.mp3");

        Assert.Equal("Chausson, Ernest", result.RawComposer);
        Assert.Equal("Sym in B flat, Op 20", result.RawWork);
        Assert.Equal("Lent: Allegro vivo", result.RawMovement);
        Assert.Equal(1, result.MovementNumber);
    }

    [Fact]
    public void ParseFilename_StandaloneOverture()
    {
        var result = TagParser.ParseFilename(
            "01 Lalo, Edouard-  , Le Roi d'Ys Ov.mp3");

        Assert.Equal("Lalo, Edouard", result.RawComposer);
        Assert.Equal("Le Roi d'Ys Ov", result.RawWork);
        Assert.Null(result.RawMovement);
    }

    [Fact]
    public void ParseFilename_BarraudStandalone()
    {
        var result = TagParser.ParseFilename(
            "07 Barraud, Henry-  , Offrande a une ombre.mp3");

        Assert.Equal("Barraud, Henry", result.RawComposer);
        Assert.Equal("Offrande a une ombre", result.RawWork);
        Assert.Null(result.RawMovement);
    }

    [Fact]
    public void ParseFilename_NamounaSuiteMovement()
    {
        var result = TagParser.ParseFilename(
            "02 Lalo, Edouard- Namouna Suite No 1 , 001 Prelude.mp3");

        Assert.Equal("Lalo, Edouard", result.RawComposer);
        Assert.Equal("Namouna Suite No 1", result.RawWork);
        Assert.Equal("Prelude", result.RawMovement);
        Assert.Equal(1, result.MovementNumber);
    }

    [Fact]
    public void ParseFilename_Movement3WithHyphenSeparator()
    {
        var result = TagParser.ParseFilename(
            "10 Chausson, Ernest- Sym in B flat, Op 20 , 003 Anime- Tres anime.mp3");

        Assert.Equal("Chausson, Ernest", result.RawComposer);
        Assert.Equal("Sym in B flat, Op 20", result.RawWork);
        // Hyphen in "Anime- Tres anime" is converted to colon for later processing
        Assert.Equal("Anime: Tres anime", result.RawMovement);
        Assert.Equal(3, result.MovementNumber);
    }

    // --- ParseComposerName ---

    [Theory]
    [InlineData("Lalo, Edouard:", "Lalo", "Edouard")]
    [InlineData("Chausson, Ernest", "Chausson", "Ernest")]
    [InlineData("Bach, Johann Sebastian", "Bach", "Johann Sebastian")]
    public void ParseComposerName_SplitsCorrectly(string raw, string expectedLast, string expectedFirst)
    {
        var (last, first) = TagParser.ParseComposerName(raw);
        Assert.Equal(expectedLast, last);
        Assert.Equal(expectedFirst, first);
    }

    // --- ExpandAbbreviations ---

    [Theory]
    [InlineData("Sym in B flat", "Symphony in B flat")]
    [InlineData("Ov", "Overture")]
    [InlineData("Conc for Vn", "Concerto for Violin")]
    [InlineData("Pf Conc", "Piano Concerto")]
    public void ExpandAbbreviations_ExpandsKnownTerms(string input, string expected)
    {
        Assert.Equal(expected, TagParser.ExpandAbbreviations(input));
    }

    [Theory]
    [InlineData("No 1", "#1")]
    [InlineData("No. 5", "#5")]
    [InlineData("No 12", "#12")]
    [InlineData("Suite No 1", "Suite #1")]
    public void ExpandAbbreviations_NoBecomesHash(string input, string expected)
    {
        Assert.Equal(expected, TagParser.ExpandAbbreviations(input));
    }

    [Theory]
    [InlineData("Op 20", "Op. 20")]
    [InlineData("Op. 20", "Op. 20")]   // already has period, no doubling
    [InlineData("Op. 65", "Op. 65")]
    public void ExpandAbbreviations_OpGetsPeriodWithoutDoubling(string input, string expected)
    {
        Assert.Equal(expected, TagParser.ExpandAbbreviations(input));
    }

    // --- RestoreDiacritics (case-aware) ---

    [Theory]
    [InlineData("Prelude", "Pr\u00e9lude")]
    [InlineData("prelude", "pr\u00e9lude")]
    [InlineData("Tres lent", "Tr\u00e8s lent")]
    [InlineData("tres lent", "tr\u00e8s lent")]
    [InlineData("Anime", "Anim\u00e9")]
    [InlineData("anime", "anim\u00e9")]
    [InlineData("Theme varie", "Th\u00e8me vari\u00e9")]
    [InlineData("Fete foraine", "F\u00eate foraine")]
    [InlineData("Serenade", "S\u00e9r\u00e9nade")]
    [InlineData("Offrande a une ombre", "Offrande \u00e0 une ombre")]
    public void RestoreDiacritics_RestoresWithCorrectCase(string input, string expected)
    {
        Assert.Equal(expected, TagParser.RestoreDiacritics(input));
    }

    [Theory]
    [InlineData("Edouard", "\u00c9douard")]
    [InlineData("Cesar", "C\u00e9sar")]
    [InlineData("Frederic", "Fr\u00e9d\u00e9ric")]
    public void RestoreDiacritics_RestoresComposerFirstNames(string input, string expected)
    {
        Assert.Equal(expected, TagParser.RestoreDiacritics(input));
    }

    // --- InsertFormDash ---

    [Theory]
    [InlineData("Namouna Suite #1", "Namouna\u2014Suite #1")]
    [InlineData("Scheherazade Suite", "Scheherazade\u2014Suite")]
    [InlineData("Carmen Suite #2", "Carmen\u2014Suite #2")]
    public void InsertFormDash_InsertsEmDash(string input, string expected)
    {
        Assert.Equal(expected, TagParser.InsertFormDash(input));
    }

    [Theory]
    [InlineData("Symphony in B-flat")]
    [InlineData("Concerto in E-flat")]
    [InlineData("Le roi d'Ys")]
    [InlineData("Offrande \u00e0 une ombre")]
    public void InsertFormDash_NoChangeWhenNotApplicable(string input)
    {
        Assert.Equal(input, TagParser.InsertFormDash(input));
    }

    // --- SplitSectionWord ---

    [Theory]
    [InlineData("Le Roi d'Ys Overture", "Le Roi d'Ys", "Overture")]
    [InlineData("Aida Ouverture", "Aida", "Ouverture")]
    public void SplitSectionWord_SplitsOverture(string input, string expectedWork, string expectedSection)
    {
        var (work, section) = TagParser.SplitSectionWord(input);
        Assert.Equal(expectedWork, work);
        Assert.Equal(expectedSection, section);
    }

    [Theory]
    [InlineData("Namouna Suite #1")]
    [InlineData("Symphony in B-flat")]
    [InlineData("Offrande \u00e0 une ombre")]
    public void SplitSectionWord_NoSplitForNonSectionWords(string input)
    {
        var (work, section) = TagParser.SplitSectionWord(input);
        Assert.Equal(input, work);
        Assert.Null(section);
    }

    // --- NormalizeTempoSeparators ---

    [Theory]
    [InlineData("Lent: Allegro vivo", "Lent - Allegro vivo")]
    [InlineData("Anime: Tres anime", "Anime - Tres anime")]
    [InlineData("Tres lent", "Tres lent")]
    public void NormalizeTempoSeparators_ReplacesColonsWithDashes(string input, string expected)
    {
        Assert.Equal(expected, TagParser.NormalizeTempoSeparators(input));
    }

    // --- Full pipeline tests (from filenames, as the real service does) ---

    [Fact]
    public void FullPipeline_ChaussonSymphonyMovement1()
    {
        var parsed = TagParser.ParseFilename(
            "08 Chausson, Ernest- Sym in B flat, Op 20 , 001 Lent- Allegro vivo.mp3");

        var work = TagParser.ExpandAbbreviations(parsed.RawWork!);
        work = TagParser.RestoreDiacritics(work);
        work = CataloguingRules.HyphenateKeys(work);
        work = TagParser.InsertFormDash(work);

        var mov = TagParser.ExpandAbbreviations(parsed.RawMovement!);
        mov = TagParser.RestoreDiacritics(mov);
        mov = TagParser.NormalizeTempoSeparators(mov);
        var movFormatted = CataloguingRules.PadMovementNumber(
            $"{parsed.MovementNumber}. {mov}", 3);

        var name = CataloguingRules.FormatTrackName(work, movFormatted);

        Assert.Equal("Symphony in B-flat, Op. 20 - 1. Lent - Allegro vivo", name);
    }

    [Fact]
    public void FullPipeline_ChaussonSymphonyMovement3()
    {
        var parsed = TagParser.ParseFilename(
            "10 Chausson, Ernest- Sym in B flat, Op 20 , 003 Anime- Tres anime.mp3");

        var work = TagParser.ExpandAbbreviations(parsed.RawWork!);
        work = TagParser.RestoreDiacritics(work);
        work = CataloguingRules.HyphenateKeys(work);
        work = TagParser.InsertFormDash(work);

        var mov = TagParser.ExpandAbbreviations(parsed.RawMovement!);
        mov = TagParser.RestoreDiacritics(mov);
        mov = TagParser.NormalizeTempoSeparators(mov);
        var movFormatted = CataloguingRules.PadMovementNumber(
            $"{parsed.MovementNumber}. {mov}", 3);

        var name = CataloguingRules.FormatTrackName(work, movFormatted);

        // "Très animé" lowercase — matching the raw "Tres anime"
        Assert.Equal("Symphony in B-flat, Op. 20 - 3. Anim\u00e9 - Tr\u00e8s anim\u00e9", name);
    }

    [Fact]
    public void FullPipeline_NamounaSuiteMovement()
    {
        var parsed = TagParser.ParseFilename(
            "02 Lalo, Edouard- Namouna Suite No 1 , 001 Prelude.mp3");

        var work = TagParser.ExpandAbbreviations(parsed.RawWork!);
        work = TagParser.RestoreDiacritics(work);
        work = CataloguingRules.HyphenateKeys(work);
        work = TagParser.InsertFormDash(work);

        var mov = TagParser.ExpandAbbreviations(parsed.RawMovement!);
        mov = TagParser.RestoreDiacritics(mov);
        mov = TagParser.NormalizeTempoSeparators(mov);
        var movFormatted = CataloguingRules.PadMovementNumber(
            $"{parsed.MovementNumber}. {mov}", 5);

        var name = CataloguingRules.FormatTrackName(work, movFormatted);

        Assert.Equal("Namouna\u2014Suite #1 - 1. Pr\u00e9lude", name);
    }

    [Fact]
    public void FullPipeline_StandaloneOverture()
    {
        var parsed = TagParser.ParseFilename(
            "01 Lalo, Edouard-  , Le Roi d'Ys Ov.mp3");

        Assert.Equal("Le Roi d'Ys Ov", parsed.RawWork);
        Assert.Null(parsed.RawMovement);

        var work = TagParser.ExpandAbbreviations(parsed.RawWork!);
        work = TagParser.RestoreDiacritics(work);
        work = CataloguingRules.HyphenateKeys(work);

        // Overture is a section word — split it off as an unnumbered section
        var (workTitle, section) = TagParser.SplitSectionWord(work);

        var name = CataloguingRules.FormatTrackName(workTitle, section);

        Assert.Equal("Le Roi d'Ys - Overture", name);
    }

    [Fact]
    public void FullPipeline_BarraudStandalone()
    {
        var parsed = TagParser.ParseFilename(
            "07 Barraud, Henry-  , Offrande a une ombre.mp3");

        Assert.Equal("Offrande a une ombre", parsed.RawWork);
        Assert.Null(parsed.RawMovement);

        var work = TagParser.ExpandAbbreviations(parsed.RawWork!);
        work = TagParser.RestoreDiacritics(work);

        var name = CataloguingRules.FormatTrackName(work);

        Assert.Equal("Offrande \u00e0 une ombre", name);
    }

    [Fact]
    public void ExpandAbbreviations_Idempotent_NoDoublePeriod()
    {
        // Already-expanded "Op." should not become "Op.."
        var once = TagParser.ExpandAbbreviations("Symphony in B flat, Op 20");
        Assert.Equal("Symphony in B flat, Op. 20", once);

        var twice = TagParser.ExpandAbbreviations(once);
        Assert.Equal("Symphony in B flat, Op. 20", twice);
    }
}
