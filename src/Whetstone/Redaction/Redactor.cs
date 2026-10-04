using System.Text.RegularExpressions;

namespace Whetstone.Redaction;

/// <summary>The redacted text and the kinds of marker it now holds, each once.</summary>
public sealed record Redacted(string Text, IReadOnlyList<string> Kinds)
{
    public bool Changed => Kinds.Count > 0;
}

/// <summary>
/// Replaces secret-shaped text with <c>[REDACTED:kind]</c> before anything is stored (docs/privacy.md, ADR 0003). Our own
/// patterns plus an entropy rule; names, emails and paths are not secrets and stay. Every pattern runs in linear time
/// (<see cref="RegexOptions.NonBacktracking"/>), so no input can hold the request, and the whole text is redacted before any
/// cut, so a secret is never left half there. A miss is a bug: add the shape to the corpus first.
/// </summary>
public static partial class Redactor
{
    public const string PrivateKey = "private-key";
    public const string Jwt = "jwt";
    public const string Header = "header";
    public const string Password = "password";
    public const string Token = "token";
    public const string HighEntropy = "high-entropy";

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>Entropy (bits per character) a long mixed string needs to be taken for a secret; pure hex has fewer symbols, so a lower bar.</summary>
    private const double MixedEntropy = 4.0;

    private const double HexEntropy = 3.0;

    private const int LongestKept = 31;

