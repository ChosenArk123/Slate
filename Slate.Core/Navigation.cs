using System.Net;

namespace Slate.Core;

public static class Navigation
{
    public const string NewTab = "slate://newtab";
    public static readonly string[] SearchEngines = ["DuckDuckGo", "Google", "Bing"];

    public static string Resolve(string? input, string engine = "DuckDuckGo")
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0 || text.Equals(NewTab, StringComparison.OrdinalIgnoreCase)) return NewTab;

        if (text.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase))
        {
            var target = text["view-source:".Length..].Trim();
            return "view-source:" + Resolve(target, engine);
        }

        if (!text.Any(char.IsWhiteSpace))
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var explicitUri) &&
                explicitUri.Scheme is "http" or "https" && !string.IsNullOrEmpty(explicitUri.Host) &&
                string.IsNullOrEmpty(explicitUri.UserInfo) && !text.Contains('\\'))
            {
                // HTTPS-First: Upgrade public HTTP requests to HTTPS.
                // Local development endpoints (localhost, loopback IP) remain HTTP.
                if (explicitUri.Scheme == "http" && !IsLoopbackOrLocalHost(explicitUri.Host))
                {
                    var builder = new UriBuilder(explicitUri) { Scheme = "https" };
                    if (explicitUri.IsDefaultPort) builder.Port = -1;
                    return builder.Uri.AbsoluteUri;
                }
                return explicitUri.AbsoluteUri;
            }
            var candidate = "https://" + text;
            if (!text.Contains("://") && !text.Contains('\\') && Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Host.Contains('.') || uri.Host == "localhost" || IPAddress.TryParse(uri.Host.Trim('[', ']'), out _)) &&
                string.IsNullOrEmpty(uri.UserInfo))
            {
                bool loopback = IsLoopbackOrLocalHost(uri.Host);
                return loopback ? new Uri("http://" + text).AbsoluteUri : uri.AbsoluteUri;
            }
        }
        return Search(text, engine);
    }

    public static string Search(string text, string engine) => (engine switch
    {
        "Google" => "https://www.google.com/search?q=",
        "Bing" => "https://www.bing.com/search?q=",
        _ => "https://duckduckgo.com/?q="
    }) + Uri.EscapeDataString(text);

    public static bool IsWebUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && !string.IsNullOrEmpty(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) && !url!.Contains('\\');

    public static bool IsAllowedFrameUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is "http" or "https") return IsWebUrl(url);
        if (uri.Scheme == "about") return url is "about:blank" or "about:srcdoc";
        return uri.Scheme is "data" or "blob" or "javascript";
    }

    public static bool IsLoopbackOrLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string cleaned = host.Trim().Trim('[', ']');
        if (cleaned.EndsWith('.')) cleaned = cleaned.TrimEnd('.');
        if (cleaned.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (cleaned.Length > ".localhost".Length && cleaned.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(cleaned, out var ip))
        {
            return IPAddress.IsLoopback(ip);
        }
        return false;
    }

    public static bool IsLoopbackOrLocalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return IsLoopbackOrLocalHost(uri.Host);
    }

    public static bool IsPublicHttp(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !IsLoopbackOrLocalHost(uri.Host);
    }

    public static bool IsSecureOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return IsLoopbackOrLocalHost(uri.Host);
        }
        return false;
    }

    public static string? WebOrigin(string? url) => IsWebUrl(url)
        ? new Uri(url!).GetLeftPart(UriPartial.Authority)
        : null;

    public static string DisplayHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    public static bool IsLocalFileUrl(string? url)
    {
        return TryGetSafeLocalFilePath(url, out _);
    }

    public static bool TryGetSafeLocalFilePath(string? url, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Host)) return false;
        try
        {
            var localPath = uri.LocalPath.Replace('/', '\\');
            if (!DownloadSafety.IsSafeLocalFilePath(localPath)) return false;
            path = Path.GetFullPath(localPath);
            return !IsDangerousExtension(Path.GetExtension(path));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDangerousExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        var ext = extension.Trim().TrimEnd('.', ' ').TrimStart('.').ToLowerInvariant();
        return ext is
            // Executable binaries & installers
            "app" or "application" or "appinstaller" or "appref-ms" or "appx" or "appxbundle" or
            "bat" or "cmd" or "com" or "cpl" or "diagcab" or "dll" or "exe" or "gadget" or "hta" or
            "inf" or "ins" or "iso" or "isp" or "jar" or "jnlp" or "js" or "jse" or "lnk" or "msc" or
            "msi" or "msix" or "msixbundle" or "msp" or "mst" or "ocx" or "pif" or
            // Scripts & configuration vectors
            "ps1" or "ps1xml" or "ps2" or "ps2xml" or "psc1" or "psc2" or "reg" or "scf" or "scr" or
            "sct" or "shb" or "sys" or "url" or "vb" or "vbe" or "vbs" or "ws" or "wsc" or "wsf" or "wsh" or
            // Modern Windows packages, shortcuts, containers, disk images, sandbox configs, and help files
            "vhd" or "vhdx" or "img" or "settingcontent-ms" or "search-ms" or "desklink" or "mapimail" or
            "theme" or "themepack" or "chm" or "wsb" or
            // Certificates & keystores
            "cer" or "crt" or "der" or "pfx" or "p12" or
            // Web queries
            "iqy" or "rqy";
    }

    public static bool IsViewSourceUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) && url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase);
}
