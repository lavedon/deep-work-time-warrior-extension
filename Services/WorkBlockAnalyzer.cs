using DeepWork.Models;

namespace DeepWork.Services;

public sealed class WorkBlockAnalyzer
{
    public List<WorkBlock> BuildBlocksForDate(IEnumerable<TrackedInterval> intervals, DateOnly date, TimeSpan maxGap)
    {
        var dayStart = TimeAnalyzer.AtLocalStart(date);
        var dayEnd = TimeAnalyzer.AtLocalStart(date.AddDays(1));

        var clippedIntervals = intervals
            .Select(interval => Clip(interval, dayStart, dayEnd))
            .Where(interval => interval is not null)
            .Select(interval => interval!)
            .OrderBy(interval => interval.Start)
            .ThenBy(interval => interval.End)
            .ToList();

        return BuildBlocks(clippedIntervals, maxGap)
            .Where(block => block.Category != WorkCategory.Other)
            .ToList();
    }

    public List<WorkBlock> BuildBlocks(IEnumerable<TrackedInterval> intervals, TimeSpan maxGap)
    {
        var blocks = new List<WorkBlock>();
        MutableBlock? current = null;

        foreach (var interval in intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End))
        {
            if (current is null)
            {
                current = MutableBlock.FromInterval(interval);
                continue;
            }

            var sameCategory = current.Category == interval.Category;
            var gap = interval.Start > current.End ? interval.Start - current.End : TimeSpan.Zero;

            if (sameCategory && gap <= maxGap)
            {
                current.Add(interval);
            }
            else
            {
                blocks.Add(current.ToWorkBlock());
                current = MutableBlock.FromInterval(interval);
            }
        }

        if (current is not null)
            blocks.Add(current.ToWorkBlock());

        return blocks;
    }

    private static TrackedInterval? Clip(TrackedInterval interval, DateTimeOffset fromInclusive, DateTimeOffset toExclusive)
    {
        if (interval.End <= fromInclusive || interval.Start >= toExclusive)
            return null;

        var start = interval.Start < fromInclusive ? fromInclusive : interval.Start;
        var end = interval.End > toExclusive ? toExclusive : interval.End;

        if (end <= start)
            return null;

        return interval with { Start = start, End = end };
    }

    private sealed class MutableBlock
    {
        private MutableBlock(TrackedInterval interval)
        {
            Category = interval.Category;
            Start = interval.Start;
            End = interval.End;
            TrackedTime = interval.Duration;
            DeepWorkTime = interval.IsDeepWork ? interval.Duration : TimeSpan.Zero;
            IntervalCount = 1;
        }

        public WorkCategory Category { get; }
        public DateTimeOffset Start { get; }
        public DateTimeOffset End { get; private set; }
        public TimeSpan TrackedTime { get; private set; }
        public TimeSpan DeepWorkTime { get; private set; }
        public int IntervalCount { get; private set; }

        public static MutableBlock FromInterval(TrackedInterval interval) => new(interval);

        public void Add(TrackedInterval interval)
        {
            if (interval.End > End)
                End = interval.End;

            TrackedTime += interval.Duration;
            if (interval.IsDeepWork)
                DeepWorkTime += interval.Duration;

            IntervalCount++;
        }

        public WorkBlock ToWorkBlock() => new(Category, Start, End, TrackedTime, DeepWorkTime, IntervalCount);
    }
}
