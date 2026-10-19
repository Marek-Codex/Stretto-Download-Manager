using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Stretto.Core;

/// <summary>
/// A short mark taken from where a file came from, to tell two downloads of the same name
/// apart. Two videos can share a title, a site and a quality, but not an address.
/// </summary>
public static class SourceTag
{
    private const int Length = 6;

    private static readonly Regex GenericName = new(
        @"^(master|index|playlist|manifest|chunklist|video|audio|media|stream|download|file|watch|get)\d*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The start of the address's own file name when it says something, otherwise a few
    /// characters of a hash of host and path. The query is left out on purpose: sites that
    /// sign their media rotate the token, which would make one video look like many.
    /// </summary>
    public static string For(Uri url)
    {
        var last = Uri.UnescapeDataString(url.AbsolutePath.TrimEnd('/')).Split('/').LastOrDefault() ?? "";
        var stem = new string(Path.GetFileNameWithoutExtension(last).Where(char.IsAsciiLetterOrDigit).ToArray());

        if (stem.Length >= 4 && !GenericName.IsMatch(stem))
            return stem[..Math.Min(Length, stem.Length)];

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Host.ToLowerInvariant() + url.AbsolutePath));
        return Convert.ToHexString(hash)[..Length].ToLowerInvariant();
    }
}
