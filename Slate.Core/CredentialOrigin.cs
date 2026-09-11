namespace Slate.Core;

/// <summary>Exact web origin identity; stricter than general navigation display utilities.</summary>
public static class CredentialOrigin
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 8192 || value.Any(char.IsWhiteSpace) || value.Contains('\\') ||
            !Navigation.IsWebUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0) return null;
        try
        {
            var host = uri.IdnHost.ToLowerInvariant();
            if (host.Length == 0 || host.EndsWith('.')) return null; // Ambiguous trailing-dot identities fail closed.
            if (uri.HostNameType == UriHostNameType.IPv6) host = "[" + host.Trim('[', ']') + "]";
            return uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        }
        catch (UriFormatException) { return null; }
    }
}
