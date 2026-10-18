using System.Globalization;
using System.Text.Json;
using Whetstone.Outcomes;

namespace Whetstone.Server;

/// <summary>
/// <see cref="ICommitHistory"/> from the git on PATH. A stored repository is the main checkout's folder name, so a checkout is found by
/// that name among the folders under a root, at most two levels down. Commits are the checkout's configured user's, on any ref.
/// </summary>
public sealed class GitOutcomes(string root) : ICommitHistory
{
    private Dictionary<string, string>? _checkouts;
    private readonly Dictionary<string, string?> _emails = new(StringComparer.Ordinal);

    public string? CheckoutOf(string repository)
    {
        _checkouts ??= Find(root);
        return _checkouts.GetValueOrDefault(repository);
    }

    public IReadOnlyList<Commit>? CommitsBetween(string checkout, DateTimeOffset since, DateTimeOffset until)
    {
        if (Email(checkout) is not { } email)
            return null;
        var log = GitHistory.Git(checkout, "log", "--all", $"--since=@{Seconds(since)}", $"--until=@{Seconds(until - TimeSpan.FromSeconds(1))}", $"--author={email}", "--format=%H %ct");
        if (log is null)
            return null;
        return [.. log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(parts => new Commit(parts[0], DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], CultureInfo.InvariantCulture))))];
    }

    public bool? Reverted(string checkout, IReadOnlyList<string> shas, DateTimeOffset until)
    {
        var args = new List<string> { "log", "--all", $"--until=@{Seconds(until)}", "--format=%H", "-1" };
        args.AddRange(shas.Select(sha => $"--grep=This reverts commit {sha}"));
        return GitHistory.Git(checkout, [.. args]) is { } found ? found.Length > 0 : null;
    }

    private string? Email(string checkout)
    {
        if (!_emails.TryGetValue(checkout, out var email))
            _emails[checkout] = email = GitHistory.Git(checkout, "config", "user.email") is { Length: > 0 } e ? e : null;
        return email;
    }

    private static string Seconds(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>Folders under the root, and one level below, that are a main checkout (a <c>.git</c> folder, not a worktree's file).</summary>
    private static Dictionary<string, string> Find(string root)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
            return found;
        foreach (var level in new[] { Directory.EnumerateDirectories(root), Directory.EnumerateDirectories(root).SelectMany(SafeChildren) })
            foreach (var directory in level)
                if (Directory.Exists(Path.Combine(directory, ".git")))
                    found.TryAdd(Path.GetFileName(directory), directory);
        return found;
    }

    private static IEnumerable<string> SafeChildren(string directory)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(directory)];
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

/// <summary>
/// <see cref="IForge"/> through the GitHub command-line client, run in the checkout. A pull request is found by its first commit; "first
/// push green" is read from the checks on the pull request's first commit, which is the first push when that push held one commit.
/// </summary>
public sealed class GitHubForge : IForge
{
    private static readonly string[] Passing = ["success", "neutral", "skipped"];

    public (bool? Merged, bool? FirstPushGreen) PullRequestOf(string checkout, IReadOnlyList<string> shas)
    {
        if (Json(checkout, "pr", "list", "--state", "all", "--search", shas[0], "--json", "number,mergedAt", "--limit", "5") is not { ValueKind: JsonValueKind.Array } prs
            || prs.GetArrayLength() == 0)
            return (null, null);
        var merged = prs.EnumerateArray().Any(pr => pr.TryGetProperty("mergedAt", out var at) && at.ValueKind == JsonValueKind.String && at.GetString() is { Length: > 0 });
        var number = prs[0].GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture);
        if (Json(checkout, "pr", "view", number, "--json", "commits") is not { } view
            || !view.TryGetProperty("commits", out var commits) || commits.GetArrayLength() == 0
            || GitHistory.Run("gh", checkout, "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner") is not { Length: > 0 } repository)
            return (merged, null);
        var first = commits[0].GetProperty("oid").GetString();
        if (Json(checkout, "api", $"repos/{repository}/commits/{first}/check-runs") is not { } runs
            || !runs.TryGetProperty("check_runs", out var checks) || checks.GetArrayLength() == 0)
            return (merged, null);
        var conclusions = checks.EnumerateArray().Select(c => c.TryGetProperty("conclusion", out var v) ? v.GetString() : null).ToList();
        return (merged, conclusions.Any(c => c is null) ? null : conclusions.All(c => Passing.Contains(c, StringComparer.Ordinal)));
    }

    private static JsonElement? Json(string checkout, params string[] args)
    {
        if (GitHistory.Run("gh", checkout, args) is not { Length: > 0 } text)
            return null;
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
