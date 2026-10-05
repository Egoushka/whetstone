using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.Client;
using Whetstone.Server;
using static Whetstone.Tests.McpHarness;

namespace Whetstone.Tests;

/// <summary>whetstone serve on a free loopback port: MCP at /v1/mcp behind a bearer key, /health open.</summary>
public sealed class HttpServerTests : IAsyncDisposable
{
    private const string Key = "test-key";

    private WebApplication? _app;

    private async Task<Uri> Start(IEnhancer? enhancer = null)
    {
        _app = WhetstoneServer.Create(new ServerSettings(0, Key), enhancer ?? new PassThrough());
        await _app.StartAsync();
        return new Uri(_app.Urls.First());
    }

    private static Task<McpClient> Connect(Uri address, string key = Key) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address, "/v1/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
        }));

    [Fact]
    public async Task Both_tools_are_listed_with_the_instructions()
    {
        await using var client = await Connect(await Start());

        var tools = await client.ListToolsAsync();

        Assert.Equal([Tools.Enhance, Tools.Feedback], tools.Select(t => t.Name).Order());
        Assert.Equal(WhetstoneServer.Instructions, client.ServerInstructions);
    }

    [Fact]
    public async Task Enhance_returns_the_prompt_unchanged()
    {
        await using var client = await Connect(await Start());

        var response = Enhanced(await Call(client, Tools.Enhance,
            """{"prompt":"Fix the failing test in the parser module","context":{"repository":"example/app","commit":"abc1234","client":"test"}}"""));

        Assert.Equal("Fix the failing test in the parser module", response.Prompt);
        Assert.False(response.Changed);
        Assert.Null(response.TemplateId);
        Assert.Null(response.TemplateVersion);
        Assert.False(response.HeldOut);
    }

    // A hook that sends initialize and one call per prompt must not leave a session behind each time: the SDK keeps a stateful
    // session for two hours by default, so thousands of prompts a day grew the process to gigabytes.
    [Fact]
    public async Task Initialize_opens_no_session_and_a_call_needs_none()
    {
        var address = await Start();
        using var http = new HttpClient { BaseAddress = address };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        using var initialize = await http.PostAsync("/v1/mcp", Json(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"hook","version":"1"}}}"""));
        using var call = await http.PostAsync("/v1/mcp", Json(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"enhance","arguments":{"prompt":"review my diff"}}}"""));

        Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        Assert.False(initialize.Headers.Contains("Mcp-Session-Id"));
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Contains("review my diff", await call.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_late_enhancer_answers_with_the_original_over_mcp()
    {
        await using var client = await Connect(await Start(Enhancers.Slow(TimeSpan.FromSeconds(10))));

        var response = Enhanced(await Call(client, Tools.Enhance, """{"prompt":"review my diff","deadline_ms":100}"""));

        Assert.Equal("review my diff", response.Prompt);
        Assert.False(response.Changed);
    }

    [Theory]
    [InlineData("""{"prompt":""}""", "prompt")]
    [InlineData("""{"prompt":"p","deadline_ms":10}""", "deadline_ms")]
    [InlineData("""{"prompt":"p","model":"x"}""", "model")]
    public async Task An_invalid_enhance_request_is_an_error(string json, string named)
    {
        await using var client = await Connect(await Start());

        Assert.Contains(named, Error(await Call(client, Tools.Enhance, json)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feedback_is_accepted_with_an_empty_result()
    {
        await using var client = await Connect(await Start());

        var call = await Call(client, Tools.Feedback,
            """{"request_id":"req-0002","outcome":{"rewrite_accepted":true,"model_overridden":false,"score":0.9,"cost_usd":0.04,"model":"provider/model-a"}}""");

        Assert.NotEqual(true, call.IsError);
        Assert.Equal(JsonValueKind.Object, call.StructuredContent!.Value.ValueKind);
        Assert.Empty(call.StructuredContent.Value.EnumerateObject());
    }

    [Fact]
    public async Task Feedback_with_a_score_out_of_range_is_an_error()
    {
        await using var client = await Connect(await Start());

        Assert.Contains("score", Error(await Call(client, Tools.Feedback, """{"request_id":"r","outcome":{"score":1.5}}""")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_without_the_key_is_refused_and_health_is_open()
    {
        var address = await Start();
        using var http = new HttpClient { BaseAddress = address };

        using var mcp = await http.PostAsync(new Uri("/v1/mcp", UriKind.Relative), new StringContent("{}"));
        using var health = await http.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", JsonDocument.Parse(await health.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_wrong_key_is_refused()
    {
        var address = await Start();
        using var http = new HttpClient { BaseAddress = address };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        using var mcp = await http.PostAsync(new Uri("/v1/mcp", UriKind.Relative), new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
    }

    [Fact]
    public void Listening_beyond_loopback_needs_allowed_hosts() =>
        Assert.Throws<InvalidOperationException>(() => WhetstoneServer.Create(new ServerSettings(0, Key, Listen: "0.0.0.0"), new PassThrough()));

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
