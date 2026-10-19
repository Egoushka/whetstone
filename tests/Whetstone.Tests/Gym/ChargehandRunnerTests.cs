using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Whetstone.Gym;
using Whetstone.Server;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Gym;

/// <summary>ADR 0006: the chargehand gym client replays read-only without touching whetstone, and the judge maps its pick back to an arm.</summary>
public sealed class ChargehandRunnerTests : IDisposable
{
    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string Completed =
        """{"contract_version":"result/v1","task_id":"run-1","status":"completed","summary":"Three files.","claims":[{"text":"It sleeps 2s."}],"usage":{"input":10,"output":20,"cache_read":300,"cache_write":40,"usd":0.05}}""";

    private GymSettings Settings()
    {
        Directory.CreateDirectory(Path.Combine(_data.Path, "app", ".git"));
        return new GymSettings(new Uri("http://localhost:4300"), "k", "cheap", 0.15, [_data.Path], TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_finished_run_is_read_for_its_status_cost_and_answer_and_skips_the_enhancer()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, Completed));
        var runner = new ChargehandRunner(Settings(), new HttpClient(handler));
        var result = await runner.RunAsync(new ReplayRequest("c1", "Example/app", "abcdef1", "Where does ingest sleep?", GymArms.Control), default);
        Assert.True(result.Completed);
        Assert.Equal(("run-1", 0.05, 300L), (result.ClientRunId, result.CostUsd, result.CacheReadTokens));
        Assert.Equal("Three files.\nIt sleeps 2s.", result.Answer);
        var (message, body) = Assert.Single(handler.Seen);
        Assert.Equal("Bearer k", message.Headers.Authorization!.ToString());
        var sent = JsonNode.Parse(body!)!;
        Assert.False(sent["context"]!["enhance"]!.GetValue<bool>());
        Assert.False(sent["context"]!["interactive"]!.GetValue<bool>());
        Assert.Equal("abcdef1", sent["context"]!["repository"]!["commit"]!.GetValue<string>());
        Assert.EndsWith("app", sent["context"]!["repository"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_that_is_still_going_is_polled_until_it_finishes()
    {
        var calls = 0;
        var handler = new Handler((request, _) => request.Method == HttpMethod.Post || ++calls < 2
            ? Json(HttpStatusCode.Accepted, """{"contract_version":"run-status/v1","run_id":"r9","status":"running"}""")
            : Json(HttpStatusCode.OK, Completed));
        var runner = new ChargehandRunner(Settings(), new HttpClient(handler), TimeSpan.FromMilliseconds(1));
        var result = await runner.RunAsync(new ReplayRequest("c1", "app", "abcdef1", "p", GymArms.Challenger), default);
        Assert.True(result.Completed);
        Assert.Contains(handler.Seen, s => s.Request.RequestUri!.AbsolutePath == "/v1/runs/r9");
    }

    [Fact]
    public async Task A_repository_without_a_checkout_is_not_run()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, Completed));
        var runner = new ChargehandRunner(Settings(), new HttpClient(handler));
        var result = await runner.RunAsync(new ReplayRequest("c1", "Example/missing", "abcdef1", "p", GymArms.Control), default);
        Assert.Equal((null, "no_checkout"), (result.Completed, result.Status));
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task A_failed_run_and_a_refusal_are_recorded_as_not_completed()
    {
        var failed = new ChargehandRunner(Settings(), new HttpClient(new Handler((_, _) => Json(HttpStatusCode.OK, Completed.Replace("completed", "failed")))));
        Assert.False((await failed.RunAsync(new ReplayRequest("c", "app", "abcdef1", "p", GymArms.Control), default)).Completed);
        var refused = new ChargehandRunner(Settings(), new HttpClient(new Handler((_, _) => Json(HttpStatusCode.BadRequest, """{"errors":["x"]}"""))));
        var result = await refused.RunAsync(new ReplayRequest("c", "app", "abcdef1", "p", GymArms.Control), default);
        Assert.Equal((false, "400"), (result.Completed, result.Status));
    }

    [Fact]
    public void Settings_are_all_or_nothing()
    {
        Assert.Null(GymSettings.From(_ => null, out var none));
        Assert.Null(none);
        Assert.Null(GymSettings.From(n => n == "WHETSTONE_GYM_URL" ? "http://localhost:4300" : null, out var partial));
        Assert.NotNull(partial);
        var all = GymSettings.From(n => n switch { "WHETSTONE_GYM_URL" => "http://localhost:4300", "WHETSTONE_GYM_KEY" => "k", "WHETSTONE_GYM_REPOS" => "/a:/b", _ => null }, out var problem);
        Assert.Null(problem);
        Assert.Equal((2, "cheap", 0.15), (all!.RepositoryRoots.Count, all.Preset, all.RunBudgetUsd));
    }

    private sealed class Writer(string text) : IChallengerWriter
    {
        public string? User { get; private set; }

        public Task<WriterReply> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct)
        {
            User = user;
            return Task.FromResult(new WriterReply(text, 1000, 2));
        }
    }

    private static readonly WriterSettings Priced = new(new Uri("http://localhost"), "judge-m", "k", 1, 10, 3, 15);

    [Theory]
    [InlineData("A", false, "control")]
    [InlineData("B", false, "challenger")]
    [InlineData("A", true, "challenger")]
    [InlineData("b.", true, "control")]
    [InlineData("TIE", false, "tie")]
    [InlineData("neither", true, "tie")]
    public async Task The_judge_maps_its_pick_back_to_an_arm(string reply, bool flipped, string winner)
    {
        var writer = new Writer(reply);
        var judge = new PairwiseJudge(writer, Priced);
        var (won, cost) = await judge.JudgeAsync("ask", "control answer", "challenger answer", flipped, default);
        Assert.Equal(winner, won);
        Assert.Equal(0.00303, cost!.Value, 6);
        Assert.Contains(flipped ? "Answer A:\nchallenger answer" : "Answer A:\ncontrol answer", writer.User);
        Assert.Equal("judge-m", judge.Model);
    }
}
