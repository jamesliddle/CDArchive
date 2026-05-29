namespace CDArchive.Core.Helpers;

/// <summary>
/// Pure helper for collapsing a free-text performer name to a comparison key.
/// Used by the iTunes import "already imported" filter (H24) and by the iTunes
/// album-level dedup (M5) to anchor a `(album-title, performer)` lookup that
/// survives common formatting drift between sources.
///
/// <para>The algorithm: lowercase → strip everything non-alphanumeric ASCII
/// → split into tokens → sort tokens ordinally → concat. So
/// <c>"Karajan, Herbert von"</c> and <c>"Herbert von Karajan"</c> both
/// normalise to <c>herbertkarajanvon</c>.</para>
///
/// <para>Diacritic-aware normalisation is NOT done here — <c>Dvořák</c> still
/// differs from <c>Dvorak</c>. That's a documented limitation; revisit when
/// the iTunes Persistent ID threading (M27) lands and the importer can match
/// by stable iTunes ID instead of by name.</para>
/// </summary>
public static class PerformerNormalisation
{
    /// <summary>
    /// Normalises a free-text performer name to its dedup key. Returns the
    /// empty string for null / whitespace input.
    /// </summary>
    public static string NormalisePerformer(string? performer)
    {
        if (string.IsNullOrWhiteSpace(performer)) return string.Empty;

        var lower = performer.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        bool prevWasSeparator = true;
        var tokens = new List<string>();
        foreach (var ch in lower)
        {
            if (ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9')
            {
                sb.Append(ch);
                prevWasSeparator = false;
            }
            else if (!prevWasSeparator)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                prevWasSeparator = true;
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());

        // Sort tokens by ordinal so name order doesn't matter.
        tokens.Sort(StringComparer.Ordinal);
        return string.Concat(tokens);
    }
}
