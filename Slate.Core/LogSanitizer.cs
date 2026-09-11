using System.Text.RegularExpressions;

namespace Slate.Core;

/// <summary>
/// Redacts sensitive information (credentials, tokens, keys, passwords, headers) from diagnostics and crash logs.
/// </summary>
public static class LogSanitizer
{
    public const int MaximumLogCharacters = 32_768;

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";

        string value = message.Length > MaximumLogCharacters
            ? message[..MaximumLogCharacters] + "\n[TRUNCATED]"
            : message;

        // Redact URL userinfo credentials (e.g. https://user:password@host/path)
        value = Regex.Replace(value,
            @"(?i)(https?://[^:\s/@]+):[^@\s/]+@",
            "$1:[REDACTED]@");

        // Redact sensitive HTTP headers
        value = Regex.Replace(value,
            @"(?im)^(\s*(?:authorization|proxy-authorization|cookie|set-cookie|x-api-key|api-key|x-auth-token|session-token|x-csrf-token|x-xsrf-token)\s*:\s*)[^\r\n]*",
            "$1[REDACTED]");

        // Redact Bearer tokens
        value = Regex.Replace(value,
            @"(?i)\bbearer\s+[a-z0-9\-._~+/]+=*",
            "Bearer [REDACTED]");

        // Redact Basic auth tokens
        value = Regex.Replace(value,
            @"(?i)\bbasic\s+[a-z0-9+/=]+",
            "Basic [REDACTED]");

        // Redact JSON sensitive properties: "password": "...", "token": 12345, etc.
        value = Regex.Replace(value,
            "(?i)(?<prefix>[\\\"']?(?:password|passwd|pwd|token|auth|authorization|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|private[_-]?key|session[_-]?(?:id|token))[\\\"']?\\s*:\\s*)(?<quote>[\\\"']?)(?:\\\\.|(?!\\k<quote>)[^,}\\r\\n])+\\k<quote>",
            "${prefix}\"[REDACTED]\"");

        // Redact key-value pairs (query strings, form parameters, config settings)
        value = Regex.Replace(value,
            @"(?i)\b(password|passwd|pwd|token|auth|authorization|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|private[_-]?key|session[_-]?(?:id|token))\b\s*=\s*[^&;,\s\r\n}]+",
            "$1=[REDACTED]");

        // Redact PEM private keys
        value = Regex.Replace(value,
            @"(?s)-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
            "[REDACTED PRIVATE KEY]");

        return value;
    }
}
