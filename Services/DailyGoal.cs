using DeepWork.Models;

namespace DeepWork.Services;

public sealed record DailyGoal
{
    private DailyGoal(string label, string normalizedTag, WorkCategory? category, TimeSpan duration)
    {
        Label = label;
        NormalizedTag = normalizedTag;
        Category = category;
        Duration = duration;
    }

    public string Label { get; }
    public string NormalizedTag { get; }
    public WorkCategory? Category { get; }
    public TimeSpan Duration { get; }

    public string DisplayName => Category?.DisplayName() ?? Label;
    public string Color => Category?.Color() ?? "green";

    public static DailyGoal ForTag(string tag, TimeSpan duration)
    {
        var label = tag.Trim();
        var normalized = CategoryMapper.NormalizeTag(label);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Goal tag must contain at least one letter or digit.");

        return new DailyGoal(label, normalized, category: null, duration);
    }

    public static DailyGoal ForCategory(WorkCategory category, TimeSpan duration)
    {
        return new DailyGoal(category.DisplayName(), CategoryMapper.NormalizeTag(category.DisplayName()), category, duration);
    }
}
