using Microsoft.Extensions.Hosting;
using Whetstone;
using Whetstone.Server;
using Whetstone.Storage;

const string Usage = """
    usage: whetstone <command>
      mcp                                   the MCP tools over stdio, for a client that starts whetstone itself
      serve [--listen ADDR] [--port N]      MCP over HTTP at /v1/mcp and GET /health (127.0.0.1:7340 by default);
                                            the bearer key is read from WHETSTONE_API_KEY
    Every request is stored, redacted, in <WHETSTONE_DATA_DIR>/<WHETSTONE_USER>/whetstone.db
    (user data directory and "owner" by default).
    """;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var enhancer = new PassThrough();

var user = Environment.GetEnvironmentVariable("WHETSTONE_USER") is { Length: > 0 } named ? named : SqliteStores.DefaultUser;
if (!SqliteStores.ValidUser(user))
{
    Console.Error.WriteLine("whetstone: WHETSTONE_USER is 1 to 64 characters of a-z, 0-9, '_' and '-'");
    return 2;
}
var dataDirectory = Environment.GetEnvironmentVariable("WHETSTONE_DATA_DIR") is { Length: > 0 } dir
    ? dir
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "whetstone");
using var stores = new SqliteStores(dataDirectory);
// The reason is an exception type: a message may quote a prompt.
var store = new GuardedStore(stores.ForUser(user), reason => Console.Error.WriteLine($"whetstone: could not store a request ({reason})"));
Console.Error.WriteLine($"whetstone: storing requests, redacted, in {stores.PathFor(user)}");

switch (args)
{
    case ["mcp"]:
        using (var host = WhetstoneServer.CreateStdio(enhancer, Console.OpenStandardInput(), Console.OpenStandardOutput(), store))
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
        await using (var app = WhetstoneServer.Create(new ServerSettings(port, key, Option("--listen") ?? "127.0.0.1"), enhancer, store))
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
