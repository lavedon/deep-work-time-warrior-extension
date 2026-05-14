namespace DeepWork.Services;

public static class DayOfWeekParser
{
    public static readonly IReadOnlyList<DayOfWeek> WeekOrder =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    ];

    public static bool TryParse(string value, out DayOfWeek day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "mon":
            case "monday":
                day = DayOfWeek.Monday;
                return true;
            case "tue":
            case "tues":
            case "tuesday":
                day = DayOfWeek.Tuesday;
                return true;
            case "wed":
            case "weds":
            case "wednesday":
                day = DayOfWeek.Wednesday;
                return true;
            case "thu":
            case "thur":
            case "thurs":
            case "thursday":
                day = DayOfWeek.Thursday;
                return true;
            case "fri":
            case "friday":
                day = DayOfWeek.Friday;
                return true;
            case "sat":
            case "saturday":
                day = DayOfWeek.Saturday;
                return true;
            case "sun":
            case "sunday":
                day = DayOfWeek.Sunday;
                return true;
            default:
                return false;
        }
    }

    public static IReadOnlySet<DayOfWeek> ParseList(string raw, string optionName)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new HashSet<DayOfWeek>();

        var set = new HashSet<DayOfWeek>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParse(part, out var day))
                throw new ArgumentException($"{optionName}: invalid day of week '{part}'. Use mon/tue/wed/thu/fri/sat/sun.");

            set.Add(day);
        }

        return set;
    }

    public static string FormatList(IReadOnlySet<DayOfWeek> days)
    {
        if (days.Count == 0)
            return "none";

        var ordered = WeekOrder.Where(days.Contains).Select(day => day.ToString()[..3]);
        return string.Join(", ", ordered);
    }
}
