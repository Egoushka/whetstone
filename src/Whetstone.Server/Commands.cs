using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whetstone.Contracts;
using Whetstone.Gym;
using Whetstone.Import;
using Whetstone.Outcomes;
using Whetstone.Retrieval;
using Whetstone.Storage;
using Whetstone.Templates;

namespace Whetstone.Server;

/// <summary>
/// <c>whetstone export</c> and <c>whetstone forget</c> (ADR 0003): what a person does to their own store from a terminal. They are
/// commands, not MCP tools, so no client and no agent can call them. Options are strict: an unknown or repeated option is a usage
/// error, never a wider selection, because the second command deletes.
/// </summary>
public static partial class Commands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;

    public const string ExportUsage = "usage: whetstone export [--repository R] [--before DATE] [--text REGEX]";

    public const string ForgetUsage = "usage: whetstone forget (--all | [--repository R] [--before DATE] [--text REGEX] [--imported]) [--confirm]";

    /// <summary>One <c>export/v1</c> record per line on <paramref name="output"/>, oldest first. Nothing else goes there.</summary>
    public static async Task<int> ExportAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(options, ["--repository", "--before", "--text"], [], out var parsed, out var problem) || !Filter(parsed!, out var filter, out problem))
            return Refuse(error, problem!, ExportUsage);
        try
        {
            await foreach (var record in admin.ExportAsync(filter!, ct))
            {
                await output.WriteAsync(JsonSerializer.Serialize(record, ContractJson.Options));
                await output.WriteAsync('\n');
            }
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "export", e);
        }
    }

    public const string ReindexUsage = "usage: whetstone reindex";

    public const string GymUsage = """
        usage: whetstone gym select --kind K [--limit N] [--confirm]
               whetstone gym run --challenger ID@VERSION [--cases N] [--budget-usd B]
               whetstone gym report --challenger ID@VERSION
        """;

    /// <summary>
    /// The replay gym (ADR 0006). <c>select</c> picks stored requests of a read-only kind that can be replayed and, with
    /// <c>--confirm</c>, stores them as cases: an id and a hash, never a copy of the prompt.
    /// </summary>
    public static async Task<int> GymAsync(
        IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct,
        IReplayRunner? runner = null, IJudge? judge = null, Func<DateTimeOffset>? clock = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        if (options.Count > 0 && options[0] is "run" or "report")
            return await GymTrialAsync(options, admin, output, error, runner, judge, clock, ct);
        if (options.Count == 0 || options[0] != "select")
            return Refuse(error, "a gym command is select, run or report", GymUsage);
        if (!Parse([.. options.Skip(1)], ["--kind", "--limit"], ["--confirm"], out var parsed, out var problem))
            return Refuse(error, problem!, GymUsage);
        if (!parsed!.TryGetValue("--kind", out var kind))
            return Refuse(error, "--kind is needed", GymUsage);
        if (!GymCases.Replayable(kind))
            return Refuse(error, $"'{kind}' is not a read-only kind the gym replays: {string.Join(", ", BuiltInTemplates.ReadOnlyKinds)}", GymUsage);
        var limit = 30;
        if (parsed.TryGetValue("--limit", out var text) && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
            return Refuse(error, "--limit is a whole number above 0", GymUsage);
        try
        {
            var records = new List<ExportRecord>();
            await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
                records.Add(record);
            var existing = (await admin.GymCasesAsync(ct)).Select(c => c.SourceRequestId).ToHashSet(StringComparer.Ordinal);
            var cases = GymCases.Select(records, kind, limit, existing, DateTimeOffset.UtcNow, out var report);
            await output.WriteAsync(
                $"{report.OfKind} stored requests of {kind}: {report.NoCommit} without a repository and commit, {report.LiveState} about live state, "
                + $"{report.Redacted} with a redaction marker, {report.AlreadyCases} already cases; {report.Chosen} chosen.\n");
            if (!parsed.ContainsKey("--confirm"))
            {
                await output.WriteAsync("Nothing was stored. Add --confirm to store them as cases.\n");
                return Ok;
            }
            if (await admin.AddGymCasesAsync(cases, ct) is { } refused)
                return Fail(error, "gym", new InvalidOperationException(refused));
            await output.WriteAsync($"Stored {cases.Count} cases.\n");
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "gym", e);
        }
    }

    /// <summary><c>gym run</c> and <c>gym report</c> for one challenger version (ADR 0006): screening only, never live evidence.</summary>
    private static async Task<int> GymTrialAsync(
        IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, IReplayRunner? runner, IJudge? judge,
        Func<DateTimeOffset> clock, CancellationToken ct)
    {
        var running = options[0] == "run";
        if (!Parse([.. options.Skip(1)], running ? ["--challenger", "--cases", "--budget-usd"] : ["--challenger"], [], out var parsed, out var problem))
            return Refuse(error, problem!, GymUsage);
        if (!parsed!.TryGetValue("--challenger", out var named) || named.Split('@') is not [{ Length: > 0 } id, { Length: > 0 } version])
            return Refuse(error, "--challenger ID@VERSION is needed", GymUsage);
        var cases = 30;
        if (parsed.TryGetValue("--cases", out var text) && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out cases) || cases < 1))
            return Refuse(error, "--cases is a whole number above 0", GymUsage);
        var budget = 1.0;
        if (parsed.TryGetValue("--budget-usd", out text) && (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out budget) || budget <= 0 || !double.IsFinite(budget)))
            return Refuse(error, "--budget-usd is a number of dollars above 0", GymUsage);
        try
        {
            var (runs, verdicts) = await admin.GymResultsAsync(ct);
            if (!running)
            {
                var screen = Screen.Of(runs, verdicts, id, version);
                var cost = screen.MedianCostRatio is { } r ? FormattableString.Invariant($"median cost ratio {r:0.00} over {screen.CostPairs} pairs; ") : "no paired cost; ";
                await output.WriteAsync(
                    $"{id} v{version}: {screen.Pairs} pairs on one model; completed control {screen.ControlCompleted}, challenger {screen.ChallengerCompleted}; {cost}"
                    + $"judge: {screen.Wins} for the challenger, {screen.Losses} for the control, {screen.Ties} ties.\n"
                    + $"{screen.Verdict}: {screen.Why}.\nThe gym decides nothing live: a pass only lets the challenger enter a live trial (whetstone template add).\n");
                return Ok;
            }
            if (runner is null)
                return Refuse(error, "no gym client is configured: the chargehand runner is not built yet (ADR 0006)", GymUsage);
            var template = (await admin.TemplatesAsync(ct)).Select(t => t.Template).FirstOrDefault(t => t.Id == id && t.Version == version);
            if (template is null)
                return Refuse(error, $"no stored template {id} v{version}", GymUsage);
            var gymCases = await admin.GymCasesAsync(ct);
            var wanted = gymCases.Select(c => c.SourceRequestId).ToHashSet(StringComparer.Ordinal);
            var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
                if (wanted.Contains(record.RequestId))
                    prompts[record.RequestId] = record.Prompt;
            var (newRuns, newVerdicts, report) = await GymReplay.RunAsync(gymCases, prompts, template, runs, runner, judge, budget, cases, clock, ct);
            if (await admin.AddGymResultsAsync(newRuns, newVerdicts, ct) is { } refused)
                return Fail(error, "gym", new InvalidOperationException(refused));
            await output.WriteAsync(FormattableString.Invariant(
                $"{report.Cases} cases of {template.Kind}: {report.Ran} runs, {report.ReusedControls} controls reused, {report.Skipped} skipped, {newVerdicts.Count} judged; spent about ${report.Spent:0.00} of ${budget:0.00}{(report.BudgetReached ? ", stopped at the budget" : "")}.\n"));
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException or HttpRequestException)
        {
            return Fail(error, "gym", e);
        }
    }

    public const string LabelUsage = "usage: whetstone label list [--kind K] [--limit N] | whetstone label set REQUEST_ID yes|no|clear";

    /// <summary>
    /// The owner's hand labels (design, "Validity first"): <c>list</c> prints runs with a reported completion and no label, newest
    /// first, with what the proxy says; <c>set</c> records the owner's verdict: the run completed without needing a re-ask.
    /// </summary>
    public static async Task<int> LabelAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        switch (options.ToArray())
        {
            case ["set", var id, var verdict, .. var rest]:
                if (rest.Length > 0)
                    return Refuse(error, $"unknown option '{rest[0]}'", LabelUsage);
                if (verdict is not ("yes" or "no" or "clear"))
                    return Refuse(error, $"a label is yes, no or clear, not '{verdict}'", LabelUsage);
                bool? label = verdict == "clear" ? null : verdict == "yes";
                try
                {
                    if (await admin.LabelAsync(id, label, ct) is { } problem)
                        return Fail(error, "label", new InvalidOperationException(problem));
                    await output.WriteAsync($"{id}: {verdict}\n");
                    return Ok;
                }
                catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
                {
                    return Fail(error, "label", e);
                }
            case ["list", .. var rest]:
                if (!Parse(rest, ["--kind", "--limit"], [], out var parsed, out var listProblem))
                    return Refuse(error, listProblem!, LabelUsage);
                var limit = 20;
                if (parsed!.TryGetValue("--limit", out var text) && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
                    return Refuse(error, "--limit is a whole number above 0", LabelUsage);
                parsed.TryGetValue("--kind", out var kind);
                try
                {
                    var candidates = new List<ExportRecord>();
                    await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
                        if (record is { HandLabel: null, Outcome.Completed: not null } && (kind is null || record.Context.TaskKind == kind))
                            candidates.Add(record);
                    foreach (var record in candidates.OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal).Take(limit))
                    {
                        var run = TrialRun.From(record, CostWeights.Default);
                        var excerpt = record.Prompt.ReplaceLineEndings(" ");
                        await output.WriteAsync($"{record.RequestId}  {record.Context.TaskKind ?? "-"}  completed {Yes(run.Completed)}, rework {Yes(run.Rework)}  {(excerpt.Length > 100 ? excerpt[..100] + "..." : excerpt)}\n");
                    }
                    return Ok;
                }
                catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
                {
                    return Fail(error, "label", e);
                }
            default:
                return Refuse(error, "a label command is list or set", LabelUsage);
        }
    }

    private static string Yes(bool? value) => value switch { true => "yes", false => "no", null => "-" };

    /// <summary>Upgrades the store file if it is an older version and rebuilds its search index from the stored requests.</summary>
    public static async Task<int> ReindexAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (options.Count > 0)
            return Refuse(error, $"unknown option '{options[0]}'", ReindexUsage);
        try
        {
            var covered = await admin.ReindexAsync(ct);
            await output.WriteAsync($"Indexed {covered} {Noun(covered)}.\n");
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "reindex", e);
        }
    }

    public const string ReplayUsage = "usage: whetstone replay [--min-chars N]";

    /// <summary>
    /// Reads your store and prints, for each scored request, the earlier request that best matches it and the match's score, then how
    /// many requests are eligible to be retrieved. Changes nothing and is not an answer to any client: it is how the owner picks a
    /// threshold for retrieval (docs/specs/2026-10-05-it-retrieves-design.md).
    /// </summary>
    public static async Task<int> ReplayAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(options, ["--min-chars"], [], out var parsed, out var problem))
            return Refuse(error, problem!, ReplayUsage);
        var minChars = 0;
        if (parsed!.TryGetValue("--min-chars", out var text) && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out minChars)))
            return Refuse(error, "--min-chars takes a whole number of characters", ReplayUsage);
        try
        {
            var rows = new List<ExportRecord>();
            await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
                rows.Add(record);
            var report = Replay.Run(rows, minChars);
            await output.WriteAsync(ReplayText.Format(report));
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "replay", e);
        }
    }

    public const string TemplateUsage = """
        usage: whetstone template list
               whetstone template export
               whetstone template add --kind K --id ID --version V --after TEXT [--before TEXT] [--source S] [--champion] [--allow-similar] [--note TEXT]
               whetstone template promote --kind K --id ID --version V [--note TEXT]
               whetstone template retire --kind K --id ID --version V [--note TEXT]
        """;

    /// <summary>
    /// The owner's act on the template store (design, "Validity first": decisions are applied by the owner). <c>add</c> stores a new
    /// version as the kind's challenger (or champion with <c>--champion</c> when it has none), <c>promote</c> makes the challenger the
    /// champion and retires the old one, <c>retire</c> takes a version out of service. Retired rows stay, so the record does.
    /// </summary>
    public static async Task<int> TemplateAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (options.Count == 0)
            return Refuse(error, "a template command is list, add, promote or retire", TemplateUsage);
        var verb = options[0];
        var rest = options.Skip(1).ToList();
        try
        {
            switch (verb)
            {
                case "list":
                    if (rest.Count > 0)
                        return Refuse(error, $"unknown option '{rest[0]}'", TemplateUsage);
                    var rows = await admin.TemplatesAsync(ct);
                    if (rows.Count == 0)
                        await output.WriteAsync("No templates are stored.\n");
                    foreach (var row in rows.OrderBy(r => r.Template.Kind, StringComparer.Ordinal).ThenBy(r => r.CreatedAt))
                        await output.WriteAsync($"{row.Template.Kind}  {row.Template.Id} v{row.Template.Version}  {row.Role}  ({row.Template.Source}, {row.CreatedAt:yyyy-MM-dd})\n");
                    return Ok;
                case "export":
                    {
                        if (rest.Count > 0)
                            return Refuse(error, $"unknown option '{rest[0]}'", TemplateUsage);
                        // Every version, retired ones too: the negative library (evidence volume, decision 5) is what lost, with the counts at the time.
                        var decisions = await admin.TemplateDecisionsAsync(ct);
                        foreach (var row in (await admin.TemplatesAsync(ct)).OrderBy(r => r.Template.Kind, StringComparer.Ordinal).ThenBy(r => r.CreatedAt).ThenBy(r => r.Template.Id, StringComparer.Ordinal))
                        {
                            var t = row.Template;
                            var own = decisions.Where(d => d.Kind == t.Kind && d.TemplateId == t.Id && d.Version == t.Version).Select(d => d.Decision).ToList();
                            var record = new LibraryRecord(t.Id, t.Version, t.Kind, t.Before, t.After, t.Source, row.Role, row.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), own);
                            await output.WriteAsync(JsonSerializer.Serialize(record, ContractJson.Options) + "\n");
                        }
                        return Ok;
                    }
                case "add":
                    {
                        if (!Parse(rest, ["--kind", "--id", "--version", "--before", "--after", "--source", "--note"], ["--champion", "--allow-similar"], out var add, out var problem))
                            return Refuse(error, problem!, TemplateUsage);
                        if (!Identity(add!, out var kind, out var id, out var version, out problem))
                            return Refuse(error, problem!, TemplateUsage);
                        var template = new Template(id, version, kind, add!.GetValueOrDefault("--before", ""), add!.GetValueOrDefault("--after", ""), add!.GetValueOrDefault("--source", "hand"));
                        if (template.Before.Length == 0 && template.After.Length == 0 && template.Source != TemplateSources.FillerStrip)
                            return Refuse(error, "a template needs --before or --after text (only --source filler-strip may have none)", TemplateUsage);
                        if (template.UnknownSlots().ToList() is { Count: > 0 } unknown)
                            return Refuse(error, $"unknown slot {{{{{unknown[0]}}}}}; whetstone fills repository, commit and task_kind", TemplateUsage);
                        if (template.Render("x", null) is null)
                            return Refuse(error, "the template cannot be rendered", TemplateUsage);
                        var role = add!.ContainsKey("--champion") ? TemplateRoles.Champion : TemplateRoles.Challenger;
                        return Report(await admin.AddTemplateAsync(template, role, await DecisionAsync(admin, kind, add!, ct), ct, add!.ContainsKey("--allow-similar")), $"Stored {id} v{version} for {kind} as {role}.", output, error);
                    }
                case "promote" or "retire":
                    {
                        if (!Parse(rest, ["--kind", "--id", "--version", "--note"], [], out var change, out var problem))
                            return Refuse(error, problem!, TemplateUsage);
                        if (!Identity(change!, out var kind, out var id, out var version, out problem))
                            return Refuse(error, problem!, TemplateUsage);
                        return verb == "promote"
                            ? Report(await admin.PromoteTemplateAsync(kind, id, version, await DecisionAsync(admin, kind, change!, ct), ct), $"{id} v{version} is now the champion for {kind}; the previous champion is retired.", output, error)
                            : Report(await admin.RetireTemplateAsync(kind, id, version, await DecisionAsync(admin, kind, change!, ct), ct), $"{id} v{version} is retired for {kind}.", output, error);
                    }
                default:
                    return Refuse(error, $"unknown template command '{verb}'", TemplateUsage);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "template", e);
        }
    }

    /// <summary>What the report shows for the kind right now (runs per arm, per-version stats, looks), as JSON, with the owner's note.</summary>
    private static async Task<DecisionRecord> DecisionAsync(IStoreAdmin admin, string kind, Dictionary<string, string> options, CancellationToken ct)
    {
        var runs = new List<TrialRun>();
        await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
            if (record.Context.TaskKind == kind)
                runs.Add(TrialRun.From(record, CostWeights.Default));
        var stored = (await admin.TemplatesAsync(ct)).Where(t => t.Template.Kind == kind).ToList();
        var report = Trials.Evaluate(kind, runs, new KindTemplates(
            stored.FirstOrDefault(t => t.Role == TemplateRoles.Champion)?.Template, stored.FirstOrDefault(t => t.Role == TemplateRoles.Challenger)?.Template));
        var counts = JsonSerializer.Serialize(new { report.Runs, report.Model, report.Counted, report.Looks, report.SampleRatio, report.Monitor, report.Versions });
        return new DecisionRecord(counts, options.GetValueOrDefault("--note"));
    }

    private static bool Identity(Dictionary<string, string> options, out string kind, out string id, out string version, out string? problem)
    {
        (kind, id, version) = (options.GetValueOrDefault("--kind", ""), options.GetValueOrDefault("--id", ""), options.GetValueOrDefault("--version", ""));
        problem = null;
        if (kind.Length == 0 || id.Length == 0 || version.Length == 0)
            problem = "--kind, --id and --version are all needed";
        else if (kind == Whetstone.Kinds.PromptKinds.Other)
            problem = "kind 'other' is never templated";
        else if (!NamePattern().IsMatch(id) || !NamePattern().IsMatch(version))
            problem = "--id and --version are 1 to 64 characters of letters, digits, '.', '_' and '-'";
        return problem is null;
    }

    private static int Report(string? problem, string done, TextWriter output, TextWriter error)
    {
        if (problem is not null)
        {
            error.Write($"whetstone: template: {problem}\n");
            return Failed;
        }
        output.Write(done + "\n");
        return Ok;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NamePattern();

    public const string ReportUsage = "usage: whetstone report";

    /// <summary>
    /// Reads your store and prints, per kind of prompt in a template trial, the runs each arm got, how they went and what the
    /// promotion rule says (docs/specs/2026-10-10-templates-everywhere-design.md). Changes nothing.
    /// </summary>
    public static async Task<int> ReportAsync(
        IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct, TrialSettings? settings = null)
    {
        settings ??= TrialSettings.Default;
        if (!Parse(options, [], [], out _, out var problem))
            return Refuse(error, problem!, ReportUsage);
        try
        {
            var records = new List<ExportRecord>();
            await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
                records.Add(record);
            var stored = await admin.TemplatesAsync(ct);
            var current = stored
                .GroupBy(t => t.Template.Kind, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new KindTemplates(
                    g.FirstOrDefault(t => t.Role == TemplateRoles.Champion)?.Template, [.. g.Where(t => t.Role == TemplateRoles.Challenger).Select(t => t.Template)]), StringComparer.Ordinal);
            var runs = records.Where(r => r.Context.TaskKind is not null)
                .GroupBy(r => r.Context.TaskKind!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(r => TrialRun.From(r, settings.Weights)).ToList(), StringComparer.Ordinal);
            var inTrials = runs.Where(k => current.ContainsKey(k.Key) || k.Value.Any(r => r.Arm is not null)).Select(k => k.Key)
                .Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var reports = inTrials.Select(kind => Trials.Evaluate(kind, runs.GetValueOrDefault(kind) ?? [], current.GetValueOrDefault(kind, KindTemplates.None), settings.PersonTimeKinds)).ToList();
            var tracked = reports.Sum(r => r.Runs.Values.Sum());
            await output.WriteAsync(ReportText.Format(reports, records.Count - tracked, HandLabels.Of(records, settings.Weights)));
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "report", e);
        }
    }

    public const string ImportUsage = "usage: whetstone import claude-code DIR [--exclude DIR] [--exclude-text REGEX] [--confirm]";

    /// <summary>
    /// Reads past Claude Code sessions under DIR and stores what the person typed, each prompt with an implicit score
    /// (docs/specs/2026-10-09-it-imports-design.md). Counts and stops until <c>--confirm</c> is given, like <c>forget</c>.
    /// </summary>
    public static async Task<int> ImportAsync(
        IReadOnlyList<string> options, IStore store, IStoreAdmin admin, IRepositoryHistory history, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (options.Count < 2 || options[0] != "claude-code" || options[1].StartsWith("--", StringComparison.Ordinal))
            return Refuse(error, "say which client's sessions and where: claude-code DIR", ImportUsage);
        var directory = options[1];
        if (!Parse([.. options.Skip(2)], ["--exclude", "--exclude-text"], ["--confirm"], out var parsed, out var problem))
            return Refuse(error, problem!, ImportUsage);
        Regex? excludedText = null;
        if (parsed!.TryGetValue("--exclude-text", out var pattern) && !TryRegex(pattern, out excludedText, out problem))
            return Refuse(error, problem!.Replace("--text", "--exclude-text", StringComparison.Ordinal), ImportUsage);
        if (!Directory.Exists(directory))
            return Refuse(error, $"no directory '{directory}'", ImportUsage);
        IReadOnlyList<string> excluded = parsed.TryGetValue("--exclude", out var exclude) ? [exclude] : [];
        var write = parsed.ContainsKey("--confirm");
        try
        {
            var sessions = ClaudeCodeTranscripts.Read(directory);
            var report = await Importer.RunAsync(sessions, new ImportExclusions(excluded, excludedText), history, store, admin, write, DateTimeOffset.UtcNow, ct);
            await output.WriteAsync(
                $"{report.Sessions} sessions read, {report.ExcludedSessions} excluded; {report.Prompts} typed prompts: "
                + $"{report.New} new, {report.Matched} already stored by a live client, {report.Prompts - report.New - report.Matched} imported before.\n");
            var agent = report.Agent;
            await output.WriteAsync(
                $"{agent.Prompts} prompts written by agents and orchestrators: {agent.New} new, {agent.Matched} already stored by a live client, "
                + $"{agent.Prompts - agent.New - agent.Matched} imported before; {agent.Measured} with run measures, {agent.Prompts - agent.Other} with a kind other than 'other'.\n");
            await output.WriteAsync(write
                ? $"Stored {report.New + agent.New} and scored {report.Prompts}, with run measures for {agent.Measured}.\n"
                : "Nothing was stored. Add --confirm to store them.\n");
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            return Fail(error, "import", e);
        }
    }

    public const string OutcomesUsage = "usage: whetstone outcomes collect --root DIR [--forge github] [--confirm]";

    /// <summary>
    /// Fills delayed outcomes (commits, reverts and, with a forge, merged pull requests and first-push CI) for stored requests that
    /// have a session and a repository, from the checkouts under DIR (docs/specs/2026-10-19-reward-signals-design.md). Counts until
    /// <c>--confirm</c>; then reports them through the store like a client's <c>feedback</c>.
    /// </summary>
    public static async Task<int> OutcomesAsync(
        IReadOnlyList<string> options, IStore store, IStoreAdmin admin, Func<string, ICommitHistory> historyAt, IForge? github,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (options.Count == 0 || options[0] != "collect")
            return Refuse(error, "say what to do: collect", OutcomesUsage);
        if (!Parse([.. options.Skip(1)], ["--root", "--forge"], ["--confirm"], out var parsed, out var problem))
            return Refuse(error, problem!, OutcomesUsage);
        if (!parsed!.TryGetValue("--root", out var root))
            return Refuse(error, "--root is needed: the folder that holds the checkouts", OutcomesUsage);
        if (!Directory.Exists(root))
            return Refuse(error, $"no directory '{root}'", OutcomesUsage);
        if (parsed.TryGetValue("--forge", out var forge) && forge != "github")
            return Refuse(error, $"unknown forge '{forge}'; the one known is github", OutcomesUsage);
        var write = parsed.ContainsKey("--confirm");
        try
        {
            var records = new List<ExportRecord>();
            await foreach (var record in admin.ExportAsync(RowFilter.Everything, ct))
                records.Add(record);
            var now = DateTimeOffset.UtcNow;
            var found = DelayedOutcomes.Collect(records, historyAt(root), forge is null ? null : github, now, out var report);
            await output.WriteAsync(
                $"{report.Requests} requests with a session and a repository whose window has closed: {report.Located} in a checkout found under the root, "
                + $"{report.Measured} measured, {report.WithCommits} with commits; {report.Merged} in a merged pull request, {report.Reverted} reverted.\n");
            if (!write)
            {
                await output.WriteAsync("Nothing was stored. Add --confirm to store them.\n");
                return Ok;
            }
            foreach (var outcome in found)
                await store.RecordOutcomeAsync(OutcomeRow.From(outcome, now), ct);
            await output.WriteAsync($"Stored delayed outcomes for {found.Count} requests.\n");
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            return Fail(error, "outcomes", e);
        }
    }

    /// <summary>Counts what matches and stops, until <c>--confirm</c> is given; then deletes it and reports how many went.</summary>
    public static async Task<int> ForgetAsync(IReadOnlyList<string> options, IStoreAdmin admin, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(options, ["--repository", "--before", "--text"], ["--all", "--confirm", "--imported"], out var parsed, out var problem))
            return Refuse(error, problem!, ForgetUsage);
        var all = parsed!.ContainsKey("--all");
        var filtered = parsed.ContainsKey("--repository") || parsed.ContainsKey("--before") || parsed.ContainsKey("--text") || parsed.ContainsKey("--imported");
        if (all && filtered)
            return Refuse(error, "--all cannot be combined with a filter", ForgetUsage);
        if (!all && !filtered)
            return Refuse(error, "say what to forget: --all, --repository, --before, --text or --imported", ForgetUsage);
        if (!Filter(parsed, out var filter, out problem))
            return Refuse(error, problem!, ForgetUsage);
        try
        {
            if (!parsed.ContainsKey("--confirm"))
            {
                var matching = await admin.CountAsync(filter!, ct);
                await output.WriteAsync($"{matching} {Noun(matching)} match. Nothing was deleted. Run `whetstone export` first if you want a copy, then add --confirm to delete.\n");
                return Ok;
            }
            var deleted = await admin.ForgetAsync(filter!, ct);
            await output.WriteAsync($"Deleted {deleted} {Noun(deleted)}.\n");
            return Ok;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Fail(error, "forget", e);
        }
    }

    private static string Noun(int n) => n == 1 ? "request" : "requests";

    private static int Refuse(TextWriter error, string problem, string usage)
    {
        error.Write($"whetstone: {problem}\n{usage}\n");
        return Usage;
    }

    // Our own messages and SQLite's name the fault, not the data; the exception type goes first for the log reader.
    private static int Fail(TextWriter error, string command, Exception e)
    {
        error.Write($"whetstone: {command} failed ({e.GetType().Name}): {e.Message}\n");
        return Failed;
    }

    private static bool Filter(Dictionary<string, string> parsed, out RowFilter? filter, out string? problem)
    {
        filter = null;
        problem = null;
        DateTimeOffset? before = null;
        if (parsed.TryGetValue("--before", out var text))
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                problem = "--before takes a date or time such as 2026-10-01 or 2026-10-01T12:00:00Z (UTC when no offset is given)";
                return false;
            }
            before = when;
        }
        Regex? matching = null;
        if (parsed.TryGetValue("--text", out var pattern) && !TryRegex(pattern, out matching, out problem))
            return false;
        filter = new RowFilter(parsed.GetValueOrDefault("--repository"), before, matching, parsed.ContainsKey("--imported"));
        return true;
    }

    /// <summary>Case-insensitive and linear-time, so a pattern cannot hang a command on a long prompt.</summary>
    private static bool TryRegex(string pattern, out Regex? regex, out string? problem)
    {
        regex = null;
        problem = null;
        if (pattern.Length == 0)
        {
            problem = "--text needs a non-empty expression";
            return false;
        }
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            problem = $"--text is not an expression this whetstone can run: {e.Message}";
            return false;
        }
    }

    /// <summary>Strict: every token is a known option, each at most once, a valued option has a value, a flag has none.</summary>
    private static bool Parse(IReadOnlyList<string> options, string[] valued, string[] flags, out Dictionary<string, string>? parsed, out string? problem)
    {
        parsed = [];
        problem = null;
        for (var i = 0; i < options.Count; i++)
        {
            var name = options[i];
            if (!valued.Contains(name, StringComparer.Ordinal) && !flags.Contains(name, StringComparer.Ordinal))
            {
                problem = $"unknown option '{name}'";
                return false;
            }
            if (parsed.ContainsKey(name))
            {
                problem = $"{name} given twice";
                return false;
            }
            if (flags.Contains(name, StringComparer.Ordinal))
            {
                parsed[name] = "";
                continue;
            }
            if (i + 1 >= options.Count || options[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                problem = $"{name} needs a value";
                return false;
            }
            parsed[name] = options[++i];
        }
        return true;
    }
}
