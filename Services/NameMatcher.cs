using AccFiltering.Models;

namespace AccFiltering.Services;

/// <summary>
/// Compares Excel model names with ACC display names.
/// Matching is exact after trim, case folding, and removal of a trailing <c>.nwc</c>.
/// A close name is only a suggestion and is never treated as a match.
/// </summary>
public sealed class NameMatcher
{
    /// <summary>Minimum similarity for a suggestion. Suggestions are not copied.</summary>
    public const double SuggestionThreshold = 0.85;

    /// <summary>Trim, drop one trailing <c>.nwc</c>, trim again, and lowercase.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var value = name.Trim();
        const string extension = ".nwc";
        if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^extension.Length].Trim();
        }

        return value.ToLowerInvariant();
    }

    /// <summary>1 when the strings are equal, otherwise 1 minus the edit distance divided by the longer length.</summary>
    public static double Similarity(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 1;
        }

        var longest = Math.Max(left.Length, right.Length);
        if (longest == 0)
        {
            return 1;
        }

        return 1d - (double)LevenshteinDistance(left, right) / longest;
    }

    /// <summary>Classic Levenshtein distance.</summary>
    public static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    /// <summary>Pairs Excel names with source items. Unmatched names may include one suggestion.</summary>
    public MatchResult Match(IReadOnlyList<string> excelNames, IReadOnlyList<AccItem> sourceItems)
    {
        var groups = new Dictionary<string, List<AccItem>>(StringComparer.Ordinal);
        foreach (var item in sourceItems)
        {
            var key = Normalize(item.DisplayName);
            if (key.Length == 0)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(item);
        }

        var chosen = new Dictionary<string, AccItem>(StringComparer.Ordinal);
        var ambiguous = new List<string>();
        foreach (var (key, items) in groups)
        {
            var preferred = items.FirstOrDefault(item =>
                item.DisplayName.EndsWith(".nwc", StringComparison.OrdinalIgnoreCase)) ?? items[0];
            chosen[key] = preferred;
            if (items.Count > 1)
            {
                ambiguous.Add(key);
            }
        }

        var matched = new List<MatchedName>();
        var unmatched = new List<UnmatchedName>();
        var sourceKeys = chosen.Keys.ToList();

        foreach (var excelName in excelNames)
        {
            var key = Normalize(excelName);
            if (key.Length == 0)
            {
                continue;
            }

            if (chosen.TryGetValue(key, out var item))
            {
                matched.Add(new MatchedName(excelName, item));
                continue;
            }

            string? suggestion = null;
            double? best = null;
            foreach (var sourceKey in sourceKeys)
            {
                var score = Similarity(key, sourceKey);
                if (score < SuggestionThreshold)
                {
                    continue;
                }

                if (best is null || score > best)
                {
                    best = score;
                    suggestion = chosen[sourceKey].DisplayName;
                }
            }

            unmatched.Add(new UnmatchedName(excelName, suggestion, best));
        }

        return new MatchResult(matched, unmatched, ambiguous);
    }
}
