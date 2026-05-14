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

        if (options.Command == "goals")
        {
            try
            {
                await InteractiveGoals.RunAsync(GoalStore.Create(options.GoalsFile));
                return 0;
            }
            catch (Exception ex)
            {
                PrintError(ex.Message);
                return 1;
            }
        }

        if (options.Command is not ("dashboard" or "blocks"))
        {
            PrintError($"Unknown command '{options.Command}'.");
            AnsiConsole.MarkupLine("[grey]Run:[/] deepwork --help");
            return 1;
        }

        if (options.Command == "dashboard" || options.Goals.Count > 0 || options.ClearGoals || options.SkipDaysByGoal.Count > 0)
        {
            try
            {
                await ConfigureGoalsAsync(options);
            }
            catch (Exception ex)
            {
                PrintError(ex.Message);
                return 1;
            }
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

            if (options.EffectiveGoals.Any(goal => goal.Cadence == GoalCadence.Weekly))
                analysisFrom = MinDate(analysisFrom, TimeAnalyzer.GetWeekStart(requestedFrom));
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

    private static async Task ConfigureGoalsAsync(AppOptions options)
    {
        var newGoals = options.Goals.ToList();
        options.Goals.Clear();

        var goalStore = GoalStore.Create(options.GoalsFile);
        var savedGoals = options.ClearGoals ? new List<DeepWorkGoal>() : await goalStore.LoadAsync();
        var mergedGoals = newGoals.Count > 0
            ? GoalStore.Merge(savedGoals, newGoals)
            : savedGoals.ToList();

        if (options.SkipDaysByGoal.Count > 0)
            mergedGoals = ApplySkipDayOverrides(mergedGoals, options.SkipDaysByGoal);

        var hasChanges = newGoals.Count > 0 || options.SkipDaysByGoal.Count > 0;

        if (hasChanges)
        {
            await goalStore.SaveAsync(mergedGoals);
        }
        else if (options.ClearGoals)
        {
            await goalStore.ClearAsync();
        }

        options.Goals.AddRange(mergedGoals);
    }

    private static List<DeepWorkGoal> ApplySkipDayOverrides(
        IReadOnlyList<DeepWorkGoal> goals,
        IReadOnlyDictionary<string, IReadOnlySet<DayOfWeek>> overrides)
    {
        var result = goals.ToList();
        foreach (var (label, days) in overrides)
        {
            var normalized = CategoryMapper.NormalizeTag(label);
            var index = result.FindIndex(g => CategoryMapper.NormalizeTag(g.Label) == normalized);
            if (index < 0)
                throw new ArgumentException($"--skip-days references unknown goal '{label}'. Add the goal first, e.g. --goal {label}=3h.");

            result[index] = result[index].WithSkipDays(days);
        }

        return result;
    }

    private static void ParseSkipDaysFlag(
        string value,
        string optionName,
        Dictionary<string, IReadOnlySet<DayOfWeek>> map)
    {
        var separatorIndex = FindGoalSeparator(value);
        if (separatorIndex < 0)
            throw new ArgumentException($"{optionName} must use <label=days>, for example job=sat,sun.");

        var label = value[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException($"{optionName}: goal label must not be empty.");

        var daysRaw = value[(separatorIndex + 1)..].Trim();
        map[label] = DayOfWeekParser.ParseList(daysRaw, optionName);
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
        RenderGoals(analyzer, intervals, analysisFrom, requestedFrom, to, options.EffectiveGoals, today);
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

    private static void RenderGoals(
        TimeAnalyzer analyzer,
        List<TrackedInterval> intervals,
        DateOnly analysisFrom,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<DeepWorkGoal> goals,
        DateOnly today)
    {
        for (var i = 0; i < goals.Count; i++)
        {
            var goal = goals[i];
            var dailyDeepTotals = analyzer.GetDailyTotals(
                intervals,
                analysisFrom,
                to,
                interval => interval.IsDeepWork && MatchesGoal(interval, goal));

            if (goal.Cadence == GoalCadence.Weekly)
                RenderWeeklyGoal(analyzer, dailyDeepTotals, from, to, goal, today);
            else
                RenderDailyGoal(analyzer, dailyDeepTotals, from, to, goal, today);

            if (i < goals.Count - 1)
                AnsiConsole.WriteLine();
        }
    }

    private static void RenderDailyGoal(
        TimeAnalyzer analyzer,
        Dictionary<DateOnly, TimeSpan> dailyDeepTotals,
        DateOnly from,
        DateOnly to,
        DeepWorkGoal goal,
        DateOnly today)
    {
        var skipDays = goal.SkipDays;
        var todayIsSkipped = skipDays.Contains(to.DayOfWeek);
        var selectedDayTotal = analyzer.GetTotalForDay(dailyDeepTotals, to);
        var remaining = goal.Duration > selectedDayTotal ? goal.Duration - selectedDayTotal : TimeSpan.Zero;
        var goalDays = analyzer.CountGoalDays(dailyDeepTotals, from, to, goal.Duration, skipDays);
        var totalDays = analyzer.CountEligibleDays(from, to, skipDays);
        var currentStreak = analyzer.CalculateCurrentStreak(dailyDeepTotals, to, goal.Duration, skipDays);
        var yesterdayStreak = to > from ? analyzer.CalculateCurrentStreak(dailyDeepTotals, to.AddDays(-1), goal.Duration, skipDays) : 0;
        var longestStreak = analyzer.CalculateLongestStreak(dailyDeepTotals, from, to, goal.Duration, skipDays);
        var color = goal.Color;
        var target = FormatGoalTarget(goal);

        var goalTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Metric")
            .AddColumn("Value");

        goalTable.AddRow("Daily target", $"[bold {color}]{TimeAnalyzer.FormatDuration(goal.Duration)}[/] deep work for {target}");

        var todayLabel = to == today ? "Today" : to.ToString("MMM dd", CultureInfo.InvariantCulture);
        if (todayIsSkipped)
        {
            goalTable.AddRow(todayLabel, $"{ColorDuration(selectedDayTotal, color, bold: true)} [grey](skip day — {to.DayOfWeek})[/]");
        }
        else
        {
            goalTable.AddRow(todayLabel,
                $"{ColorDuration(selectedDayTotal, color, bold: true)} [grey]({TimeAnalyzer.FormatPercent(Ratio(selectedDayTotal, goal.Duration))})[/]");
        }

        if (todayIsSkipped)
            goalTable.AddRow("Remaining", "[grey]skipped[/]");
        else
            goalTable.AddRow("Remaining", remaining == TimeSpan.Zero ? "[green]met[/]" : $"[yellow]{TimeAnalyzer.FormatDuration(remaining)}[/]");

        goalTable.AddRow($"Goal days ({from:MMM dd}–{to:MMM dd})", $"[bold]{goalDays}/{totalDays}[/]");
        goalTable.AddRow("Current streak", $"[bold]{currentStreak}[/] day(s)");

        if (to == today && currentStreak == 0 && yesterdayStreak > 0)
            goalTable.AddRow("Streak through yesterday", $"[bold]{yesterdayStreak}[/] day(s)");

        goalTable.AddRow("Longest streak in range", $"[bold]{longestStreak}[/] day(s)");

        if (skipDays.Count > 0)
            goalTable.AddRow("Skip days", $"[grey]{Markup.Escape(DayOfWeekParser.FormatList(skipDays))}[/]");

        AnsiConsole.Write(new Panel(goalTable)
            .Header($"[bold {color}]{Markup.Escape(goal.DisplayName)} Daily Deep Work Goal[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(color));
    }

    private static void RenderWeeklyGoal(
        TimeAnalyzer analyzer,
        Dictionary<DateOnly, TimeSpan> dailyDeepTotals,
        DateOnly from,
        DateOnly to,
        DeepWorkGoal goal,
        DateOnly today)
    {
        var selectedWeekStart = TimeAnalyzer.GetWeekStart(to);
        var selectedWeekTotal = analyzer.GetWeekTotal(dailyDeepTotals, selectedWeekStart);
        var remaining = goal.Duration > selectedWeekTotal ? goal.Duration - selectedWeekTotal : TimeSpan.Zero;
        var goalWeeks = analyzer.CountGoalWeeks(dailyDeepTotals, from, to, goal.Duration);
        var totalWeeks = analyzer.CountWeeks(from, to);
        var currentStreak = analyzer.CalculateCurrentWeekStreak(dailyDeepTotals, to, goal.Duration);
        var previousWeekStreak = analyzer.CalculateCurrentWeekStreak(dailyDeepTotals, selectedWeekStart.AddDays(-1), goal.Duration);
        var longestStreak = analyzer.CalculateLongestWeekStreak(dailyDeepTotals, from, to, goal.Duration);
        var lastWeekStart = selectedWeekStart.AddDays(-7);
        var lastWeekTotal = analyzer.GetWeekTotal(dailyDeepTotals, lastWeekStart);
        var color = goal.Color;
        var target = FormatGoalTarget(goal);
        var selectedWeekLabel = selectedWeekStart == TimeAnalyzer.GetWeekStart(today) && to == today
            ? "This week"
            : $"Week of {selectedWeekStart.ToString("MMM dd", CultureInfo.InvariantCulture)}";

        var goalTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Metric")
            .AddColumn("Value");

        goalTable.AddRow("Weekly target", $"[bold {color}]{TimeAnalyzer.FormatDuration(goal.Duration)}[/] deep work for {target}");
        goalTable.AddRow($"{selectedWeekLabel} ({FormatWeekRange(selectedWeekStart)})",
            $"{ColorDuration(selectedWeekTotal, color, bold: true)} [grey]({TimeAnalyzer.FormatPercent(Ratio(selectedWeekTotal, goal.Duration))})[/]");
        goalTable.AddRow("Remaining", remaining == TimeSpan.Zero ? "[green]met[/]" : $"[yellow]{TimeAnalyzer.FormatDuration(remaining)}[/]");
        goalTable.AddRow($"Last week ({FormatWeekRange(lastWeekStart)})",
            $"{ColorDuration(lastWeekTotal, color)} [grey]({TimeAnalyzer.FormatPercent(Ratio(lastWeekTotal, goal.Duration))})[/]");
        goalTable.AddRow($"Goal weeks ({TimeAnalyzer.GetWeekStart(from):MMM dd}–{TimeAnalyzer.GetWeekStart(to):MMM dd})", $"[bold]{goalWeeks}/{totalWeeks}[/]");
        goalTable.AddRow("Current weekly streak", $"[bold]{currentStreak}[/] week(s)");

        if (selectedWeekStart == TimeAnalyzer.GetWeekStart(today) && to == today && currentStreak == 0 && previousWeekStreak > 0)
            goalTable.AddRow("Streak through last week", $"[bold]{previousWeekStreak}[/] week(s)");

        goalTable.AddRow("Longest weekly streak in range", $"[bold]{longestStreak}[/] week(s)");
        goalTable.AddRow("Week starts", "[bold]Monday[/]");

        if (goal.SkipDays.Count > 0)
            goalTable.AddRow("Skip days", $"[grey]{Markup.Escape(DayOfWeekParser.FormatList(goal.SkipDays))} (informational — weekly totals include skipped days)[/]");

        AnsiConsole.Write(new Panel(goalTable)
            .Header($"[bold {color}]{Markup.Escape(goal.DisplayName)} Weekly Deep Work Goal[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(color));
    }

    private static bool MatchesGoal(TrackedInterval interval, DeepWorkGoal goal)
    {
        foreach (var component in goal.Components)
        {
            if (component.Category is WorkCategory category)
            {
                if (interval.Category == category)
                    return true;
            }
            else if (interval.Tags.Any(tag => CategoryMapper.NormalizeTag(tag) == component.NormalizedTag))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatGoalTarget(DeepWorkGoal goal)
    {
        if (goal.IsComposite)
        {
            var parts = goal.Components.Select(component => component.Category is WorkCategory category
                ? $"[bold]{Markup.Escape(category.DisplayName())}[/]"
                : $"tag [bold]{Markup.Escape(component.Label)}[/]");

            return string.Join(" + ", parts);
        }

        return goal.Category is null
            ? $"tag [bold]{Markup.Escape(goal.Label)}[/]"
            : $"{Markup.Escape(goal.DisplayName)} category";
    }

    private static string FormatWeekRange(DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(6);
        return $"Mon {weekStart.ToString("MMM dd", CultureInfo.InvariantCulture)}–Sun {weekEnd.ToString("MMM dd", CultureInfo.InvariantCulture)}";
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
        AnsiConsole.MarkupLine("[grey]Tip:[/] combine tags with [yellow]+[/] in a goal, e.g. [grey]--goal study=okta+leetcode 4h[/].");
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
        AnsiConsole.MarkupLine("  [grey]deepwork[/] [yellow]goals[/]      (interactive menu)");
        AnsiConsole.MarkupLine("  [grey]deepwork[/] [yellow]aliases[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Examples[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --job-goal 3h[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --goals job 3h[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --goals-week job 30h[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --goal writing=2h --goal-week writing=10h[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --goal study=okta+leetcode 4h[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --goal-week study=okta+leetcode 25h[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --goal job=3h --skip-days job=sat,sun[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork goals[/]        (interactive menu)");
        AnsiConsole.MarkupLine("  [grey]deepwork --clear-goals[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --leetcode-goal 1h --anki-goal 30m[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork --non-deep-tags admin,meeting,break,email[/]");
        AnsiConsole.MarkupLine("  [grey]deepwork blocks --days 7 --max-gap 90m[/]");
        AnsiConsole.MarkupLine("  [grey]timew export :month > sample.json && deepwork --file sample.json[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]Goal flags are saved as defaults in {Markup.Escape(GoalStore.Create().FilePath)} and reused on future runs.[/]");
        AnsiConsole.MarkupLine("[grey]Passing the same goal again updates its saved duration.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Options[/]");
        AnsiConsole.MarkupLine("  [yellow]--days <n>[/]              Days to display/analyze when --from is omitted. Default: 30; blocks default: 1.");
        AnsiConsole.MarkupLine("  [yellow]--from <yyyy-MM-dd>[/]     Start date.");
        AnsiConsole.MarkupLine("  [yellow]--to <yyyy-MM-dd>[/]       End date. Default: today.");
        AnsiConsole.MarkupLine("  [yellow]--job-goal <duration>[/]   Daily deep-work goal for Job. Default goal when no custom goal is supplied: 3h.");
        AnsiConsole.MarkupLine("  [yellow]--job-goal-week <duration>[/] Weekly deep-work goal for Job; weeks start Monday.");
        AnsiConsole.MarkupLine("  [yellow]--goal <tag=duration>[/]   Daily deep-work goal for any tag; repeatable.");
        AnsiConsole.MarkupLine("  [yellow]--goals <tag> <duration>[/] Daily deep-work goal for any tag; repeatable.");
        AnsiConsole.MarkupLine("  [yellow]--goal-week <tag=duration>[/] Weekly deep-work goal for any tag; weeks start Monday.");
        AnsiConsole.MarkupLine("  [yellow]--goals-week <tag> <duration>[/] Weekly deep-work goal for any tag; weeks start Monday.");
        AnsiConsole.MarkupLine("  [yellow]--goal <label>=<a>+<b> <duration>[/] Composite goal summing time from multiple tags/categories.");
        AnsiConsole.MarkupLine("  [yellow]--skip-days <label>=<days>[/] Exclude days of week from a daily goal's streak/goal-days. e.g. job=sat,sun. Use 'none' to clear.");
        AnsiConsole.MarkupLine("  [yellow]--<tag>-goal <duration>[/] Shorthand for a daily tag goal, e.g. --anki-goal 30m.");
        AnsiConsole.MarkupLine("  [yellow]--<tag>-goal-week <duration>[/] Shorthand for a weekly tag goal.");
        AnsiConsole.MarkupLine("  [yellow]--goals-file <path>[/]     JSON file for saved default goals.");
        AnsiConsole.MarkupLine("  [yellow]--clear-goals[/]           Delete saved default goals; combine with goal flags to replace them.");
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

                return RequireNextValue();
            }

            string RequireNextValue()
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {name}.");

                index++;
                return args[index];
            }

            DeepWorkGoal RequireGoalPairOrAssignment(GoalCadence cadence)
            {
                return ParseTagGoalPairOrAssignment(RequireValue(), RequireNextValue, name, cadence);
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

                case "--goals-file":
                    options.GoalsFile = RequireValue();
                    break;

                case "--clear-goals":
                    options.ClearGoals = ReadOptionalBool(inlineValue);
                    break;

                case "--job-goal":
                case "--goal-job-daily":
                    options.Goals.Add(DeepWorkGoal.ForCategory(WorkCategory.Job, DurationParser.Parse(RequireValue(), name)));
                    break;

                case "--job-goal-week":
                case "--job-week-goal":
                case "--goal-job-week":
                case "--goal-job-weekly":
                    options.Goals.Add(DeepWorkGoal.ForCategory(WorkCategory.Job, DurationParser.Parse(RequireValue(), name), GoalCadence.Weekly));
                    break;

                case "--leetcode-goal":
                case "--goal-leetcode-daily":
                    options.Goals.Add(DeepWorkGoal.ForCategory(WorkCategory.Leetcode, DurationParser.Parse(RequireValue(), name)));
                    break;

                case "--leetcode-goal-week":
                case "--leetcode-week-goal":
                case "--goal-leetcode-week":
                case "--goal-leetcode-weekly":
                    options.Goals.Add(DeepWorkGoal.ForCategory(WorkCategory.Leetcode, DurationParser.Parse(RequireValue(), name), GoalCadence.Weekly));
                    break;

                case "--goal":
                case "--tag-goal":
                    options.Goals.Add(ParseTagGoal(RequireValue(), RequireNextValue, name));
                    break;

                case "--goals":
                case "--goal-daily":
                case "--goals-daily":
                    options.Goals.Add(RequireGoalPairOrAssignment(GoalCadence.Daily));
                    break;

                case "--goal-week":
                case "--goals-week":
                case "--weekly-goal":
                case "--week-goal":
                case "--tag-goal-week":
                    options.Goals.Add(RequireGoalPairOrAssignment(GoalCadence.Weekly));
                    break;

                case "--max-gap":
                    options.MaxGap = DurationParser.Parse(RequireValue(), name);
                    break;

                case "--skip-days":
                case "--skip-day":
                case "--exclude-days":
                case "--exclude-day":
                    ParseSkipDaysFlag(RequireValue(), name, options.SkipDaysByGoal);
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
                    if (TryGetGoalTagFromOptionName(name, out var goalTag, out var cadence))
                    {
                        options.Goals.Add(DeepWorkGoal.ForTag(goalTag, DurationParser.Parse(RequireValue(), name), cadence));
                        break;
                    }

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
        "goal" => "goals",
        "goals" => "goals",
        "menu" => "goals",
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

    private static DeepWorkGoal ParseTagGoal(string value, Func<string> readNextValue, string optionName, GoalCadence cadence = GoalCadence.Daily)
    {
        if (value.Contains('+'))
            return ParseCompositeGoal(value, readNextValue, optionName, cadence);

        var separatorIndex = FindGoalSeparator(value);

        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
            throw new ArgumentException($"{optionName} must use <tag=duration>, for example writing=2h.");

        var tag = value[..separatorIndex].Trim();
        var durationText = value[(separatorIndex + 1)..].Trim();
        return DeepWorkGoal.ForTag(tag, DurationParser.Parse(durationText, optionName), cadence);
    }

    private static DeepWorkGoal ParseTagGoalPairOrAssignment(
        string firstValue,
        Func<string> readNextValue,
        string optionName,
        GoalCadence cadence)
    {
        if (firstValue.Contains('+'))
            return ParseCompositeGoal(firstValue, readNextValue, optionName, cadence);

        if (FindGoalSeparator(firstValue) >= 0)
            return ParseTagGoal(firstValue, readNextValue, optionName, cadence);

        var tag = firstValue.Trim();
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException($"{optionName} must use <tag> <duration> or <tag=duration>, for example writing 2h.");

        var durationText = readNextValue();
        return DeepWorkGoal.ForTag(tag, DurationParser.Parse(durationText, optionName), cadence);
    }

    private static DeepWorkGoal ParseCompositeGoal(
        string firstValue,
        Func<string> readNextValue,
        string optionName,
        GoalCadence cadence)
    {
        var firstPlus = firstValue.IndexOf('+', StringComparison.Ordinal);
        var lastPlus = firstValue.LastIndexOf('+');

        var labelSeparator = FindGoalSeparator(firstValue[..firstPlus]);
        string label;
        int tagsStart;

        if (labelSeparator >= 0)
        {
            label = firstValue[..labelSeparator].Trim();
            tagsStart = labelSeparator + 1;
        }
        else
        {
            label = string.Empty;
            tagsStart = 0;
        }

        var afterLastPlus = firstValue[(lastPlus + 1)..];
        var trailingSeparator = FindGoalSeparator(afterLastPlus);

        string tagsRaw;
        string durationText;

        if (trailingSeparator >= 0)
        {
            var absoluteSeparator = lastPlus + 1 + trailingSeparator;
            tagsRaw = firstValue[tagsStart..absoluteSeparator];
            durationText = firstValue[(absoluteSeparator + 1)..].Trim();
        }
        else
        {
            tagsRaw = firstValue[tagsStart..];
            durationText = readNextValue().Trim();
        }

        var tagParts = tagsRaw.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tagParts.Length == 0)
            throw new ArgumentException($"{optionName} must list at least one tag, for example study=okta+leetcode 4h.");

        var components = tagParts.Select(GoalComponent.ForTag).ToArray();
        return DeepWorkGoal.Composite(label, components, DurationParser.Parse(durationText, optionName), cadence);
    }

    private static int FindGoalSeparator(string value)
    {
        var equalsIndex = value.IndexOf('=', StringComparison.Ordinal);
        var colonIndex = value.IndexOf(':', StringComparison.Ordinal);

        return (equalsIndex, colonIndex) switch
        {
            (>= 0, >= 0) => Math.Min(equalsIndex, colonIndex),
            (>= 0, _) => equalsIndex,
            (_, >= 0) => colonIndex,
            _ => -1
        };
    }

    private static bool TryGetGoalTagFromOptionName(string optionName, out string tag, out GoalCadence cadence)
    {
        tag = string.Empty;
        cadence = GoalCadence.Daily;

        if (!optionName.StartsWith("--", StringComparison.Ordinal))
            return false;

        var name = optionName[2..];
        const string goalSuffix = "-goal";
        const string goalDailyPrefix = "goal-";
        const string goalDailySuffix = "-daily";
        const string goalWeekSuffix = "-goal-week";
        const string weeklyGoalSuffix = "-weekly-goal";
        const string goalPrefix = "goal-";
        const string weekSuffix = "-week";
        const string weeklySuffix = "-weekly";

        if (name.EndsWith(goalWeekSuffix, StringComparison.Ordinal) && name.Length > goalWeekSuffix.Length)
        {
            tag = name[..^goalWeekSuffix.Length];
            cadence = GoalCadence.Weekly;
        }
        else if (name.EndsWith(weeklyGoalSuffix, StringComparison.Ordinal) && name.Length > weeklyGoalSuffix.Length)
        {
            tag = name[..^weeklyGoalSuffix.Length];
            cadence = GoalCadence.Weekly;
        }
        else if (name.StartsWith(goalPrefix, StringComparison.Ordinal)
            && name.EndsWith(weekSuffix, StringComparison.Ordinal)
            && name.Length > goalPrefix.Length + weekSuffix.Length)
        {
            tag = name[goalPrefix.Length..^weekSuffix.Length];
            cadence = GoalCadence.Weekly;
        }
        else if (name.StartsWith(goalPrefix, StringComparison.Ordinal)
            && name.EndsWith(weeklySuffix, StringComparison.Ordinal)
            && name.Length > goalPrefix.Length + weeklySuffix.Length)
        {
            tag = name[goalPrefix.Length..^weeklySuffix.Length];
            cadence = GoalCadence.Weekly;
        }
        else if (name.EndsWith(goalSuffix, StringComparison.Ordinal) && name.Length > goalSuffix.Length)
        {
            tag = name[..^goalSuffix.Length];
        }
        else if (name.StartsWith(goalDailyPrefix, StringComparison.Ordinal)
            && name.EndsWith(goalDailySuffix, StringComparison.Ordinal)
            && name.Length > goalDailyPrefix.Length + goalDailySuffix.Length)
        {
            tag = name[goalDailyPrefix.Length..^goalDailySuffix.Length];
        }
        else
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(CategoryMapper.NormalizeTag(tag));
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
