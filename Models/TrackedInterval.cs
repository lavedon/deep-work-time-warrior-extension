namespace DeepWork.Models;

public sealed record TrackedInterval(
    string Id,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string> Tags,
    WorkCategory Category,
    bool IsDeepWork)
{
    public TimeSpan Duration => End - Start;
    public DateOnly StartDate => DateOnly.FromDateTime(Start.LocalDateTime);
}
