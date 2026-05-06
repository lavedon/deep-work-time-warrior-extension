using System.Globalization;
using System.Text.Json;
using DeepWork.Models;

namespace DeepWork.Services;

public sealed class TimewarriorExportParser
{
    private readonly CategoryMapper _categoryMapper;

    public TimewarriorExportParser(CategoryMapper categoryMapper)
    {
        _categoryMapper = categoryMapper;
    }

    public List<TrackedInterval> Parse(
        string json,
        ISet<string> normalizedDeepTags,
        bool allTrackedIsDeep,
        DateTimeOffset now,
        DateTimeOffset? fromInclusive = null,
        DateTimeOffset? toExclusive = null)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected Timewarrior export JSON to be an array.");

        var intervals = new List<TrackedInterval>();
        var index = 0;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            if (!TryGetString(element, "start", out var startText))
                continue;

            var start = ParseTimestamp(startText);
            var end = TryGetString(element, "end", out var endText)
                ? ParseTimestamp(endText)
                : now;

            if (end <= start)
                continue;

            if (fromInclusive is not null && end <= fromInclusive.Value)
                continue;

            if (toExclusive is not null && start >= toExclusive.Value)
                continue;

            if (fromInclusive is not null && start < fromInclusive.Value)
                start = fromInclusive.Value;

            if (toExclusive is not null && end > toExclusive.Value)
                end = toExclusive.Value;

            if (end <= start)
                continue;

            var tags = ReadTags(element);
            var category = _categoryMapper.MapCategory(tags);
            var isDeep = allTrackedIsDeep || CategoryMapper.HasAnyNormalizedTag(tags, normalizedDeepTags);
            var id = ReadId(element, index);

            intervals.Add(new TrackedInterval(id, start, end, tags, category, isDeep));
        }

        return intervals
            .OrderBy(interval => interval.Start)
            .ThenBy(interval => interval.End)
            .ToList();
    }

    private static IReadOnlyList<string> ReadTags(JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Array)
            return [];

        var tags = new List<string>();
        foreach (var tagElement in tagsElement.EnumerateArray())
        {
            if (tagElement.ValueKind == JsonValueKind.String)
            {
                var tag = tagElement.GetString();
                if (!string.IsNullOrWhiteSpace(tag))
                    tags.Add(tag);
            }
        }

        return tags;
    }

    private static string ReadId(JsonElement element, int fallbackIndex)
    {
        if (!element.TryGetProperty("id", out var idElement))
            return fallbackIndex.ToString(CultureInfo.InvariantCulture);

        return idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? fallbackIndex.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number => idElement.GetRawText(),
            _ => fallbackIndex.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParseExact(
                value,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var utcTimewTimestamp))
        {
            return utcTimewTimestamp.ToLocalTime();
        }

        if (DateTimeOffset.TryParseExact(
                value,
                "yyyyMMdd'T'HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var localTimewTimestamp))
        {
            return localTimewTimestamp.ToLocalTime();
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsedTimestamp))
        {
            return parsedTimestamp.ToLocalTime();
        }

        throw new InvalidOperationException($"Could not parse Timewarrior timestamp '{value}'.");
    }
}
