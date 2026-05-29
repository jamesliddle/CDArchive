using System.Text.Json;
using System.Text.Json.Nodes;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Renames instrument occurrences inside a piece's flexible
/// <c>Instrumentation</c> JSON. The Instrumentation field accepts several
/// shapes (see CLAUDE.md):
/// <list type="bullet">
///   <item>Top-level string elements: <c>["piano", "violin"]</c></item>
///   <item><c>instrument</c> property: <c>{"instrument": "clarinet", "key": "B-flat"}</c></item>
///   <item><c>alternate_instrument</c> property: <c>{"instrument": "horn", "alternate_instrument": "cornetto"}</c></item>
///   <item><c>orchestra</c> array elements: <c>{"orchestra": ["flute", "oboe"]}</c></item>
///   <item><c>section</c> property value: <c>{"section": "violin", "number": 1}</c></item>
/// </list>
/// This helper walks the tree and rewrites every string in the
/// instrument-bearing positions when it matches a key in
/// <c>renames</c>. Match is case-insensitive (the pick-list editor's
/// behaviour). The walk recurses into nested objects so an
/// <c>orchestra</c> with embedded <c>instrument</c> objects renames
/// at every level.
///
/// <para>Returns a new <see cref="JsonElement"/> (the source one is
/// immutable). Returns the input unchanged when <paramref name="renames"/>
/// is empty or no matching strings are found.</para>
/// </summary>
public static class InstrumentationRenamer
{
    /// <summary>
    /// Walks <paramref name="source"/> and returns a copy with every
    /// matching instrument name rewritten. The count of rewrites is
    /// returned via <paramref name="renameCount"/> so the caller can tell
    /// the user how many fields actually moved.
    /// </summary>
    public static JsonElement? ApplyRenames(
        JsonElement? source,
        IReadOnlyDictionary<string, string> renames,
        out int renameCount)
    {
        renameCount = 0;
        if (source is null || renames.Count == 0) return source;

        var node = JsonNode.Parse(source.Value.GetRawText());
        if (node is null) return source;

        var ciRenames = new Dictionary<string, string>(renames, StringComparer.OrdinalIgnoreCase);
        renameCount = WalkArray(node as JsonArray, ciRenames);

        if (renameCount == 0) return source;

        // Re-serialize so the caller can store back into JsonElement?.
        var rewritten = node.ToJsonString();
        using var doc = JsonDocument.Parse(rewritten);
        return doc.RootElement.Clone();
    }

    private static int WalkArray(JsonArray? array, IReadOnlyDictionary<string, string> renames)
    {
        if (array is null) return 0;
        int count = 0;
        for (int i = 0; i < array.Count; i++)
        {
            var item = array[i];
            switch (item)
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    if (renames.TryGetValue(s, out var renamed))
                    {
                        array[i] = JsonValue.Create(renamed);
                        count++;
                    }
                    break;

                case JsonObject obj:
                    count += WalkObject(obj, renames);
                    break;
            }
        }
        return count;
    }

    private static int WalkObject(JsonObject obj, IReadOnlyDictionary<string, string> renames)
    {
        int count = 0;

        // "instrument" + "alternate_instrument" + "section" — scalar string
        // fields that hold instrument names.
        count += RewriteStringField(obj, "instrument",          renames);
        count += RewriteStringField(obj, "alternate_instrument", renames);
        count += RewriteStringField(obj, "section",             renames);

        // "instrument" might also be an OBJECT (e.g. nested shape). Walk it.
        if (obj["instrument"] is JsonObject nestedInst)
            count += WalkObject(nestedInst, renames);
        if (obj["alternate_instrument"] is JsonObject nestedAlt)
            count += WalkObject(nestedAlt, renames);

        // "orchestra" is an array of instrument entries (string or nested).
        if (obj["orchestra"] is JsonArray orch)
            count += WalkArray(orch, renames);

        return count;
    }

    /// <summary>
    /// Rewrites <paramref name="obj"/>[<paramref name="fieldName"/>] when it's
    /// a string that matches a rename. Object/array values are ignored here
    /// — the caller handles nested walks separately.
    /// </summary>
    private static int RewriteStringField(JsonObject obj, string fieldName,
        IReadOnlyDictionary<string, string> renames)
    {
        if (obj[fieldName] is JsonValue v && v.TryGetValue<string>(out var s) &&
            renames.TryGetValue(s, out var renamed))
        {
            obj[fieldName] = JsonValue.Create(renamed);
            return 1;
        }
        return 0;
    }
}
