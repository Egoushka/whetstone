using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Contracts;
using Whetstone.Server;

namespace Whetstone.Tests;

/// <summary>enhance never blocks the client: a late or failing enhancer yields the original prompt inside the deadline.</summary>
public class DeadlineTests
{
    /// <summary>The late enhancers take 10 s; answering well inside that proves the deadline, without a margin a cold CI runner can miss.</summary>
    private static readonly TimeSpan NotWaiting = TimeSpan.FromSeconds(3);

    private static readonly EnhanceRequest Request = new("Fix the failing test in the parser module", DeadlineMs: 200);

    [Fact]
    public async Task An_answer_inside_the_deadline_is_returned()
    {
        // A generous deadline: this checks the success path, and a cold, busy runner must not turn it into a timing test.
        var response = await Tools.AnswerAsync(Enhancers.Rewriting(), Request with { DeadlineMs = 10_000 }, NullLogger.Instance, CancellationToken.None);

        Assert.True(response.Changed);
        Assert.Equal("tpl-test", response.TemplateId);
    }

    [Fact]
    public async Task A_late_enhancer_yields_the_original_prompt_before_the_deadline()
    {
        var clock = Stopwatch.StartNew();
        var response = await Tools.AnswerAsync(Enhancers.Slow(TimeSpan.FromSeconds(10)), Request, NullLogger.Instance, CancellationToken.None);

        Assert.True(clock.Elapsed < NotWaiting, $"answered after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(Request.Prompt, response.Prompt);
        Assert.False(response.Changed);
        Assert.Null(response.TemplateId);
        Assert.Contains("deadline", response.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocking_enhancer_yields_the_original_prompt_before_the_deadline()
    {
        var clock = Stopwatch.StartNew();
        var response = await Tools.AnswerAsync(Enhancers.Blocking(TimeSpan.FromSeconds(10)), Request, NullLogger.Instance, CancellationToken.None);

        Assert.True(clock.Elapsed < NotWaiting, $"answered after {clock.ElapsedMilliseconds} ms");
        Assert.False(response.Changed);
    }

    [Fact]
    public async Task A_failing_enhancer_yields_the_original_prompt()
    {
        var response = await Tools.AnswerAsync(Enhancers.Failing(), Request, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(Request.Prompt, response.Prompt);
        Assert.False(response.Changed);
        Assert.Contains("failed", response.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_pass_through_has_its_own_request_id()
    {
        var pass = new PassThrough();

        var first = await pass.EnhanceAsync(Request, CancellationToken.None);
        var second = await pass.EnhanceAsync(Request, CancellationToken.None);

        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.StartsWith("req-", first.RequestId, StringComparison.Ordinal);
    }
}
