namespace Stretto.Core;

public enum BatchWildcard { Numbers, Letters }

/// <summary>
/// Expands a wildcard address such as https://host/images/edge*.png into the concrete
/// list of URLs it stands for. Pure string work, kept in Core so it can be tested without
/// touching the network.
/// </summary>
public static class BatchExpander
{
    /// <summary>A typo in the range should not queue a hundred thousand downloads.</summary>
    public const int MaxResults = 5000;

    public static IReadOnlyList<string> Expand(
        string address, BatchWildcard kind, string from, string to, int padWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Enter an address.");

        if (!address.Contains('*'))
            throw new ArgumentException("The address needs an asterisk marking where the range goes.");

        var values = kind == BatchWildcard.Letters
            ? LetterRange(from, to)
            : NumberRange(from, to, padWidth);

        return [.. values.Select(v => address.Replace("*", v))];
    }

    private static List<string> LetterRange(string from, string to)
    {
        if (from.Length != 1 || to.Length != 1)
            throw new ArgumentException("For letters, From and To must each be a single character.");

        var start = from[0];
        var end = to[0];

        if (!char.IsLetter(start) || !char.IsLetter(end))
            throw new ArgumentException("For letters, From and To must both be letters.");

        if (start > end)
            throw new ArgumentException($"From ({start}) comes after To ({end}).");

        return [.. Enumerable.Range(start, end - start + 1).Select(c => ((char)c).ToString())];
    }

    private static List<string> NumberRange(string from, string to, int padWidth)
    {
        if (!long.TryParse(from, out var start) || !long.TryParse(to, out var end))
            throw new ArgumentException("For numbers, From and To must both be whole numbers.");

        if (start > end)
            throw new ArgumentException($"From ({start}) is greater than To ({end}).");

        var count = end - start + 1;
        if (count > MaxResults)
            throw new ArgumentException($"That range is {count:N0} files; the limit is {MaxResults:N0}.");

        return [.. Enumerable.Range(0, (int)count)
            .Select(i => (start + i).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    .PadLeft(padWidth, '0'))];
    }

    /// <summary>Splits pasted text into links, tolerating blank lines and stray whitespace.</summary>
    public static IReadOnlyList<string> ParseLinks(string text) =>
        [.. (text ?? "")
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => Uri.TryCreate(line, UriKind.Absolute, out var uri)
                           && uri.Scheme is "http" or "https")];
}
