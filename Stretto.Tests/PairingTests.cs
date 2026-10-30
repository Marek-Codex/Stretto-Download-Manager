using System.Reflection;
using Stretto.Core;
using Stretto.App.Services;

namespace Stretto.Tests
{
    public static partial class Program
    {
        private static Task PairingRevocation()
        {
            using var folder = new TempFolder();
            var original = DownloadStore.Directory;
            DownloadStore.Directory = folder.Path;
            const string revoked = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string retained = "chrome-extension://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string legacy = "chrome-extension://cccccccccccccccccccccccccccccccc";
            try
            {
                var legacyToken = new string('d', 32);
                File.WriteAllText(Path.Combine(folder.Path, "extensions.json"), System.Text.Json.JsonSerializer.Serialize(new[] { legacy }));
                File.WriteAllText(Path.Combine(folder.Path, "pairing.txt"), legacyToken);
                Assert.Equal(legacyToken, Pairing.ApprovedToken(legacy), "existing approvals migrate without another prompt");
                Pairing.Forget(legacy);
                var generation = Pairing.ApprovalGeneration;
                Assert.True(Pairing.ApprovedToken(revoked) is null, "an unapproved origin is not silently issued a token");
                var oldToken = Pairing.ApproveAndGetToken(revoked, generation);
                Assert.True(oldToken is not null, "explicit approval issues a token");
                Pairing.ApproveAndGetToken(retained, generation);
                var mirror = Path.Combine(folder.Path, "pairing.txt");
                File.SetAttributes(mirror, FileAttributes.ReadOnly);
                try { Pairing.Forget(revoked); }
                finally { File.SetAttributes(mirror, FileAttributes.Normal); }
                Assert.True(Pairing.Token != oldToken, "revocation rotates the shared secret");
                Assert.True(Pairing.ApprovedToken(revoked) is null, "revoked origin cannot silently reconnect");
                Assert.Equal(Pairing.Token, Pairing.ApprovedToken(retained), "retained origin reconnects silently with the new token");
                Assert.True(Pairing.ApproveAndGetToken(revoked, generation) is null, "an old approval dialog cannot issue the rotated token");

                // Reload the persisted approval and token, as a new app process would.
                ResetPairingCache();
                Assert.True(Pairing.ApprovedToken(revoked) is null, "revocation survives reloading the approval store");
                Assert.True(Pairing.Token != oldToken, "a failed legacy token-file write cannot resurrect the revoked token on restart");
                Assert.True(Pairing.ApproveAndGetToken(revoked, Pairing.ApprovalGeneration) is not null, "fresh explicit consent can reconnect");

                var beforeFailedSave = Pairing.Token;
                var state = Path.Combine(folder.Path, "pairing-state.json");
                File.SetAttributes(state, FileAttributes.ReadOnly);
                try
                {
                    try { Pairing.Forget(revoked); throw new AssertionException("failed permission persistence must be reported"); }
                    catch (IOException) { }
                }
                finally { File.SetAttributes(state, FileAttributes.Normal); }
                Assert.Equal(beforeFailedSave, Pairing.ApprovedToken(revoked), "failed revocation does not pretend to have committed");
                ResetPairingCache();
                Assert.Equal(beforeFailedSave, Pairing.ApprovedToken(revoked), "failed save leaves one consistent permission state after restart");

                File.WriteAllText(state, "{broken state");
                ResetPairingCache();
                Assert.True(Pairing.ApprovedToken(revoked) is null, "a corrupt authoritative store does not restore legacy approvals");
                Assert.True(Pairing.ApprovedToken(legacy) is null, "a corrupt store cannot revive the old approvals file");
                Assert.True(Pairing.Token != oldToken && Pairing.Token != legacyToken, "neither legacy token copy is restored");
                Assert.True(Pairing.Token != beforeFailedSave, "a corrupt store does not resurrect an older token");
            }
            finally { ResetPairingCache(); DownloadStore.Directory = original; }
            return Task.CompletedTask;
        }

        private static void ResetPairingCache()
        {
            foreach (var name in new[] { "_approved", "_token" })
                typeof(Pairing).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        }
    }
}

// Pairing itself has no UI dependency. These two adapters replace only the app's
// logging and bundled-folder lookup; the production trust/token code is linked above.
namespace Stretto.App
{
    internal static class App { public static void Log(string message) => AppLog.Write(message); }
}
namespace Stretto.App.Services
{
    internal static class BundledExtensions { public static string Root => AppContext.BaseDirectory; }
}
