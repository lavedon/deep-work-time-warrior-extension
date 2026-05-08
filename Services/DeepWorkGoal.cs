using DeepWork.Models;

namespace DeepWork.Services;

public enum GoalCadence
{
    Daily,
    Weekly
}

public sealed record DeepWorkGoal
{
    private DeepWorkGoal(string label, string normalizedTag, WorkCategory? category, TimeSpan duration, GoalCadence cadence)
    {
        Label = label;
        NormalizedTag = normalizedTag;
        Category = category;
        Duration = duration;
        Cadence = cadence;
    }

    public string Label { get; }
    public string NormalizedTag { get; }
    public WorkCategory? Category { get; }
    public TimeSpan Duration { get; }
    public GoalCadence Cadence { get; }

    public string DisplayName => Category?.DisplayName() ?? Label;
    public string Color => Category?.Color() ?? "green";

    public static DeepWorkGoal ForTag(string tag, TimeSpan duration, GoalCadence cadence = GoalCadence.Daily)
    {
        var label = tag.Trim();
        var normalized = CategoryMapper.NormalizeTag(label);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Goal tag must contain at least one letter or digit.");

        return new DeepWorkGoal(label, normalized, category: null, duration, cadence);
    }

    public static DeepWorkGoal ForCategory(WorkCategory category, TimeSpan duration, GoalCadence cadence = GoalCadence.Daily)
    {
        return new DeepWorkGoal(category.DisplayName(), CategoryMapper.NormalizeTag(category.DisplayName()), category, duration, cadence);
    }
}
