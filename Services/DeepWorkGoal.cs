using DeepWork.Models;

namespace DeepWork.Services;

public enum GoalCadence
{
    Daily,
    Weekly
}

public sealed record DeepWorkGoal
{
    private DeepWorkGoal(
        string label,
        IReadOnlyList<GoalComponent> components,
        TimeSpan duration,
        GoalCadence cadence,
        string color)
    {
        Label = label;
        Components = components;
        Duration = duration;
        Cadence = cadence;
        Color = color;
    }

    public string Label { get; }
    public IReadOnlyList<GoalComponent> Components { get; }
    public TimeSpan Duration { get; }
    public GoalCadence Cadence { get; }
    public string Color { get; }

    public bool IsComposite => Components.Count > 1;
    public string DisplayName => Label;

    public WorkCategory? Category => Components.Count == 1 ? Components[0].Category : null;
    public string NormalizedTag => Components.Count == 1 ? Components[0].NormalizedTag : string.Empty;

    public static DeepWorkGoal ForTag(string tag, TimeSpan duration, GoalCadence cadence = GoalCadence.Daily)
    {
        var label = tag.Trim();
        var normalized = CategoryMapper.NormalizeTag(label);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Goal tag must contain at least one letter or digit.");

        var component = new GoalComponent(label, normalized, Category: null);
        return new DeepWorkGoal(label, [component], duration, cadence, "green");
    }

    public static DeepWorkGoal ForCategory(WorkCategory category, TimeSpan duration, GoalCadence cadence = GoalCadence.Daily)
    {
        var component = GoalComponent.ForCategory(category);
        return new DeepWorkGoal(category.DisplayName(), [component], duration, cadence, category.Color());
    }

    public static DeepWorkGoal Composite(
        string label,
        IEnumerable<GoalComponent> components,
        TimeSpan duration,
        GoalCadence cadence = GoalCadence.Daily)
    {
        var list = components.ToArray();
        if (list.Length == 0)
            throw new ArgumentException("Composite goal must have at least one component.");

        if (list.Length == 1)
        {
            var only = list[0];
            return only.Category is WorkCategory cat
                ? ForCategory(cat, duration, cadence)
                : ForTag(only.Label, duration, cadence);
        }

        var displayLabel = string.IsNullOrWhiteSpace(label)
            ? string.Join("+", list.Select(c => c.Label))
            : label.Trim();

        return new DeepWorkGoal(displayLabel, list, duration, cadence, "blue");
    }
}
