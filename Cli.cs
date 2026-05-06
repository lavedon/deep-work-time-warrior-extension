using System.Globalization;
using DeepWork.Models;
using DeepWork.Services;
using Spectre.Console;

namespace DeepWork;

public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        AppOptions options;
        try
        {
            options = ParseArgs(args);
        }
        catch (Exception ex)
        {
            PrintError(ex.Message);
            AnsiConsole.MarkupLine("[grey]Run:[/] deepwork --help");
            return 1;
        }

        options.Command = NormalizeCommand(options.Command);

        if (options.Help || options.Command == "help")
        {
            RenderHelp();
            return 0;
        }

        if (options.ListAliases || options.Command == "aliases")
        {
            RenderAliases(options);
            return 0;
        }

        if (options.Command is not ("dashboard" or "blocks"))
        {
            PrintError($"Unknown command '{options.Command}'.");
            AnsiConsole.MarkupLine("[grey]Run:[/] deepwork --help");
            return 1;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (options.Command == "blocks" && options.From is null && options.To is null && !options.DaysSpecified)
            options.Days = 1;

        var requestedTo = options.EffectiveTo(today);
        var requestedFrom = options.EffectiveFrom(today);

        if (requestedFrom > requestedTo)
        {
            PrintError("--from must be on or before --to.");
            return 1;
        }

        var analysisFrom = requestedFrom;
        if (options.Command == "dashboard")
        {
            analysisFrom = MinDate(
                requestedFrom,
                requestedTo.AddDays(-29),
                TimeAnalyzer.GetWeekStart(requestedTo).AddDays(-7));
        }

        List<TrackedInterval> intervals;
        try
        {
            intervals = await LoadIntervalsAsync(options, analysisFrom, requestedTo);
        }
        catch (Exception ex)
        {
            PrintError(ex.Message);
            return 1;
        }

        if (options.Command == "dashboard")
        {
            RenderDashboard(options, intervals, analysisFrom, requestedFrom, requestedTo, today);
        }
        else
        {
            RenderBlocksRange(options, intervals, requestedFrom, requestedTo);
        }

        return 0;
    }

    private static async Task<List<TrackedInterval>> LoadIntervalsAsync(AppOptions options, DateOnly fromInclusive, DateOnly toInclusive)
    {
        var toExclusive = toInclusive.AddDays(1);
        string json;

        if (!string.IsNullOrWhiteSpace(options.ExportFile))
        {
            json = await File.ReadAllTextAsync(options.ExportFile);
        }
        else
        {
            json = await new TimewarriorClient().ExportAsync(fromInclusive, toExclusive);
        }

        var parser = new TimewarriorExportParser(CategoryMapper.CreateDefault());
        return parser.Parse(
            json,
            options.NonDeepTags,
            DateTimeOffset.Now,
            TimeAnalyzer.AtLocalStart(fromInclusive),
            TimeAnalyzer.AtLocalStart(toExclusive));
    }

    private static void RenderDashboard(
        AppOptions options,
        List<TrackedInterval> intervals,
        DateOnly analysisFrom,
        DateOnly requestedFrom,
        DateOnly to,
        DateOnly today)
    {
        var analyzer = new TimeAnalyzer();
        var deepByCategory = analyzer.GetDailyTotalsByCategory(intervals, analysisFrom, to, deepOnly: true);
        var totalDeep = analyzer.GetDailyTotals(intervals, analysisFrom, to, interval => interval.IsDeepWork);

        AnsiConsole.Write(new Rule("[bold blue]Deep Work / Timewarrior Analytics[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();

        var source = string.IsNullOrWhiteSpace(options.ExportFile)
            ? "timew export"
            : options.ExportFile;
        AnsiConsole.MarkupLine($"[grey]Source:[/] {Markup.Escape(source)}");
        AnsiConsole.MarkupLine($"[grey]Analysis range:[/] {analysisFrom:yyyy-MM-dd} → {to:yyyy-MM-dd}");
        AnsiConsole.MarkupLine($"[grey]Deep work rule:[/] tracked intervals count as deep unless tagged non-deep ({Markup.Escape(FormatTagList(options.NonDeepTags))})");

        if (intervals.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No intervals found for this range.[/]");
        }
        else if (!intervals.Any(interval => interval.IsDeepWork))
        {
            AnsiConsole.MarkupLine("[yellow]All intervals in this range have non-deep tags.[/]");
        }

        var uncategorizedCount = intervals.Count(interval => interval.Category == WorkCategory.Other);
        if (uncategorizedCount > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{uncategorizedCount} interval(s) were uncategorized.[/] Run [grey]deepwork aliases[/] to see default tag aliases.");
        }

        AnsiConsole.WriteLine();
        RenderSummaryTable(analyzer, deepByCategory, totalDeep, to, today);
        AnsiConsole.WriteLine();
        RenderJobGoal(analyzer, deepByCategory[WorkCategory.Job], requestedFrom, to, options.JobDailyGoal, today);
        AnsiConsole.WriteLine();
        RenderBlocksForDate(options, intervals, to, to == today ? "Today's Work Blocks" : $"Work Blocks for {to:yyyy-MM-dd}");
        AnsiConsole.WriteLine();
        RenderLastSevenDays(analyzer, deepByCategory, totalDeep, to);
    }

    private static void RenderSummaryTable(
        TimeAnalyzer analyzer,
        IReadOnlyDictionary<WorkCategory, Dictionary<DateOnly, TimeSpan>> deepByCategory,
        Dictionary<DateOnly, TimeSpan> totalDeep,
        DateOnly to,
        DateOnly today)
    {
        var yesterday = to.AddDays(-1);
        var thisWeekStart = TimeAnalyzer.GetWeekStart(to);
        var lastWeekStart = thisWeekStart.AddDays(-7);

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Deep Work Summary[/]")
            .AddColumn("Metric")
            .AddColumn(new TableColumn("[bold blue]Total[/]").Centered());

        foreach (var category in WorkCategoryExtensions.CoreCategories)
            summaryTable.AddColumn(new TableColumn($"[bold {category.Color()}]{category.DisplayName()}[/]").Centered());

        AddSummaryRow(
            summaryTable,
            to == today ? "Today" : to.ToString("MMM dd", CultureInfo.InvariantCulture),
            ColorDuration(analyzer.GetTotalForDay(totalDeep, to), "blue"),
            category => ColorDuration(analyzer.GetTotalForDay(deepByCategory[category], to), category.Color()));

        AddSummaryRow(
            summaryTable,
            to == today ? "Yesterday" : yesterday.ToString("MMM dd", CultureInfo.InvariantCulture),
            ColorDuration(analyzer.GetTotalForDay(totalDeep, yesterday), "blue"),
            category => ColorDuration(analyzer.GetTotalForDay(deepByCategory[category], yesterday), category.Color()));

        AddSummaryRow(
            summaryTable,
            $"[bold]This Week[/] [grey](Mon {thisWeekStart:MMM dd})[/]",
            ColorDuration(analyzer.GetWeekTotal(totalDeep, thisWeekStart), "blue", bold: true),
            category => ColorDuration(analyzer.GetWeekTotal(deepByCategory[category], thisWeekStart), category.Color(), bold: true));

        AddSummaryRow(
            summaryTable,
            $"Last Week [grey](Mon {lastWeekStart:MMM dd})[/]",
            ColorDuration(analyzer.GetWeekTotal(totalDeep, lastWeekStart), "blue"),
            category => ColorDuration(analyzer.GetWeekTotal(deepByCategory[category], lastWeekStart), category.Color()));

        AddSummaryRow(
            summaryTable,
            "[bold]7-day avg[/]",
            ColorDuration(analyzer.CalculateRollingAverage(totalDeep, to, 7), "blue", bold: true),
            category => ColorDuration(analyzer.CalculateRollingAverage(deepByCategory[category], to, 7), category.Color(), bold: true));

        AddSummaryRow(
            summaryTable,
            "[bold]30-day avg[/]",
            ColorDuration(analyzer.CalculateRollingAverage(totalDeep, to, 30), "blue", bold: true),
            category => ColorDuration(analyzer.CalculateRollingAverage(deepByCategory[category], to, 30), category.Color(), bold: true));

        AnsiConsole.Write(summaryTable);
    }

    private static void AddSummaryRow(Table table, string label, string total, Func<WorkCategory, string> categoryValueFactory)
    {
        var row = new List<string> { label, total };
        row.AddRange(WorkCategoryExtensions.CoreCategories.Select(categoryValueFactory));
        table.AddRow(row.ToArray());
    }

    private static void RenderJobGoal(
        TimeAnalyzer analyzer,
        Dictionary<DateOnly, TimeSpan> jobDailyDeepTotals,
        DateOnly from,
        DateOnly to,
        TimeSpan dailyGoal,
        DateOnly today)
    {
        var selectedDayTotal = analyzer.GetTotalForDay(jobDailyDeepTotals, to);
        var remaining = dailyGoal > selectedDayTotal ? dailyGoal - selectedDayTotal : TimeSpan.Zero;
        var goalDays = analyzer.CountGoalDays(jobDailyDeepTotals, from, to, dailyGoal);
        var totalDays = to.DayNumber - from.DayNumber + 1;
        var currentStreak = analyzer.CalculateCurrentStreak(jobDailyDeepTotals, to, dailyGoal);
        var yesterdayStreak = to > from ? analyzer.CalculateCurrentStreak(jobDailyDeepTotals, to.AddDays(-1), dailyGoal) : 0;
        var longestStreak = analyzer.CalculateLongestStreak(jobDailyDeepTotals, from, to, dailyGoal);

        var goalTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Metric")
            .AddColumn("Value");

        goalTable.AddRow("Daily target", $"[bold green]{TimeAnalyzer.FormatDuration(dailyGoal)}[/] deep Job work");
        goalTable.AddRow(to == today ? "Today" : to.ToString("MMM dd", CultureInfo.InvariantCulture),
            $"{ColorDuration(selectedDayTotal, "green", bold: true)} [grey]({TimeAnalyzer.FormatPercent(Ratio(selectedDayTotal, dailyGoal))})[/]");
        goalTable.AddRow("Remaining", remaining == TimeSpan.Zero ? "[green]met[/]" : $"[yellow]{TimeAnalyzer.FormatDuration(remaining)}[/]");
        goalTable.AddRow($"Goal days ({from:MMM dd}–{to:MMM dd})", $"[bold]{goalDays}/{totalDays}[/]");
        goalTable.AddRow("Current streak", $"[bold]{currentStreak}[/] day(s)");

        if (to == today && currentStreak == 0 && yesterdayStreak > 0)
            goalTable.AddRow("Streak through yesterday", $"[bold]{yesterdayStreak}[/] day(s)");

        goalTable.AddRow("Longest streak in range", $"[bold]{longestStreak}[/] day(s)");

        AnsiConsole.Write(new Panel(goalTable)
            .Header("[bold green]Job Deep Work Goal[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle("green"));
    }

    private static void RenderBlocksRange(AppOptions options, List<TrackedInterval> intervals, DateOnly from, DateOnly to)
    {
        AnsiConsole.Write(new Rule("[bold blue]Work Blocks[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            RenderBlocksForDate(options, intervals, date, $"Work Blocks for {date:yyyy-MM-dd}");
            if (date < to)
                AnsiConsole.WriteLine();
        }
    }

    private static void RenderBlocksForDate(AppOptions options, List<TrackedInterval> intervals, DateOnly date, string title)
    {
        var blocks = new WorkBlockAnalyzer().BuildBlocksForDate(intervals, date, options.MaxGap);

        if (blocks.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(title)}:[/] no known-category work blocks.");
            return;
        }

        var blockTable = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold]{Markup.Escape(title)}[/]")
            .AddColumn("Category")
            .AddColumn("Block")
            .AddColumn(new TableColumn("Scheduled").RightAligned())
            .AddColumn(new TableColumn("Tracked").RightAligned())
            .AddColumn(new TableColumn("Deep").RightAligned())
            .AddColumn(new TableColumn("Efficiency").RightAligned())
            .AddColumn(new TableColumn("Focus").RightAligned())
            .AddColumn(new TableColumn("Coverage").RightAligned());

        foreach (var block in blocks)
        {
            blockTable.AddRow(
                $"[{block.Category.Color()}]{block.Category.DisplayName()}[/]",
                $"{block.Start.LocalDateTime:HH:mm}–{block.End.LocalDateTime:HH:mm}",
                TimeAnalyzer.FormatDuration(block.ScheduledTime),
                TimeAnalyzer.FormatDuration(block.TrackedTime),
                ColorDuration(block.DeepWorkTime, block.Category.Color()),
                RatioMarkup(block.EfficiencyRatio),
                RatioMarkup(block.FocusRatio),
                RatioMarkup(block.TrackingCoverage));
        }

        AnsiConsole.Write(blockTable);
        AnsiConsole.MarkupLine("[grey]Efficiency = deep / scheduled block; Focus = deep / tracked; Coverage = tracked / scheduled.[/]");
    }

    private static void RenderLastSevenDays(
        TimeAnalyzer analyzer,
        IReadOnlyDictionary<WorkCategory, Dictionary<DateOnly, TimeSpan>> deepByCategory,
        Dictionary<DateOnly, TimeSpan> totalDeep,
        DateOnly to)
    {
        var dailyTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Last 7 Days[/]")
            .AddColumn("Day")
            .AddColumn(new TableColumn("[bold blue]Total[/]").Centered());

        foreach (var category in WorkCategoryExtensions.CoreCategories)
            dailyTable.AddColumn(new TableColumn($"[bold {category.Color()}]{category.DisplayName()}[/]").Centered());

        for (var i = 0; i < 7; i++)
        {
            var date = to.AddDays(-i);
            var dayName = date.DayOfWeek.ToString()[..3];
            var dateText = date.ToString("MMM dd", CultureInfo.InvariantCulture);
            var row = new List<string>
            {
                $"{dayName} {dateText}",
                ColorDuration(analyzer.GetTotalForDay(totalDeep, date), "blue")
            };

            row.AddRange(WorkCategoryExtensions.CoreCategories.Select(category =>
                ColorDuration(analyzer.GetTotalForDay(deepByCategory[category], date), category.Color())));

            dailyTable.AddRow(row.ToArray());
        }

        AnsiConsole.Write(dailyTable);
    }

    private static void RenderAliases(AppOptions options)
    {
        AnsiConsole.Write(new Rule("[bold blue]Default Tag Mapping[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();

        var aliasTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Category")
            .AddColumn("Recognized tags/aliases");

        foreach (var (category, aliases) in CategoryMapper.GetDefaultAliasRows())
            aliasTable.AddRow($"[{category.Color()}]{category.DisplayName()}[/]", Markup.Escape(aliases));

        AnsiConsole.Write(aliasTable);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]Non-deep tags:[/] {Markup.Escape(FormatTagList(options.NonDeepTags))}");
        AnsiConsole.MarkupLine("[grey]Tip:[/] tags are normalized, so lc-review, LCReview, and lc_review are equivalent.");
    }

    private static void RenderHelp()
    {
        AnsiConsole.Write(new Rule("[bold blue]deepwork[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Timewarrior analytics for deep work trends, goals, streaks, and inferred work blocks.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork[/] [yellow]dashboard[/] [grey][[options]][/]");
        AnsiConsole.MarkupLine("  [grey]deepwork[/] [yellow]blocks[/] [grey][[options]][/]");
        AnsiConsole.MarkupLine("  [grey]deepwork[/] [yellow]aliases[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Examples[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --job-goal 3h[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --non-deep-tags admin,meeting,break,email[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork blocks --days 7 --max-gap 90m[/]");
        AnsiConsole.MarkupLine("  [grey]timew export :month > sample.json && deepwork --file sample.json[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Options[/]");
        AnsiConsole.MarkupLine("  [yellow]--days <n>[/]              Days to display/analyze when --from is omitted. Default: 30; blocks default: 1.");
        AnsiConsole.MarkupLine("  [yellow]--from <yyyy-MM-dd>[/]     Start date.");
        AnsiConsole.MarkupLine("  [yellow]--to <yyyy-MM-dd>[/]       End date. Default: today.");
        AnsiConsole.MarkupLine("  [yellow]--job-goal <duration>[/]   Daily deep-work goal for Job. Default: 3h.");
        AnsiConsole.MarkupLine("  [yellow]--max-gap <duration>[/]    Same-category block merge gap. Default: 2h.");
        AnsiConsole.MarkupLine("  [yellow]--non-deep-tags <csv>[/]   Tags that exclude intervals from deep work. Default: admin, meeting, shallow, break, lunch, email, slack, chat, call.");
        AnsiConsole.MarkupLine("  [yellow]--file <path>[/]           Read Timewarrior export JSON from a file instead of running timew.");
        AnsiConsole.MarkupLine("  [yellow]-h, --help[/]              Show help.");
    }

    private static AppOptions ParseArgs(string[] args)
    {
        var options = new AppOptions();
        var index = 0;

        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
        {
            options.Command = args[0];
            index = 1;
        }

        for (; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{arg}'.");

            var (name, inlineValue) = SplitOption(arg);

            string RequireValue()
            {
                if (inlineValue is not null)
                    return inlineValue;

                if (index + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {name}.");

                index++;
                return args[index];
            }

            switch (name)
            {
                case "-h":
                case "--help":
                    options.Help = ReadOptionalBool(inlineValue);
                    break;

                case "--aliases":
                case "--list-aliases":
                    options.ListAliases = ReadOptionalBool(inlineValue);
                    break;

                case "-d":
                case "--days":
                    options.Days = ParsePositiveInt(RequireValue(), name);
                    options.DaysSpecified = true;
                    break;

                case "--from":
                    options.From = ParseDate(RequireValue(), name);
                    break;

                case "--to":
                    options.To = ParseDate(RequireValue(), name);
                    break;

                case "--job-goal":
                case "--goal-job-daily":
                    options.JobDailyGoal = DurationParser.Parse(RequireValue(), name);
                    break;

                case "--max-gap":
                    options.MaxGap = DurationParser.Parse(RequireValue(), name);
                    break;

                case "--non-deep-tags":
                    options.NonDeepTags.Clear();
                    foreach (var tag in RequireValue().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        var normalized = CategoryMapper.NormalizeTag(tag);
                        if (!string.IsNullOrWhiteSpace(normalized))
                            options.NonDeepTags.Add(normalized);
                    }
                    break;

                case "--file":
                case "--export-file":
                    options.ExportFile = RequireValue();
                    break;

                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        return options;
    }

    private static string NormalizeCommand(string command) => command.Trim().ToLowerInvariant() switch
    {
        "" => "dashboard",
        "dash" => "dashboard",
        "dashboard" => "dashboard",
        "block" => "blocks",
        "blocks" => "blocks",
        "alias" => "aliases",
        "aliases" => "aliases",
        "help" => "help",
        _ => command.Trim().ToLowerInvariant()
    };

    private static (string Name, string? Value) SplitOption(string arg)
    {
        var equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex < 0)
            return (arg, null);

        return (arg[..equalsIndex], arg[(equalsIndex + 1)..]);
    }

    private static bool ReadOptionalBool(string? value)
    {
        if (value is null)
            return true;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => throw new ArgumentException($"Invalid boolean value '{value}'.")
        };
    }

    private static int ParsePositiveInt(string value, string optionName)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0)
            return result;

        throw new ArgumentException($"{optionName} must be a positive integer.");
    }

    private static DateOnly ParseDate(string value, string optionName)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;

        throw new ArgumentException($"Invalid date for {optionName}: '{value}'. Use yyyy-MM-dd.");
    }

    private static DateOnly MinDate(params DateOnly[] dates)
    {
        var min = dates[0];
        foreach (var date in dates.Skip(1))
        {
            if (date < min)
                min = date;
        }

        return min;
    }

    private static string ColorDuration(TimeSpan duration, string color, bool bold = false)
    {
        var style = bold ? $"bold {color}" : color;
        return $"[{style}]{TimeAnalyzer.FormatDuration(duration)}[/]";
    }

    private static string FormatTagList(IEnumerable<string> tags)
    {
        var orderedTags = tags.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return orderedTags.Count == 0 ? "none" : string.Join(", ", orderedTags);
    }

    private static string RatioMarkup(double ratio)
    {
        var color = ratio switch
        {
            >= 0.70 => "green",
            >= 0.40 => "yellow",
            _ => "red"
        };

        return $"[{color}]{TimeAnalyzer.FormatPercent(ratio)}[/]";
    }

    private static double Ratio(TimeSpan numerator, TimeSpan denominator)
    {
        if (denominator <= TimeSpan.Zero)
            return 0;

        return numerator.TotalSeconds / denominator.TotalSeconds;
    }

    private static void PrintError(string message)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
    }
}
