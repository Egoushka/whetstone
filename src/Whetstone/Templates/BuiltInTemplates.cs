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

    private static readonly string[] ReadOnlyKinds =
    [
        "agent/explore", "agent/plan", "agent/claude-code-guide", "agent/box-reader", "agent/general-purpose/explore", "agent/general-purpose/research",
    ];

    private static readonly string[] WriteKinds =
    [
        "agent/general-purpose", "agent/general-purpose/implement", "agent/general-purpose/review", "agent/general-purpose/ship",
    ];

    public static IReadOnlyList<Template> All { get; } =
    [
        .. ReadOnlyKinds.Select(kind => new Template(ReadOnlyId, Version, kind, "", ReadOnlyAfter, TemplateSources.BuiltIn)),
        .. WriteKinds.Select(kind => new Template(WriteId, Version, kind, "", WriteAfter, TemplateSources.BuiltIn)),
    ];
}
