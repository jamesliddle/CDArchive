using System.Text.RegularExpressions;

namespace CDArchive.Core.Services;

/// <summary>
/// Parses raw ID3 tag values and filenames into structured fields (composer, work title, movement)
/// and expands common abbreviations.
/// </summary>
public static class TagParser
{
    // "Composer, First: Work Title / Movement"  (common tag format from ripping software)
    private static readonly Regex ComposerWorkMovementRegex = new(
        @"^(?<composer>.+?):\s*(?<work>.*?)\s*/\s*(?<movement>.+)$",
        RegexOptions.Compiled);

    // "Composer, First: Work Title"  (single-movement work, same tag format)
    private static readonly Regex ComposerWorkRegex = new(
        @"^(?<composer>.+?):\s*(?<work>.+)$",
        RegexOptions.Compiled);

    // Filename leading track number
    private static readonly Regex FilenameTrackNumberRegex = new(
        @"^\d+\s+",
        RegexOptions.Compiled);

    // Work/movement separator in filenames: " , " (space before and after comma)
    // as opposed to regular commas in titles like "B flat, Op 20" (no space before comma)
    private const string FilenameSeparator = " , ";

    // Strip leading zero-padded movement number: "001 Lent: Allegro" → "Lent: Allegro"
    private static readonly Regex LeadingMovementNumberRegex = new(
        @"^0*(\d+)\s+(.+)$",
        RegexOptions.Compiled);

    // Colons used as tempo transition separators: "Lent: Allegro vivo" → "Lent - Allegro vivo"
    private static readonly Regex ColonAsSeparatorRegex = new(
        @"(?<=\w)\s*:\s*(?=[A-Z])",
        RegexOptions.Compiled);

    // Hyphens used as tempo transition separators in filenames: "Lent- Allegro vivo"
    private static readonly Regex HyphenAsSeparatorRegex = new(
        @"(?<=\w)\s*-\s+(?=[A-Z])",
        RegexOptions.Compiled);

    // "No" or "No." followed by a number → "#N" (no space)
    private static readonly Regex WorkNumberRegex = new(
        @"\bNo\.?\s*(\d+)",
        RegexOptions.Compiled);

    // "Op" not already followed by a period → "Op."
    private static readonly Regex OpRegex = new(
        @"\bOp\b(?!\.)",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sym"] = "Symphony",
        ["Symph"] = "Symphony",
        ["Conc"] = "Concerto",
        ["Ov"] = "Overture",
        ["Ovt"] = "Overture",
        ["Qt"] = "Quartet",
        ["Qnt"] = "Quintet",
        ["Str"] = "String",
        ["Ste"] = "Suite",
        ["Var"] = "Variations",
        ["Vars"] = "Variations",
        ["Mvt"] = "Movement",
        ["Pf"] = "Piano",
        ["Vn"] = "Violin",
        ["Vc"] = "Cello",
        ["Fl"] = "Flute",
        ["Ob"] = "Oboe",
        ["Cl"] = "Clarinet",
        ["Hn"] = "Horn",
    };

    // Diacritics dictionary stores the lowercase canonical form.
    // RestoreDiacritics matches the case pattern of the input.
    private static readonly Dictionary<string, string> CommonDiacritics = new(StringComparer.OrdinalIgnoreCase)
    {
        // French musical terms
        ["prelude"] = "prélude",
        ["tres"] = "très",
        ["serenade"] = "sérénade",
        ["fete"] = "fête",
        ["anime"] = "animé",
        ["theme varie"] = "thème varié",
        ["theme"] = "thème",
        ["varie"] = "varié",
        ["etude"] = "étude",
        ["elegie"] = "élégie",
        ["entree"] = "entrée",
        ["poeme"] = "poème",
        ["cortege"] = "cortège",

        // French titles
        ["offrande a une ombre"] = "offrande à une ombre",

        // Composer first names (common ASCII → proper)
        ["edouard"] = "édouard",
        ["cesar"] = "césar",
        ["frederic"] = "frédéric",
        ["bela"] = "béla",
        ["zoltan"] = "zoltán",
        ["antonin"] = "antonín",
        ["leos"] = "leoš",
        ["bedrich"] = "bedřich",
    };

