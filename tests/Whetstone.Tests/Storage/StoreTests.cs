using Whetstone.Contracts;
using Whetstone.Storage;
using Whetstone.Tests.Redaction;

namespace Whetstone.Tests.Storage;

/// <summary>Goal 0.2: one SQLite file per user, redacted before the write, outcomes joined by request id.</summary>
public sealed class StoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public StoreTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private static EnhanceResponse Answer(string id = "req-1", bool changed = false) =>
        new("answer", changed, null, null, "test", null, id, HeldOut: false);

    private static RequestRow Row(string prompt, EnhanceContext? context = null, string id = "req-1") =>
        RequestRow.From(new EnhanceRequest(prompt, context), Answer(id), At);

    private IStore Store(string user = "owner") => _stores.ForUser(user);

    [Fact]
    public async Task A_request_is_stored_with_its_context()
    {
        var context = new EnhanceContext("example/app", "abc1234", "review", "test-client");

        await Store().RecordAsync(Row("review my diff", context), CancellationToken.None);

        var row = Assert.Single(Db.Requests(_data.FileFor("owner")));
        Assert.Equal("req-1", row["request_id"]);
        Assert.Equal("review my diff", row["prompt"]);
        Assert.Equal(("example/app", "abc1234", "review", "test-client"), ((string)row["repository"]!, (string)row["commit_sha"]!, (string)row["task_kind"]!, (string)row["client"]!));
        Assert.Equal(0L, row["changed"]);
        Assert.Equal(0L, row["truncated"]);
        Assert.Equal("2026-10-05T12:00:00.0000000Z", row["created_at"]);
        Assert.Null(row["rewrite_accepted"]);
    }

    [Fact]
    public async Task Every_seeded_secret_is_absent_from_the_file_after_a_replay()
    {
        var store = Store();
        var i = 0;
        foreach (var seeded in Corpus.Secrets)
            foreach (var context in Corpus.Contexts)
            {
                var prompt = context.Replace("{0}", seeded.Text, StringComparison.Ordinal).Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
                await store.RecordAsync(Row(prompt, id: $"req-{i++}"), CancellationToken.None);
            }

        var file = Db.Everything(_data.FileFor("owner"));

        Assert.Equal(Corpus.Secrets.Count * Corpus.Contexts.Count, Db.Requests(_data.FileFor("owner")).Count);
        foreach (var seeded in Corpus.Secrets)
            Assert.False(file.Contains(seeded.Value, StringComparison.Ordinal), $"{seeded.Name} is in the file");
    }

    [Fact]
    public async Task The_context_is_redacted_too()
    {
        var url = Corpus.Secrets.Single(s => s.Name == "url password");
        var token = Corpus.Secrets.Single(s => s.Name == "npm token");

        await Store().RecordAsync(Row("p", new EnhanceContext(Repository: url.Text, Client: token.Text)), CancellationToken.None);

        var file = Db.Everything(_data.FileFor("owner"));
        Assert.DoesNotContain(url.Value, file, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Value, file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_prompt_is_cut_after_redaction_and_a_secret_on_the_cut_does_not_leak_a_piece()
    {
        var secret = Corpus.Secrets.Single(s => s.Name == "npm token").Text;
        var prompt = new string('x', RequestRow.MaxPromptChars - 10) + " " + secret + " " + new string('y', 10_000);

        await Store().RecordAsync(Row(prompt), CancellationToken.None);

        var row = Assert.Single(Db.Requests(_data.FileFor("owner")));
        var stored = (string)row["prompt"]!;
        Assert.Equal(1L, row["truncated"]);
        Assert.True(stored.Length <= RequestRow.MaxPromptChars);
        Assert.EndsWith(RequestRow.CutMarker, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(secret[..12], Db.Everything(_data.FileFor("owner")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", RequestRow.MaxPromptChars));

        var stored = Row(emoji).Prompt;

        Assert.True(stored.Length <= RequestRow.MaxPromptChars);
        var body = stored[..^RequestRow.CutMarker.Length];
        Assert.False(char.IsHighSurrogate(body[^1]));
    }

    [Fact]
    public async Task Feedback_fills_the_outcome_and_the_last_report_wins()
    {
        var store = Store();
        await store.RecordAsync(Row("p"), CancellationToken.None);

        Assert.True(await store.RecordOutcomeAsync(Outcome("req-1", rewriteAccepted: false, score: 0.2), CancellationToken.None));
        Assert.True(await store.RecordOutcomeAsync(Outcome("req-1", rewriteAccepted: true, score: 0.9, cost: 0.04m, model: "provider/model-a"), CancellationToken.None));

        var row = Assert.Single(Db.Requests(_data.FileFor("owner")));
        Assert.Equal(1L, row["rewrite_accepted"]);
        Assert.Equal(0.9, row["score"]);
        Assert.Equal(0.04, row["cost_usd"]);
        Assert.Equal("provider/model-a", row["model"]);
        Assert.Null(row["model_overridden"]);
        Assert.NotNull(row["feedback_at"]);
    }

    [Fact]
    public async Task Feedback_for_an_unknown_request_stores_nothing()
    {
        var store = Store();
        await store.RecordAsync(Row("p"), CancellationToken.None);

        var found = await store.RecordOutcomeAsync(Outcome("req-unknown", rewriteAccepted: true), CancellationToken.None);

        Assert.False(found);
        Assert.Null(Assert.Single(Db.Requests(_data.FileFor("owner")))["rewrite_accepted"]);
    }

    [Fact]
    public async Task A_reported_model_is_redacted()
    {
        var token = Corpus.Secrets.Single(s => s.Name == "npm token");
        var store = Store();
        await store.RecordAsync(Row("p"), CancellationToken.None);

        await store.RecordOutcomeAsync(Outcome("req-1", model: token.Text), CancellationToken.None);

        Assert.DoesNotContain(token.Value, Db.Everything(_data.FileFor("owner")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_users_never_share_a_file()
    {
        await Store("alice").RecordAsync(Row("alice's prompt"), CancellationToken.None);
        await Store("bob").RecordAsync(Row("bob's prompt", id: "req-2"), CancellationToken.None);

        Assert.NotEqual(_stores.PathFor("alice"), _stores.PathFor("bob"));
        Assert.Equal("alice's prompt", Assert.Single(Db.Requests(_data.FileFor("alice")))["prompt"]);
        Assert.Equal("bob's prompt", Assert.Single(Db.Requests(_data.FileFor("bob")))["prompt"]);
        Assert.False(await Store("bob").RecordOutcomeAsync(Outcome("req-1", rewriteAccepted: true), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("A")]
    [InlineData(".hidden")]
    [InlineData("-dash")]
    [InlineData("has space")]
    public void A_user_id_that_is_not_a_safe_name_is_refused(string user) =>
        Assert.Throws<ArgumentException>(() => _stores.ForUser(user));

    [Fact]
    public void A_user_id_is_at_most_64_characters()
    {
        Assert.True(SqliteStores.ValidUser(new string('a', 64)));
        Assert.False(SqliteStores.ValidUser(new string('a', 65)));
    }

    [Fact]
    public async Task The_directory_and_the_file_are_readable_by_their_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return; // no Unix file modes there

        await Store().RecordAsync(Row("p"), CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(_data.FileFor("owner"))!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_data.FileFor("owner")));
    }

    [Fact]
    public async Task Parallel_requests_all_land()
    {
        var store = Store();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => store.RecordAsync(Row($"prompt {i}", id: $"req-{i}"), CancellationToken.None)));

        Assert.Equal(50, Db.Requests(_data.FileFor("owner")).Count);
    }

    [Fact]
    public async Task A_store_from_a_newer_whetstone_is_refused_not_changed()
    {
        await Store().RecordAsync(Row("p"), CancellationToken.None);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_data.FileFor("owner")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }
        using var fresh = new SqliteStores(_data.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.ForUser("owner").RecordAsync(Row("q", id: "req-2"), CancellationToken.None));
    }

    private static OutcomeRow Outcome(string id, bool? rewriteAccepted = null, double? score = null, decimal? cost = null, string? model = null) =>
        OutcomeRow.From(new FeedbackRequest(id, new FeedbackOutcome(rewriteAccepted, null, score, cost, model)), At.AddMinutes(5));
}
