using Whetstone.Contracts;
using Whetstone.Kinds;

namespace Whetstone.Tests.Kinds;

/// <summary>Goal 0.6, decision 2: the reason a follow-up corrects the run, by rules over its opening, in English and Ukrainian.</summary>
public sealed class CorrectionTests
{
    [Theory]
    [InlineData("stop, that's the wrong file", "stop")]
    [InlineData("зачекай, не туди", "stop")]
    [InlineData("no, I meant the export command", "misread")]
    [InlineData("я мав на увазі інший сервіс", "misread")]
    [InlineData("wrong file, the parser lives in src/parse", "wrong_target")]
    [InlineData("не той файл", "wrong_target")]
    [InlineData("I didn't ask you to rename everything", "too_much")]
    [InlineData("no, only change the test", "too_much")]
    [InlineData("ні, лише тест", "too_much")]
    [InlineData("no, shorter please", "style")]
    [InlineData("no, use a dictionary instead", "wrong_approach")]
    [InlineData("не так, зроби через чергу", "wrong_approach")]
    [InlineData("you forgot the migration", "incomplete")]
    [InlineData("it still fails on tabs", "incomplete")]
    [InlineData("ти забув про міграцію", "incomplete")]
    [InlineData("fyi the config moved to settings.json", "new_info")]
    [InlineData("до речі, конфіг тепер у settings.json", "new_info")]
    [InlineData("no", "other")]
    [InlineData("that's wrong", "other")]
    public void A_correction_gets_its_reason(string prompt, string reason) => Assert.Equal(reason, Corrections.Reason(prompt));

    [Theory]
    [InlineData("thanks, now add a changelog entry")]
    [InlineData("just run the tests")]
    [InlineData("only for linux builds, add the matrix")]
    [InlineData("use a queue instead of polling for the importer")]
    [InlineData("add the missing index to the orders table")]
    [InlineData("continue")]
    [InlineData("лише перевір лінтер")]
    public void A_prompt_that_moves_on_is_not_a_correction(string prompt) => Assert.Null(Corrections.Reason(prompt));

    [Fact]
    public void Every_reason_a_rule_gives_is_one_the_contract_knows() =>
        Assert.All(Corrections.Known, r => Assert.Contains(r, CorrectionReasons.All));

    [Theory]
    [InlineData("incomplete", true)]
    [InlineData("style", true)]
    [InlineData("new_info", false)]
    [InlineData("stop", false)]
    [InlineData("other", false)]
    [InlineData(null, false)]
    public void Rework_is_a_reason_that_blames_the_prompt_or_the_run(string? reason, bool rework) => Assert.Equal(rework, CorrectionReasons.IsRework(reason));
}
