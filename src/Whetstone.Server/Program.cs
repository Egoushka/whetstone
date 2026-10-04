using Microsoft.Extensions.Hosting;
using Whetstone;
using Whetstone.Server;

const string Usage = """
    usage: whetstone <command>
      mcp                                   the MCP tools over stdio, for a client that starts whetstone itself
      serve [--listen ADDR] [--port N]      MCP over HTTP at /v1/mcp and GET /health (127.0.0.1:7340 by default);
                                            the bearer key is read from WHETSTONE_API_KEY
    """;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var enhancer = new PassThrough();

switch (args)
{
    case ["mcp"]:
        using (var host = WhetstoneServer.CreateStdio(enhancer, Console.OpenStandardInput(), Console.OpenStandardOutput()))
        {
            // The transport stops the host when the client closes stdin.
            await host.RunAsync(cts.Token);
        }
        return 0;
    case ["serve", .. var options]:
        string? Option(string name) => options.SkipWhile(o => o != name).Skip(1).FirstOrDefault();
        if (Environment.GetEnvironmentVariable("WHETSTONE_API_KEY") is not { Length: > 0 } key)
        {
            Console.Error.WriteLine("whetstone serve: set WHETSTONE_API_KEY");
            return 2;
        }
        if (!int.TryParse(Option("--port") ?? "7340", out var port))
        {
            Console.Error.WriteLine("whetstone serve: --port must be a number");
            return 2;
        }
        await using (var app = WhetstoneServer.Create(new ServerSettings(port, key, Option("--listen") ?? "127.0.0.1"), enhancer))
        {
            await app.StartAsync(cts.Token);
            Console.Error.WriteLine($"whetstone serve: {string.Join(", ", app.Urls)} (MCP /v1/mcp, GET /health)");
            await app.WaitForShutdownAsync(cts.Token);
        }
        return 0;
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}
