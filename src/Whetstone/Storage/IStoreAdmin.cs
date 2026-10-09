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

/// <summary>
/// What the commands <c>export</c> and <c>forget</c> do to one user's store (ADR 0003). Separate from <see cref="IStore"/> on
/// purpose: the server and its tools hold only <see cref="IStore"/>, so no client and no agent can reach these. They never create
/// a store: a user with no file has nothing to export or forget.
/// </summary>
public interface IStoreAdmin
{
    Task<int> CountAsync(RowFilter filter, CancellationToken ct);

    IAsyncEnumerable<ExportRecord> ExportAsync(RowFilter filter, CancellationToken ct);

    /// <summary>Upgrades an older file and rebuilds the search index from the stored rows. Returns how many rows it covers.</summary>
    Task<int> ReindexAsync(CancellationToken ct);

    /// <summary>
    /// How an import recognises a prompt it has seen: the row <paramref name="importedId"/> if it is stored, else a request a live
    /// client (any id without the import prefix) sent from <paramref name="client"/> with exactly this (already redacted) prompt,
    /// within <paramref name="within"/> of <paramref name="at"/>; null when there is neither. Other imported rows never match, so
    /// two past prompts that redact to the same text stay two.
    /// </summary>
    Task<string?> FindAsync(string importedId, string client, string prompt, DateTimeOffset at, TimeSpan within, CancellationToken ct);

    /// <summary>Deletes the matching rows and rewrites the file so their text is not left in it. Returns how many went.</summary>
    Task<int> ForgetAsync(RowFilter filter, CancellationToken ct);
}
