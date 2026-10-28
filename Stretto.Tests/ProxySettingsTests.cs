using Stretto.Core;

namespace Stretto.Tests;

public static partial class Program
{
    private static Task ProtectsProxySettings()
    {
        if (!OperatingSystem.IsWindows()) throw new SkippedException("Windows DPAPI check");
        using var folder = new TempFolder();
        var originalDirectory = DownloadStore.Directory;
        var originalRules = CategoryRules.Active;
        DownloadStore.Directory = folder.Path;
        try
        {
            const string password = "stretto-test-proxy-secret";
            var path = Path.Combine(folder.Path, "settings.json");
            new AppSettings { ProxyPassword = password, ProxyUsername = "test-user" }.Save();
            var text = File.ReadAllText(path);
            Assert.False(text.Contains(password, StringComparison.Ordinal), "proxy password is not written in plaintext");
            Assert.True(text.Contains("ProtectedProxyPassword", StringComparison.Ordinal), "protected password is persisted");
            Assert.Equal(password, AppSettings.Load().ProxyPassword, "same Windows user can restore the password");
            File.WriteAllText(path, "{\"ProxyPassword\":\"" + password + "\"}");
            var legacy = AppSettings.Load();
            Assert.Equal(password, legacy.ProxyPassword, "legacy password remains usable");
            legacy.Save();
            Assert.False(File.ReadAllText(path).Contains(password, StringComparison.Ordinal), "next save migrates plaintext");
            File.WriteAllText(path, "{\"ProtectedProxyPassword\":\"not base64\"}");
            Assert.Equal("", AppSettings.Load().ProxyPassword, "corrupt password does not prevent app startup");
        }
        finally { DownloadStore.Directory = originalDirectory; CategoryRules.Use(originalRules); }
        return Task.CompletedTask;
    }
}
