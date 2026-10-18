using Whetstone.Contracts;
using Whetstone.Gym;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Gym;

/// <summary>ADR 0006: which stored requests the gym may replay, stored as an id and a hash, never a copy of the prompt.</summary>
public sealed class GymSelectTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public GymSelectTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private async Task Seed(string id, int minute, string prompt, string kind = "agent/explore", string? commit = "0123456789abcdef0123456789abcdef01234567")
    {
        var answer = new EnhanceResponse(prompt, false, null, null, "", kind, id, false);
        await _stores.ForUser("owner").RecordAsync(RequestRow.From(new EnhanceRequest(prompt, new EnhanceContext("example-app", commit, kind, "claude-code/agent")), answer, T0.AddMinutes(minute)), CancellationToken.None);
    }

    private async Task<(int Code, string Out, string Err)> Gym(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.GymAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Select_keeps_replayable_requests_newest_first_and_says_why_the_rest_were_left_out()
    {
        await Seed("old", 0, "find where the retry policy is configured");
        await Seed("new", 5, "list the files that parse the config");
        await Seed("no-commit", 6, "find the cache layer", commit: null);
        await Seed("live", 7, "check why CI fails on the main branch");
        await Seed("secret", 8, "find where the token " + "sk-ant-api03-" + new string('A', 95) + " is read");
        await Seed("other-kind", 9, "implement the parser", kind: "agent/general-purpose/implement");

        var (code, output, _) = await Gym("select", "--kind", "agent/explore", "--confirm");

        Assert.Equal(Commands.Ok, code);
        Assert.Contains("5 stored requests of agent/explore: 1 without a repository and commit, 1 about live state, 1 with a redaction marker, 0 already cases; 2 chosen.", output, StringComparison.Ordinal);
        var cases = await _stores.AdminFor("owner").GymCasesAsync(CancellationToken.None);
        Assert.Equal(["new", "old"], cases.OrderByDescending(c => c.SourceRequestId == "new").Select(c => c.SourceRequestId));
        Assert.All(cases, c => Assert.Equal(32, c.PromptHash.Length));
        Assert.DoesNotContain(Db.Query(_data.FileFor("owner"), "SELECT * FROM gym_cases"), row => row.Values.Any(v => v is string s && s.Contains("retry policy", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Select_counts_until_confirm_and_never_picks_a_case_twice()
    {
        await Seed("r1", 0, "find where the retry policy is configured");

        Assert.Contains("Nothing was stored", (await Gym("select", "--kind", "agent/explore")).Out, StringComparison.Ordinal);
        Assert.Empty(await _stores.AdminFor("owner").GymCasesAsync(CancellationToken.None));
        await Gym("select", "--kind", "agent/explore", "--confirm");
        Assert.Contains("1 already cases; 0 chosen", (await Gym("select", "--kind", "agent/explore", "--confirm")).Out, StringComparison.Ordinal);
        Assert.Single(await _stores.AdminFor("owner").GymCasesAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData]
    [InlineData("select")]
    [InlineData("select", "--kind", "agent/general-purpose/implement")]
    [InlineData("select", "--kind", "agent/explore", "--limit", "0")]
    public async Task Gym_refuses_what_it_cannot_do(params string[] options)
    {
        var (code, _, error) = await Gym(options);

        Assert.NotEqual(Commands.Ok, code);
        Assert.Contains(Commands.GymUsage, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_read_only_kinds_are_replayable()
    {
        Assert.True(GymCases.Replayable("agent/explore"));
        Assert.False(GymCases.Replayable("typed/continue"));
        Assert.False(GymCases.Replayable("fetch/extract"));
    }
}
