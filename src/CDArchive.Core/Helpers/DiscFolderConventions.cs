using System.Text.RegularExpressions;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Single source of truth for the "Disc N" folder-naming convention
/// across the archive pipeline. Pre-fix (Rework M35) three different
/// services held three different representations of the same idea —
/// scaffolding formatted with conditional zero-padding, the scanner had
/// a regex, the locator had a string-format with no padding, and the
/// cataloguer used a glob. The locator's no-padding format silently lost
/// playback on 10+ disc box sets that the scaffolding had created with
/// padded folder names (Rework H46): the locator looked for
/// <c>Disc 1</c>, the on-disk folder was <c>Disc 01</c>.
///
/// <para>
/// Every site now routes through this helper. Sibling helper
/// <see cref="DiscFolderOrdering"/> handles the numeric sort (Rework
/// H29); together the two cover the entire "Disc N" surface area.
/// </para>
/// </summary>
public static class DiscFolderConventions
{
    /// <summary>
    /// The folder name <see cref="Services.AlbumScaffoldingService"/>
    /// writes when scaffolding a multi-disc album. Pads to two digits
    /// only when the box set has 10+ discs — short albums stay
    /// unpadded for readability.
    /// </summary>
    public static string Format(int discNumber, int totalDiscs) =>
        totalDiscs >= 10 ? $"Disc {discNumber:D2}" : $"Disc {discNumber}";

    /// <summary>
    /// Yields the folder names the locator should try when resolving a
    /// disc back to disk. An archive scaffolded with this helper writes
    /// either the unpadded or the padded form depending on totalDiscs;
    /// the locator doesn't know totalDiscs at resolve time (the caller
    /// only gives it one disc), so it must try both forms.
    /// </summary>
    public static IEnumerable<string> CandidateNames(int discNumber)
    {
        // Unpadded first — matches the scaffolding default for &lt;10-disc albums,
        // which covers the vast majority of the catalogue. The padded form
        // is the fallback for 10+ disc box sets; only yield it when it
        // actually differs from the unpadded form (disc 10 onwards
        // produces the same two-digit string either way).
        var unpadded = $"Disc {discNumber}";
        yield return unpadded;
        if (discNumber > 0 && discNumber < 10)
            yield return $"Disc {discNumber:D2}";
    }

    /// <summary>
    /// Glob pattern for <see cref="System.IO.Directory.GetDirectories(string, string)"/>
    /// when enumerating an album's disc folders. The trailing wildcard
    /// matches both padded and unpadded forms (and the "Disc N-M" sub-disc
    /// shape) without needing per-caller regex.
    /// </summary>
    public const string SearchPattern = "Disc *";

    /// <summary>
    /// Matches the disc-folder leaf-name shape: <c>Disc N</c>,
    /// <c>Disc NN</c>, or <c>Disc N-M</c>. Used by the archive scanner
    /// to filter <c>EnumerateDirectories</c> results.
    /// </summary>
    public static bool IsDiscFolderName(string name) => DiscFolderRegex.IsMatch(name);

    private static readonly Regex DiscFolderRegex =
        new(@"^Disc \d+(-\d+)?$", RegexOptions.Compiled);
}
