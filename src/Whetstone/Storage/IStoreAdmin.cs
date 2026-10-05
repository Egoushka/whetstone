using System.Globalization;
using Whetstone.Contracts;

namespace Whetstone.Storage;

/// <summary>
/// Which stored requests a command means. Both fields narrow it; neither set means every request, which a command must ask for
/// by name (<c>forget --all</c>), never by leaving a filter out.
/// </summary>
/// <param name="Repository">The stored repository, exactly (it is stored redacted).</param>
/// <param name="Before">Requests answered strictly before this instant.</param>
public sealed record RowFilter(string? Repository = null, DateTimeOffset? Before = null)
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

    /// <summary>Deletes the matching rows and rewrites the file so their text is not left in it. Returns how many went.</summary>
    Task<int> ForgetAsync(RowFilter filter, CancellationToken ct);
}
