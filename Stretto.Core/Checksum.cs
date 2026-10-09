using System.Security.Cryptography;

namespace Stretto.Core;

/// <summary>
/// Hashing a finished file and comparing it with the checksum the site published.
///
/// The algorithm is read from the length of what was pasted, because that is how people
/// meet checksums: a hex string beside a download link with no label worth trusting.
/// MD5 and SHA-1 are offered only because sites still publish them; they detect a damaged
/// download, not a tampered one, and the result text says SHA-256 or better when it matters.
/// </summary>
public static class Checksum
{
    public enum Kind { Md5, Sha1, Sha256, Sha512 }

    public static string Label(Kind kind) => kind switch
    {
        Kind.Md5 => "MD5",
        Kind.Sha1 => "SHA-1",
        Kind.Sha256 => "SHA-256",
        _ => "SHA-512",
    };

    /// <summary>
    /// Strips what a copied checksum tends to arrive with: spaces, a trailing file name as
    /// in <c>sha256sum</c> output, and upper case. Returns null when nothing hex is left.
    /// </summary>
    public static string? Normalize(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return null;

        // "<hash>  file.zip" and "<hash> *file.zip": the hash is the first token.
        var first = pasted.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        var hex = first.Replace(":", "").ToLowerInvariant();
        return hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>The algorithm a hex string of this length belongs to, or null if none fits.</summary>
    public static Kind? Detect(string? pasted) => Normalize(pasted)?.Length switch
    {
        32 => Kind.Md5,
        40 => Kind.Sha1,
        64 => Kind.Sha256,
        128 => Kind.Sha512,
        _ => null,
    };

    /// <summary>Lower-case hex digest of the file, reporting the fraction read as it goes.</summary>
    public static async Task<string> ComputeAsync(
        string path, Kind kind, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using HashAlgorithm hash = kind switch
        {
            Kind.Md5 => MD5.Create(),
            Kind.Sha1 => SHA1.Create(),
            Kind.Sha256 => SHA256.Create(),
            _ => SHA512.Create(),
        };

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            hash.TransformBlock(buffer, 0, read, null, 0);
            done += read;
            if (stream.Length > 0)
                progress?.Report((double)done / stream.Length);
        }

        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }

    /// <summary>True when the pasted checksum equals the computed one, ignoring case and spacing.</summary>
    public static bool Matches(string? pasted, string computed) =>
        Normalize(pasted) is { } wanted && wanted == computed.ToLowerInvariant();
}
