using System.Text.RegularExpressions;

namespace CDArchive.Core.Helpers;

public static class StringSimilarity
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var lower = input.ToLowerInvariant();
        var cleaned = Regex.Replace(lower, @"[^a-z0-9\s]", "");
        var collapsed = Regex.Replace(cleaned, @"\s+", " ");
        return collapsed.Trim();
    }

    public static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a))
            return string.IsNullOrEmpty(b) ? 0 : b.Length;
        if (string.IsNullOrEmpty(b))
            return a.Length;

        var lengthA = a.Length;
        var lengthB = b.Length;
        var distances = new int[lengthA + 1, lengthB + 1];

        for (int i = 0; i <= lengthA; i++)
            distances[i, 0] = i;
        for (int j = 0; j <= lengthB; j++)
            distances[0, j] = j;

        for (int i = 1; i <= lengthA; i++)
        {
            for (int j = 1; j <= lengthB; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[lengthA, lengthB];
    }
}
