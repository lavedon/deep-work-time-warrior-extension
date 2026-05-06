using System.Globalization;

namespace DeepWork.Services;

public static class DurationParser
{
    public static TimeSpan Parse(string value, string optionName)
    {
        if (TryParse(value, out var duration))
            return duration;

        throw new ArgumentException($"Invalid duration for {optionName}: '{value}'. Try values like 3h, 90m, 1h30m, or 01:30.");
    }

    public static bool TryParse(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim().ToLowerInvariant();

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
            return true;

        if (!value.Any(char.IsLetter))
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bareHours))
            {
                duration = TimeSpan.FromHours(bareHours);
                return duration >= TimeSpan.Zero;
            }

            return false;
        }

        var total = TimeSpan.Zero;
        var index = 0;
        var parsedAny = false;

        while (index < value.Length)
        {
            while (index < value.Length && (char.IsWhiteSpace(value[index]) || value[index] == ','))
                index++;

            if (index >= value.Length)
                break;

            var numberStart = index;
            while (index < value.Length && (char.IsDigit(value[index]) || value[index] == '.'))
                index++;

            if (numberStart == index)
                return false;

            var numberText = value[numberStart..index];
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return false;

            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;

            var unitStart = index;
            while (index < value.Length && char.IsLetter(value[index]))
                index++;

            var unit = value[unitStart..index];
            if (string.IsNullOrEmpty(unit))
                unit = "h";

            var segment = unit switch
            {
                "d" or "day" or "days" => TimeSpan.FromDays(number),
                "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(number),
                "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(number),
                "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(number),
                _ => TimeSpan.MinValue
            };

            if (segment == TimeSpan.MinValue)
                return false;

            total += segment;

            parsedAny = true;
        }

        duration = total;
        return parsedAny && duration >= TimeSpan.Zero;
    }
}
