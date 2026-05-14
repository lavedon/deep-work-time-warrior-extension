using DeepWork.Models;

namespace DeepWork.Services;

public enum GoalCadence
{
    Daily,
    Weekly
}

public sealed record DeepWorkGoal
{
    private static readonly IReadOnlySet<DayOfWeek> EmptySkipDays = new HashSet<DayOfWeek>();

    private DeepWorkGoal(
        string label,
        IReadOnlyList<GoalComponent> components,
        TimeSpan duration,
        GoalCadence cadence,
        string color,
        IReadOnlySet<DayOfWeek> skipDays)
    {
        Label = label;
        Components = components;
        Duration = duration;
        Cadence = cadence;
        Color = color;
        SkipDays = skipDays;
    }

    public string Label { get; }
    public IReadOnlyList<GoalComponent> Components { get; }
    public TimeSpan Duration { get; }
    public GoalCadence Cadence { get; }
    public string Color { get; }
    public IReadOnlySet<DayOfWeek> SkipDays { get; }

    public bool IsComposite => Components.Count > 1;
    public string DisplayName => Label;

    public WorkCategory? Category => Components.Count == 1 ? Components[0].Category : null;
    public string NormalizedTag => Components.Count == 1 ? Components[0].NormalizedTag : string.Empty;

    public DeepWorkGoal WithSkipDays(IEnumerable<DayOfWeek>? skipDays)
    {
        return new DeepWorkGoal(Label, Components, Duration, Cadence, Color, BuildSkipDays(skipDays));
    }

    public DeepWorkGoal WithDuration(TimeSpan duration)
    {
        return new DeepWorkGoal(Label, Components, duration, Cadence, Color, SkipDays);
    }

    public static DeepWorkGoal ForTag(
        string tag,
        TimeSpan duration,
        GoalCadence cadence = GoalCadence.Daily,
        IEnumerable<DayOfWeek>? skipDays = null)
    {
        var label = tag.Trim();
        var normalized = CategoryMapper.NormalizeTag(label);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Goal tag must contain at least one letter or digit.");

        var component = new GoalComponent(label, normalized, Category: null);
        return new DeepWorkGoal(label, [component], duration, cadence, "green", BuildSkipDays(skipDays));
    }

    public static DeepWorkGoal ForCategory(
        WorkCategory category,
        TimeSpan duration,
        GoalCadence cadence = GoalCadence.Daily,
        IEnumerable<DayOfWeek>? skipDays = null)
    {
        var component = GoalComponent.ForCategory(category);
        return new DeepWorkGoal(category.DisplayName(), [component], duration, cadence, category.Color(), BuildSkipDays(skipDays));
    }

    public static DeepWorkGoal Composite(
        string label,
        IEnumerable<GoalComponent> components,
        TimeSpan duration,
        GoalCadence cadence = GoalCadence.Daily,
        IEnumerable<DayOfWeek>? skipDays = null)
    {
        var list = components.ToArray();
        if (list.Length == 0)
            throw new ArgumentException("Composite goal must have at least one component.");

        if (list.Length == 1)
        {
            var only = list[0];
            return only.Category is WorkCategory cat
                ? ForCategory(cat, duration, cadence, skipDays)
                : ForTag(only.Label, duration, cadence, skipDays);
        }

        var displayLabel = string.IsNullOrWhiteSpace(label)
            ? string.Join("+", list.Select(c => c.Label))
            : label.Trim();

        return new DeepWorkGoal(displayLabel, list, duration, cadence, "blue", BuildSkipDays(skipDays));
    }

    private static IReadOnlySet<DayOfWeek> BuildSkipDays(IEnumerable<DayOfWeek>? days)
    {
        if (days is null)
            return EmptySkipDays;

        var set = new HashSet<DayOfWeek>(days);
        return set.Count == 0 ? EmptySkipDays : set;
    }
}
