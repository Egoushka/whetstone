using System.Collections.Concurrent;
using Whetstone.Context;
using Whetstone.Contracts;
using Whetstone.Kinds;
using Whetstone.Redaction;

namespace Whetstone.Templates;

/// <summary>
/// Design decisions 3 and 4: a request of a kind with a champion gets that template, a challenger or nothing, by the arm its id
/// hashes to. A request with no kind, or of kind <c>other</c>, is not templated and is not an arm. A kind with no champion is
/// held out entirely: answered as <paramref name="inner"/> does, and recorded as the baseline. Any failure returns the inner answer.
/// </summary>
/// <remarks>
/// A template with a <c>{{memory}}</c> slot is filled from <paramref name="memory"/>, ranked by what <paramref name="ledger"/> learned,
/// within <see cref="MemoryTimeout"/>; a late, empty or failed recall drops the slot's line and serves the rest (goal 0.9).
/// </remarks>
public sealed class TemplateEnhancer(IEnhancer inner, ITemplates templates, IMemorySource? memory = null, IMemoryLedger? ledger = null) : IEnhancer
{
    /// <summary>How long a recall may take before the slot is left empty; inside the request's own deadline.</summary>
    public static readonly TimeSpan MemoryTimeout = TimeSpan.FromMilliseconds(900);

    /// <summary>How long a kind's allocation stands before the tallies are read again.</summary>
    private static readonly TimeSpan ShareLife = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, double[] Shares)> _shares = new(StringComparer.Ordinal);

    private long _failures;

    /// <summary>The enhancer this one adds to, so a health report can still reach it.</summary>
    public IEnhancer Inner => inner;

    /// <summary>Lookups that failed or ran out of time since the process started.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>The kind the client sent, or, for a typed Claude Code prompt that came with none, the one its opening words give; null when not templated.</summary>
    private static string? KindOf(EnhanceRequest request)
    {
        var kind = request.Context?.TaskKind is { Length: > 0 } sent ? sent
            : request.Context?.Client == PromptKinds.TypedClient ? PromptKinds.ForTyped(request.Prompt) : null;
        return kind is null or PromptKinds.Other ? null : kind;
    }

    /// <summary>The champion's share first, then each challenger's, from how each has done; read at most once a minute per kind.</summary>
    private async Task<double[]> SharesAsync(string kind, KindTemplates found, CancellationToken ct)
    {
        var now = TimeProvider.System.GetUtcNow();
        if (_shares.TryGetValue(kind, out var cached) && cached.Until > now && cached.Shares.Length == found.Challengers.Count + 1)
            return cached.Shares;
        var tallies = await templates.TalliesAsync(kind, ct);
        var shares = Allocation.Shares([.. new[] { found.Champion! }.Concat(found.Challengers).Select(t => tallies.GetValueOrDefault(Allocation.Key(t)))]);
        _shares[kind] = (now + ShareLife, shares);
        return shares;
    }

    private async Task<(string? Block, List<MemoryExposure> Shown)> RecallAsync(EnhanceRequest request, CancellationToken ct)
    {
        if (memory is null)
            return (null, []);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(MemoryTimeout);
        try
        {
            // Only redacted text leaves the process (docs/privacy.md).
            var recalled = await memory.RecallAsync(Redactor.Redact(request.Prompt).Text, request.Context?.Repository, MemoryRanking.SlotSize * 2, limit.Token);
            var records = ledger is null || recalled.Count == 0 ? new Dictionary<string, MemoryRecord>()
                : await ledger.MemoryRecordsAsync([.. recalled.Select(m => m.DocumentKey).Distinct(StringComparer.Ordinal)], limit.Token);
            var ranked = MemoryRanking.Rank(recalled, records);
            return (MemoryRanking.Block(ranked), [.. ranked.Select((m, i) => new MemoryExposure(m.DocumentKey, m.Id, MemoryRanking.Hash(m.Text), i + 1, m.Score))]);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, []);
        }
    }

    public async Task<EnhanceResponse> EnhanceAsync(EnhanceRequest request, CancellationToken ct)
    {
        var answer = await inner.EnhanceAsync(request, ct);
        if (KindOf(request) is not { } kind)
            return answer;
        try
        {
            // A level the client chose has its own champion when one is stored under "<kind>@<level>"; its runs are then that kind's.
            var found = await templates.ForKindAsync(kind, ct);
            if (request.Context?.Level is { Length: > 0 } level && await templates.ForKindAsync($"{kind}@{level}", ct) is { Champion: not null } leveled)
                (kind, found) = ($"{kind}@{level}", leveled);
            var (arm, index) = found.Champion is null ? (Arms.HeldOut, -1)
                : found.Challengers.Count >= 2 ? Arms.Assign(kind, answer.RequestId, await SharesAsync(kind, found, ct))
                : (Arms.Assign(kind, answer.RequestId, found.Challenger is not null), 0);
            var chosen = arm switch { Arms.Champion => found.Champion, Arms.Challenger => found.Challengers[index], _ => null };
            var held = answer with { HeldOut = arm == Arms.HeldOut, TaskKind = kind, Arm = arm };
            Dictionary<string, bool>? drawn = null;
            if (chosen is not null && found.Traits.Count > 0)
            {
                // Held-out requests get no trait. The rest draw each trait alone, so a trait's effect is independent of the arm.
                drawn = found.Traits.ToDictionary(t => t.Id, t => t.On(answer.RequestId), StringComparer.Ordinal);
                var added = found.Traits.Where(t => drawn[t.Id]).Select(t => t.Text).ToList();
                if (added.Count > 0)
                    chosen = chosen with { After = string.Join("\n\n", new[] { chosen.After }.Concat(added).Where(t => t.Length > 0)) };
            }
            // The filler stripper is the one source that changes the original, by deleting listed phrases; the answer names them.
            var (stripped, removed) = chosen?.Source == TemplateSources.FillerStrip ? FillerStripper.Strip(answer.Prompt) : (answer.Prompt, []);
            var (block, shown) = chosen?.UsesMemory == true ? await RecallAsync(request, ct) : (null, []);
            if (chosen is null || chosen.Render(stripped, request.Context, block) is not { } prompt)
                return held;
            if (shown.Count > 0 && ledger is not null)
            {
                try
                {
                    await ledger.RecordExposuresAsync(answer.RequestId, shown, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // The prompt is served either way; an exposure not recorded only means one run credits no document.
                }
            }
            return held with
            {
                Traits = drawn,
                Prompt = prompt,
                Changed = prompt != answer.Prompt,
                TemplateId = chosen.Id,
                TemplateVersion = chosen.Version,
                Reason = $"template: {chosen.Id} v{chosen.Version} ({arm})" + (removed.Count > 0 ? "; removed: " + string.Join(", ", removed) : "")
                    + (drawn?.Where(t => t.Value).Select(t => t.Key).ToList() is { Count: > 0 } on ? "; traits: " + string.Join(", ", on) : ""),
            };
        }
        catch (Exception)
        {
            // Broken or out of time (the server cancels at the deadline): the inner answer, not an arm, since none was served.
            Interlocked.Increment(ref _failures);
            return answer;
        }
    }
}
