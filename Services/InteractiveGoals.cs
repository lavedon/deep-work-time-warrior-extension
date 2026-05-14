using DeepWork.Models;
using Spectre.Console;

namespace DeepWork.Services;

public static class InteractiveGoals
{
    public static async Task RunAsync(GoalStore store)
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold blue]deepwork — Goals[/]").RuleStyle("blue"));
            AnsiConsole.MarkupLine($"[grey]Goals file: {Markup.Escape(store.FilePath)}[/]");
            AnsiConsole.WriteLine();

            List<DeepWorkGoal> goals;
            try
            {
                goals = await store.LoadAsync();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Could not load goals:[/] {Markup.Escape(ex.Message)}");
                return;
            }

            RenderGoalList(goals);
            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What would you like to do?[/]")
                    .AddChoices(
                        "Add daily goal",
                        "Add weekly goal",
                        "Edit goal duration",
                        "Edit skip days",
                        "Remove goal",
                        "Clear all goals",
                        "Exit"));

            if (choice == "Exit")
                return;

            try
            {
                switch (choice)
                {
                    case "Add daily goal":
                        await AddGoalAsync(store, goals, GoalCadence.Daily);
                        break;
                    case "Add weekly goal":
                        await AddGoalAsync(store, goals, GoalCadence.Weekly);
                        break;
                    case "Edit goal duration":
                        await EditDurationAsync(store, goals);
                        break;
                    case "Edit skip days":
                        await EditSkipDaysAsync(store, goals);
                        break;
                    case "Remove goal":
                        await RemoveGoalAsync(store, goals);
                        break;
                    case "Clear all goals":
                        await ClearGoalsAsync(store);
                        break;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
            Console.ReadKey(intercept: true);
        }
    }

    private static void RenderGoalList(IReadOnlyList<DeepWorkGoal> goals)
    {
        if (goals.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No goals configured yet.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Current Goals[/]")
            .AddColumn("Cadence")
            .AddColumn("Label")
            .AddColumn("Target")
            .AddColumn(new TableColumn("Duration").Centered())
            .AddColumn("Skip days");

        foreach (var goal in goals)
        {
            var cadence = goal.Cadence == GoalCadence.Weekly ? "Weekly" : "Daily";
            var target = FormatComponents(goal);
            var dur = TimeAnalyzer.FormatDuration(goal.Duration);
            var skip = goal.SkipDays.Count == 0
                ? "[grey]—[/]"
                : Markup.Escape(DayOfWeekParser.FormatList(goal.SkipDays));
            table.AddRow(cadence, Markup.Escape(goal.Label), Markup.Escape(target), dur, skip);
        }

        AnsiConsole.Write(table);
    }

    private static string FormatComponents(DeepWorkGoal goal)
    {
        var parts = goal.Components.Select(c => c.Category is WorkCategory cat ? cat.DisplayName() : c.Label);
        return string.Join(" + ", parts);
    }

    private static async Task AddGoalAsync(GoalStore store, IReadOnlyList<DeepWorkGoal> existing, GoalCadence cadence)
    {
        AnsiConsole.MarkupLine($"[bold]Add {cadence.ToString().ToLowerInvariant()} goal[/]");
        AnsiConsole.MarkupLine("[grey]Known categories: Job, Leetcode, Anki, Okta. Combine with '+' for composite (e.g. okta+leetcode).[/]");

        var componentsText = AnsiConsole.Prompt(
            new TextPrompt<string>("Components:")
                .Validate(input => string.IsNullOrWhiteSpace(input)
                    ? ValidationResult.Error("Enter at least one tag.")
                    : ValidationResult.Success()));

        var components = componentsText
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(GoalComponent.ForTag)
            .ToArray();

        if (components.Length == 0)
            throw new ArgumentException("At least one component is required.");

        var label = string.Empty;
        if (components.Length > 1)
        {
            label = AnsiConsole.Prompt(
                new TextPrompt<string>("Label (blank for auto):")
                    .AllowEmpty());
        }

        var durationInput = AnsiConsole.Prompt(
            new TextPrompt<string>("Duration (e.g. 3h, 30m, 1h30m):")
                .Validate(ValidateDuration));
        var duration = DurationParser.Parse(durationInput, "duration");

        IEnumerable<DayOfWeek>? skipDays = null;
        if (cadence == GoalCadence.Daily)
        {
            var selected = AnsiConsole.Prompt(
                new MultiSelectionPrompt<DayOfWeek>()
                    .Title("Skip which days? (space to toggle, enter for none)")
                    .NotRequired()
                    .UseConverter(d => d.ToString())
                    .AddChoices(DayOfWeekParser.WeekOrder));
            skipDays = selected;
        }

        var newGoal = DeepWorkGoal.Composite(label, components, duration, cadence, skipDays);
        var merged = GoalStore.Merge(existing, new[] { newGoal });
        await store.SaveAsync(merged);

        AnsiConsole.MarkupLine($"[green]Added:[/] {Markup.Escape(newGoal.DisplayName)} — {Markup.Escape(TimeAnalyzer.FormatDuration(duration))} ({cadence.ToString().ToLowerInvariant()})");
    }

    private static async Task EditDurationAsync(GoalStore store, IReadOnlyList<DeepWorkGoal> existing)
    {
        if (existing.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No goals to edit.[/]");
            return;
        }

        var goal = PromptGoal(existing, "Edit which goal's duration?");
        var durationInput = AnsiConsole.Prompt(
            new TextPrompt<string>($"New duration (current: {TimeAnalyzer.FormatDuration(goal.Duration)}):")
                .Validate(ValidateDuration));

        var updated = goal.WithDuration(DurationParser.Parse(durationInput, "duration"));
        await store.SaveAsync(ReplaceGoal(existing, goal, updated));
        AnsiConsole.MarkupLine($"[green]Updated:[/] {Markup.Escape(updated.DisplayName)} → {Markup.Escape(TimeAnalyzer.FormatDuration(updated.Duration))}");
    }

    private static async Task EditSkipDaysAsync(GoalStore store, IReadOnlyList<DeepWorkGoal> existing)
    {
        if (existing.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No goals to edit.[/]");
            return;
        }

        var dailyGoals = existing.Where(g => g.Cadence == GoalCadence.Daily).ToList();
        if (dailyGoals.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No daily goals to edit — skip days only affect daily streaks.[/]");
            return;
        }

        var goal = PromptGoal(dailyGoals, "Edit skip days for which goal?");
        AnsiConsole.MarkupLine($"[grey]Current skip days: {Markup.Escape(DayOfWeekParser.FormatList(goal.SkipDays))}[/]");

        var prompt = new MultiSelectionPrompt<DayOfWeek>()
            .Title($"Skip days for {Markup.Escape(goal.DisplayName)} (space to toggle, enter to confirm)")
            .NotRequired()
            .UseConverter(d => d.ToString())
            .AddChoices(DayOfWeekParser.WeekOrder);

        foreach (var day in goal.SkipDays)
            prompt.Select(day);

        var selected = AnsiConsole.Prompt(prompt);
        var updated = goal.WithSkipDays(selected);
        await store.SaveAsync(ReplaceGoal(existing, goal, updated));

        AnsiConsole.MarkupLine($"[green]Updated:[/] skip days for {Markup.Escape(updated.DisplayName)} = {Markup.Escape(DayOfWeekParser.FormatList(updated.SkipDays))}");
    }

    private static async Task RemoveGoalAsync(GoalStore store, IReadOnlyList<DeepWorkGoal> existing)
    {
        if (existing.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No goals to remove.[/]");
            return;
        }

        var goal = PromptGoal(existing, "Remove which goal?");
        if (!AnsiConsole.Confirm($"Remove '{Markup.Escape(goal.DisplayName)}' ({goal.Cadence})?", defaultValue: false))
            return;

        var newList = existing.Where(g => !ReferenceEquals(g, goal)).ToList();
        await store.SaveAsync(newList);
        AnsiConsole.MarkupLine($"[green]Removed:[/] {Markup.Escape(goal.DisplayName)}");
    }

    private static async Task ClearGoalsAsync(GoalStore store)
    {
        if (!AnsiConsole.Confirm("Delete all saved goals?", defaultValue: false))
            return;

        await store.ClearAsync();
        AnsiConsole.MarkupLine("[green]Cleared all goals.[/]");
    }

    private static DeepWorkGoal PromptGoal(IReadOnlyList<DeepWorkGoal> goals, string title)
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<DeepWorkGoal>()
                .Title(title)
                .UseConverter(g =>
                {
                    var cadence = g.Cadence == GoalCadence.Weekly ? "weekly" : "daily";
                    var skip = g.SkipDays.Count == 0
                        ? string.Empty
                        : $" [skip {DayOfWeekParser.FormatList(g.SkipDays)}]";
                    return $"{cadence} — {g.DisplayName} — {TimeAnalyzer.FormatDuration(g.Duration)}{skip}";
                })
                .AddChoices(goals));
    }

    private static List<DeepWorkGoal> ReplaceGoal(IReadOnlyList<DeepWorkGoal> goals, DeepWorkGoal oldGoal, DeepWorkGoal newGoal)
    {
        return goals.Select(g => ReferenceEquals(g, oldGoal) ? newGoal : g).ToList();
    }

    private static ValidationResult ValidateDuration(string input)
    {
        try
        {
            DurationParser.Parse(input, "duration");
            return ValidationResult.Success();
        }
        catch (Exception ex)
        {
            return ValidationResult.Error(ex.Message);
        }
    }
}
