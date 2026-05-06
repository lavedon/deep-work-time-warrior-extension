namespace DeepWork.Models;

public sealed record WorkBlock(
    WorkCategory Category,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan TrackedTime,
    TimeSpan DeepWorkTime,
    int IntervalCount)
{
    public TimeSpan ScheduledTime => End - Start;

    public double EfficiencyRatio => Ratio(DeepWorkTime, ScheduledTime);
    public double FocusRatio => Ratio(DeepWorkTime, TrackedTime);
    public double TrackingCoverage => Ratio(TrackedTime, ScheduledTime);

    private static double Ratio(TimeSpan numerator, TimeSpan denominator)
    {
        if (denominator <= TimeSpan.Zero)
            return 0;

        return numerator.TotalSeconds / denominator.TotalSeconds;
    }
}
