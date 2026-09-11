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
                return explicitUri.AbsoluteUri;
            var candidate = "https://" + text;
            if (!text.Contains("://") && !text.Contains('\\') && Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Host.Contains('.') || uri.Host == "localhost" || IPAddress.TryParse(uri.Host.Trim('[', ']'), out _)) &&
                string.IsNullOrEmpty(uri.UserInfo))
            {
                bool loopback = uri.Host == "localhost" || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
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

    public static bool IsSecureOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            string host = uri.Host.Trim('[', ']');
            return host == "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        }
        return false;
    }

    public static string? WebOrigin(string? url) => IsWebUrl(url)
        ? new Uri(url!).GetLeftPart(UriPartial.Authority)
        : null;

    public static string DisplayHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    public static bool IsLocalFileUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.IsFile || uri.IsUnc) return false;
        var ext = Path.GetExtension(uri.LocalPath);
        if (IsDangerousExtension(ext)) return false;
        return true;
    }

    public static bool IsDangerousExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext is "exe" or "bat" or "cmd" or "ps1" or "vbs" or "msi" or "dll" or "com" or "scr" or "reg" or "hta" or "cpl" or "pif";
    }

    public static bool IsViewSourceUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) && url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase);
}
