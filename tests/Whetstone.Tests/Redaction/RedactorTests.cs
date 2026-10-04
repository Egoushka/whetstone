using System.Diagnostics;
using Whetstone.Redaction;

namespace Whetstone.Tests.Redaction;

/// <summary>Goal 0.2, bar part 1: every seeded secret shape is redacted, every look-alike is kept.</summary>
public class RedactorTests
{
    public static TheoryData<string> SecretCases() => new(Corpus.Secrets.Select(s => s.Name));

    public static TheoryData<string> KeepCases() => new(Corpus.Keep.Select(k => k.Name));

    private static Corpus.Seeded Seeded(string name) => Corpus.Secrets.Single(s => s.Name == name);

    [Theory]
    [MemberData(nameof(SecretCases))]
    public void A_secret_is_replaced_by_a_marker_of_its_kind(string name)
    {
        var seeded = Seeded(name);

        var result = Redactor.Redact(seeded.Text);

        Assert.Contains(Redactor.MarkerFor(seeded.Kind), result.Text, StringComparison.Ordinal);
        Assert.Contains(seeded.Kind, result.Kinds);
        Assert.DoesNotContain(seeded.Value, result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SecretCases))]
    public void A_secret_does_not_survive_in_any_surrounding(string name)
    {
        var seeded = Seeded(name);
        foreach (var context in Corpus.Contexts)
        {
            var text = context.Replace("{0}", seeded.Text, StringComparison.Ordinal).Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);

            var redacted = Redactor.Redact(text).Text;

            Assert.False(redacted.Contains(seeded.Value, StringComparison.Ordinal), $"{name} in \"{context}\": the value survived");
        }
    }

    [Theory]
    [MemberData(nameof(KeepCases))]
    public void A_look_alike_is_kept_exactly(string name)
    {
        var text = Corpus.Keep.Single(k => k.Name == name).Text;

        var result = Redactor.Redact(text);

        Assert.Equal(text, result.Text);
        Assert.False(result.Changed);
    }

    [Fact]
    public void The_text_around_a_secret_is_kept()
    {
        var secret = Seeded("github classic token").Text;

        var result = Redactor.Redact($"Deploy failed with {secret} in the header; see ticket 4521.");

        Assert.Equal("Deploy failed with [REDACTED:token] in the header; see ticket 4521.", result.Text);
    }

    [Fact]
    public void An_assignment_keeps_its_name_and_separator()
    {
        var result = Redactor.Redact("DB_PASSWORD=hunter2 and other_setting=on");

        Assert.Equal("DB_PASSWORD=[REDACTED:password] and other_setting=on", result.Text);
    }

    [Fact]
    public void A_url_password_goes_but_the_user_and_host_stay()
    {
        var result = Redactor.Redact("use postgres://app:s3cretpw@db.example.test/app");

        Assert.Equal("use postgres://app:[REDACTED:password]@db.example.test/app", result.Text);
    }

    [Fact]
    public void A_private_key_cut_off_before_its_end_is_redacted_to_the_end_of_the_text()
    {
        var key = Seeded("private key block").Text;
        var cut = key[..key.IndexOf("-----END", StringComparison.Ordinal)];

        var result = Redactor.Redact($"my key:\n{cut}");

        Assert.Equal("my key:\n[REDACTED:private-key]", result.Text);
    }

    [Fact]
    public void Redacting_twice_changes_nothing()
    {
        var all = string.Join("\n", Corpus.Secrets.Select(s => s.Text).Concat(Corpus.Keep.Select(k => k.Text)));

        var once = Redactor.Redact(all).Text;

        Assert.Equal(once, Redactor.Redact(once).Text);
    }

    [Fact]
    public void Kinds_are_listed_once_each()
    {
        var jwt = Seeded("jwt").Text;
        var token = Seeded("npm token").Text;

        var result = Redactor.Redact($"{token} {jwt} {token}");

        Assert.Equal(["jwt", "token"], result.Kinds.Order());
    }

    [Fact]
    public void Unlabelled_hex_of_a_sha_length_is_kept_and_other_hex_is_not()
    {
        var sha1 = new string('a', 20) + new string('7', 20);

        Assert.Equal(sha1, Redactor.Redact(sha1).Text);
        Assert.Contains("[REDACTED:high-entropy]", Redactor.Redact("9f86d081884c7d659a2feaa0c55ad015").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hostile_input_is_linear_not_a_stall()
    {
        var clock = Stopwatch.StartNew();

        _ = Redactor.Redact(new string('a', 2_000_000));
        _ = Redactor.Redact(string.Concat(Enumerable.Repeat("-----BEGIN PRIVATE KEY----- ", 50_000)));
        _ = Redactor.Redact(string.Concat(Enumerable.Repeat("token=", 200_000)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    [Fact]
    public void The_corpus_covers_every_kind_the_redactor_can_emit()
    {
        var emitted = Corpus.Secrets.Select(s => s.Kind).Distinct().Order();

        Assert.Equal(new[] { Redactor.Header, Redactor.HighEntropy, Redactor.Jwt, Redactor.Password, Redactor.PrivateKey, Redactor.Token }.Order(), emitted);
    }
}
