using DeepWork.Models;

namespace DeepWork.Services;

public sealed class TimeAnalyzer
{
    public Dictionary<DateOnly, TimeSpan> GetDailyTotals(
        IEnumerable<TrackedInterval> intervals,
        DateOnly fromInclusive,
        DateOnly toInclusive,
        Func<TrackedInterval, bool>? predicate = null)
    {
        var dailyTotals = InitializeDailyTotals(fromInclusive, toInclusive);
        predicate ??= _ => true;

        foreach (var interval in intervals.Where(predicate))
        {
            foreach (var (date, duration) in SplitByLocalDay(interval.Start, interval.End))
            {
                if (date >= fromInclusive && date <= toInclusive)
                    dailyTotals[date] += duration;
            }
        }

        return dailyTotals;
    }

    public Dictionary<WorkCategory, Dictionary<DateOnly, TimeSpan>> GetDailyTotalsByCategory(
        IEnumerable<TrackedInterval> intervals,
        DateOnly fromInclusive,
        DateOnly toInclusive,
        bool deepOnly)
    {
        var result = new Dictionary<WorkCategory, Dictionary<DateOnly, TimeSpan>>();

        foreach (var category in WorkCategoryExtensions.CoreCategories)
        {
            result[category] = GetDailyTotals(
                intervals,
                fromInclusive,
                toInclusive,
                interval => interval.Category == category && (!deepOnly || interval.IsDeepWork));
        }

        return result;
    }

    public Dictionary<DateOnly, TimeSpan> SumCategoryTotals(
        IReadOnlyDictionary<WorkCategory, Dictionary<DateOnly, TimeSpan>> totalsByCategory,
        DateOnly fromInclusive,
        DateOnly toInclusive)
    {
        var totals = InitializeDailyTotals(fromInclusive, toInclusive);

        foreach (var categoryTotals in totalsByCategory.Values)
        {
            foreach (var (date, duration) in categoryTotals)
            {
                if (date >= fromInclusive && date <= toInclusive)
                    totals[date] += duration;
            }
        }

        return totals;
    }

    public TimeSpan CalculateRollingAverage(Dictionary<DateOnly, TimeSpan> dailyTotals, DateOnly endDate, int windowDays)
    {
        if (windowDays <= 0)
            return TimeSpan.Zero;

        var startDate = endDate.AddDays(-(windowDays - 1));
        var totalTicks = 0L;

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            totalTicks += dailyTotals.TryGetValue(date, out var total) ? total.Ticks : 0;
        }

        return TimeSpan.FromTicks(totalTicks / windowDays);
    }

    public TimeSpan GetTotalForDay(Dictionary<DateOnly, TimeSpan> dailyTotals, DateOnly date)
    {
        return dailyTotals.TryGetValue(date, out var total) ? total : TimeSpan.Zero;
    }

    public static DateOnly GetWeekStart(DateOnly date)
    {
        var daysFromMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-daysFromMonday);
    }

    public TimeSpan GetWeekTotal(Dictionary<DateOnly, TimeSpan> dailyTotals, DateOnly weekStart)
    {
        var totalTicks = 0L;
        for (var i = 0; i < 7; i++)
        {
            var date = weekStart.AddDays(i);
            if (dailyTotals.TryGetValue(date, out var dayTotal))
                totalTicks += dayTotal.Ticks;
        }

        return TimeSpan.FromTicks(totalTicks);
    }

    public int CountGoalDays(Dictionary<DateOnly, TimeSpan> dailyTotals, DateOnly fromInclusive, DateOnly toInclusive, TimeSpan goal)
    {
        var count = 0;
        for (var date = fromInclusive; date <= toInclusive; date = date.AddDays(1))
        {
            if (GetTotalForDay(dailyTotals, date) >= goal)
                count++;
        }

        return count;
    }

    public int CalculateCurrentStreak(Dictionary<DateOnly, TimeSpan> dailyTotals, DateOnly endDate, TimeSpan goal)
    {
        if (dailyTotals.Count == 0)
            return 0;

        var earliest = dailyTotals.Keys.Min();
        var streak = 0;

        for (var date = endDate; date >= earliest; date = date.AddDays(-1))
        {
            if (GetTotalForDay(dailyTotals, date) < goal)
                break;

            streak++;
        }

        return streak;
    }

    public int CalculateLongestStreak(Dictionary<DateOnly, TimeSpan> dailyTotals, DateOnly fromInclusive, DateOnly toInclusive, TimeSpan goal)
    {
        var current = 0;
        var longest = 0;

        for (var date = fromInclusive; date <= toInclusive; date = date.AddDays(1))
        {
            if (GetTotalForDay(dailyTotals, date) >= goal)
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }

    public static DateTimeOffset AtLocalStart(DateOnly date)
    {
        var localDateTime = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return new DateTimeOffset(localDateTime);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        return $"{hours}h {minutes:D2}m";
    }

    public static string FormatPercent(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
            ratio = 0;

        return $"{ratio * 100:0}%";
    }

    private static Dictionary<DateOnly, TimeSpan> InitializeDailyTotals(DateOnly fromInclusive, DateOnly toInclusive)
    {
        var dailyTotals = new Dictionary<DateOnly, TimeSpan>();
        for (var date = fromInclusive; date <= toInclusive; date = date.AddDays(1))
        {
            dailyTotals[date] = TimeSpan.Zero;
        }

        return dailyTotals;
    }

    private static IEnumerable<(DateOnly Date, TimeSpan Duration)> SplitByLocalDay(DateTimeOffset start, DateTimeOffset end)
    {
        var cursor = start;

        while (cursor < end)
        {
            var date = DateOnly.FromDateTime(cursor.LocalDateTime);
            var nextDayStart = AtLocalStart(date.AddDays(1));
            var segmentEnd = end < nextDayStart ? end : nextDayStart;

            if (segmentEnd <= cursor)
                yield break;

            yield return (date, segmentEnd - cursor);
            cursor = segmentEnd;
        }
    }
}
