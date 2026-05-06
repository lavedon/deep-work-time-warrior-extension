using DeepWork.Models;

namespace DeepWork.Services;

public sealed class AppOptions
{
    public string Command { get; set; } = "dashboard";
    public int Days { get; set; } = 30;
    public bool DaysSpecified { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public TimeSpan MaxGap { get; set; } = TimeSpan.FromHours(2);
    public List<DailyGoal> Goals { get; } = [];
    public string? ExportFile { get; set; }
    public bool Help { get; set; }
    public bool ListAliases { get; set; }
    public HashSet<string> NonDeepTags { get; } = CategoryMapper.NormalizeSet(CategoryMapper.DefaultNonDeepTags);

    public IReadOnlyList<DailyGoal> EffectiveGoals => Goals.Count == 0
        ? [DailyGoal.ForCategory(WorkCategory.Job, TimeSpan.FromHours(3))]
        : Goals;

    public DateOnly EffectiveTo(DateOnly today) => To ?? today;

    public DateOnly EffectiveFrom(DateOnly today)
    {
        if (From is not null)
            return From.Value;

        return EffectiveTo(today).AddDays(-(Days - 1));
    }
}
