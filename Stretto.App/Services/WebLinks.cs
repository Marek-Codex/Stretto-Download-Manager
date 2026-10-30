using System.Diagnostics;

namespace Stretto.App.Services;

/// <summary>
/// Opens a web address in the default browser. Addresses here come from pages, extensions
/// and release feeds, and the shell will happily "open" file:, UNC or custom-protocol
/// targets by running them, so only http and https get through.
/// </summary>
public static class WebLinks
{
    public static bool Open(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https"))
        {
            App.Log($"Refused to open a non-web address: {url}");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception)
        {
            App.Log($"Could not open {address.Host}: {exception.Message}");
            return false;
        }
    }
}
