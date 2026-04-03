using System.Text.RegularExpressions;

namespace CDArchive.Core.Services;

/// <summary>
/// Pure formatting functions that implement the cataloguing conventions.
/// All methods are static and side-effect-free for easy testing.
/// </summary>
public static class CataloguingRules
{
    private static readonly Regex KeyPattern = new(
        @"\b([A-Ga-g])\s+(flat|sharp)\b",
        RegexOptions.Compiled);

    private static readonly Regex WorkNumberPattern = new(
        @"#(\d+)",
        RegexOptions.Compiled);

    private static readonly Regex MovementNumberPattern = new(
        @"^(\d+)\.\s",
        RegexOptions.Compiled);

    /// <summary>
    /// Hyphenates key names: "B flat" → "B-flat", "c sharp" → "c-sharp".
    /// Preserves the original case (uppercase = major, lowercase = minor).
    /// </summary>
    public static string HyphenateKeys(string text)
    {
        return KeyPattern.Replace(text, m => $"{m.Groups[1].Value}-{m.Groups[2].Value}");
    }

    /// <summary>
    /// Pads the work number (#N) based on the maximum count in the composer's catalogue.
    /// For example, if maxInCatalogue is 15, #3 becomes #03; if 104, #3 becomes #003.
    /// When maxInCatalogue is 9 or fewer, no padding is applied.
    /// </summary>
    public static string PadWorkNumber(string text, int maxInCatalogue)
    {
        if (maxInCatalogue <= 9)
            return text;

        int digits = maxInCatalogue switch
        {
            <= 99 => 2,
            <= 999 => 3,
            _ => 4
        };

        return WorkNumberPattern.Replace(text, m =>
        {
            var num = int.Parse(m.Groups[1].Value);
            return $"#{num.ToString().PadLeft(digits, '0')}";
        });
    }

    /// <summary>
    /// Pads the movement number at the start of a movement string.
    /// "1. Allegro" with totalMovements=12 becomes "01. Allegro".
    /// When totalMovements is 9 or fewer, no padding is applied.
    /// </summary>
    public static string PadMovementNumber(string movementText, int totalMovements)
    {
        if (totalMovements <= 9)
            return movementText;

        int digits = totalMovements switch
        {
            <= 99 => 2,
            <= 999 => 3,
            _ => 4
        };

        return MovementNumberPattern.Replace(movementText, m =>
        {
            var num = int.Parse(m.Groups[1].Value);
            return $"{num.ToString().PadLeft(digits, '0')}. ";
        });
    }

    /// <summary>
    /// Formats a complete track name from work title and optional movement.
    /// Applies key hyphenation. Movement padding requires the caller to pre-pad
    /// the movement string using PadMovementNumber.
    /// </summary>
    public static string FormatTrackName(string workTitle, string? movement = null)
    {
        var name = HyphenateKeys(workTitle.Trim());

        if (!string.IsNullOrWhiteSpace(movement))
            name += $" - {movement.Trim()}";

        return name;
    }

    /// <summary>
    /// Formats a composer string: "Last, First (birth–death)".
    /// The en-dash (–) is used between dates per convention.
    /// </summary>
    public static string FormatComposer(string lastName, string firstName, int? birthYear, int? deathYear)
    {
        var name = $"{lastName.Trim()}, {firstName.Trim()}";

        if (birthYear.HasValue)
        {
            var death = deathYear.HasValue ? deathYear.Value.ToString() : "";
            name += $" ({birthYear.Value}\u2013{death})";
        }

        return name;
    }

    /// <summary>
    /// Formats an artist string for orchestral works: "Ensemble, Conductor".
    /// </summary>
    public static string FormatArtist(string ensemble, string conductor)
    {
        if (string.IsNullOrWhiteSpace(conductor))
            return ensemble.Trim();

        return $"{ensemble.Trim()}, {conductor.Trim()}";
    }

    /// <summary>
    /// Determines how many digits to use for movement numbers
    /// based on the total movement count within a work.
    /// </summary>
    public static int MovementDigits(int totalMovements) => totalMovements switch
    {
        <= 9 => 1,
        <= 99 => 2,
        <= 999 => 3,
        _ => 4
    };

    /// <summary>
    /// Determines how many digits to use for work numbers
    /// based on the max count in the composer's catalogue.
    /// </summary>
    public static int WorkNumberDigits(int maxInCatalogue) => maxInCatalogue switch
    {
        <= 9 => 1,
        <= 99 => 2,
        <= 999 => 3,
        _ => 4
    };
}
