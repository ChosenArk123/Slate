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
            if (c < 32 || c == 127 || c == ':' || InvalidFileNameChars.Contains(c))
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
}
