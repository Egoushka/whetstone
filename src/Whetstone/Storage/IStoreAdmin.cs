using System.Globalization;
using Whetstone.Contracts;

namespace Whetstone.Storage;

/// <summary>
/// Which stored requests a command means. Every field narrows it; none set means every request, which a command must ask for
/// by name (<c>forget --all</c>), never by leaving a filter out.
/// </summary>
/// <param name="Repository">The stored repository, exactly (it is stored redacted).</param>
/// <param name="Before">Requests answered strictly before this instant.</param>
/// <param name="Text">Requests whose stored (redacted) prompt matches this expression, ignoring case.</param>
/// <param name="Imported">Only requests an import stored, not a live client: how an import is undone.</param>
public sealed record RowFilter(string? Repository = null, DateTimeOffset? Before = null, System.Text.RegularExpressions.Regex? Text = null, bool Imported = false)
{
    public static readonly RowFilter Everything = new();

    /// <summary>The time as the store keeps it, so a comparison of the two texts is a comparison of the instants.</summary>
    internal string? BeforeText => Before?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>What the owner did to a template, as <c>template_decisions</c> keeps it: the counts the report showed (JSON) and a note.</summary>
public sealed record DecisionRecord(string? Counts, string? Note);

/// <summary>
/// What the commands <c>export</c> and <c>forget</c> do to one user's store (ADR 0003). Separate from <see cref="IStore"/> on
/// purpose: the server and its tools hold only <see cref="IStore"/>, so no client and no agent can reach these. They never create
/// a store: a user with no file has nothing to export or forget.
/// </summary>
public interface IStoreAdmin
{
    Task<int> CountAsync(RowFilter filter, CancellationToken ct);

    IAsyncEnumerable<ExportRecord> ExportAsync(RowFilter filter, CancellationToken ct);

    /// <summary>Every stored template version with its role; empty for a file from before the template store.</summary>
    Task<IReadOnlyList<Whetstone.Templates.TemplateRow>> TemplatesAsync(CancellationToken ct);

    /// <summary>What the challenger writer's model calls have cost since <paramref name="since"/>, in dollars; 0 for a file before schema 10.</summary>
    Task<double> WriterSpendSinceAsync(DateTimeOffset since, CancellationToken ct);

    /// <summary>Records one model call's cost against the weekly limit. Null-safe on a file before schema 10: it throws, as the command checks first.</summary>
    Task AddWriterSpendAsync(DateTimeOffset at, string kind, string model, double usd, CancellationToken ct);

    /// <summary>Every trait with whether it is still drawn; empty for a file before schema 9.</summary>
    Task<IReadOnlyList<(Whetstone.Templates.Trait Trait, bool Active)>> TraitsAsync(CancellationToken ct);

    /// <summary>Adds a trait to a kind (at most <see cref="Whetstone.Templates.Trait.MaxPerKind"/> active ones). Null when done, else why not.</summary>
    Task<string?> AddTraitAsync(Whetstone.Templates.Trait trait, CancellationToken ct);

    /// <summary>Stops drawing a trait for a kind; its runs stay. Null when done, else why not.</summary>
    Task<string?> RetireTraitAsync(string kind, string id, CancellationToken ct);

    /// <summary>What the owner decided about each template version, oldest first; empty for a file before schema 6.</summary>
    Task<IReadOnlyList<(string Kind, string TemplateId, string Version, LibraryDecision Decision)>> TemplateDecisionsAsync(CancellationToken ct);

    /// <summary>
    /// Stores a new template version in <paramref name="role"/> (champion or challenger). Null when done, else why not. A text too close
    /// to one the kind has retired (<see cref="Whetstone.Templates.Similarity"/>) is refused unless <paramref name="allowSimilar"/>.
    /// </summary>
    Task<string?> AddTemplateAsync(Whetstone.Templates.Template version, string role, DecisionRecord? decision, CancellationToken ct, bool allowSimilar = false);

    /// <summary>Makes a kind's challenger its champion; the champion it replaces is retired, its record kept. Null when done, else why not.</summary>
    Task<string?> PromoteTemplateAsync(string kind, string id, string version, DecisionRecord? decision, CancellationToken ct);

    /// <summary>Retires a champion or challenger, keeping its record; a kind left with no champion is held out entirely. Null when done, else why not.</summary>
    Task<string?> RetireTemplateAsync(string kind, string id, string version, DecisionRecord? decision, CancellationToken ct);

    /// <summary>Upgrades an older file and rebuilds the search index from the stored rows. Returns how many rows it covers.</summary>
    Task<int> ReindexAsync(CancellationToken ct);

    /// <summary>
    /// How an import recognises a prompt it has seen: the row <paramref name="importedId"/> if it is stored, else a request a live
    /// client (any id without the import prefix) sent from <paramref name="client"/> with exactly this (already redacted) prompt,
    /// within <paramref name="within"/> of <paramref name="at"/>; null when there is neither. Other imported rows never match, so
    /// two past prompts that redact to the same text stay two.
    /// </summary>
    Task<string?> FindAsync(string importedId, string client, string prompt, DateTimeOffset at, TimeSpan within, CancellationToken ct);

    /// <summary>Every replay case (ADR 0006); empty for a file before schema 6.</summary>
    Task<IReadOnlyList<Whetstone.Gym.GymCase>> GymCasesAsync(CancellationToken ct);

    /// <summary>Stores new replay cases; a case already stored is left as it is. Null when done, else why not.</summary>
    Task<string?> AddGymCasesAsync(IReadOnlyList<Whetstone.Gym.GymCase> cases, CancellationToken ct);

    /// <summary>Every stored replay run and judge verdict; empty for a file before schema 6.</summary>
    Task<(IReadOnlyList<Whetstone.Gym.GymRun> Runs, IReadOnlyList<Whetstone.Gym.GymVerdict> Verdicts)> GymResultsAsync(CancellationToken ct);

    /// <summary>Stores replay runs and verdicts in one transaction. Null when done, else why not.</summary>
    Task<string?> AddGymResultsAsync(IReadOnlyList<Whetstone.Gym.GymRun> runs, IReadOnlyList<Whetstone.Gym.GymVerdict> verdicts, CancellationToken ct);

    /// <summary>Marks replay cases as a template's unit cases (pinned). Null when done, else why not.</summary>
    Task<string?> PinGymCasesAsync(IReadOnlyList<string> caseIds, CancellationToken ct);

    /// <summary>Sets (or, with null, clears) the owner's hand label on one request. Null when done, else why not.</summary>
    Task<string?> LabelAsync(string requestId, bool? label, CancellationToken ct);

    /// <summary>Deletes the matching rows and rewrites the file so their text is not left in it. Returns how many went.</summary>
    Task<int> ForgetAsync(RowFilter filter, CancellationToken ct);
}
