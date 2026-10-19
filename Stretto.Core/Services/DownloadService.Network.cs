using System.Net;

namespace Stretto.Core;

/// <summary>The HTTP stack: building clients, proxy routes and per-site credentials.</summary>
public sealed partial class DownloadService
{
    private sealed record DownloadNetwork(HttpClient Http, SegmentedDownloader Downloader);
    private sealed record ProxyRoute(System.Net.IWebProxy? HttpProxy, Socks5ProxyOptions? Socks5);

    private static DownloadNetwork CreateNetwork(AppSettings settings)
    {
        var route = ResolveRoute(settings.ProxyMode, settings.ProxyProtocol,
            settings.ProxyHost, settings.ProxyPort, settings.ProxyUsername, settings.ProxyPassword);
        var http = SegmentedDownloader.CreateDefaultClient(
            settings.ConnectionTimeoutSeconds, route.HttpProxy, settings.UseWindowsCredentials,
            socks5Proxy: route.Socks5);
        return new DownloadNetwork(http, new SegmentedDownloader(http, settings.MaxRetriesPerSegment));
    }

    /// <summary>
    /// The HTTP stack for one download. Global downloads share the long-lived client;
    /// proxy overrides and site credentials get a transient handler because both are
    /// handler-level settings rather than request headers.
    /// </summary>
    private (DownloadNetwork Network, bool Owned) NetworkFor(DownloadItem item)
    {
        var login = ResolveLogin(item);
        if (item.ProxyModeOverride is null && login is null)
            return (_network, false);

        var route = item.ProxyModeOverride is { } overrideMode
            ? ResolveRoute(overrideMode, item.ProxyProtocolOverride ?? _settings.ProxyProtocol,
                item.ProxyHost ?? "", item.ProxyPort, item.ProxyUsername, item.ProxyPassword)
            : ResolveRoute(_settings.ProxyMode, _settings.ProxyProtocol,
                _settings.ProxyHost, _settings.ProxyPort, _settings.ProxyUsername, _settings.ProxyPassword);
        var credentials = login is null && !_settings.UseWindowsCredentials
            ? null
            : CreateCredentials(item.Url, login, _settings.UseWindowsCredentials);
        var http = SegmentedDownloader.CreateDefaultClient(
            _settings.ConnectionTimeoutSeconds, route.HttpProxy, _settings.UseWindowsCredentials,
            credentials, route.Socks5);
        return (new DownloadNetwork(http, new SegmentedDownloader(http, _settings.MaxRetriesPerSegment)), true);
    }

    private static SiteCredentials.Login? ResolveLogin(DownloadItem item) =>
        string.IsNullOrEmpty(item.Username)
            ? SiteCredentials.For(item.Url)
            : new SiteCredentials.Login(item.Username, item.Password ?? "");

    /// <summary>
    /// HTTP credentials are offered only after a server challenge and only to the saved
    /// site's origin. This supports Basic and Digest without placing a reusable
    /// Authorization header on every request or carrying it across redirects.
    /// </summary>
    private static ICredentials CreateCredentials(
        string url,
        SiteCredentials.Login? login,
        bool useWindowsCredentials)
    {
        var scoped = new CredentialCache();
        if (login is not null && Uri.TryCreate(url, UriKind.Absolute, out var address)
            && address.Scheme is "http" or "https")
        {
            var origin = new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
            var site = new NetworkCredential(login.Username, login.Password);
            foreach (var scheme in new[] { "Basic", "Digest", "NTLM", "Negotiate" })
                scoped.Add(origin, scheme, site);
        }

        return new ScopedCredentials(scoped, useWindowsCredentials);
    }

    private sealed class ScopedCredentials(CredentialCache siteCredentials, bool useWindowsCredentials) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (siteCredentials.GetCredential(uri, authType) is { } site)
                return site;

            return useWindowsCredentials && authType is ("NTLM" or "Negotiate" or "Kerberos")
                ? CredentialCache.DefaultNetworkCredentials
                : null;
        }
    }

    private static ProxyRoute ResolveRoute(
        ProxyMode mode, ProxyProtocol protocol, string host, int port, string? username, string? password)
    {
        if (mode == ProxyMode.None)
            return new ProxyRoute(SegmentedDownloader.NoProxy, null);

        if (mode != ProxyMode.Manual || string.IsNullOrWhiteSpace(host))
            return new ProxyRoute(null, null);

        if (protocol == ProxyProtocol.Socks5)
            return new ProxyRoute(null, new Socks5ProxyOptions(host.Trim(), port,
                string.IsNullOrWhiteSpace(username) ? null : username, password));

        var proxy = new System.Net.WebProxy(host.Trim(), port);

        if (!string.IsNullOrEmpty(username))
            proxy.Credentials = new System.Net.NetworkCredential(username, password ?? "");

        return new ProxyRoute(proxy, null);
    }
}
