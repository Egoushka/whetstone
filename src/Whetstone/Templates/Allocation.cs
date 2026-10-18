using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Whetstone.Templates;

/// <summary>Reported runs of one template version that went well (completed, not reworked) and that did not.</summary>
public readonly record struct Tally(int Wins, int Losses);

/// <summary>
/// How a kind with two or more challengers shares its non-held-out traffic (docs/specs/2026-10-19-evidence-volume-design.md,
/// decision 1): by Thompson sampling on completion, with a floor per arm. The held-out share is fixed by <see cref="Arms.Bucket"/>
/// before this runs, and promotion still happens only at the fixed looks of <see cref="Trials"/>: this changes who gets traffic.
/// </summary>
public static class Allocation
{
    /// <summary>No arm gets less than this share of the non-held-out traffic, so a late bloomer keeps being tried.</summary>
    public const double Floor = 0.15;

    private const int Draws = 2000;

    public static string Key(Template template) => Key(template.Id, template.Version);

    public static string Key(string? id, string? version) => id + "\0" + version;

    /// <summary>
    /// The share of non-held-out traffic for each arm, in the order given, summing to 1: each arm's chance of having the highest
    /// completion rate under a Beta(1 + wins, 1 + losses) belief, scaled into what is left above the floors. The draws use a seed from
    /// the tallies, so the same tallies give the same shares.
    /// </summary>
    public static double[] Shares(IReadOnlyList<Tally> arms)
    {
        var n = arms.Count;
        if (n == 0)
            return [];
        if (n * Floor >= 1)
            return [.. Enumerable.Repeat(1.0 / n, n)];
        var seed = arms.Aggregate(17, (h, t) => HashCode.Combine(h, t.Wins, t.Losses));
        var random = new Random(seed);
        var best = new int[n];
        for (var d = 0; d < Draws; d++)
        {
            var top = 0;
            var topValue = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                var value = Beta(random, 1 + arms[i].Wins, 1 + arms[i].Losses);
                if (value > topValue)
                    (top, topValue) = (i, value);
            }
            best[top]++;
        }
        return [.. best.Select(b => Floor + ((1 - (n * Floor)) * b / Draws))];
    }

    /// <summary>The arm for a request among the non-held-out ones: an index into the shares, from a hash other than the held-out one.</summary>
    public static int Pick(string kind, string requestId, IReadOnlyList<double> shares)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + requestId + "\0draw"));
        var u = BinaryPrimitives.ReadUInt32BigEndian(hash) / 4294967296.0;
        var edge = 0.0;
        for (var i = 0; i < shares.Count; i++)
        {
            edge += shares[i];
            if (u < edge)
                return i;
        }
        return shares.Count - 1;
    }

    private static double Beta(Random random, double a, double b)
    {
        var x = Gamma(random, a);
        var y = Gamma(random, b);
        return x / (x + y);
    }

    /// <summary>Marsaglia and Tsang's method for shape 1 or more, which is all a Beta(1 + wins, 1 + losses) needs.</summary>
    private static double Gamma(Random random, double shape)
    {
        var d = shape - (1.0 / 3);
        var c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = Normal(random);
                v = 1 + (c * x);
            }
            while (v <= 0);
            v = v * v * v;
            var u = 1 - random.NextDouble();
            if (Math.Log(u) < (0.5 * x * x) + d - (d * v) + (d * Math.Log(v)))
                return d * v;
        }
    }

    private static double Normal(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
