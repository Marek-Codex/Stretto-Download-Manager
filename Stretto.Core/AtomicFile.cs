using System.Text;

namespace Stretto.Core;

/// <summary>Replaces small state files only after the complete new contents are written.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents) =>
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The target must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporary = fullPath + ".tmp";
        File.WriteAllBytes(temporary, contents);
        File.Move(temporary, fullPath, overwrite: true);
    }
}
