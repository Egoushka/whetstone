using Whetstone.Contracts;
using Whetstone.Outcomes;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Outcomes;

/// <summary>Goal 0.6, decision 1: commits, reverts and pull requests after a request, owned by the request until the next one of its session.</summary>
public sealed class DelayedOutcomeTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = T0.AddDays(10);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public DelayedOutcomeTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private sealed class History(params Commit[] commits) : ICommitHistory
    {
        public HashSet<string> RevertedShas { get; } = [];

        public string? CheckoutOf(string repository) => repository == "example-app" ? "/checkouts/example-app" : null;

        public IReadOnlyList<Commit>? CommitsBetween(string checkout, DateTimeOffset since, DateTimeOffset until) =>
            [.. commits.Where(c => c.At >= since && c.At < until)];

        public bool? Reverted(string checkout, IReadOnlyList<string> shas, DateTimeOffset until) => shas.Any(RevertedShas.Contains);
    }

    private sealed class Forge : IForge
    {
        public (bool? Merged, bool? FirstPushGreen) PullRequestOf(string checkout, IReadOnlyList<string> shas) => shas.Contains("c2") ? (true, false) : (null, null);
    }

    private static ExportRecord Record(string id, int minute, string? session = "s-1", string? repository = "example-app") => new(
        id, T0.AddMinutes(minute).UtcDateTime.ToString("O"), "p", false, new ExportContext(repository, null, "typed/implement", "claude-code", session),
        new ExportAnswer(false, null, null, false), null);

    private static Commit At(string sha, int minute) => new(sha, T0.AddMinutes(minute));

    [Fact]
    public void A_request_owns_the_commits_until_the_next_request_of_its_session()
    {
        var records = new[] { Record("r1", 0), Record("r2", 30), Record("other", 200, session: "s-2") };
        var history = new History(At("c1", 10), At("c2", 20), At("c3", 40));

        var found = DelayedOutcomes.Collect(records, history, null, Later, out var report).ToDictionary(f => f.RequestId, f => f.Outcome);

        Assert.Equal(2, found["r1"].Commits);
        Assert.Equal(1, found["r2"].Commits);
        Assert.Equal(0, found["other"].Commits);
        Assert.Equal((3, 3, 3, 2), (report.Requests, report.Located, report.Measured, report.WithCommits));
    }

    [Fact]
    public void A_revert_is_reported_at_once_and_no_revert_only_after_seven_days()
    {
        var history = new History(At("c1", 10), At("c2", 20));
        history.RevertedShas.Add("c2");
        var clean = new History(At("c1", 10));

        var reverted = DelayedOutcomes.Collect([Record("r1", 0)], history, null, T0.AddDays(1), out _).Single().Outcome;
        var tooSoon = DelayedOutcomes.Collect([Record("r1", 0)], clean, null, T0.AddDays(1), out _).Single().Outcome;
        var settled = DelayedOutcomes.Collect([Record("r1", 0)], clean, null, Later, out _).Single().Outcome;

        Assert.Equal((true, null, false), (reverted.Reverted, tooSoon.Reverted, settled.Reverted));
    }

    [Fact]
    public void The_forge_says_whether_the_pull_request_merged_and_ci_passed_on_its_first_push()
    {
        var found = DelayedOutcomes.Collect([Record("r1", 0)], new History(At("c1", 10), At("c2", 20)), new Forge(), Later, out var report).Single().Outcome;

        Assert.Equal((true, false), (found.PrMerged, found.CiFirstPushGreen));
        Assert.Equal(1, report.Merged);
    }

    [Fact]
    public void Requests_without_a_session_a_known_checkout_or_a_closed_window_are_left_alone()
    {
        var records = new[] { Record("no-session", 0, session: null), Record("elsewhere", 0, session: "s-3", repository: "unknown"), Record("open", 150, session: "s-4") };

        var found = DelayedOutcomes.Collect(records, new History(At("c1", 10)), null, T0.AddHours(3), out var report);

        Assert.Empty(found);
        Assert.Equal((1, 0), (report.Requests, report.Located));
    }

    [Fact]
    public async Task Collect_counts_until_confirm_then_stores_through_the_store()
    {
        var answer = new EnhanceResponse("p", false, null, null, "", null, "r1", false);
        await _stores.ForUser("owner").RecordAsync(
            RequestRow.From(new EnhanceRequest("add a retry", new EnhanceContext("example-app", null, "typed/implement", "claude-code", "s-1")), answer, T0), CancellationToken.None);
        var history = new History(new Commit("c1", T0.AddMinutes(10)));
        var root = Directory.CreateDirectory(Path.Combine(_data.Path, "checkouts")).FullName;

        async Task<string> Run(params string[] options)
        {
            var (output, error) = (new StringWriter(), new StringWriter());
            var code = await Commands.OutcomesAsync(["collect", "--root", root, .. options], _stores.ForUser("owner"), _stores.AdminFor("owner"), _ => history, null, output, error, CancellationToken.None);
            Assert.Equal((Commands.Ok, ""), (code, error.ToString()));
            return output.ToString();
        }

        Assert.Contains("Nothing was stored", await Run(), StringComparison.Ordinal);
        Assert.Null(Assert.Single(Db.Requests(_data.FileFor("owner")))["commits"]);
        Assert.Contains("Stored delayed outcomes for 1 requests", await Run("--confirm"), StringComparison.Ordinal);
        Assert.Equal(1L, Assert.Single(Db.Requests(_data.FileFor("owner")))["commits"]);
    }

    [Fact]
    public void Git_finds_the_checkout_by_name_its_users_commits_and_a_revert_of_them()
    {
        var root = Directory.CreateDirectory(Path.Combine(_data.Path, "code")).FullName;
        var checkout = Directory.CreateDirectory(Path.Combine(root, "group", "example-app")).FullName;
        foreach (var args in new[] { new[] { "init", "-q" }, ["config", "user.email", "someone@example.com"], ["config", "user.name", "Someone"], ["config", "commit.gpgsign", "false"] })
            Assert.NotNull(GitHistory.Git(checkout, args));
        File.WriteAllText(Path.Combine(checkout, "a.txt"), "a");
        Assert.NotNull(GitHistory.Git(checkout, "add", "a.txt"));
        Assert.NotNull(GitHistory.Git(checkout, "commit", "-q", "-m", "add a"));
        var sha = GitHistory.Git(checkout, "rev-parse", "HEAD")!;
        var git = new GitOutcomes(root);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(checkout, git.CheckoutOf("example-app"));
        Assert.Null(git.CheckoutOf("group"));
        Assert.Equal(sha, Assert.Single(git.CommitsBetween(checkout, now.AddHours(-1), now.AddHours(1))!).Sha);
        Assert.False(git.Reverted(checkout, [sha], now.AddHours(1)));
        Assert.NotNull(GitHistory.Git(checkout, "revert", "--no-edit", "HEAD"));
        Assert.True(git.Reverted(checkout, [sha], now.AddHours(1)));
    }

    [Theory]
    [InlineData]
    [InlineData("collect")]
    [InlineData("collect", "--root", "/no/such/folder")]
    [InlineData("collect", "--root", ".", "--forge", "gitlab")]
    public async Task Collect_refuses_what_it_cannot_do(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());

        var code = await Commands.OutcomesAsync(options, _stores.ForUser("owner"), _stores.AdminFor("owner"), _ => new History(), null, output, error, CancellationToken.None);

        Assert.NotEqual(Commands.Ok, code);
        Assert.Contains(Commands.OutcomesUsage, error.ToString(), StringComparison.Ordinal);
    }
}
