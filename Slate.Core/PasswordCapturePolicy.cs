namespace Slate.Core;

/// <summary>Native-side eligibility checks for password submission capture.</summary>
public static class PasswordCapturePolicy
{
    public static bool TryNormalizeSubmission(string? pageUrl, string? username, string? password,
        bool isPrivate, bool isTemporary, out string origin)
    {
        origin = "";
        if (isPrivate || isTemporary) return false;

        var normalized = CredentialOrigin.Normalize(pageUrl);
        if (normalized is null || !CredentialVault.IsValidCredential(normalized, username, password)) return false;

        origin = normalized;
        return true;
    }
}
