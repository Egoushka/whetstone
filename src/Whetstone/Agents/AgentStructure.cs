using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Whetstone.Context;
using Whetstone.Contracts;
using Whetstone.Templates;

namespace Whetstone.Agents;

/// <summary>An orchestrator kind scored by the runs its prompts started (docs/specs/2026-10-19-agent-structure-design.md, decision 1).</summary>
public sealed record ParentLine(string Kind, int Parents, int Children, int ChildrenMeasured, int ChildrenGood, double? MedianChildCost);

/// <summary>An output kind and template version, scored by its reader (decision 4).</summary>
public sealed record OutputLine(string Kind, string Template, int Runs, double? MedianReadMs, double? MedianEditChars);

/// <summary>A proposed kind: prompts that share a distinctive word (decision 2).</summary>
public sealed record KindProposal(string Word, int Count, IReadOnlyList<string> Examples);

/// <summary>Agent structure (goal 0.12): parents scored by children, kinds proposed from words, outputs scored by readers, graduation text.</summary>
public static partial class AgentStructure
{
    /// <summary>Per parent kind: how many child runs completed without rework, and what the children of one parent cost together.</summary>
    public static IReadOnlyList<ParentLine> Parents(IReadOnlyList<ExportRecord> records, CostWeights weights)
    {
        var byId = records.ToDictionary(r => r.RequestId, StringComparer.Ordinal);
        var children = records.Where(r => r.Context.ParentRequestId is { } p && byId.ContainsKey(p)).GroupBy(r => r.Context.ParentRequestId!, StringComparer.Ordinal).ToList();
        return [.. children
            .GroupBy(g => byId[g.Key].Context.TaskKind ?? "no kind", StringComparer.Ordinal)
            .Select(kind =>
            {
                var runs = kind.SelectMany(g => g).Select(r => TrialRun.From(r, weights)).ToList();
                var measured = runs.Where(r => r.Completed is not null).ToList();
                var costs = kind.Select(g => g.Select(r => TrialRun.From(r, weights).Cost).ToList()).Where(c => c.All(v => v is not null)).Select(c => c.Sum(v => v!.Value)).ToList();
                return new ParentLine(kind.Key, kind.Count(), runs.Count, measured.Count, measured.Count(r => r.Completed == true && r.Rework != true), Trials.Median(costs));
            })
            .OrderBy(l => l.Kind, StringComparer.Ordinal)];
    }

    /// <summary>Per <c>output/</c> kind and template version: the reader's median time and edits.</summary>
    public static IReadOnlyList<OutputLine> Outputs(IReadOnlyList<ExportRecord> records) => [.. records
        .Where(r => r.Context.TaskKind?.StartsWith("output/", StringComparison.Ordinal) == true && (r.Outcome?.ReadMs is not null || r.Outcome?.EditChars is not null))
        .GroupBy(r => (Kind: r.Context.TaskKind!, Template: r.Answer.TemplateId is { } t ? $"{t} v{r.Answer.TemplateVersion}" : "no template"))
        .Select(g => new OutputLine(g.Key.Kind, g.Key.Template, g.Count(),
            Trials.Median([.. g.Where(r => r.Outcome!.ReadMs is not null).Select(r => (double)r.Outcome!.ReadMs!.Value)]),
            Trials.Median([.. g.Where(r => r.Outcome!.EditChars is not null).Select(r => (double)r.Outcome!.EditChars!.Value)])))
        .OrderBy(l => l.Kind, StringComparer.Ordinal).ThenBy(l => l.Template, StringComparer.Ordinal)];

