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

    public string BuildViewSourceHtml(string targetUrl, string rawSource)
    {
        string encodedUrl = WebUtility.HtmlEncode(targetUrl);
        string encodedSource = WebUtility.HtmlEncode(rawSource);

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>Source of {{encodedUrl}}</title>
                <style>
                    body { font-family: Consolas, 'Courier New', monospace; font-size: 13px; line-height: 1.5; padding: 16px; background: #1e1e1e; color: #d4d4d4; }
                    pre { margin: 0; white-space: pre-wrap; word-break: break-all; }
                </style>
            </head>
            <body>
                <pre><code>{{encodedSource}}</code></pre>
            </body>
            </html>
            """;
    }
}
