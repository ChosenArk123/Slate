using System;
using System.IO;
using System.Net;
using Slate.Core;

namespace Slate.Services;

public sealed class NavigationCoordinator
{
    public string ResolveInput(string? input, string searchEngine = "DuckDuckGo")
    {
        return Navigation.Resolve(input, searchEngine);
    }

    private readonly HashSet<string> _allowedInsecureHosts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsInsecureHttpAllowed(string host) => _allowedInsecureHosts.Contains(host);

    public void AllowInsecureHttp(string host) => _allowedInsecureHosts.Add(host);

    /// <summary>
    /// Detects attempts to silently downgrade an active secure HTTPS navigation to unencrypted public HTTP.
    /// </summary>
    public bool IsDowngradeAttempt(string? currentUrl, string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUrl) || string.IsNullOrWhiteSpace(targetUrl)) return false;
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri) ||
            !Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)) return false;

        // Only flag if moving from HTTPS to public HTTP
        if (currentUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            targetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return !Navigation.IsLoopbackOrLocalHost(targetUri.Host);
        }

        return false;
    }

    public bool IsSafeToNavigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // View-source is supported internally
        if (Navigation.IsViewSourceUrl(url)) return true;

        // Allowed web schemes
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
            url.Equals(Navigation.NewTab, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Local file validation
        if (Navigation.IsLocalFileUrl(url))
        {
            try
            {
                var uri = new Uri(url);
                if (uri.IsUnc) return false;
                var path = uri.LocalPath;
                if (!DownloadSafety.IsSafeLocalFilePath(path)) return false;
                if (Navigation.IsDangerousExtension(Path.GetExtension(path))) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public string BuildViewSourceHtml(string targetUrl, string sourceCode)
    {
        var lines = sourceCode.Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Source of ");
        sb.Append(WebUtility.HtmlEncode(targetUrl));
        sb.Append("</title><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'\"><style>");
        sb.Append(@"
body { margin: 0; padding: 16px; background: #121212; color: #dcdcdc; font-family: 'Consolas', 'Cascadia Code', 'Courier New', monospace; font-size: 13px; line-height: 1.5; }
.line { display: flex; }
.ln { width: 50px; text-align: right; margin-right: 18px; user-select: none; color: #707070; flex-shrink: 0; }
.code { white-space: pre-wrap; word-break: break-all; flex: 1; }
");
        sb.Append("</style></head><body>");
        for (int i = 0; i < lines.Length; i++)
        {
            var lineNum = i + 1;
            var lineText = lines[i].TrimEnd('\r');
            var encoded = WebUtility.HtmlEncode(lineText);
            sb.Append("<div class=\"line\"><span class=\"ln\">");
            sb.Append(lineNum);
            sb.Append("</span><span class=\"code\">");
            sb.Append(string.IsNullOrEmpty(encoded) ? "&nbsp;" : encoded);
            sb.Append("</span></div>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
