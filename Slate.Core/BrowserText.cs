using System.Text;

namespace Slate.Core;

/// <summary>Bounds page- and profile-controlled text before it reaches UI or persistence.</summary>
public static class BrowserText
{
    public const int MaximumTitleLength = 512;

    public static string SanitizeTitle(string? value, string fallback = "Untitled") =>
        SanitizeLabel(value, MaximumTitleLength, fallback);

    public static string SanitizeLabel(string? value, int maximumLength, string fallback = "Untitled")
    {
        if (maximumLength is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        string normalized = Normalize(value, maximumLength);
        if (normalized.Length > 0) return normalized;
        normalized = Normalize(fallback, maximumLength);
        return normalized.Length > 0 ? normalized : "Untitled"[..Math.Min(8, maximumLength)];
    }

    private static string Normalize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var result = new StringBuilder(Math.Min(value.Length, maximumLength));
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace && result.Length < maximumLength) result.Append(' ');
            pendingSpace = false;
            if (result.Length >= maximumLength) break;
            result.Append(character);
        }
        if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result.Length--;
        return result.ToString();
    }
}
