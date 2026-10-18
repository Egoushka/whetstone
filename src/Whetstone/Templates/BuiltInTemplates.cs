namespace Whetstone.Templates;

/// <summary>
/// The agent brief completer: the first champion (research note "Whetstone's best prompt gains constrain agents"). It adds what a
/// subagent's prompt most often lacks, a report format, a stop rule with a length cap and, where the job is only to look, a
/// read-only line. It adds only text that is true of every prompt of the kind; it states no file, command or fact of its own.
/// </summary>
public static class BuiltInTemplates
{
    public const string ReadOnlyId = "agent-brief-readonly";

    public const string WriteId = "agent-brief-write";

    public const string Version = "1";

    private const string ReadOnlyAfter =
        "Report format: what you found, the evidence (file paths and line numbers, commands run, results), and anything you could not confirm.\n"
        + "When the task is done, stop and report; do not keep searching once you can answer. Keep the report under 300 words.\n"
        + "This task is read-only: do not edit, create or delete files, and do not commit.";

    private const string WriteAfter =
        "Report format: what you found or changed, how you checked it (the command you ran and its result), and anything left undone.\n"
        + "When the task is done and the checks pass, stop and report. Keep the report under 300 words.\n"
        + "Stay within the task above; if something else needs doing, say so in the report instead of doing it.";

    /// <summary>Kinds whose subagent only reads; the replay gym replays only these (ADR 0006).</summary>
    public static readonly IReadOnlyList<string> ReadOnlyKinds =
    [
        "agent/explore", "agent/plan", "agent/claude-code-guide", "agent/box-reader", "agent/general-purpose/explore", "agent/general-purpose/research",
    ];

    private static readonly string[] WriteKinds =
    [
        "agent/general-purpose", "agent/general-purpose/implement", "agent/general-purpose/review", "agent/general-purpose/ship",
    ];

    public const string ImplementId = "typed-implement-check";

    public const string DebugId = "typed-debug-root-cause";

    public const string ResearchId = "typed-research-evidence";

    public const string ReviewId = "typed-review-findings";

    /// <summary>The model rewrite the Claude Code mod runs on typed prompts (ADR 0005); a challenger, never a champion.</summary>
    public const string RewriteId = "typed-rewrite-haiku";

    public const string RewriteVersion = "r1";

    public static readonly string[] TypedKinds = ["typed/implement", "typed/debug", "typed/research", "typed/review"];

    // Each is true of every prompt of its kind and states no file, command or fact of its own.
    private static readonly (string Id, string Kind, string After)[] Typed =
    [
        (ImplementId, "typed/implement",
            "Before you report this done, run the repository's own build and tests (the commands in its CLAUDE.md or README) and give their result. Say what you did not verify."),
        (DebugId, "typed/debug",
            "Reproduce the problem first and name the root cause, with the evidence, before you change code. After the fix, rerun the reproduction and say what you saw."),
        (ResearchId, "typed/research",
            "For each claim say whether you confirmed it from a source you opened (give the file and line, or the URL) or are inferring it. End with what stays unknown."),
        (ReviewId, "typed/review",
            "Order the findings by severity, each with file and line and the evidence for it. Report only what you can point to, and say what you did not check."),
    ];

    /// <summary>The typed-prompt challengers: the model rewrite, one per typed kind that has a champion.</summary>
    public static IReadOnlyList<Template> TypedChallengers { get; } =
        [.. TypedKinds.Select(kind => new Template(RewriteId, RewriteVersion, kind, "", "", TemplateSources.Model))];

    public static IReadOnlyList<Template> All { get; } =
    [
        .. Typed.Select(t => new Template(t.Id, Version, t.Kind, "", t.After, TemplateSources.BuiltIn)),
        .. ReadOnlyKinds.Select(kind => new Template(ReadOnlyId, Version, kind, "", ReadOnlyAfter, TemplateSources.BuiltIn)),
        .. WriteKinds.Select(kind => new Template(WriteId, Version, kind, "", WriteAfter, TemplateSources.BuiltIn)),
    ];
}
