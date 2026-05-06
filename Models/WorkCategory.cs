namespace DeepWork.Models;

public enum WorkCategory
{
    Job = 0,
    LCReview = 1,
    LCNew = 2,
    Anki = 3,
    Okta = 4,
    Other = 99
}

public static class WorkCategoryExtensions
{
    public static string DisplayName(this WorkCategory category) => category switch
    {
        WorkCategory.Job => "Job",
        WorkCategory.LCReview => "LCReview",
        WorkCategory.LCNew => "LCNew",
        WorkCategory.Anki => "Anki",
        WorkCategory.Okta => "Okta",
        _ => "Other"
    };

    public static string Color(this WorkCategory category) => category switch
    {
        WorkCategory.Job => "green",
        WorkCategory.LCReview => "red",
        WorkCategory.LCNew => "cyan",
        WorkCategory.Anki => "yellow",
        WorkCategory.Okta => "magenta",
        _ => "grey"
    };

    public static IReadOnlyList<WorkCategory> CoreCategories { get; } =
    [
        WorkCategory.Job,
        WorkCategory.LCReview,
        WorkCategory.LCNew,
        WorkCategory.Anki,
        WorkCategory.Okta
    ];
}
