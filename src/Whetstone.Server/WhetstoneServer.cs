using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using Whetstone.Storage;

namespace Whetstone.Server;

/// <param name="Port">0 picks a free one.</param>
/// <param name="ApiKey">Required as a bearer token on /v1/mcp, even on loopback: any local process can reach the port.</param>
/// <param name="Listen">Address to bind; loopback unless the server runs on a private network.</param>
/// <param name="AllowedHosts">Host names accepted besides localhost and 127.0.0.1; required when Listen is not loopback.</param>
public sealed record ServerSettings(int Port, string ApiKey, string Listen = "127.0.0.1", IReadOnlyList<string>? AllowedHosts = null);

/// <summary>
/// The MCP tools "enhance" and "feedback" over streamable HTTP at /v1/mcp, with GET /health for supervisors; the same
/// tools over stdio for a client that starts whetstone itself.
/// </summary>
public static class WhetstoneServer
{
    public const int MaxRequestBytes = 1_000_000;

    private const long MiB = 1024 * 1024;

    /// <summary>Sent to MCP clients on initialize so they know when to call whetstone without loading the tools first.</summary>
    public const string Instructions =
        "whetstone returns a better version of a prompt before you run it. Call enhance with the prompt and the context the "
        + "user allows (repository, commit, task_kind, client); show the returned prompt as a diff and send whichever the user "
        + "picks. If enhance is slow or fails, send the original. After the run, call feedback with the request_id and the outcome.";

    public static string Version { get; } = typeof(WhetstoneServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static WebApplication Create(ServerSettings settings, IEnhancer enhancer, IStore? store = null)
    {
        if (string.IsNullOrEmpty(settings.ApiKey))
            throw new ArgumentException("an API key is required", nameof(settings));
        var address = IPAddress.Parse(settings.Listen);
        IReadOnlyList<string> hosts = settings.AllowedHosts ?? [];
        // Beyond loopback the Host check is the only guard against DNS rebinding, so it must name the server.
        if (!IPAddress.IsLoopback(address) && hosts.Count == 0)
            throw new InvalidOperationException($"listen address {settings.Listen} is not loopback; allowed hosts must name the server");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(address, settings.Port);
            k.Limits.MaxRequestBodySize = MaxRequestBytes;
        });
        // A page in the owner's browser could reach the port through DNS rebinding; only a known Host is accepted.
        builder.Services.AddHostFiltering(o => o.AllowedHosts = ["localhost", "127.0.0.1", .. hosts]);
        AddTools(builder.Services, enhancer, store)
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);

        var app = builder.Build();
        app.UseHostFiltering();
        var expected = Encoding.UTF8.GetBytes($"Bearer {settings.ApiKey}");
        app.Use(async (ctx, next) =>
        {
            // /health says only that the process answers, so a supervisor needs no key.
            if (ctx.Request.Path == "/health"
                || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()), expected))
            {
                await next(ctx);
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });
        var memory = store ?? NullStore.Instance;
        app.MapGet("/health", () =>
        {
            // Process numbers for a supervisor: a 16-hour run once reached a 9.5 GB footprint with no cause on record.
            using var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            return Results.Json(new
            {
                status = "ok",
                version = Version,
                store_failures = memory.Failures,
                uptime_seconds = (long)(DateTime.Now - process.StartTime).TotalSeconds,
                cpu_seconds = (long)process.TotalProcessorTime.TotalSeconds,
                threads = process.Threads.Count,
                heap_mb = GC.GetTotalMemory(false) / MiB,
                committed_mb = gc.TotalCommittedBytes / MiB,
                gen2_collections = GC.CollectionCount(2),
            });
        });
        app.MapMcp("/v1/mcp");
        return app;
    }

    /// <summary>
    /// `whetstone mcp`: the same tools over stdio, one session, no listener and so no key. Protocol traffic owns
    /// <paramref name="output"/> (the process's stdout), so every log line goes to stderr.
    /// </summary>
    public static IHost CreateStdio(IEnhancer enhancer, Stream input, Stream output, IStore? store = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(LogLevel.Warning);
        AddTools(builder.Services, enhancer, store).WithStreamServerTransport(input, output);
        return builder.Build();
    }

    private static IMcpServerBuilder AddTools(IServiceCollection services, IEnhancer enhancer, IStore? store)
    {
        services.AddSingleton(enhancer);
        services.AddSingleton(store ?? NullStore.Instance);
        return services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "whetstone", Version = Version };
                o.ServerInstructions = Instructions;
            })
            .WithTools([Tools.CreateEnhance(), Tools.CreateFeedback()]);
    }
}
