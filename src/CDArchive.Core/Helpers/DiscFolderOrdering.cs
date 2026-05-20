using System.Text.RegularExpressions;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Numeric sort over disc-folder paths so "Disc 10" doesn't land between
/// "Disc 1" and "Disc 2". Pre-fix both <c>ArchiveScannerService</c> and
/// <c>CataloguingService</c> used <c>.OrderBy(d => d)</c> on the raw path
/// string — lexicographic, which silently scrambles the disc order on any
/// 10+ disc box set (Rework H29).
///
/// <para>
/// Names supported: <c>Disc 1</c>, <c>Disc 12</c>, <c>Disc 01</c>
/// (zero-padded), and <c>Disc 1-2</c> (sub-disc, e.g. an alternate take or
/// bonus disc filed under disc 1). The leading integer is the primary
/// sort key; the trailing <c>-N</c> integer (when present) is the
/// secondary. Non-matching names sort to the end so an unexpected folder
/// doesn't quietly jump to the front.
/// </para>
/// </summary>
public static class DiscFolderOrdering
{
    private static readonly Regex DiscNumberRegex =
        new(@"^Disc (\d+)(?:-(\d+))?$", RegexOptions.Compiled);

    /// <summary>
    /// Parses the (primary, secondary) numeric key from a disc folder
    /// name. Returns <c>(int.MaxValue, int.MaxValue)</c> for names that
    /// don't match the <c>Disc N[-N]</c> shape — those sort last.
    /// </summary>
    public static (int Primary, int Secondary) ParseDiscKey(string folderName)
    {
        var match = DiscNumberRegex.Match(folderName);
        if (!match.Success) return (int.MaxValue, int.MaxValue);

        int primary = int.TryParse(match.Groups[1].Value, out var p) ? p : int.MaxValue;
        int secondary = 0;
        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var s))
            secondary = s;
        return (primary, secondary);
    }

    /// <summary>
    /// Orders disc-folder paths by their leaf name's disc number, with
    /// sub-disc as tiebreaker and the full path as final tiebreaker.
    /// </summary>
    public static IEnumerable<string> OrderByDiscNumber(IEnumerable<string> paths)
    {
        return paths
            .Select(p => (Path: p, Key: ParseDiscKey(System.IO.Path.GetFileName(p))))
            .OrderBy(t => t.Key.Primary)
            .ThenBy(t => t.Key.Secondary)
            .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Path);
    }
}