    // Musical form words that can follow a proper name with an em-dash separator.
    private static readonly HashSet<string> FormWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Suite", "Concerto", "Symphony", "Symphonie", "Sonata", "Sonate",
        "Quartet", "Quintet", "Trio", "Overture", "Ouverture", "Serenade",
        "Variations", "Rhapsody", "Rhapsodie", "Fantasia", "Fantasie",
        "Mass", "Messe", "Requiem", "Cantata", "Cantate", "Oratorio",
        "Divertimento", "Nocturne", "Ballade", "Scherzo", "Polonaise",
        "March", "Marche",
    };

    // Form words that represent sections of a larger work (e.g., overture to an opera)
    // rather than the form of the work itself. These use spaced dash (work - section)
    // instead of em-dash (Name—Form).
    private static readonly HashSet<string> SectionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Overture", "Ouverture",
    };

    /// <summary>
    /// Parses a raw TIT2 tag value into structured fields.
    /// </summary>
    public static ParsedTag Parse(string rawTitle)
    {
        var result = new ParsedTag();

        // Try "Composer: Work / Movement" pattern
        var cwm = ComposerWorkMovementRegex.Match(rawTitle);
        if (cwm.Success)
        {
            result.RawComposer = cwm.Groups["composer"].Value.Trim();
            result.RawWork = cwm.Groups["work"].Value.Trim();
            result.RawMovement = cwm.Groups["movement"].Value.Trim();
            NormalizeMovement(result);
            PromoteStandaloneTitle(result);
            return result;
        }

        // Try "Composer: Work" pattern (no movement)
        var cw = ComposerWorkRegex.Match(rawTitle);
        if (cw.Success)
        {
            result.RawComposer = cw.Groups["composer"].Value.Trim();
            result.RawWork = cw.Groups["work"].Value.Trim();
            return result;
        }

        // Fallback: treat the whole thing as a work title
        result.RawWork = rawTitle.Trim();
        return result;
    }

    /// <summary>
    /// Parses metadata from the MP3 filename, which is more reliable than
    /// re-reading already-modified tags. Format:
    /// "NN Composer, First- Work , NNN Movement.mp3"
    /// The first "- " separates composer from the rest.
    /// The last " , " (space-comma-space) separates work from movement.
    /// Hyphens within movement text substitute for colons in tempo markings.
    /// </summary>
    public static ParsedTag ParseFilename(string filename)
    {
        var result = new ParsedTag();

        var name = Path.GetFileNameWithoutExtension(filename);

        // Strip leading track number
        name = FilenameTrackNumberRegex.Replace(name, "");
        if (string.IsNullOrEmpty(name))
        {
            result.RawWork = name;
            return result;
        }

        // Split composer from rest at first "- "
        var dashIdx = name.IndexOf("- ", StringComparison.Ordinal);
        if (dashIdx < 0)
        {
            result.RawWork = name.Trim();
            return result;
        }

        result.RawComposer = name[..dashIdx].Trim();
        var rest = name[(dashIdx + 1)..]; // keep leading spaces for separator matching

        // Split work from movement at last " , " (space-comma-space)
        var sepIdx = rest.LastIndexOf(FilenameSeparator, StringComparison.Ordinal);
        if (sepIdx >= 0)
        {
            result.RawWork = rest[..sepIdx].Trim();
            result.RawMovement = rest[(sepIdx + FilenameSeparator.Length)..].Trim();
        }
        else
        {
            result.RawWork = rest.Trim();
        }

        // Normalize movement: strip leading number, convert hyphen tempo separators
        NormalizeMovement(result);
        if (result.RawMovement != null)
        {
            result.RawMovement = HyphenAsSeparatorRegex.Replace(result.RawMovement, ": ");
        }

        PromoteStandaloneTitle(result);

        return result;
    }

    /// <summary>
    /// Parses a "Last, First" or "Last, First:" composer string into name parts.
    /// </summary>
    public static (string LastName, string FirstName) ParseComposerName(string raw)
    {
        var cleaned = raw.TrimEnd(':').Trim();
        var parts = cleaned.Split(',', 2);
        if (parts.Length == 2)
            return (parts[0].Trim(), parts[1].Trim());
        return (cleaned, "");
    }

    public static string ExpandAbbreviations(string text)
    {
        // First handle "No/No." + number → "#N"
        var result = WorkNumberRegex.Replace(text, m => $"#{m.Groups[1].Value}");

        // Handle "Op" → "Op." only when not already followed by a period
        result = OpRegex.Replace(result, "Op.");

        // Then expand remaining abbreviations as whole words
        result = Regex.Replace(result, @"\b(\w+)\b", match =>
        {
            return Abbreviations.TryGetValue(match.Value, out var expanded)
                ? expanded
                : match.Value;
        });

        return result;
    }

    /// <summary>
    /// Restores diacritics on known musical terms and composer names.
    /// Preserves the case pattern of the input: if the input word is capitalized,
    /// the replacement is capitalized; if lowercase, the replacement is lowercase.
    /// </summary>
    public static string RestoreDiacritics(string text)
    {
        var result = text;

        // Multi-word phrases first (longest first)
        foreach (var kvp in CommonDiacritics.Where(k => k.Key.Contains(' ')).OrderByDescending(k => k.Key.Length))
        {
            result = Regex.Replace(result, Regex.Escape(kvp.Key), m => MatchCase(m.Value, kvp.Value),
                RegexOptions.IgnoreCase);
        }

        // Single words — replace whole words only
        foreach (var kvp in CommonDiacritics.Where(k => !k.Key.Contains(' ')))
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(kvp.Key)}\b", m => MatchCase(m.Value, kvp.Value),
                RegexOptions.IgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Inserts an em-dash between a proper name and a following musical form word.
    /// "Namouna Suite #1" → "Namouna—Suite #1".
    /// Only applies when the title starts with a non-form word followed by a form word.
    /// </summary>
    public static string InsertFormDash(string workTitle)
    {
        var words = workTitle.Split(' ');
        if (words.Length < 2)
            return workTitle;

        if (FormWords.Contains(words[0]))
            return workTitle;

        for (int i = 1; i < words.Length; i++)
        {
            if (FormWords.Contains(words[i]))
            {
                var properName = string.Join(' ', words.Take(i));
                var formPart = string.Join(' ', words.Skip(i));
                return $"{properName}\u2014{formPart}";
            }
        }

        return workTitle;
    }

    /// <summary>
    /// Splits a section word (e.g., Overture) off a work title into a separate section.
    /// "Le Roi d'Ys Overture" → ("Le Roi d'Ys", "Overture").
    /// Returns null section if no section word is found or the title starts with a form word.
    /// Section words represent parts of a larger work (overture to an opera) and use the
    /// work-movement separator (spaced dash) rather than em-dash.
    /// </summary>
    public static (string Work, string? Section) SplitSectionWord(string workTitle)
    {
        var words = workTitle.Split(' ');
        if (words.Length < 2)
            return (workTitle, null);

        if (FormWords.Contains(words[0]))
            return (workTitle, null);

        for (int i = 1; i < words.Length; i++)
        {
            if (SectionWords.Contains(words[i]))
            {
                var properName = string.Join(' ', words.Take(i));
                var sectionPart = string.Join(' ', words.Skip(i));
                return (properName, sectionPart);
            }
        }

        return (workTitle, null);
    }

    /// <summary>
    /// Converts colon-separated tempo transitions to dash-separated.
    /// "Lent: Allegro vivo" → "Lent - Allegro vivo"
    /// </summary>
    public static string NormalizeTempoSeparators(string text)
    {
        return ColonAsSeparatorRegex.Replace(text, " - ");
    }

    /// <summary>
    /// When work is empty and movement has no number, the "movement" is really
    /// a standalone work title (e.g., "Composer: / Offrande a une ombre").
    /// Promote it to RawWork.
    /// </summary>
    private static void PromoteStandaloneTitle(ParsedTag result)
    {
        if (string.IsNullOrEmpty(result.RawWork) && result.MovementNumber == null && result.RawMovement != null)
        {
            result.RawWork = result.RawMovement;
            result.RawMovement = null;
        }
    }

    private static string MatchCase(string original, string replacement)
    {
        if (original.Length == 0)
            return replacement;

        if (original == original.ToUpperInvariant())
            return replacement.ToUpperInvariant();

        if (original == original.ToLowerInvariant())
            return replacement.ToLowerInvariant();

        if (char.IsUpper(original[0]))
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];

        return replacement;
    }

    private static void NormalizeMovement(ParsedTag result)
    {
        if (result.RawMovement == null)
            return;

        var numMatch = LeadingMovementNumberRegex.Match(result.RawMovement);
        if (numMatch.Success)
        {
            result.MovementNumber = int.Parse(numMatch.Groups[1].Value);
            result.RawMovement = numMatch.Groups[2].Value.Trim();
        }
    }

    public class ParsedTag
    {
        public string? RawComposer { get; set; }
        public string? RawWork { get; set; }
        public string? RawMovement { get; set; }
        public int? MovementNumber { get; set; }
    }
}
