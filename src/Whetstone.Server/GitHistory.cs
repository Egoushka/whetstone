using System.Diagnostics;
using System.Globalization;
using Whetstone.Import;

namespace Whetstone.Server;

/// <summary>
/// <see cref="IRepositoryHistory"/> from the git on PATH. A directory a session worked in may be gone (a removed worktree), so a
/// path inside a worktree folder falls back to the checkout that holds it. Any git failure is "not a repository" or "no commit".
/// </summary>
public sealed class GitHistory : IRepositoryHistory
{
    private static readonly string WorktreeFolder = $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}worktrees{Path.DirectorySeparatorChar}";

    private readonly Dictionary<string, (string? Checkout, string? Email)> _known = new(StringComparer.Ordinal);

    public string? RepositoryOf(string workingDirectory) =>
        Checkout(workingDirectory).Checkout is { } checkout ? Path.GetFileName(checkout) : null;

    public bool CommittedBetween(string workingDirectory, DateTimeOffset since, DateTimeOffset until)
    {
        var (checkout, email) = Checkout(workingDirectory);
        if (checkout is null || email is null)
            return false;
        var from = since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var to = until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return Git(checkout, "log", "--all", $"--since=@{from}", $"--until=@{to}", $"--author={email}", "--format=%H", "-1") is { Length: > 0 };
    }

    private (string? Checkout, string? Email) Checkout(string workingDirectory)
    {
        if (_known.TryGetValue(workingDirectory, out var known))
            return known;
        var directory = workingDirectory;
        if (!Directory.Exists(directory) && directory.IndexOf(WorktreeFolder, StringComparison.Ordinal) is var cut and > 0)
            directory = directory[..cut];
        string? checkout = null;
        if (Directory.Exists(directory) && Git(directory, "rev-parse", "--path-format=absolute", "--git-common-dir") is { Length: > 0 } common)
        {
            // The common directory of a linked worktree is the main checkout's .git: name the repository by the main checkout.
            checkout = Path.GetFileName(common) == ".git" ? Path.GetDirectoryName(common) : Git(directory, "rev-parse", "--show-toplevel");
        }
        var email = checkout is null ? null : Git(checkout, "config", "user.email");
        return _known[workingDirectory] = (string.IsNullOrEmpty(checkout) ? null : checkout, string.IsNullOrEmpty(email) ? null : email);
    }

    private static string? Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(directory);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
