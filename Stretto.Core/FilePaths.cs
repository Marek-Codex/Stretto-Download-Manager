namespace Stretto.Core;

public static class FilePaths
{
    /// <summary>
    /// Picks the file a download writes to. Without <paramref name="overwrite"/>, an existing
    /// file pushes the download to "name (2).ext", "name (3).ext" and so on -- unless
    /// <paramref name="isOwnPartial"/> says the file already there is this download's own work
    /// in progress, in which case it is resumed rather than avoided.
    /// </summary>
    public static string ClashFree(
        string directory, string name, bool overwrite, Func<string, bool>? isOwnPartial = null)
    {
        var path = Path.Combine(directory, name);
        if (overwrite || !File.Exists(path) || isOwnPartial?.Invoke(path) == true)
            return path;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        // Ten thousand copies of one name is not a real folder. A unique name still beats
        // quietly overwriting somebody's file, which is what returning the path would do.
        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }
}
