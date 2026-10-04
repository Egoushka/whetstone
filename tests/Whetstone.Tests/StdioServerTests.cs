using System.IO.Pipelines;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Whetstone.Server;
using static Whetstone.Tests.McpHarness;

namespace Whetstone.Tests;

/// <summary>`whetstone mcp` in process: two pipes stand in for the child's stdin and stdout.</summary>
public sealed class StdioServerTests : IAsyncDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private IHost? _host;

    [Fact]
    public async Task Enhance_runs_over_stdio_with_the_same_instructions()
    {
        _host = WhetstoneServer.CreateStdio(new PassThrough(), _toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        await _host.StartAsync();
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream()));

        var response = Enhanced(await Call(client, Tools.Enhance, """{"prompt":"review my diff"}"""));

        Assert.Equal(WhetstoneServer.Instructions, client.ServerInstructions);
        Assert.Equal("review my diff", response.Prompt);
        Assert.False(response.Changed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
