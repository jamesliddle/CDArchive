using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CDArchive.Core.Models;

/// <summary>
/// A piece entry in the Classical Canon reference data.
/// Represents a hierarchical work structure: pieces can contain subpieces
/// (movements, sections, numbers within a set, etc.).
/// </summary>
public class CanonPiece
{
    [JsonPropertyName("composer")]
    public string? Composer { get; set; }

    /// <summary>
    /// All credited composers, including the principal (no role) and contributors (with role).
    /// When null the piece has a single composer given by <see cref="Composer"/>.
    /// </summary>
    [JsonPropertyName("composers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ComposerCredit>? Composers { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("title_English")]
    public string? TitleEnglish { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("form")]
    public string? Form { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("key_tonality")]
    public string? KeyTonality { get; set; }

    [JsonPropertyName("key_mode")]
    public string? KeyMode { get; set; }

    [JsonPropertyName("catalog_info")]
    public List<CatalogInfo>? CatalogInfo { get; set; }

    [JsonPropertyName("instrumentation")]
    public JsonElement? Instrumentation { get; set; }

    [JsonPropertyName("instrumentation_category")]
    public string? InstrumentationCategory { get; set; }

    /// <summary>
    /// Explicit override controlling whether subpieces display a sequence number.
    /// When null the default applies: Opera → unnumbered, everything else → numbered.
    /// Serialised only when set, so the JSON stays clean for the common case.
    /// </summary>
    [JsonPropertyName("numbered_subpieces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NumberedSubpieces { get; set; }

    /// <summary>
    /// The number assigned to the first subpiece when subpieces are numbered.
    /// Defaults to 1 when null. Set e.g. to 0 for zero-based numbering.
    /// Serialised only when non-default.
    /// </summary>
    [JsonPropertyName("subpieces_start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SubpiecesStart { get; set; }

    /// <summary>
    /// Optional visible number for this subpiece within its parent, independent of the
    /// ordering <see cref="Number"/>.  Used for traditional opera numbering (No. 1, 2 …)
    /// where some items (arias, ensembles, finales) carry a number and others
    /// (recitatives, interludes) do not.  When set, this value is always displayed
    /// regardless of the parent's <c>numbered_subpieces</c> flag.
    /// </summary>
    [JsonPropertyName("music_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MusicNumber { get; set; }

    [JsonPropertyName("publication_year")]
    public int? PublicationYear { get; set; }

    [JsonPropertyName("composition_years")]
    public JsonElement? CompositionYears { get; set; }

    [JsonPropertyName("text_author")]
    public JsonElement? TextAuthor { get; set; }

    [JsonPropertyName("subpieces")]
    public List<CanonPiece>? Subpieces { get; set; }

    [JsonPropertyName("versions")]
    public List<CanonPieceVersion>? Versions { get; set; }

    [JsonPropertyName("arrangements")]
    public JsonElement? Arrangements { get; set; }

    [JsonPropertyName("roles")]
    public JsonElement? Roles { get; set; }

    [JsonPropertyName("tempos")]
    public List<TempoInfo>? Tempos { get; set; }

    [JsonPropertyName("first_line")]
    public string? FirstLine { get; set; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }

    [JsonPropertyName("variants")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VariantInfo>? Variants { get; set; }

    [JsonPropertyName("cadenza")]
    public JsonElement? Cadenza { get; set; }

    [JsonPropertyName("title_number")]
    public JsonElement? TitleNumber { get; set; }

    // Number words for set titles: "Two Piano Trios", "Six String Quartets", etc.
    private static readonly string[] NumberWords =
        ["", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine",
         "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen",
         "Seventeen", "Eighteen", "Nineteen", "Twenty", "Twenty-One", "Twenty-Two",
         "Twenty-Three", "Twenty-Four"];

    /// <summary>
    /// Full display title including catalogue info, for tooltips and general use.
    /// </summary>
    [JsonIgnore]
    public string DisplayTitle => BuildDisplayTitle(includeCatalog: true);

    /// <summary>
    /// Display title without catalogue info, for use in the pieces list
    /// where the catalogue is shown in its own column.
    /// </summary>
    [JsonIgnore]
    public string DisplayTitleShort => BuildDisplayTitle(includeCatalog: false);

    /// <summary>
    /// Display title for use when rendered as a subpiece.
    /// Sub-works (those with their own subpieces) use the top-level "Form #N" format;
    /// leaf movements use the "N. Form / N. Tempo" format.
    /// Leaf movements also append their assigned roles, e.g. " (Florestan, Leonore)".
    /// </summary>
    [JsonIgnore]
    public string SubpieceDisplayTitle => BuildDisplayTitle(includeCatalog: false, isSubpiece: true) + RolesSuffix;

    /// <summary>
    /// Roles suffix for leaf-subpiece display: " (Role1, Role2)".
    /// Returns an empty string for pieces that have sub-movements of their own,
    /// and for any piece whose roles are stored as full objects rather than
    /// name-only string references.
    /// </summary>
    [JsonIgnore]
    private string RolesSuffix
    {
        get
        {
            if (HasSubpieces) return "";
            if (Roles?.ValueKind != JsonValueKind.Array) return "";
            var roles = Roles.Value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
            return roles.Count > 0 ? $" ({string.Join(", ", roles)})" : "";
        }
    }

    /// <summary>
    /// Multi-line tooltip text. For movements with multiple tempo indications,
    /// each tempo is shown on its own line for readability.
    /// </summary>
    [JsonIgnore]
    public string ToolTipText
    {
        get
        {
            if (Tempos is { Count: > 1 })
            {
                var descriptions = CollectTempoDescriptions(Tempos);
                var prefix = Number.HasValue ? $"{Number}. " : "";
                var formPart = !string.IsNullOrEmpty(Form) ? $"{TitleCase(Form)}. " : "";
                var firstLine = $"{prefix}{formPart}{descriptions[0]}";
                var lines = new List<string> { firstLine };
                for (int i = 1; i < descriptions.Count; i++)
                    lines.Add($"  {descriptions[i]}");
                return string.Join("\n", lines);
            }
            return DisplayTitle;
        }
    }

    private string BuildDisplayTitle(bool includeCatalog, bool isSubpiece = false, bool showNumber = true)
    {
        // If there's an explicit title, use it (append key, optionally catalog, nickname)
        if (!string.IsNullOrEmpty(Title))
        {
            var titleNumPrefix = isSubpiece ? SubpiecePrefix(showNumber) : "";
            var parts = new List<string> { Title };
            if (!string.IsNullOrEmpty(Key))
                parts[0] += $" in {Key}";
            if (includeCatalog && !string.IsNullOrEmpty(Catalog))
                parts.Add(Catalog);
            var titleResult = titleNumPrefix + string.Join(", ", parts);
            if (!string.IsNullOrEmpty(Subtitle))
                titleResult += $", {Subtitle}";
            if (!string.IsNullOrEmpty(Nickname))
                titleResult += $" \"{Nickname}\"";
            return titleResult;
        }
        // Sets: derive from subpieces, e.g., "Three Piano Sonatas"
        if (string.Equals(Form, "set", StringComparison.OrdinalIgnoreCase)
            && Subpieces is { Count: > 0 })
        {
            return DeriveSetTitle(includeCatalog);
        }

        // Movement-level: combine number, form and tempos
        // e.g., "3. Scherzo. Allegro assai" or "1. Lent - Allegro vivo"
        var tempo = TempoDescription;
        if (!string.IsNullOrEmpty(tempo))
        {
            var prefix = isSubpiece ? SubpiecePrefix(showNumber)
                       : Number.HasValue ? $"{Number}. " : "";
            if (!string.IsNullOrEmpty(Form))
                return $"{prefix}{TitleCase(Form)}. {tempo}";
            return $"{prefix}{tempo}";
        }

        // Vocal number with a first line but no tempo indication
        // e.g., "1. Duet. Jetzt, Schätzchen, jetzt sind wir allein"
        if (!string.IsNullOrEmpty(FirstLine))
        {
            var prefix = isSubpiece ? SubpiecePrefix(showNumber)
                       : Number.HasValue ? $"{Number}. " : "";
            if (!string.IsNullOrEmpty(Form))
                return $"{prefix}{TitleCase(Form)}. {FirstLine}";
            return $"{prefix}{FirstLine}";
        }

        // Derive a title from form + number + key + optionally catalog.
        // Top-level pieces and sub-works (subpieces that have their own movements):
        //   "Form #N [in Key]"  (e.g. "Piano Sonata #1 in f", "String Quartet #13")
        // Leaf movements:
        //   "N. Form [in Key]"  (e.g. "6. March")
        var isSubWork = isSubpiece && HasSubpieces;
        var mainPart = "";

        if (!string.IsNullOrEmpty(Form))
        {
            mainPart = TitleCase(Form);
            if (Number.HasValue && (!isSubpiece || isSubWork))
                mainPart += $" #{Number}";
        }

        if (!string.IsNullOrEmpty(Key))
            mainPart += $" in {Key}";

        mainPart = mainPart.Trim();

        var suffixes = new List<string>();
        if (!string.IsNullOrEmpty(mainPart))
            suffixes.Add(mainPart);

        if (includeCatalog && !string.IsNullOrEmpty(Catalog))
            suffixes.Add(Catalog);

        var body      = suffixes.Count > 0 ? string.Join(", ", suffixes) : "";
        var numPrefix = isSubpiece && !isSubWork ? SubpiecePrefix(showNumber) : "";
        var result    = body.Length > 0 ? $"{numPrefix}{body}"
                      : !string.IsNullOrEmpty(MusicNumber) ? MusicNumber
                      : Number.HasValue ? $"{Number}" : "(untitled)";

        if (!string.IsNullOrEmpty(Subtitle))
            result += $", {Subtitle}";
        if (!string.IsNullOrEmpty(Nickname))
            result += $" \"{Nickname}\"";

        return result;
    }

    private string DeriveSetTitle(bool includeCatalog)
    {
        var count = Subpieces!.Count;
        var subForm = Subpieces
            .Select(s => s.Form)
            .FirstOrDefault(f => !string.IsNullOrEmpty(f));

        var parts = new List<string>();

        // "Three Piano Sonatas" or "24 Pieces"
        var countStr = count < NumberWords.Length ? NumberWords[count] : count.ToString();
        if (!string.IsNullOrEmpty(subForm))
        {
            var plural = Pluralize(TitleCase(subForm));
            parts.Add($"{countStr} {plural}");
        }
        else
        {
            parts.Add($"{countStr} Pieces");
        }

        if (includeCatalog && !string.IsNullOrEmpty(Catalog))
            parts.Add(Catalog);

        var result = string.Join(", ", parts);

        if (!string.IsNullOrEmpty(Subtitle))
            result += $" {Subtitle}";
        if (!string.IsNullOrEmpty(Nickname))
            result += $" \"{Nickname}\"";

        return result;
    }

    private static string TitleCase(string value) => CanonFormat.TitleCase(value);

    /// <summary>
    /// Display title for use when this piece is rendered as a subpiece,
    /// with explicit control over whether the sequence number is prepended.
    /// </summary>
    public string BuildSubpieceTitle(bool showNumber) =>
        BuildDisplayTitle(includeCatalog: false, isSubpiece: true, showNumber: showNumber) + RolesSuffix;

    /// <summary>
    /// Reorders this piece's <see cref="CatalogInfo"/> so that entries whose
    /// <see cref="CatalogInfo.Catalog"/> prefix appears in <paramref name="preferredPrefixes"/>
    /// are placed first, in the preference order. Unmatched entries keep
    /// their original relative order after the preferred ones.
    /// Recurses into subpieces and versions so the composer's preference
    /// applies consistently throughout the piece tree.
    /// </summary>
    /// <param name="preferredPrefixes">
    /// Ordered list of catalog prefixes (e.g. ["Op.", "B."]). Null or empty
    /// is a no-op, leaving the piece's catalog order untouched.
    /// </param>
    public void SortCatalogInfoByPreference(IReadOnlyList<string>? preferredPrefixes)
    {
        if (preferredPrefixes is null || preferredPrefixes.Count == 0) return;

        SortCatalogInfoList(CatalogInfo, preferredPrefixes);

        if (Subpieces is not null)
            foreach (var sub in Subpieces)
                sub.SortCatalogInfoByPreference(preferredPrefixes);

        // CanonPieceVersion doesn't host its own SortCatalogInfoByPreference method —
        // versions can't nest further versions, so we just sort their catalog list
        // and recurse into their subpieces here.
        if (Versions is not null)
        {
            foreach (var ver in Versions)
            {
                SortCatalogInfoList(ver.CatalogInfo, preferredPrefixes);
                if (ver.Subpieces is not null)
                    foreach (var sub in ver.Subpieces)
                        sub.SortCatalogInfoByPreference(preferredPrefixes);
            }
        }
    }

    private static void SortCatalogInfoList(
        List<CatalogInfo>? list, IReadOnlyList<string> preferredPrefixes)
    {
        if (list is null || list.Count <= 1) return;

        // Stable partition: matched entries ordered by preference index, then
        // unmatched entries in original order. Unknown prefixes get rank
        // int.MaxValue, which keeps them at the end.
        int RankOf(CatalogInfo ci)
        {
            for (var i = 0; i < preferredPrefixes.Count; i++)
            {
                if (string.Equals(preferredPrefixes[i], ci.Catalog,
                                  StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return int.MaxValue;
        }

        // Pair each entry with its original index so OrderBy stability
        // preserves the relative order of unmatched entries.
        var reordered = list
            .Select((ci, originalIndex) => (ci, rank: RankOf(ci), originalIndex))
            .OrderBy(t => t.rank)
            .ThenBy(t => t.originalIndex)
            .Select(t => t.ci)
            .ToList();

        list.Clear();
        list.AddRange(reordered);
    }

    /// <summary>
    /// Computes the numeric prefix string for subpiece display.
    /// Priority: MusicNumber (always shown when set) → ordering Number (shown when
    /// <paramref name="showNumber"/> is true) → empty string.
    /// </summary>
    private string SubpiecePrefix(bool showNumber) =>
        !string.IsNullOrEmpty(MusicNumber) ? $"{MusicNumber}. " :
        showNumber && Number.HasValue    ? $"{Number}. "      : "";

    /// <summary>
    /// Simple English pluralization for musical forms.
    /// </summary>
    private static string Pluralize(string form)
    {
        if (string.IsNullOrEmpty(form)) return form;
        if (form.EndsWith("o", StringComparison.Ordinal))
            return form + "s";  // Trio → Trios, Concerto → Concertos
        if (form.EndsWith("y", StringComparison.Ordinal)
            && form.Length > 1 && !"aeiou".Contains(form[^2]))
            return form[..^1] + "ies"; // Rhapsody → Rhapsodies
        if (form.EndsWith("s", StringComparison.Ordinal)
            || form.EndsWith("x", StringComparison.Ordinal))
            return form + "es";
        return form + "s";
    }

    /// <summary>
    /// A one-line summary for list display.
    /// </summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(Form))
            {
                var formStr = Form;
                if (Number.HasValue)
                    formStr += $" #{Number}";
                parts.Add(formStr);
            }

            if (!string.IsNullOrEmpty(Title))
                parts.Add(Title);

            if (!string.IsNullOrEmpty(Key))
                parts.Add($"in {Key}");

            if (!string.IsNullOrEmpty(Catalog))
                parts.Add(Catalog);

            var result = parts.Count > 0 ? string.Join(", ", parts) : "(untitled)";

            if (!string.IsNullOrEmpty(Subtitle))
                result += $", {Subtitle}";
            if (!string.IsNullOrEmpty(Nickname))
                result += $" \"{Nickname}\"";

            return result;
        }
    }

    /// <summary>
    /// Formats the key: uppercase for major (e.g., "E-flat"), lowercase for minor (e.g., "f").
    /// No mode word is included — case indicates the mode.
    /// </summary>
    [JsonIgnore]
    public string Key
    {
        get
        {
            if (string.IsNullOrEmpty(KeyTonality))
                return "";
            if (string.Equals(KeyMode, "minor", StringComparison.OrdinalIgnoreCase))
                return KeyTonality.ToLowerInvariant();
            return KeyTonality;
        }
    }

    /// <summary>
    /// Formats catalog info. Shows "Op. 2 #1" when both number and subnumber are present,
    /// "Op. 2" for number only, "#1" for subnumber only.
    /// </summary>
    [JsonIgnore]
    public string Catalog
    {
        get
        {
            if (CatalogInfo == null || CatalogInfo.Count == 0)
                return "";
            var first = CatalogInfo[0];
            if (!string.IsNullOrEmpty(first.CatalogNumber) && !string.IsNullOrEmpty(first.CatalogSubnumber))
                return $"{first.Catalog} {first.CatalogNumber} #{first.CatalogSubnumber}".Trim();
            if (!string.IsNullOrEmpty(first.CatalogNumber))
                return $"{first.Catalog} {first.CatalogNumber}".Trim();
            if (!string.IsNullOrEmpty(first.CatalogSubnumber))
                return $"{first.Catalog} #{first.CatalogSubnumber}".Trim();
            return first.Catalog.Trim();
        }
    }

    /// <summary>
    /// The catalog prefix for sorting (e.g., "Op.", "WoO").
    /// Pieces without catalog info sort last.
    /// </summary>
    [JsonIgnore]
    public string CatalogSortPrefix =>
        CatalogInfo is { Count: > 0 } ? CatalogInfo[0].Catalog.Trim() : "\uFFFF";

    /// <summary>
    /// Numeric catalog sort key for proper ordering (e.g., Op. 2 before Op. 10).
    /// Extracts the leading digits from entries like "121a" so they sort as 121.
    /// </summary>
    [JsonIgnore]
    public int CatalogSortNumber
    {
        get
        {
            if (CatalogInfo == null || CatalogInfo.Count == 0)
                return int.MaxValue;
            var num = CatalogInfo[0].CatalogNumber ?? CatalogInfo[0].CatalogSubnumber ?? "";
            var digits = new string(num.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : int.MaxValue;
        }
    }

    /// <summary>
    /// Trailing suffix after the catalog number for sub-sorting (e.g., "a" in "121a").
    /// </summary>
    [JsonIgnore]
    public string CatalogSortSuffix
    {
        get
        {
            if (CatalogInfo == null || CatalogInfo.Count == 0)
                return "";
            var num = CatalogInfo[0].CatalogNumber ?? CatalogInfo[0].CatalogSubnumber ?? "";
            return new string(num.SkipWhile(char.IsDigit).ToArray());
        }
    }

    /// <summary>
    /// Title-cased instrumentation category, e.g., "Chamber", "Piano", "Orchestra".
    /// </summary>
    [JsonIgnore]
    public string Category => TitleCase(InstrumentationCategory ?? "");

    /// <summary>
    /// Whether this piece has subpieces (movements, sections, etc.).
    /// </summary>
    [JsonIgnore]
    public bool HasSubpieces => Subpieces != null && Subpieces.Count > 0;

    /// <summary>
    /// Whether subpieces of this piece should display a sequence number.
    /// Respects the explicit <see cref="NumberedSubpieces"/> override; falls back to
    /// the category-based default: a piece with a known non-Opera category → true,
    /// Opera category or no category (intermediate structural nodes like acts/scenes) → false.
    /// </summary>
    [JsonIgnore]
    public bool EffectiveSubpiecesNumbered =>
        NumberedSubpieces ?? (!string.IsNullOrEmpty(InstrumentationCategory) &&
            !string.Equals(InstrumentationCategory, "Opera",
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Children to display in the tree view.
    /// When the piece has versions, returns an "Original" node followed by one node per version,
    /// so the tree shows piece → Original / version-description → movement.
    /// Otherwise falls back to direct subpieces (piece → movement).
    /// </summary>
    [JsonIgnore]
    public System.Collections.IList? TreeChildren
    {
        get
        {
            var showNums = EffectiveSubpiecesNumbered;
            if (Versions is { Count: > 0 })
            {
                var nodes = new List<object> { new PieceOriginalNode(this) };
                nodes.AddRange(Versions.Select(v => new VersionDisplayNode(v, showNums, this)));
                return nodes;
            }
            return HasSubpieces
                ? Subpieces!.Select(sp => new SubpieceDisplayNode(sp, showNums, this)).ToList()
                : null;
        }
    }

    /// <summary>
    /// Whether this piece has any tree children (versions with subpieces, or direct subpieces).
    /// </summary>
    [JsonIgnore]
    public bool HasTreeChildren => TreeChildren != null;

    // Keep old method names as wrappers for backward compatibility
    public string FormatKey() => Key;
    public string FormatCatalog() => Catalog;

    /// <summary>
    /// Readable summary of the instrumentation, flattening nested structures.
    /// e.g., "piano, violin, cello" or "piano, clarinet in B♭, horn in E♭, bassoon"
    /// </summary>
    [JsonIgnore]
    public string InstrumentationSummary
    {
        get
        {
            if (Instrumentation == null || Instrumentation.Value.ValueKind != JsonValueKind.Array)
                return "";
            var parts = new List<string>();
            foreach (var item in Instrumentation.Value.EnumerateArray())
                parts.Add(DescribeInstrument(item));
            return string.Join(", ", parts);
        }
    }

    private static string DescribeInstrument(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? "";

        if (el.ValueKind != JsonValueKind.Object)
            return el.ToString();

        // Orchestra grouping: {"orchestra": [...]}
        if (el.TryGetProperty("orchestra", out var orch) && orch.ValueKind == JsonValueKind.Array)
        {
            var members = new List<string>();
            foreach (var m in orch.EnumerateArray())
                members.Add(DescribeInstrument(m));
            return $"orchestra ({string.Join(", ", members)})";
        }

        // Section: {"section": "violin", "number": 1}
        if (el.TryGetProperty("section", out var sect))
        {
            var s = sect.GetString() ?? "";
            if (el.TryGetProperty("number", out var sn))
                return $"{s} {sn.GetInt32()}";
            return s;
        }

        // Simple instrument with optional key/number: {"instrument": "clarinet", "key": "B-flat"}
        if (el.TryGetProperty("instrument", out var inst))
        {
            string name;
            if (inst.ValueKind == JsonValueKind.String)
                name = inst.GetString() ?? "";
            else if (inst.ValueKind == JsonValueKind.Object)
                name = DescribeInstrument(inst);
            else
                name = inst.ToString();

            if (el.TryGetProperty("key", out var key))
                name += $" in {key.GetString()}";
            if (el.TryGetProperty("number", out var num))
                name += $" {num.GetInt32()}";

            // Alternate instrument
            if (el.TryGetProperty("alternate_instrument", out var alt))
            {
                var altName = alt.ValueKind == JsonValueKind.String
                    ? alt.GetString() ?? ""
                    : DescribeInstrument(alt);
                name += $" (or {altName})";
            }

            return name;
        }

        return el.ToString();
    }

    /// <summary>
    /// Sets the Instrumentation from a comma-separated string of instrument names.
    /// Each item becomes a simple string entry in the JSON array.
    /// </summary>
    public void SetInstrumentationFromList(string commaSeparated)
    {
        if (string.IsNullOrWhiteSpace(commaSeparated))
        {
            Instrumentation = null;
            return;
        }

        var items = commaSeparated
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (items.Count == 0)
        {
            Instrumentation = null;
            return;
        }

        var json = JsonSerializer.Serialize(items);
        Instrumentation = JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Returns true if the instrumentation contains complex entries (objects, not just strings).
    /// </summary>
    [JsonIgnore]
    public bool HasComplexInstrumentation
    {
        get
        {
            if (Instrumentation == null || Instrumentation.Value.ValueKind != JsonValueKind.Array)
                return false;
            return Instrumentation.Value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String);
        }
    }

    /// <summary>
    /// Formats the tempo descriptions for a movement, flattening nested tempos.
    /// Multiple tempos are separated by " - ".
    /// </summary>
    [JsonIgnore]
    public string TempoDescription
    {
        get
        {
            if (Tempos == null || Tempos.Count == 0) return "";
            var descriptions = CollectTempoDescriptions(Tempos);
            return string.Join(" - ", descriptions);
        }
    }

    /// <summary>
    /// Recursively collects tempo descriptions from potentially nested tempo structures.
    /// </summary>
    private static List<string> CollectTempoDescriptions(List<TempoInfo> tempos)
    {
        var result = new List<string>();
        foreach (var t in tempos.OrderBy(t => t.Number))
        {
            if (!string.IsNullOrEmpty(t.Description))
                result.Add(t.Description);
            else if (t.SubTempos is { Count: > 0 })
                result.AddRange(CollectTempoDescriptions(t.SubTempos));
        }
        return result;
    }

    public override string ToString() => Summary;
}

public class ComposerCredit
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Creative role, e.g. "arr.", "orch.", "transcr.", "compl."  Null = principal composer.</summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrEmpty(Role) ? Name : $"{Name} ({Role})";
}

public class CatalogInfo
{
    [JsonPropertyName("catalog")]
    public string Catalog { get; set; } = "";

    [JsonPropertyName("catalog_number")]
    public string? CatalogNumber { get; set; }

    [JsonPropertyName("catalog_subnumber")]
    public string? CatalogSubnumber { get; set; }
}

public class TempoInfo
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("tempo_description")]
    public string? Description { get; set; }

    [JsonPropertyName("tempos")]
    public List<TempoInfo>? SubTempos { get; set; }
}

/// <summary>
/// A role in a vocal work (opera, oratorio, song cycle, etc.),
/// with a character name, voice type, and optional description.
/// </summary>
public class RoleEntry
{
    /// <summary>Character name, e.g. "Florestan", "Chorus of prisoners".</summary>
    public string Name { get; set; } = "";

    /// <summary>Voice type, e.g. "tenor", "soprano", "mixed chorus".</summary>
    public string? VoiceType { get; set; }

    /// <summary>Brief character description, e.g. "A prisoner".</summary>
    public string? Description { get; set; }

    /// <summary>Display label for list views, e.g. "Florestan — tenor (A prisoner)".</summary>
    public string DisplayLabel
    {
        get
        {
            var label = Name;
            if (!string.IsNullOrEmpty(VoiceType))
                label += $" — {CanonFormat.TitleCase(VoiceType)}";
            if (!string.IsNullOrEmpty(Description))
                label += $" ({Description})";
            return label;
        }
    }

    /// <summary>
    /// Parses a roles JSON array into a typed list.
    /// Supports two element shapes:
    /// - Objects: <c>{"name": "Florestan", "voice_type": "Tenor", "description": "..."}</c>
    ///   — used at the top-level piece to define the full cast.
    /// - Strings: <c>"Florestan"</c> — used on subpieces to reference characters by name only.
    /// </summary>
    public static List<RoleEntry> ParseRoles(JsonElement el)
    {
        var result = new List<RoleEntry>();
        if (el.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var name = item.GetString();
                if (!string.IsNullOrEmpty(name))
                    result.Add(new RoleEntry { Name = name });
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;
            var entry = new RoleEntry();
            if (item.TryGetProperty("name", out var nameProp))
                entry.Name = nameProp.GetString() ?? "";
            if (item.TryGetProperty("voice_type", out var vt))
                entry.VoiceType = vt.GetString();
            if (item.TryGetProperty("description", out var desc))
                entry.Description = desc.GetString();
            if (!string.IsNullOrEmpty(entry.Name))
                result.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// Serializes a list of RoleEntry objects to a JSON array element.
    /// Returns null if the list is empty.
    /// Entries with only a name (no voice type or description) are written as plain
    /// strings, matching the subpiece reference format. Entries with additional detail
    /// are written as objects.
    /// </summary>
    public static JsonElement? SerializeRoles(IList<RoleEntry> roles)
    {
        if (roles.Count == 0) return null;
        var array = new JsonArray();
        foreach (var r in roles)
        {
            if (string.IsNullOrEmpty(r.VoiceType) && string.IsNullOrEmpty(r.Description))
            {
                // Name-only reference — serialize as a plain string (subpiece format).
                array.Add(JsonValue.Create(r.Name));
            }
            else
            {
                var obj = new JsonObject { ["name"] = r.Name };
                if (!string.IsNullOrEmpty(r.VoiceType))   obj["voice_type"]  = r.VoiceType;
                if (!string.IsNullOrEmpty(r.Description))  obj["description"] = r.Description;
                array.Add(obj);
            }
        }
        return JsonDocument.Parse(array.ToJsonString()).RootElement.Clone();
    }

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// A known variant of a piece, subpiece, or version —
/// e.g., a particular manuscript source, a different ending, or a disputed reading.
/// </summary>
public class VariantInfo
{
    /// <summary>Short label shown in the list, e.g. "Autograph ending".</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Optional extended description, edited in a modal window.</summary>
    [JsonPropertyName("long_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LongDescription { get; set; }

    public override string ToString() => Description;
}

/// <summary>
/// Defines an ensemble template in the pick lists.
/// Fixed ensembles (e.g. String Quartet) have a canonical member list.
/// Variable ensembles (e.g. Chamber Orchestra) have no predefined members;
/// the actual membership is specified per piece.
/// </summary>
public class EnsembleDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("members")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Members { get; set; }

    /// <summary>True if this ensemble has a fixed, canonical membership.</summary>
    [JsonIgnore]
    public bool IsFixed => Members is { Count: > 0 };

    public override string ToString() => Name;
}

/// <summary>
/// A parsed representation of one entry in a piece's instrumentation list.
/// Supports simple instrument names, part numbering (e.g. "violin 1"), transposition keys,
/// alternate instruments, and ensemble references.
/// </summary>
public class InstrumentEntry
{
    /// <summary>Instrument name, e.g. "clarinet", "violin".</summary>
    public string Instrument { get; set; } = "";

    /// <summary>
    /// Part number within the ensemble, e.g. 1 for "oboe 1" or 2 for "violin 2".
    /// Null when only one of this instrument is present.
    /// </summary>
    public int? PartNumber { get; set; }

    /// <summary>Transposition key, e.g. "B-flat", "C".</summary>
    public string? Key { get; set; }

    /// <summary>Alternate instrument the player may double on, e.g. "english horn".</summary>
    public string? Alternate { get; set; }

    /// <summary>True if this entry represents an ensemble rather than a single instrument.</summary>
    public bool IsEnsemble { get; set; }

    /// <summary>
    /// Per-piece member list for variable ensembles.
    /// Null for fixed ensembles (membership comes from the pick list definition)
    /// and for single instruments.
    /// </summary>
    public List<InstrumentEntry>? Members { get; set; }

    /// <summary>Display label, e.g. "Clarinet in B-flat 1" or "Violin (or Viola)".</summary>
    public string DisplayLabel
    {
        get
        {
            if (IsEnsemble)
            {
                var label = CanonFormat.TitleCase(Instrument);
                if (Members is { Count: > 0 })
                    label += ": " + string.Join(", ", Members.Select(m => m.DisplayLabel));
                return label;
            }

            var name = CanonFormat.TitleCase(Instrument);
            if (!string.IsNullOrEmpty(Key))       name += $" in {CanonFormat.TitleCase(Key)}";
            if (PartNumber.HasValue)              name += $" {PartNumber}";
            if (!string.IsNullOrEmpty(Alternate)) name += $" (or {CanonFormat.TitleCase(Alternate)})";
            return name;
        }
    }

    /// <summary>
    /// Parses a JSON instrumentation array into a flat list of InstrumentEntry objects.
    /// Orchestra groupings are flattened into individual entries.
    /// </summary>
    public static List<InstrumentEntry> ParseInstrumentation(JsonElement el)
    {
        var result = new List<InstrumentEntry>();
        if (el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                ParseItem(item, result);
        return result;
    }

    private static void ParseItem(JsonElement el, List<InstrumentEntry> result)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            result.Add(new InstrumentEntry { Instrument = el.GetString() ?? "" });
            return;
        }
        if (el.ValueKind != JsonValueKind.Object) return;

        // Ensemble: { "ensemble": "String Quartet" } or { "ensemble": "Chamber Orchestra", "members": [...] }
        if (el.TryGetProperty("ensemble", out var ens) && ens.ValueKind == JsonValueKind.String)
        {
            var entry = new InstrumentEntry
            {
                Instrument = ens.GetString() ?? "",
                IsEnsemble = true
            };
            if (el.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
            {
                entry.Members = [];
                foreach (var m in members.EnumerateArray())
                    ParseItem(m, entry.Members);
            }
            result.Add(entry);
            return;
        }

        // Orchestra grouping: { "orchestra": [...] } — flatten members
        if (el.TryGetProperty("orchestra", out var orch) && orch.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in orch.EnumerateArray())
                ParseItem(m, result);
            return;
        }

        // Section: { "section": "violin", "number": 1 }
        if (el.TryGetProperty("section", out var sect))
        {
            var entry = new InstrumentEntry { Instrument = sect.GetString() ?? "" };
            if (el.TryGetProperty("number", out var sn) && sn.ValueKind == JsonValueKind.Number)
                entry.PartNumber = sn.GetInt32();
            result.Add(entry);
            return;
        }

        // Standard entry: { "instrument": "clarinet", "key": "B-flat", "number": 1 }
        if (el.TryGetProperty("instrument", out var inst))
        {
            var entry = new InstrumentEntry
            {
                Instrument = inst.ValueKind == JsonValueKind.String
                    ? (inst.GetString() ?? "")
                    : inst.ToString()
            };
            if (el.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
                entry.Key = key.GetString();
            if (el.TryGetProperty("number", out var num) && num.ValueKind == JsonValueKind.Number)
                entry.PartNumber = num.GetInt32();
            if (el.TryGetProperty("alternate_instrument", out var alt))
            {
                // Alternate may be a string or a nested object
                entry.Alternate = alt.ValueKind == JsonValueKind.String
                    ? alt.GetString()
                    : alt.TryGetProperty("instrument", out var altInst)
                        ? altInst.GetString() +
                          (alt.TryGetProperty("key", out var altKey)
                              ? $" in {altKey.GetString()}"
                              : "")
                        : alt.ToString();
            }
            result.Add(entry);
            return;
        }
    }

    /// <summary>
    /// Serializes a list of InstrumentEntry objects back to a JSON array element.
    /// Returns null if the list is empty.
    /// </summary>
    public static JsonElement? SerializeInstrumentation(IList<InstrumentEntry> entries)
    {
        if (entries.Count == 0) return null;

        var array = new JsonArray();
        foreach (var e in entries)
        {
            if (e.IsEnsemble)
            {
                var obj = new JsonObject { ["ensemble"] = e.Instrument };
                if (e.Members is { Count: > 0 })
                {
                    var membersEl = SerializeInstrumentation(e.Members);
                    if (membersEl.HasValue)
                        obj["members"] = JsonNode.Parse(membersEl.Value.GetRawText());
                }
                array.Add(obj);
            }
            else if (!e.PartNumber.HasValue && string.IsNullOrEmpty(e.Key) && string.IsNullOrEmpty(e.Alternate))
            {
                array.Add(JsonValue.Create(e.Instrument));
            }
            else
            {
                var obj = new JsonObject { ["instrument"] = e.Instrument };
                if (!string.IsNullOrEmpty(e.Key))      obj["key"] = e.Key;
                if (e.PartNumber.HasValue)              obj["number"] = e.PartNumber.Value;
                if (!string.IsNullOrEmpty(e.Alternate)) obj["alternate_instrument"] = e.Alternate;
                array.Add(obj);
            }
        }

        return JsonDocument.Parse(array.ToJsonString()).RootElement.Clone();
    }

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// A display node wrapping a subpiece (movement/section) for display in the piece tree.
/// Uses the "N. Form" prefix format rather than the top-level "Form #N" format.
/// This class has no WPF dependencies — it lives in CDArchive.Core.
/// </summary>
public class SubpieceDisplayNode
{
    private readonly bool _showNumber;
    private readonly bool _isExpandable;
    private readonly List<SubpieceDisplayNode>? _filteredChildren;

    public SubpieceDisplayNode(CanonPiece piece, bool showNumber = true, CanonPiece? parentPiece = null,
                                bool isExpandable = true, List<SubpieceDisplayNode>? filteredChildren = null)
    {
        Piece = piece;
        _showNumber = showNumber;
        ParentPiece = parentPiece;
        _isExpandable = isExpandable;
        _filteredChildren = filteredChildren;
    }

    public CanonPiece Piece { get; }

    /// <summary>
    /// The top-level piece that owns this subpiece (or its ancestor subpiece).
    /// Used to inherit composer and other contributors when editing.
    /// </summary>
    public CanonPiece? ParentPiece { get; }

    /// <summary>Display label, with numbering governed by the parent piece's flag.</summary>
    public string DisplayTitle => Piece.BuildSubpieceTitle(_showNumber);

    /// <summary>Whether this subpiece has its own sub-movements.</summary>
    public bool HasChildren =>
        _isExpandable && (_filteredChildren is { Count: > 0 } || Piece.Subpieces is { Count: > 0 });

    /// <summary>Sub-movements of this subpiece, also wrapped as SubpieceDisplayNodes.</summary>
    public List<SubpieceDisplayNode>? Children =>
        !_isExpandable ? null :
        _filteredChildren ?? (Piece.HasSubpieces
            ? Piece.Subpieces!.Select(sp => new SubpieceDisplayNode(sp, Piece.EffectiveSubpiecesNumbered, ParentPiece)).ToList()
            : null);

    public override string ToString() => DisplayTitle;
}

/// <summary>
/// A display node representing the original version of a piece in the tree.
/// Always the first child when a piece has any versions; double-clicking edits the piece itself.
/// This class has no WPF dependencies — it lives in CDArchive.Core.
/// </summary>
public class PieceOriginalNode
{
    private readonly bool _isExpandable;
    private readonly List<SubpieceDisplayNode>? _filteredChildren;

    public PieceOriginalNode(CanonPiece piece, bool isExpandable = true,
                              List<SubpieceDisplayNode>? filteredChildren = null)
    {
        Piece = piece;
        _isExpandable = isExpandable;
        _filteredChildren = filteredChildren;
    }

    public CanonPiece Piece { get; }

    public string DisplayTitle => "Original";

    /// <summary>Whether the original version has its own movements to expand.</summary>
    public bool HasChildren =>
        _isExpandable && (_filteredChildren is { Count: > 0 } || Piece.HasSubpieces);

    /// <summary>Movements of the original piece, wrapped for subpiece display.</summary>
    public List<SubpieceDisplayNode>? Children =>
        !_isExpandable ? null :
        _filteredChildren ?? (Piece.HasSubpieces
            ? Piece.Subpieces!.Select(sp => new SubpieceDisplayNode(sp, Piece.EffectiveSubpiecesNumbered, Piece)).ToList()
            : null);

    public override string ToString() => DisplayTitle;
}

/// <summary>
/// A display node representing a version in the piece tree.
/// Used as the second and subsequent children when a piece has versions.
/// This class has no WPF dependencies — it lives in CDArchive.Core.
/// </summary>
public class VersionDisplayNode
{
    private readonly bool _showNumber;
    private readonly bool _isExpandable;
    private readonly List<SubpieceDisplayNode>? _filteredChildren;

    public VersionDisplayNode(CanonPieceVersion version, bool showNumber = true, CanonPiece? parentPiece = null,
                               bool isExpandable = true, List<SubpieceDisplayNode>? filteredChildren = null)
    {
        Version = version;
        _showNumber = showNumber;
        ParentPiece = parentPiece;
        _isExpandable = isExpandable;
        _filteredChildren = filteredChildren;
    }

    public CanonPieceVersion Version { get; }

    /// <summary>The piece this version belongs to. Used when saving after a direct tree edit.</summary>
    public CanonPiece? ParentPiece { get; }

    /// <summary>Label shown in the tree row (e.g., "Version: Orchestra").</summary>
    public string DisplayTitle => "Version: " + (Version.Description ?? "(no description)");

    /// <summary>Movements belonging to this version, wrapped for subpiece display.</summary>
    public List<SubpieceDisplayNode>? Children =>
        !_isExpandable ? null :
        _filteredChildren ?? Version.Subpieces?.Select(sp => new SubpieceDisplayNode(sp, _showNumber, ParentPiece)).ToList();

    /// <summary>Whether this version has movements to expand.</summary>
    public bool HasSubpieces =>
        _isExpandable && (_filteredChildren is { Count: > 0 } || Version.Subpieces is { Count: > 0 });

    public override string ToString() => DisplayTitle;
}

public class CanonPieceVersion
{
    /// <summary>Free-text label identifying this version (e.g. "Original version", "arr. for string quartet").</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("composer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Composer { get; set; }

    [JsonPropertyName("composers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ComposerCredit>? Composers { get; set; }

    [JsonPropertyName("form")]
    public string? Form { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("title_English")]
    public string? TitleEnglish { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("music_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MusicNumber { get; set; }

    [JsonPropertyName("key_tonality")]
    public string? KeyTonality { get; set; }

    [JsonPropertyName("key_mode")]
    public string? KeyMode { get; set; }

    [JsonPropertyName("catalog_info")]
    public List<CatalogInfo>? CatalogInfo { get; set; }

    [JsonPropertyName("instrumentation_category")]
    public string? InstrumentationCategory { get; set; }

    [JsonPropertyName("instrumentation")]
    public JsonElement? Instrumentation { get; set; }

    [JsonPropertyName("publication_year")]
    public int? PublicationYear { get; set; }

    [JsonPropertyName("composition_years")]
    public JsonElement? CompositionYears { get; set; }

    [JsonPropertyName("numbered_subpieces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NumberedSubpieces { get; set; }

    [JsonPropertyName("subpieces_start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SubpiecesStart { get; set; }

    [JsonPropertyName("first_line")]
    public string? FirstLine { get; set; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }

    [JsonPropertyName("variants")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VariantInfo>? Variants { get; set; }

    [JsonPropertyName("text_author")]
    public JsonElement? TextAuthor { get; set; }

    [JsonPropertyName("roles")]
    public JsonElement? Roles { get; set; }

    [JsonPropertyName("tempos")]
    public List<TempoInfo>? Tempos { get; set; }

    [JsonPropertyName("subpieces")]
    public List<CanonPiece>? Subpieces { get; set; }

    [JsonPropertyName("contributing_composers")]
    public JsonElement? ContributingComposers { get; set; }

    public override string ToString() => Description ?? Title ?? "(no description)";
}

/// <summary>
/// A header node grouping contributed works by creative role and original composer.
/// E.g., "orch: Mussorgsky, Modest" groups all pieces where the selected composer/author
/// orchestrated works by Mussorgsky.
/// </summary>
public class ContributedRoleGroupNode
{
    public ContributedRoleGroupNode(string role, string composerName, List<ContributedPieceNode> pieces)
    {
        Role = role;
        ComposerName = composerName;
        Pieces = pieces;
    }

    public string Role { get; }
    public string ComposerName { get; }
    public string DisplayTitle => $"{Role.TrimEnd('.')}: {ComposerName}";
    public List<ContributedPieceNode> Pieces { get; }

    public override string ToString() => DisplayTitle;
}

/// <summary>
/// A display node wrapping a piece in the contributed-works section of the tree.
/// Its <see cref="Children"/> are filtered so that only nodes on the path to (and
/// descendants of) the contribution point are expandable.
/// </summary>
public class ContributedPieceNode
{
    public ContributedPieceNode(CanonPiece piece, System.Collections.IList? children)
    {
        Piece = piece;
        Children = children;
    }

    public CanonPiece Piece { get; }
    public string DisplayTitle => Piece.DisplayTitleShort;
    public string Catalog => Piece.Catalog;
    public bool HasChildren => Children is { Count: > 0 };
    public System.Collections.IList? Children { get; }

    public override string ToString() => DisplayTitle;
}

/// <summary>
/// Finds works where a given composer/author is an Other Contributor and builds
/// filtered tree structures showing only the path to the contribution point.
/// </summary>
public static class ContributedWorksFinder
{
    /// <summary>
    /// Returns true if the named person is an Other Contributor anywhere in the
    /// piece hierarchy (root piece, any version, or any subpiece at any depth).
    /// </summary>
    public static bool IsContributor(CanonPiece piece, string composerName)
    {
        var roles = CollectAllRoles(piece, composerName);
        return roles.Count > 0;
    }

    /// <summary>
    /// Scans all pieces and returns contributed-work groups for the given composer name.
    /// Each group is keyed by (role, original composer) and contains contributed piece nodes
    /// with filtered children.
    /// </summary>
    public static List<ContributedRoleGroupNode> FindContributedGroups(
        IEnumerable<CanonPiece> allPieces, string composerName)
    {
        var groups = new Dictionary<string, (string role, string composer, List<ContributedPieceNode> pieces)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var piece in allPieces)
        {
            // Skip the composer's own pieces
            if (string.Equals(piece.Composer, composerName, StringComparison.OrdinalIgnoreCase))
                continue;

            var roles = CollectAllRoles(piece, composerName);
            if (roles.Count == 0) continue;

            var role = roles[0];
            var children = BuildFilteredTree(piece, composerName);
            var key = $"{role}\0{piece.Composer}";

            if (!groups.TryGetValue(key, out var group))
            {
                group = (role, piece.Composer ?? "", []);
                groups[key] = group;
            }
            group.pieces.Add(new ContributedPieceNode(piece, children));
        }

        return groups.Values
            .OrderBy(g => g.role, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.composer, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ContributedRoleGroupNode(g.role, g.composer, g.pieces))
            .ToList();
    }

    /// <summary>
    /// Builds the filtered tree children for a contributed piece.
    /// Nodes on the path to the contribution point are expandable;
    /// all descendants of the contribution point are fully expandable;
    /// everything else is shown but not expandable.
    /// </summary>
    private static System.Collections.IList? BuildFilteredTree(CanonPiece piece, string composerName)
    {
        // Contribution at root level → entire tree is expandable
        if (HasDirectContribution(piece.Composers, composerName))
            return piece.TreeChildren;

        var showNums = piece.EffectiveSubpiecesNumbered;

        if (piece.Versions is { Count: > 0 })
        {
            var nodes = new List<object>();

            // Original node: expandable only if path goes through the piece's own subpieces
            if (HasContributionInSubpieces(piece.Subpieces, composerName))
            {
                var filtered = BuildFilteredSubpieces(piece.Subpieces!, showNums, piece, composerName);
                nodes.Add(new PieceOriginalNode(piece, filteredChildren: filtered));
            }
            else
            {
                nodes.Add(new PieceOriginalNode(piece, isExpandable: false));
            }

            // Each version: expandable if it has a direct contribution or a path through its subpieces
            foreach (var v in piece.Versions)
            {
                if (HasDirectContribution(v.Composers, composerName))
                {
                    // Direct contribution on the version → fully expandable
                    nodes.Add(new VersionDisplayNode(v, showNums, piece));
                }
                else if (HasContributionInSubpieces(v.Subpieces, composerName))
                {
                    // Path goes through version's subpieces
                    var filtered = BuildFilteredSubpieces(v.Subpieces!, showNums, piece, composerName);
                    nodes.Add(new VersionDisplayNode(v, showNums, piece, filteredChildren: filtered));
                }
                else
                {
                    // No contribution path → not expandable
                    nodes.Add(new VersionDisplayNode(v, showNums, piece, isExpandable: false));
                }
            }

            return nodes;
        }
        else if (piece.Subpieces is { Count: > 0 })
        {
            return BuildFilteredSubpieces(piece.Subpieces, showNums, piece, composerName);
        }

        return null;
    }

    /// <summary>
    /// Builds a filtered subpiece list where only path/target/descendant nodes are expandable.
    /// </summary>
    private static List<SubpieceDisplayNode> BuildFilteredSubpieces(
        List<CanonPiece> subpieces, bool showNumbers, CanonPiece parentPiece, string composerName)
    {
        return subpieces.Select(sp =>
        {
            if (HasDirectContribution(sp.Composers, composerName))
            {
                // This IS the contribution target → fully expandable with normal children
                return new SubpieceDisplayNode(sp, showNumbers, parentPiece);
            }

            if (HasContributionInSubpieces(sp.Subpieces, composerName))
            {
                // On the path → expandable, but with filtered children
                var filtered = BuildFilteredSubpieces(
                    sp.Subpieces!, sp.EffectiveSubpiecesNumbered, parentPiece, composerName);
                return new SubpieceDisplayNode(sp, showNumbers, parentPiece,
                    isExpandable: true, filteredChildren: filtered);
            }

            // Not on path → shown but not expandable
            return new SubpieceDisplayNode(sp, showNumbers, parentPiece, isExpandable: false);
        }).ToList();
    }

    /// <summary>Checks if a Composers list has a credit for the given name with a non-null role.</summary>
    private static bool HasDirectContribution(List<ComposerCredit>? composers, string composerName) =>
        composers?.Any(c => string.Equals(c.Name, composerName, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(c.Role)) == true;

    /// <summary>Recursively checks if any subpiece (or its descendants) has a contribution.</summary>
    private static bool HasContributionInSubpieces(List<CanonPiece>? subpieces, string composerName)
    {
        if (subpieces == null) return false;
        return subpieces.Any(sp =>
            HasDirectContribution(sp.Composers, composerName)
            || HasContributionInSubpieces(sp.Subpieces, composerName));
    }

    /// <summary>
    /// Collects all distinct creative roles the composer holds anywhere in the piece hierarchy.
    /// </summary>
    private static List<string> CollectAllRoles(CanonPiece piece, string composerName)
    {
        var roles = new List<string>();
        CollectRolesRecursive(piece, composerName, roles);
        return roles;
    }

    private static void CollectRolesRecursive(CanonPiece piece, string composerName, List<string> roles)
    {
        AddRolesFrom(piece.Composers, composerName, roles);

        if (piece.Subpieces != null)
            foreach (var sp in piece.Subpieces)
                CollectRolesRecursive(sp, composerName, roles);

        if (piece.Versions != null)
        {
            foreach (var v in piece.Versions)
            {
                AddRolesFrom(v.Composers, composerName, roles);
                if (v.Subpieces != null)
                    foreach (var sp in v.Subpieces)
                        CollectRolesRecursive(sp, composerName, roles);
            }
        }
    }

    private static void AddRolesFrom(List<ComposerCredit>? composers, string composerName, List<string> roles)
    {
        if (composers == null) return;
        foreach (var c in composers)
        {
            if (string.Equals(c.Name, composerName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(c.Role)
                && !roles.Contains(c.Role, StringComparer.OrdinalIgnoreCase))
            {
                roles.Add(c.Role);
            }
        }
    }
}

/// <summary>
/// Shared text-formatting helpers for canon data display.
/// </summary>
public static class CanonFormat
{
    // Small connector/article words that stay lowercase unless they open the string.
    private static readonly HashSet<string> _lowerWords =
        new(StringComparer.OrdinalIgnoreCase) { "and", "or", "of", "in", "for", "the", "a", "an" };

    /// <summary>
    /// Title-cases a value: the first letter of each word is uppercased,
    /// except for small connector words (and, or, of, in, for, the, a, an)
    /// when they are not the first word.
    /// Examples: "aria and chorus" → "Aria and Chorus", "french horn" → "French Horn".
    /// </summary>
    public static string TitleCase(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        var words = value.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0 && (i == 0 || !_lowerWords.Contains(words[i])))
                words[i] = char.ToUpper(words[i][0]) + words[i][1..];
        }
        return string.Join(' ', words);
    }
}
