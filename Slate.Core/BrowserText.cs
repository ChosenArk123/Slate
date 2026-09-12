using System.Globalization;
using System.Text;

namespace Slate.Core;

/// <summary>Bounds page- and profile-controlled text before it reaches UI or persistence.</summary>
public static class BrowserText
{
    public const int MaximumTitleLength = 512;
    public const int MaximumHostLength = 256;

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

    /// <summary>
    /// Sanitizes hostnames and origins before display in security-sensitive UI (permission prompts,
    /// fullscreen indicator, dialogs) to neutralize BiDi overrides and invisible spoofing characters.
    /// </summary>
    public static string SanitizeHost(string? host, string fallback = "unknown")
    {
        if (string.IsNullOrWhiteSpace(host)) return fallback;
        string sanitized = Normalize(host, MaximumHostLength);
        return sanitized.Length > 0 ? sanitized : fallback;
    }

    /// <summary>
    /// Identifies dangerous Unicode format, BiDi override, or invisible characters that can
    /// visually disguise, spoof, or reverse strings in UI controls.
    /// Preserves legitimate international text, scripts (Arabic, Hebrew, CJK, Cyrillic), and ligatures.
    /// </summary>
    public static bool IsDangerousFormatting(char character)
    {
        // BiDi override & embedding controls (can reverse or swap file extensions/domains)
        if (character is >= '\u202A' and <= '\u202E') return true; // LRE, RLE, PDF, LRO, RLO
        if (character is >= '\u2066' and <= '\u2069') return true; // LRI, RLI, FSI, PDI
        if (character is '\u200E' or '\u200F' or '\u061C') return true; // LRM, RLM, ALM

        // Invisible / zero-width spoofers
        if (character is '\u200B' or '\uFEFF') return true; // Zero-width space, BOM
        if (character is '\u2028' or '\u2029') return true; // Line separator, Paragraph separator
        if (character is >= '\uFFF9' and <= '\uFFFB') return true; // Interlinear annotation
        if (character is '\uFFFC') return true; // Object replacement

        // Format category characters (excluding ZWNJ \u200C and ZWJ \u200D needed for Persian/Indic ligatures and emoji)
        var category = char.GetUnicodeCategory(character);
        if (category == UnicodeCategory.Format && character != '\u200C' && character != '\u200D')
            return true;

        return false;
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

            // Strip dangerous BiDi and invisible formatting characters
            if (IsDangerousFormatting(character))
            {
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

