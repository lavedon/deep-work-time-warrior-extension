using DeepWork.Services;

namespace DeepWork.Models;

public sealed record GoalComponent(string Label, string NormalizedTag, WorkCategory? Category)
{
    public static GoalComponent ForTag(string tag)
    {
        var label = tag.Trim();
        var normalized = CategoryMapper.NormalizeTag(label);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Goal component tag must contain at least one letter or digit.");

        if (CategoryMapper.TryMapTagToCategory(label, out var category))
            return new GoalComponent(category.DisplayName(), normalized, category);

        return new GoalComponent(label, normalized, null);
    }

    public static GoalComponent ForCategory(WorkCategory category)
    {
        return new GoalComponent(
            category.DisplayName(),
            CategoryMapper.NormalizeTag(category.DisplayName()),
            category);
    }
}
