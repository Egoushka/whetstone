using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>The owner's act on templates: add a challenger, promote it over the champion, retire a version (design, "Validity first").</summary>
public sealed class TemplateCommandTests : IDisposable
{
    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public TemplateCommandTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.TemplateAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private async Task Champion()
    {
        await _stores.TemplatesFor("owner").SeedAsync([new Template("base", "1", "typed/implement", "", "Base text.", "hand")], TemplateRoles.Champion, CancellationToken.None);
    }

    [Fact]
    public async Task Add_stores_a_challenger_and_promote_swaps_it_in_keeping_the_old_record()
    {
        await Champion();

        var add = await Run("add", "--kind", "typed/implement", "--id", "next", "--version", "1", "--after", "New text for {{repository}}.");
        var before = await _stores.TemplatesFor("owner").ForKindAsync("typed/implement", CancellationToken.None);
        var promote = await Run("promote", "--kind", "typed/implement", "--id", "next", "--version", "1");
        var after = await _stores.TemplatesFor("owner").ForKindAsync("typed/implement", CancellationToken.None);
        var list = await Run("list");

        Assert.Equal((Commands.Ok, ""), (add.Code, add.Err));
        Assert.Equal(("base", "next"), (before.Champion!.Id, before.Challenger!.Id));
        Assert.Equal((Commands.Ok, ""), (promote.Code, promote.Err));
        Assert.Equal(("next", null), (after.Champion!.Id, after.Challenger?.Id));
        Assert.Contains("base v1  retired", list.Out, StringComparison.Ordinal);
        Assert.Contains("next v1  champion", list.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_champion_a_reused_version_and_a_promoted_champion_are_refused()
    {
        await Champion();
        await Run("add", "--kind", "typed/implement", "--id", "next", "--version", "1", "--after", "A.");

        var second = await Run("add", "--kind", "typed/implement", "--id", "other", "--version", "1", "--after", "B.", "--champion");
        var reuse = await Run("add", "--kind", "typed/implement", "--id", "next", "--version", "1", "--after", "Changed text.");
        var champion = await Run("promote", "--kind", "typed/implement", "--id", "base", "--version", "1");

        Assert.Equal(Commands.Failed, second.Code);
        Assert.Contains("already has a champion", second.Err, StringComparison.Ordinal);
        Assert.Equal(Commands.Failed, reuse.Code);
        Assert.Contains("immutable", reuse.Err, StringComparison.Ordinal);
        Assert.Equal(Commands.Failed, champion.Code);
        Assert.Contains("only a challenger is promoted", champion.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retire_takes_a_version_out_of_service_and_a_kind_without_champion_is_held_out()
    {
        await Champion();

        var retire = await Run("retire", "--kind", "typed/implement", "--id", "base", "--version", "1");
        var again = await Run("retire", "--kind", "typed/implement", "--id", "base", "--version", "1");
        var found = await _stores.TemplatesFor("owner").ForKindAsync("typed/implement", CancellationToken.None);

        Assert.Equal(Commands.Ok, retire.Code);
        Assert.Equal(Commands.Failed, again.Code);
        Assert.Null(found.Champion);
    }

    [Theory]
    [InlineData("add", "--kind", "typed/implement", "--id", "x", "--version", "1")]
    [InlineData("add", "--kind", "other", "--id", "x", "--version", "1", "--after", "t")]
    [InlineData("add", "--kind", "k", "--id", "bad id", "--version", "1", "--after", "t")]
    [InlineData("add", "--kind", "k", "--id", "x", "--version", "1", "--after", "see {{secret}}")]
    [InlineData("promote", "--kind", "k", "--id", "x")]
    [InlineData("frobnicate")]
    [InlineData]
    public async Task Bad_input_is_a_usage_error_and_changes_nothing(params string[] options)
    {
        await Champion();

        var run = await Run(options);
        var rows = await _stores.AdminFor("owner").TemplatesAsync(CancellationToken.None);

        Assert.Equal(Commands.Usage, run.Code);
        Assert.Single(rows);
    }

    [Fact]
    public async Task Every_change_writes_a_decision_with_the_counts_and_the_note()
    {
        await Champion();

        await Run("add", "--kind", "typed/implement", "--id", "next", "--version", "1", "--after", "New text.", "--note", "try a shorter line");
        await Run("promote", "--kind", "typed/implement", "--id", "next", "--version", "1");
        await Run("retire", "--kind", "typed/implement", "--id", "next", "--version", "1", "--note", "tested badly");
        await Run("retire", "--kind", "typed/implement", "--id", "missing", "--version", "1");

        var rows = Db.Query(_data.FileFor("owner"), "SELECT action, template_id, version, counts, note FROM template_decisions ORDER BY decision_id");
        Assert.Equal(["add", "promote", "retire"], rows.Select(r => (string)r["action"]!));
        Assert.Equal(["try a shorter line", null, "tested badly"], rows.Select(r => (string?)r["note"]));
        Assert.All(rows, r => Assert.Contains("\"Versions\"", (string)r["counts"]!, StringComparison.Ordinal));
    }
}
