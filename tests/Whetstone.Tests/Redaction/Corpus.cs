namespace Whetstone.Tests.Redaction;

/// <summary>
/// Invented, secret-shaped values for the redactor's corpus. Each is assembled from fragments at run time, so no literal in
/// this repository looks like a credential to gitleaks or to a reader; none is a real key.
/// </summary>
internal static class Corpus
{
    private static string Join(params string[] parts) => string.Concat(parts);

    private static string Run(string pattern, int length) => string.Concat(Enumerable.Repeat(pattern, (length / pattern.Length) + 1))[..length];

    /// <param name="Text">The shape as it would appear in a prompt.</param>
    /// <param name="Value">The credential inside it, which must not survive; the rest (a header name, a host) may.</param>
    internal sealed record Seeded(string Name, string Kind, string Text, string Value);

    private static Seeded S(string name, string kind, string text, string? value = null) => new(name, kind, text, value ?? text);

    /// <summary>Every secret shape that must go, with the kind of marker it must become.</summary>
    public static readonly IReadOnlyList<Seeded> Secrets =
    [
        S("aws access key id", "token", Join("AK", "IA", "QWERTYUIOPASDFGH")),
        S("github classic token", "token", Join("gh", "p_", Run("aB3dE5", 36))),
        S("github fine-grained token", "token", Join("github", "_pat_", Run("aB3dE5_", 40))),
        S("gitlab token", "token", Join("gl", "pat-", Run("aB3dE5-", 22))),
        S("slack bot token", "token", Join("xo", "xb-", "1234567890-", Run("aB3dE5", 24))),
        S("slack webhook", "token", Join("https://hooks.", "slack.com/services/", "T0123ABCD/B0123ABCD/", Run("aB3dE5", 24))),
        S("stripe live key", "token", Join("s", "k_live_", Run("aB3dE5", 24))),
        S("anthropic key", "token", Join("sk", "-ant-", Run("aB3dE5-", 40))),
        S("openai key", "token", Join("sk", "-proj-", Run("aB3dE5", 48))),
        S("google api key", "token", Join("AI", "za", Run("aB3dE5", 35))),
        S("npm token", "token", Join("npm", "_", Run("aB3dE5", 36))),
        S("sendgrid key", "token", Join("S", "G.", Run("aB3dE5", 22), ".", Run("aB3dE5", 43))),
        S("hugging face token", "token", Join("h", "f_", Run("aB3dE5", 36))),
        S("jwt", "jwt", Join("ey", "JhbGciOiJIUzI1NiJ9.", "ey", "JzdWIiOiIxMjM0NTY3ODkwIn0.", Run("aB3dE5", 20))),
        S("private key block", "private-key", Join("-----BEGIN ", "RSA PRIVATE KEY-----\n", Run("MIIEowIBAAKCAQEA", 130), "\n", Run("zQ8vK2", 64), "\n-----END ", "RSA PRIVATE KEY-----"), Run("zQ8vK2", 64)),
        S("openssh private key", "private-key", Join("-----BEGIN ", "OPENSSH PRIVATE KEY-----\n", Run("b3BlbnNzaC1rZXk", 120), "\n-----END ", "OPENSSH PRIVATE KEY-----")),
        S("authorization header", "header", Join("Authorization: ", "Bearer ", Run("aB3dE5", 30)), Run("aB3dE5", 30)),
        S("basic authorization", "header", Join("authorization: ", "Basic ", "dXNlcjpodW50ZXIy"), "dXNlcjpodW50ZXIy"),
        S("cookie header", "header", Join("Cookie: ", "session=", Run("aB3dE5", 20), "; theme=dark"), Run("aB3dE5", 20)),
        S("bearer token in prose", "header", Join("curl with bearer ", Run("aB3dE5", 24), " fails"), Run("aB3dE5", 24)),
        S("url password", "password", Join("postgres://", "app:", "hunter2hunter2", "@db.example.test:5432/app"), "hunter2hunter2"),
        S("connection string password", "password", Join("Server=db.example.test;Database=app;User Id=app;", "Pass", "word=", "Tr0ub4dor&3;"), "Tr0ub4dor&3"),
        S("env assignment", "token", Join("export ", "API", "_KEY=", Run("aB3dE5", 28)), Run("aB3dE5", 28)),
        S("json credential", "token", Join("{\"client", "_secret\": \"", Run("aB3dE5", 28), "\"}"), Run("aB3dE5", 28)),
        S("yaml password", "password", Join("pass", "word: ", "correcthorsebattery"), "correcthorsebattery"),
        S("short password", "password", Join("DB_PASS", "WORD=", "hunter2"), "hunter2"),
        S("quoted token", "token", Join("auth_", "token = '", Run("aB3dE5", 20), "'"), Run("aB3dE5", 20)),
        S("high-entropy base64", "high-entropy", Join("zX9", "kQ2mV7", "pL4wR8tY1", "uN6bH3cJ5", "dG0fS9aE2")),
        S("aws secret key, unlabelled", "high-entropy", Join("wJalrXUtnFEMI/", "K7MDENG/", "bPxRfiCYEXAMPLEKEY")),
        S("32-hex key", "high-entropy", Join("9f86d081884c7d65", "9a2feaa0c55ad015")),
    ];

    /// <summary>Text that merely looks close to a secret and must come back exactly as it went in.</summary>
    public static readonly IReadOnlyList<(string Name, string Text)> Keep =
    [
        ("git sha-1", "see commit 3f2a9c1d5b7e4a8c0d6f1e2b3a4c5d6e7f8a9b0c for the change"),
        ("git sha-256", Join("object ", Run("3f2a9c1d", 64))),
        ("short sha", "fixed in abc1234"),
        ("uuid", "request 123e4567-e89b-12d3-a456-426614174000 failed"),
        ("file path", "edit src/Whetstone/Contracts/ContractSchemas.cs and the tests next to it"),
        ("long path", "open /srv/app/checkout/whetstone/src/Whetstone.Server/WhetstoneServer"),
        ("long identifier", "rename GuardedPromptEnhancerFeedbackAsyncDelegateFactory everywhere"),
        ("long kebab name", "branch feature-add-the-long-running-export-command-for-users"),
        ("token in prose", "the token: expired yesterday, ask for a new one"),
        ("password in prose", "no password is stored; passwords are never logged"),
        ("env reference", "set API_KEY=$API_KEY and TOKEN=${TOKEN} before running"),
        ("template placeholder", "password = {{password}} and api_key: <your-api-key>"),
        ("code reading env", "var token = process.env.GITHUB_TOKEN; secret = os.environ[\"SECRET\"]"),
        ("boolean flag", "use_token: true and secret=false"),
        ("url without credentials", "clone https://github.com/example/app.git then run it"),
        ("url with user only", "ssh://git@example.test/app.git"),
        ("plain sentence", "Fix the failing test in the parser module and keep the public API unchanged."),
        ("code", "if (items.Count == 0) { return Array.Empty<string>(); }"),
        ("version and date", "released 0.1.0 on 2026-10-05 as build 20261005.1"),
        ("empty", ""),
    ];

    /// <summary>Wrappers a secret turns up inside: the redaction must not depend on what surrounds it.</summary>
    public static readonly IReadOnlyList<string> Contexts =
    [
        "{0}",
        "Fix this error: {0}",
        "{0}\nthen retry",
        "```\n{0}\n```",
        "line one\n\n  {0}  \n\nline two",
        "{{\"log\":\"{0}\"}}",
        "error at 12:03:44 -> {0} <- end",
    ];
}
