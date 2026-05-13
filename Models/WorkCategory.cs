namespace DeepWork.Models;

public enum WorkCategory
{
    Job = 0,
    Leetcode = 1,
    Anki = 3,
    Okta = 4,
    Other = 99
}

public static class WorkCategoryExtensions
{
    public static string DisplayName(this WorkCategory category) => category switch
    {
        WorkCategory.Job => "Job",
        WorkCategory.Leetcode => "Leetcode",
        WorkCategory.Anki => "Anki",
        WorkCategory.Okta => "Okta",
        _ => "Other"
    };

    public static string Color(this WorkCategory category) => category switch
    {
        WorkCategory.Job => "green",
        WorkCategory.Leetcode => "red",
        WorkCategory.Anki => "yellow",
        WorkCategory.Okta => "magenta",
        _ => "grey"
    };

    public static IReadOnlyList<WorkCategory> CoreCategories { get; } =
    [
        WorkCategory.Job,
        WorkCategory.Leetcode,
        WorkCategory.Anki,
        WorkCategory.Okta
    ];
}
