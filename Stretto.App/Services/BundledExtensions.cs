using Stretto.Core;

namespace Stretto.App.Services;

/// <summary>MSIX version directories change at update; unpacked extensions need a stable path.</summary>
internal static class BundledExtensions
{
    public static string Root => StartupRegistration.IsPackaged
        ? Path.Combine(DownloadStore.Directory, "extensions")
        : Path.Combine(AppContext.BaseDirectory, "extensions");

    public static void RefreshPackagedCopy()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "extensions");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(Root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination) || File.GetLastWriteTimeUtc(file) != File.GetLastWriteTimeUtc(destination)
                || new FileInfo(file).Length != new FileInfo(destination).Length)
                File.Copy(file, destination, overwrite: true);
        }
    }
}
