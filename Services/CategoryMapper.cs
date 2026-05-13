using System.Text;
using DeepWork.Models;

namespace DeepWork.Services;

public sealed class CategoryMapper
{
    public static readonly string[] DefaultNonDeepTags =
    [
        "admin",
        "meeting",
        "meetings",
        "shallow",
        "break",
        "lunch",
        "email",
        "emails",
        "slack",
        "chat",
        "call",
        "calls"
    ];

    private static readonly Dictionary<WorkCategory, string[]> DefaultAliases = new()
    {
        [WorkCategory.Job] = ["job", "work", "dayjob", "coding", "engineering"],
        [WorkCategory.Leetcode] = [
            "leetcode", "lc",
            "lcreview", "lc-review", "leetcode-review", "leetcode_review", "review",
            "lcnew", "lc-new", "leetcode-new", "leetcode_new", "newproblems", "new-problems"
        ],
        [WorkCategory.Anki] = ["anki", "flashcards", "spacedrepetition", "spaced-repetition"],
        [WorkCategory.Okta] = ["okta"]
    };

    private readonly Dictionary<string, WorkCategory> _aliases;

    private CategoryMapper(Dictionary<string, WorkCategory> aliases)
    {
        _aliases = aliases;
    }

    public static CategoryMapper CreateDefault()
    {
        var aliases = new Dictionary<string, WorkCategory>(StringComparer.OrdinalIgnoreCase);

        foreach (var (category, categoryAliases) in DefaultAliases)
        {
            aliases[NormalizeTag(category.DisplayName())] = category;
            foreach (var alias in categoryAliases)
            {
                aliases[NormalizeTag(alias)] = category;
            }
        }

        return new CategoryMapper(aliases);
    }

    public WorkCategory MapCategory(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            if (_aliases.TryGetValue(NormalizeTag(tag), out var category))
                return category;
        }

        return WorkCategory.Other;
    }

    public bool TryMapTag(string tag, out WorkCategory category)
    {
        return _aliases.TryGetValue(NormalizeTag(tag), out category);
    }

    private static readonly Lazy<CategoryMapper> _defaultInstance = new(CreateDefault);

    public static bool TryMapTagToCategory(string tag, out WorkCategory category)
    {
        return _defaultInstance.Value.TryMapTag(tag, out category);
    }

    public static bool HasAnyNormalizedTag(IEnumerable<string> tags, ISet<string> normalizedNeedles)
    {
        foreach (var tag in tags)
        {
            if (normalizedNeedles.Contains(NormalizeTag(tag)))
                return true;
        }

        return false;
    }

    public static HashSet<string> NormalizeSet(IEnumerable<string> tags)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var normalized = NormalizeTag(tag);
            if (!string.IsNullOrWhiteSpace(normalized))
                set.Add(normalized);
        }

        return set;
    }

    public static string NormalizeTag(string tag)
    {
        var builder = new StringBuilder(tag.Length);
        foreach (var ch in tag)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    public static IEnumerable<(WorkCategory Category, string Aliases)> GetDefaultAliasRows()
    {
        foreach (var category in WorkCategoryExtensions.CoreCategories)
        {
            var aliases = DefaultAliases.TryGetValue(category, out var values)
                ? string.Join(", ", values)
                : category.DisplayName();

            yield return (category, aliases);
        }
    }
}
