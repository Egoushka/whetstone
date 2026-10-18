using System.Text.Json;
using Whetstone.Contracts;
using Whetstone.Server;
using Whetstone.Storage;
using Whetstone.Templates;
using Whetstone.Tests.Storage;

namespace Whetstone.Tests.Templates;

/// <summary>Goal 0.7, decision 5: retired templates are kept and exported, and a rewording of one is not stored again.</summary>
public sealed class NegativeLibraryTests : IDisposable
{
    private const string Lost = "Before changing anything, name the file you will edit and list the tests that cover it. Keep the diff small and stop when those tests pass.";

    private readonly TempData _data = new();
    private readonly SqliteStores _stores;

    public NegativeLibraryTests() => _stores = new SqliteStores(_data.Path);

    public void Dispose()
    {
        _stores.Dispose();
        _data.Dispose();
    }

    private static Template T(string id, string after, string kind = "typed/implement") => new(id, "1", kind, "", after, "hand");

    [Fact]
    public void A_rewording_is_close_and_an_unrelated_text_is_not()
    {
        var retired = T("lost", Lost);

        var reworded = Similarity.Closest(T("p", "Before changing anything, name the file you will edit and list the tests that cover it. Keep the diff small, and stop once the tests pass."), [retired]);
        var other = Similarity.Closest(T("q", "Summarise the failure in two sentences, then propose one experiment with a rollback plan and a deadline."), [retired]);
        var same = Similarity.Closest(T("r", Lost), [retired]);

        Assert.True(reworded!.Value.Closeness >= Similarity.TooClose);
        Assert.True(other!.Value.Closeness < 0.3);
        Assert.Equal(1.0, same!.Value.Closeness, 6);
    }

    [Fact]
    public void A_proposal_that_only_contains_a_retired_text_is_not_a_rewording_of_it()
    {
        var longer = Lost + " " + string.Join(' ', Enumerable.Range(0, 40).Select(i => $"extra{i} clause{i} paragraph{i}"));

        var near = Similarity.Closest(T("p", longer), [T("lost", Lost)]);

        Assert.True(near!.Value.Closeness < Similarity.TooClose);
    }

    [Fact]
    public void Only_retired_texts_of_the_same_kind_count_and_a_kind_with_none_has_no_neighbour()
    {
        Assert.Null(Similarity.Closest(T("p", Lost), []));
        Assert.Null(Similarity.Closest(T("p", Lost), [T("lost", Lost, "agent/explore")]));
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] options)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Commands.TemplateAsync(options, _stores.AdminFor("owner"), output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private async Task Lose()
    {
        await _stores.TemplatesFor("owner").SeedAsync([T("base", "Base text for the kind.")], TemplateRoles.Champion, CancellationToken.None);
        await Run("add", "--kind", "typed/implement", "--id", "lost", "--version", "1", "--after", Lost);
        await Run("retire", "--kind", "typed/implement", "--id", "lost", "--version", "1", "--note", "no better after 60 runs");
    }

    [Fact]
    public async Task A_proposal_too_close_to_a_retired_text_is_refused_unless_the_owner_allows_it()
    {
        await Lose();
        var reworded = "Before changing anything, name the file you will edit and list the tests that cover it. Keep the diff small, and stop once the tests pass.";

        var refused = await Run("add", "--kind", "typed/implement", "--id", "again", "--version", "1", "--after", reworded);
        var allowed = await Run("add", "--kind", "typed/implement", "--id", "again", "--version", "1", "--after", reworded, "--allow-similar");

        Assert.Equal(Commands.Failed, refused.Code);
        Assert.Contains("too close to lost v1", refused.Err, StringComparison.Ordinal);
        Assert.Equal((Commands.Ok, ""), (allowed.Code, allowed.Err));
    }

    [Fact]
    public async Task Export_lists_every_version_with_its_role_and_what_was_decided_and_each_line_fits_the_schema()
    {
        await Lose();

        var export = await Run("export");

        Assert.Equal((Commands.Ok, ""), (export.Code, export.Err));
        var lines = export.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.Empty(ContractSchemas.ValidateLibrary(JsonDocument.Parse(l).RootElement)));
        var records = lines.Select(l => JsonSerializer.Deserialize<LibraryRecord>(l, ContractJson.Options)!).ToList();
        Assert.Equal(["base", "lost"], records.Select(r => r.TemplateId).Order());
        var lost = records.Single(r => r.TemplateId == "lost");
        Assert.Equal(("retired", Lost), (lost.Role, lost.After));
        Assert.Equal(["add", "retire"], lost.Decisions.Select(d => d.Action));
        Assert.Equal("no better after 60 runs", lost.Decisions[1].Note);
    }

    [Fact]
    public async Task Export_of_an_empty_store_prints_nothing_and_takes_no_options()
    {
        Assert.Equal((Commands.Ok, ""), ((await Run("export")).Code, (await Run("export")).Out));
        Assert.Equal(Commands.Usage, (await Run("export", "--all")).Code);
    }
}
