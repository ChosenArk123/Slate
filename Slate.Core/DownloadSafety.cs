using System.Text.RegularExpressions;

namespace Slate.Core;

public static class DownloadSafety
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    public static string SanitizeFileName(string? rawFileName, string defaultName = "download")
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
            return defaultName;

        // Strip any leading/trailing directory paths and normalize separators
        string name = rawFileName.Replace('\\', '/');
        int lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0)
            name = name[(lastSlash + 1)..];

        // Strip control characters, NTFS alternate data stream markers (:), and invalid chars
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ||
                c == ':' || InvalidFileNameChars.Contains(c))
                sb.Append('_');
            else
                sb.Append(c);
        }
        name = sb.ToString().Trim();

        // Trim trailing dots and spaces (Windows files cannot end with dot or space)
        name = name.TrimEnd('.', ' ');

        // Prevent directory traversal tokens
        if (name == ".." || name == "." || string.IsNullOrWhiteSpace(name))
            name = defaultName;

        // Check for Windows reserved device names (e.g. CON, PRN, AUX, NUL, COM1.txt, LPT1.dat, nul.tar.gz)
        int firstDot = name.IndexOf('.');
        string primaryName = firstDot >= 0 ? name[..firstDot] : name;
        string baseName = Path.GetFileNameWithoutExtension(name);
        if (ReservedDeviceNames.Contains(primaryName) || ReservedDeviceNames.Contains(baseName))
        {
            name = "_" + name;
        }

        // Clamp maximum length (Windows MAX_PATH compatibility; filename max ~200 chars)
        if (name.Length > 200)
        {
            string ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = ext[..20];
            int maxBase = 200 - ext.Length;
            name = name[..maxBase] + ext;
        }

        return string.IsNullOrWhiteSpace(name) ? defaultName : name;
    }

    /// <summary>Atomically reserves a collision-free empty file inside a validated local directory.</summary>
    public static string ReserveSafeDestinationPath(string downloadFolder, string? rawSuggestedPath, string defaultName = "download")
    {
        if (!IsSafeLocalDirectory(downloadFolder)) throw new ArgumentException("A safe local download directory is required.", nameof(downloadFolder));
        string safeName = SanitizeFileName(Path.GetFileName(rawSuggestedPath), defaultName);
        string targetDir = Path.GetFullPath(downloadFolder);
        Directory.CreateDirectory(targetDir);
        if (!IsSafeLocalDirectory(targetDir)) throw new IOException("The download directory changed while it was being prepared.");
        string boundary = Path.EndsInDirectorySeparator(targetDir) ? targetDir : targetDir + Path.DirectorySeparatorChar;
        string baseName = Path.GetFileNameWithoutExtension(safeName);
        string extension = Path.GetExtension(safeName);
        for (int index = 0; index < 10_000; index++)
        {
            string candidateName = index == 0 ? safeName : $"{baseName} ({index}){extension}";
            string candidatePath = Path.GetFullPath(Path.Combine(targetDir, candidateName));
            if (!candidatePath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The download destination escaped its directory.");
            try
            {
                using var reservation = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    1, FileOptions.WriteThrough);
                reservation.Flush(true);
                return candidatePath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(candidatePath)) { }
        }
        throw new IOException("No collision-free download filename is available.");
    }

    /// <summary>Resolves a collision-free destination path inside a validated local directory and atomically reserves it.</summary>
    public static string GetSafeDestinationPath(string downloadFolder, string? rawSuggestedPath, string defaultName = "download")
        => ReserveSafeDestinationPath(downloadFolder, rawSuggestedPath, defaultName);

    public static bool IsSafeLocalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string trimmed = path.Trim();
            if (!string.Equals(path, trimmed, StringComparison.Ordinal)) return false;
            if (trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.EndsWith('.') || segment.EndsWith(' '))) return false;
            // Block UNC network paths (\\server\share) to prevent SMB credential/NTLM hash leakage
            if (trimmed.StartsWith(@"\\") || trimmed.StartsWith("//"))
                return false;

            // Must be a rooted drive path, e.g. C:\...
            if (!Path.IsPathRooted(trimmed))
                return false;

            string fullPath = Path.GetFullPath(trimmed);
            string root = Path.GetPathRoot(fullPath) ?? "";
            // Root must be like "C:\" (drive letter + colon + slash)
            if (root.Length != 3 || !char.IsLetter(root[0]) || root[1] != ':' || !Path.EndsInDirectorySeparator(root))
                return false;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)) return false;
            if (File.Exists(fullPath)) return false;
            for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSafeLocalFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string trimmed = path.Trim();
            if (!string.Equals(path, trimmed, StringComparison.Ordinal)) return false;
            if (trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.EndsWith('.') || segment.EndsWith(' '))) return false;
            if (trimmed.StartsWith(@"\\") || trimmed.StartsWith("//")) return false;
            string fullPath = Path.GetFullPath(trimmed);
            string root = Path.GetPathRoot(fullPath) ?? "";
            if (root.Length != 3 || !char.IsLetter(root[0]) || root[1] != ':' || !Path.EndsInDirectorySeparator(root)) return false;
            string fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(fileName) ||
                !string.Equals(SanitizeFileName(fileName, ""), fileName, StringComparison.Ordinal)) return false;
            string? directory = Path.GetDirectoryName(fullPath);
            if (!IsSafeLocalDirectory(directory) || Directory.Exists(fullPath)) return false;
            if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDeleteIncompleteFile(string? path)
    {
        if (!IsSafeLocalFilePath(path)) return false;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Formats a structurally safe Windows Mark-of-the-Web (Zone.Identifier) INI payload.
    /// Neutralizes line breaks, control characters, section delimiters, and length attacks
    /// to prevent INI injection into ZoneTransfer attributes.
    /// </summary>
    public static string FormatZoneIdentifier(string? sourceUrl, string? referrerUrl = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[ZoneTransfer]\r\nZoneId=3\r\n");

        string? cleanSource = SanitizeZoneUrl(sourceUrl);
        if (!string.IsNullOrEmpty(cleanSource))
        {
            sb.Append("HostUrl=").Append(cleanSource).Append("\r\n");
        }

        string? cleanReferrer = SanitizeZoneUrl(referrerUrl);
        if (!string.IsNullOrEmpty(cleanReferrer))
        {
            sb.Append("ReferrerUrl=").Append(cleanReferrer).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string? SanitizeZoneUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        string trimmed = url.Trim();
        if (trimmed.Length > 2048)
        {
            trimmed = trimmed[..2048];
        }

        // Must be an HTTP or HTTPS web URL
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Strip CR, LF, NUL, control characters, brackets, and invalid chars to prevent INI section or key injection
        var clean = new System.Text.StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (char.IsControl(c) || c == '[' || c == ']' || c == '\r' || c == '\n' || c == '\0')
            {
                continue;
            }
            clean.Append(c);
        }

        string result = clean.ToString().Trim();
        if (result.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            result.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        return null;
    }

    /// <summary>
    /// Attaches the Windows Mark-of-the-Web (Zone.Identifier) alternate data stream to downloaded files.
    /// This ensures Windows Defender, SmartScreen, and file reputation services inspect the file upon execution.
    /// Attachment is best-effort and will never fail or delete a completed download.
    /// </summary>
    public static bool AttachZoneIdentifier(string? path, string? sourceUrl = null, string? referrerUrl = null)
    {
        if (!IsSafeLocalFilePath(path) || !File.Exists(path)) return false;
        try
        {
            // Verify path has not become a reparse point (symlink/junction)
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) != 0) return false;

            string streamPath = path + ":Zone.Identifier";
            string content = FormatZoneIdentifier(sourceUrl, referrerUrl);
            File.WriteAllText(streamPath, content);
            return true;
        }
        catch
        {
            // Non-NTFS drives (e.g. FAT32/exFAT), locked files, or restricted environments may not support ADS.
            return false;
        }
    }
}
