using System.Text.Json;
using DeepWork.Models;

namespace DeepWork.Services;

public sealed class GoalStore
{
    private const int CurrentVersion = 1;

    private GoalStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static GoalStore Create(string? overridePath = null)
    {
        var path = string.IsNullOrWhiteSpace(overridePath)
            ? GetDefaultPath()
            : ExpandHomeDirectory(overridePath.Trim());

        return new GoalStore(path);
    }

    public async Task<List<DeepWorkGoal>> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return [];

        var json = await File.ReadAllTextAsync(FilePath);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var goalsElement = GetGoalsElement(document.RootElement);
            var goals = new List<DeepWorkGoal>();
            var index = 0;

            foreach (var goalElement in goalsElement.EnumerateArray())
            {
                index++;
                goals.Add(ReadGoal(goalElement, index));
            }

            return Merge(Array.Empty<DeepWorkGoal>(), goals);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Could not parse goals file '{FilePath}': {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not read goals file '{FilePath}': {ex.Message}", ex);
        }
    }

    public async Task SaveAsync(IReadOnlyList<DeepWorkGoal> goals)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(FilePath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteNumber("version", CurrentVersion);
        writer.WritePropertyName("goals");
        writer.WriteStartArray();

        foreach (var goal in goals)
            WriteGoal(writer, goal);

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync();
    }

    public Task ClearAsync()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);

        return Task.CompletedTask;
    }

    public static List<DeepWorkGoal> Merge(IReadOnlyList<DeepWorkGoal> savedGoals, IReadOnlyList<DeepWorkGoal> newGoals)
    {
        var merged = savedGoals.ToList();
        var indexesByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < merged.Count; i++)
            indexesByKey[GetGoalKey(merged[i])] = i;

        foreach (var goal in newGoals)
        {
            var key = GetGoalKey(goal);
            if (indexesByKey.TryGetValue(key, out var existingIndex))
            {
                merged[existingIndex] = goal;
            }
            else
            {
                indexesByKey[key] = merged.Count;
                merged.Add(goal);
            }
        }

        return merged;
    }

    private static JsonElement GetGoalsElement(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;

        if (root.ValueKind == JsonValueKind.Object
            && TryGetProperty(root, "goals", out var goalsElement)
            && goalsElement.ValueKind == JsonValueKind.Array)
        {
            return goalsElement;
        }

        throw new InvalidOperationException("Goals file must be a JSON object with a 'goals' array.");
    }

    private static DeepWorkGoal ReadGoal(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Goal #{index} in the goals file must be a JSON object.");

        var cadence = GoalCadence.Daily;
        if (TryGetString(element, "cadence", out var cadenceText))
            cadence = ParseCadence(cadenceText, index);

        if (!TryGetString(element, "duration", out var durationText))
            throw new InvalidOperationException($"Goal #{index} in the goals file is missing a duration.");

        var duration = DurationParser.Parse(durationText, $"goals file goal #{index}");

        if (TryGetString(element, "category", out var categoryText))
        {
            if (!TryParseCategory(categoryText, out var category))
                throw new InvalidOperationException($"Goal #{index} in the goals file has unknown category '{categoryText}'.");

            return DeepWorkGoal.ForCategory(category, duration, cadence);
        }

        if (TryGetString(element, "tag", out var tag))
            return DeepWorkGoal.ForTag(tag, duration, cadence);

        throw new InvalidOperationException($"Goal #{index} in the goals file must specify either 'tag' or 'category'.");
    }

    private static void WriteGoal(Utf8JsonWriter writer, DeepWorkGoal goal)
    {
        writer.WriteStartObject();
        writer.WriteString("cadence", goal.Cadence == GoalCadence.Weekly ? "weekly" : "daily");

        if (goal.Category is WorkCategory category)
            writer.WriteString("category", category.DisplayName());
        else
            writer.WriteString("tag", goal.Label);

        writer.WriteString("duration", FormatDurationForConfig(goal.Duration));
        writer.WriteEndObject();
    }

    private static GoalCadence ParseCadence(string value, int index)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "daily" or "day" => GoalCadence.Daily,
            "weekly" or "week" => GoalCadence.Weekly,
            _ => throw new InvalidOperationException($"Goal #{index} in the goals file has invalid cadence '{value}'.")
        };
    }

    private static bool TryParseCategory(string value, out WorkCategory category)
    {
        foreach (var candidate in Enum.GetValues<WorkCategory>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase)
                || CategoryMapper.NormalizeTag(value) == CategoryMapper.NormalizeTag(candidate.DisplayName()))
            {
                category = candidate;
                return true;
            }
        }

        category = WorkCategory.Other;
        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, propertyName, out var property))
            return false;

        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                value = property.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            case JsonValueKind.Number:
                value = property.GetRawText();
                return !string.IsNullOrWhiteSpace(value);
            default:
                return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string GetGoalKey(DeepWorkGoal goal)
    {
        var target = goal.Category is WorkCategory category
            ? $"category:{category}"
            : $"tag:{goal.NormalizedTag}";

        return $"{goal.Cadence}:{target}";
    }

    private static string FormatDurationForConfig(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var totalSeconds = (long)Math.Round(duration.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        var parts = new List<string>();

        if (hours > 0)
            parts.Add($"{hours}h");

        if (minutes > 0)
            parts.Add($"{minutes}m");

        if (seconds > 0)
            parts.Add($"{seconds}s");

        return parts.Count == 0 ? "0m" : string.Concat(parts);
    }

    private static string GetDefaultPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configHome = string.IsNullOrWhiteSpace(home)
                ? Environment.CurrentDirectory
                : Path.Combine(home, ".config");
        }

        return Path.Combine(configHome, "deepwork", "goals.json");
    }

    private static string ExpandHomeDirectory(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, path[2..]);
        }

        return path;
    }
}