    private static readonly Regex[] VendorTokens =
    [
        // AWS access key ids.
        new(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA|ANPA|ANVA|ABIA|ACCA)[A-Z0-9]{16}\b", Options),
        // GitHub: classic, OAuth, user-to-server, server-to-server, refresh, fine-grained.
        new(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})\b", Options),
        new(@"\bglpat-[A-Za-z0-9_-]{20,}", Options),
        new(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", Options),
        new(@"https://hooks\.slack\.com/services/[A-Za-z0-9/]{20,}", Options),
        new(@"\b[sr]k_(?:live|test)_[A-Za-z0-9]{16,}\b", Options),
        new(@"\bsk-ant-[A-Za-z0-9_-]{20,}", Options),
        new(@"\bsk-(?:proj-)?[A-Za-z0-9_-]{32,}", Options),
        new(@"\bAIza[0-9A-Za-z_-]{35}\b", Options),
        new(@"\bnpm_[A-Za-z0-9]{36}\b", Options),
        new(@"\bSG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}\b", Options),
        new(@"\bhf_[A-Za-z0-9]{34,}\b", Options),
        new(@"\bdop_v1_[a-f0-9]{64}\b", Options),
    ];

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----|\z)", Options)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*", Options)]
    private static partial Regex JwtToken();

    // The header name stays; its value, to the end of the line, goes.
    [GeneratedRegex(@"\b((?:proxy-)?authorization|cookie|set-cookie|x-api-key|x-auth-token)(\s*[:=]\s*)[^\r\n]+", Options | RegexOptions.IgnoreCase)]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9._~+/=-]{16,}", Options | RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    // scheme://user:PASSWORD@host: the scheme, user and host stay.
    [GeneratedRegex(@"\b([a-z][a-z0-9+.-]*://[^\s:/@]+:)([^\s@/]+)(@)", Options | RegexOptions.IgnoreCase)]
    private static partial Regex UrlPassword();

    // name = value, where the name says it is a credential. Group 1 is the name, 2 the separator, 3 the value.
    [GeneratedRegex(
        @"\b([A-Za-z0-9_.-]*(?:api[_-]?key|apikey|secret|token|passw(?:or)?d|passphrase|pwd|private[_-]?key|access[_-]?key|account[_-]?key|credentials?)[A-Za-z0-9_.-]*)(""?\s*[:=]\s*)(""[^""\r\n]{3,}""|'[^'\r\n]{3,}'|[^\s""',;]{3,})",
        Options | RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"[A-Za-z0-9+/_=-]{32,}", Options)]
    private static partial Regex LongString();

    [GeneratedRegex(@"\A[0-9a-fA-F]+\z", Options)]
    private static partial Regex Hex();

    [GeneratedRegex(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", Options)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"\[REDACTED:[a-z-]+\]", Options)]
    private static partial Regex Marker();

    public static string MarkerFor(string kind) => $"[REDACTED:{kind}]";

    public static Redacted Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var kinds = new List<string>();
        string Mark(string kind)
        {
            if (!kinds.Contains(kind))
                kinds.Add(kind);
            return MarkerFor(kind);
        }

        // Structured shapes first, so a later rule never sees half of one.
        text = PrivateKeyBlock().Replace(text, _ => Mark(PrivateKey));
        text = JwtToken().Replace(text, _ => Mark(Jwt));
        text = HeaderLine().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mark(Header));
        text = BearerToken().Replace(text, _ => Mark(Header));
        text = UrlPassword().Replace(text, m => m.Groups[1].Value + Mark(Password) + m.Groups[3].Value);
        foreach (var vendor in VendorTokens)
            text = vendor.Replace(text, _ => Mark(Token));
        text = Assignment().Replace(text, m => AssignmentValue(m, Mark));
        text = LongString().Replace(text, m => LooksLikeSecret(m.Value) ? Mark(HighEntropy) : m.Value);
        return new Redacted(text, kinds);
    }

    private static string AssignmentValue(Match m, Func<string, string> mark)
    {
        var name = m.Groups[1].Value;
        var value = m.Groups[3].Value;
        var bare = value.Trim('"', '\'');
        if (IsPlaceholder(bare) || Marker().IsMatch(bare))
            return m.Value;
        var isPassword = name.Contains("pass", StringComparison.OrdinalIgnoreCase) || name.Contains("pwd", StringComparison.OrdinalIgnoreCase);
        // "token: expired" is prose. A password is anything; another credential must look like one.
        if (!isPassword && !LooksLikeValue(bare))
            return m.Value;
        return name + m.Groups[2].Value + mark(isPassword ? Password : Token);
    }

    /// <summary>A reference to a secret, not the secret: <c>$TOKEN</c>, <c>${TOKEN}</c>, <c>{{token}}</c>, <c>&lt;your-key&gt;</c>, <c>process.env.X</c>, an unset value.</summary>
    private static bool IsPlaceholder(string value) =>
        value.StartsWith('$') || value.StartsWith("{{", StringComparison.Ordinal) || value.StartsWith('<') || value.StartsWith('%')
        || value.Contains("process.env", StringComparison.Ordinal) || value.Contains("os.environ", StringComparison.Ordinal)
        || value.Contains("getenv", StringComparison.Ordinal)
        || value.Equals("null", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)
        || value.Equals("undefined", StringComparison.OrdinalIgnoreCase) || value.Equals("string", StringComparison.OrdinalIgnoreCase);

    /// <summary>Not a plain lowercase word: has a digit, a capital or a symbol, or is long.</summary>
    private static bool LooksLikeValue(string value) => value.Length >= 16 || value.Any(c => !char.IsAsciiLetterLower(c));

    private static bool LooksLikeSecret(string candidate)
    {
        if (Uuid().IsMatch(candidate))
            return false;
        if (Hex().IsMatch(candidate))
            // A git sha (SHA-1 or SHA-256 object name) is not a secret; any other long hex run is taken for one.
            return candidate.Length is not (40 or 64) && Entropy(candidate) >= HexEntropy;
        var classes = (candidate.Any(char.IsAsciiLetterLower) ? 1 : 0) + (candidate.Any(char.IsAsciiLetterUpper) ? 1 : 0)
            + (candidate.Any(char.IsAsciiDigit) ? 1 : 0) + (candidate.Any(c => !char.IsAsciiLetterOrDigit(c)) ? 1 : 0);
        if (classes < 3 || candidate.Length <= LongestKept)
            return false;
        // A path (src/Core/Contracts/Schemas) has no digit in any segment; a base64 secret has one almost always.
        if (candidate.Contains('/') && !candidate.Any(char.IsAsciiDigit))
            return false;
        return Entropy(candidate) >= MixedEntropy;
    }

    /// <summary>Shannon entropy of the characters, in bits per character.</summary>
    internal static double Entropy(string value)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in value)
            counts[c] = counts.GetValueOrDefault(c) + 1;
        var bits = 0.0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / value.Length;
            bits -= p * Math.Log2(p);
        }
        return bits;
    }
}
