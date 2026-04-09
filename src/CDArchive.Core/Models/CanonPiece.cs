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
    public string DisplayTitle => BuildDisplayTitle(includeCatalog: true) + RolesSuffix;

    /// <summary>
    /// Display title without catalogue info, for use in the pieces list
    /// where the catalogue is shown in its own column.
    /// </summary>
    [JsonIgnore]
    public string DisplayTitleShort => BuildDisplayTitle(includeCatalog: false) + RolesSuffix;

    /// <summary>
    /// Display title for use when rendered as a subpiece.
    /// Sub-works (those with their own subpieces) use the top-level "Form #N" format;
    /// leaf movements use the "N. Form / N. Tempo" format.
    /// </summary>
    [JsonIgnore]
    public string SubpieceDisplayTitle => BuildDisplayTitle(includeCatalog: false, isSubpiece: true) + RolesSuffix;

    /// <summary>
    /// Roles suffix for movement-level display: " (Role1, Role2)".
    /// Only applies when Roles is a JSON string array (movements), not an object array (pieces).
    /// </summary>
    [JsonIgnore]
    private string RolesSuffix
    {
        get
        {
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
        BuildDisplayTitle(includeCatalog: false, isSubpiece: true, showNumber: showNumber)
        + RolesSuffix;

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
    /// Parses a piece-level roles JSON array (array of objects) into a typed list.
    /// Silently skips string entries, which belong to subpiece-level role references.
    /// </summary>
    public static List<RoleEntry> ParseRoles(JsonElement el)
    {
        var result = new List<RoleEntry>();
        if (el.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var entry = new RoleEntry();
            if (item.TryGetProperty("name", out var name))
                entry.Name = name.GetString() ?? "";
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
    /// </summary>
    public static JsonElement? SerializeRoles(IList<RoleEntry> roles)
    {
        if (roles.Count == 0) return null;
        var array = new JsonArray();
        foreach (var r in roles)
        {
            var obj = new JsonObject { ["name"] = r.Name };
            if (!string.IsNullOrEmpty(r.VoiceType))    obj["voice_type"]   = r.VoiceType;
            if (!string.IsNullOrEmpty(r.Description))   obj["description"]  = r.Description;
            array.Add(obj);
        }
        return JsonDocument.Parse(array.ToJsonString()).RootElement.Clone();
    }

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// A parsed representation of one entry in a piece's instrumentation list.
/// Supports simple instrument names, part numbering (e.g. "violin 1"), transposition keys,
/// and alternate instruments.
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

    /// <summary>Display label, e.g. "Clarinet in B-flat 1" or "Violin (or Viola)".</summary>
    public string DisplayLabel
    {
        get
        {
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
            if (!e.PartNumber.HasValue && string.IsNullOrEmpty(e.Key) && string.IsNullOrEmpty(e.Alternate))
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

    public SubpieceDisplayNode(CanonPiece piece, bool showNumber = true, CanonPiece? parentPiece = null)
    {
        Piece = piece;
        _showNumber = showNumber;
        ParentPiece = parentPiece;
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
    public bool HasChildren => Piece.Subpieces is { Count: > 0 };

    /// <summary>Sub-movements of this subpiece, also wrapped as SubpieceDisplayNodes.</summary>
    public List<SubpieceDisplayNode>? Children =>
        HasChildren
            ? Piece.Subpieces!.Select(sp => new SubpieceDisplayNode(sp, Piece.EffectiveSubpiecesNumbered, ParentPiece)).ToList()
            : null;

    public override string ToString() => DisplayTitle;
}

/// <summary>
/// A display node representing the original version of a piece in the tree.
/// Always the first child when a piece has any versions; double-clicking edits the piece itself.
/// This class has no WPF dependencies — it lives in CDArchive.Core.
/// </summary>
public class PieceOriginalNode
{
    public PieceOriginalNode(CanonPiece piece)
    {
        Piece = piece;
    }

    public CanonPiece Piece { get; }

    public string DisplayTitle => "Original";

    /// <summary>Whether the original version has its own movements to expand.</summary>
    public bool HasChildren => Piece.HasSubpieces;

    /// <summary>Movements of the original piece, wrapped for subpiece display.</summary>
    public List<SubpieceDisplayNode>? Children =>
        Piece.HasSubpieces
            ? Piece.Subpieces!.Select(sp => new SubpieceDisplayNode(sp, Piece.EffectiveSubpiecesNumbered, Piece)).ToList()
            : null;

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

    public VersionDisplayNode(CanonPieceVersion version, bool showNumber = true, CanonPiece? parentPiece = null)
    {
        Version = version;
        _showNumber = showNumber;
        ParentPiece = parentPiece;
    }

    public CanonPieceVersion Version { get; }

    /// <summary>The piece this version belongs to. Used when saving after a direct tree edit.</summary>
    public CanonPiece? ParentPiece { get; }

    /// <summary>Label shown in the tree row (e.g., "Version: Orchestra").</summary>
    public string DisplayTitle => "Version: " + (Version.Description ?? "(no description)");

    /// <summary>Movements belonging to this version, wrapped for subpiece display.</summary>
    public List<SubpieceDisplayNode>? Children =>
        Version.Subpieces?.Select(sp => new SubpieceDisplayNode(sp, _showNumber, ParentPiece)).ToList();

    /// <summary>Whether this version has movements to expand.</summary>
    public bool HasSubpieces => Version.Subpieces is { Count: > 0 };

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