    [GeneratedRegex(@"\p{L}[\p{L}\p{Nd}_-]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    private static readonly HashSet<string> Common = new(
        "this that with from have what when where which there their they them then than into about would could should will just also some more make sure please only each your here like file files code need want".Split(' '),
        StringComparer.Ordinal);

    /// <summary>
    /// Proposed kinds for prompts no rule names well (<c>other</c> and the wide kinds given): each prompt goes to its most distinctive
    /// word (the rarest among the set that it shares with at least <paramref name="minCount"/> others; in a pool of 20 or more, a word
    /// in nine prompts of ten says nothing and is skipped), and words with enough prompts become proposals. Local, no model; the owner adds a rule.
    /// </summary>
    public static IReadOnlyList<KindProposal> SuggestKinds(IReadOnlyList<ExportRecord> records, IReadOnlySet<string> wideKinds, int minCount = 5, int limit = 10)
    {
        var pool = records.Where(r => r.Context.TaskKind is null || wideKinds.Contains(r.Context.TaskKind)).ToList();
        var words = pool.Select(r => Word().Matches(r.Prompt.Length > 400 ? r.Prompt[..400] : r.Prompt).Select(m => m.Value.ToLowerInvariant())
            .Where(w => !Common.Contains(w) && !w.StartsWith("redacted", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal)).ToList();
        var frequency = words.SelectMany(w => w).GroupBy(w => w, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var assigned = pool.Select((r, i) => (Record: r, Word: words[i].Where(w => frequency[w] >= minCount && (pool.Count < 20 || frequency[w] < 0.9 * pool.Count))
            .OrderBy(w => frequency[w]).ThenBy(w => w, StringComparer.Ordinal).FirstOrDefault())).Where(x => x.Word is not null);
        return [.. assigned.GroupBy(x => x.Word!, StringComparer.Ordinal).Where(g => g.Count() >= minCount)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(limit)
            .Select(g => new KindProposal(g.Key, g.Count(), [.. g.Take(3).Select(x => Excerpt(x.Record.Prompt))]))];
    }

    /// <summary>
    /// Graduation and removal proposals (decision 3): each champion the owner promoted over a challenger, as text for shared skills or
    /// instructions with its evidence, and each ablation the report calls a removal candidate. A proposal only; opening a pull request
    /// in the owner's other repository is the owner's or a client's act.
    /// </summary>
    public static string Graduation(
        IReadOnlyList<(string Kind, string TemplateId, string Version, LibraryDecision Decision)> decisions, IReadOnlyList<Template> champions,
        IReadOnlyList<AblationLine> ablations)
    {
        var text = new StringBuilder("# Proposals from whetstone\n\nEach item is a suggestion with its evidence; nothing is changed by whetstone.\n");
        var promoted = decisions.Where(d => d.Decision.Action == "promote")
            .Select(d => (d, Champion: champions.FirstOrDefault(c => c.Kind == d.Kind && c.Id == d.TemplateId && c.Version == d.Version)))
            .Where(x => x.Champion is not null).ToList();
        text.Append("\n## Graduate: champions that beat a challenger\n\n");
        if (promoted.Count == 0)
            text.Append("None yet: no champion has been promoted over a challenger.\n");
        foreach (var (d, champion) in promoted)
        {
            text.Append(CultureInfo.InvariantCulture, $"### {d.Kind}: {d.TemplateId} v{d.Version} (promoted {d.Decision.DecidedAt})\n\n");
            if (d.Decision.Counts is { } counts)
                text.Append("Evidence: ").Append(counts).Append('\n');
            if (d.Decision.Note is { } note)
                text.Append("Note: ").Append(note).Append('\n');
            text.Append("\nProposed text for briefs of this kind:\n\n");
            foreach (var line in (champion!.Before + "\n" + champion.After).Trim().Split('\n'))
                text.Append("> ").Append(line).Append('\n');
            text.Append('\n');
        }
        text.Append("\n## Remove: rules whose absence changed nothing\n\n");
        var candidates = ablations.Where(a => a.RemovalCandidate).ToList();
        if (candidates.Count == 0)
            text.Append(CultureInfo.InvariantCulture, $"None yet: no ablation has {Ablations.Sessions} sessions with no measurable effect.\n");
        foreach (var a in candidates)
            text.Append(CultureInfo.InvariantCulture, $"- `{a.Ablation}`: {a.Sessions} sessions held it out, completed {a.Completed} of {a.Runs}, rework {a.Rework} of {a.ReworkReported}; no difference above 5 points.\n");
        return text.ToString();
    }

    private static string Excerpt(string prompt)
    {
        var flat = prompt.ReplaceLineEndings(" ").Trim();
        return flat.Length > 100 ? flat[..100] + "..." : flat;
    }
}
