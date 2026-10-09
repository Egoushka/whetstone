using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Whetstone.Templates;

/// <summary>What one request got: the champion, a challenger, or nothing (held out).</summary>
public static class Arms
{
    public const string Champion = "champion";

    public const string Challenger = "challenger";

    public const string HeldOut = "held_out";

    /// <summary>Of every 100 requests of a kind, this many are held out.</summary>
    public const int HeldOutPercent = 10;

    /// <summary>Of every 100 requests, this many get the challenger while one is on trial.</summary>
    public const int ChallengerPercent = 30;

    /// <summary>
    /// The arm for a request, from a hash of its kind and its id: a replay gets the same arm, and the kind in the hash gives each
    /// kind its own split. With a challenger on trial the split is 60, 30 and 10; without one, 90 and 10 (design decision 4).
    /// </summary>
    public static string Assign(string kind, string requestId, bool challengerOnTrial)
    {
        var bucket = Bucket(kind, requestId);
        if (bucket >= 100 - HeldOutPercent)
            return HeldOut;
        return challengerOnTrial && bucket >= 100 - HeldOutPercent - ChallengerPercent ? Challenger : Champion;
    }

    /// <summary>0 to 99, stable across runs and machines.</summary>
    public static int Bucket(string kind, string requestId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + requestId));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % 100);
    }
}
