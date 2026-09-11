using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Slate.Core;

public sealed class FaviconStore(string directory)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private int _generation;

    public string DirectoryPath { get; } = directory;
    public int Generation => Volatile.Read(ref _generation);

    public static string NormalizeOriginOrHost(string urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return string.Empty;
        string trimmed = urlOrHost.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }
        return trimmed.ToLowerInvariant();
    }

    public static string NormalizeHost(string urlOrHost) => NormalizeOriginOrHost(urlOrHost);

    public static string GetKey(string urlOrHost)
    {
        var identity = NormalizeOriginOrHost(urlOrHost);
        if (string.IsNullOrEmpty(identity)) return string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsValidPng(byte[]? bytes, int maxDimension = 512)
    {
        if (bytes is null || bytes.Length < 33) return false;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!bytes.AsSpan(0, 8).SequenceEqual(signature)) return false;

        ReadOnlySpan<byte> ihdr = [0x49, 0x48, 0x44, 0x52];
        if (!bytes.AsSpan(12, 4).SequenceEqual(ihdr)) return false;

        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width <= 0 || height <= 0 || width > maxDimension || height > maxDimension) return false;

        ReadOnlySpan<byte> iend = [0x49, 0x45, 0x4E, 0x44];
        if (bytes.AsSpan().IndexOf(iend) < 0) return false;

        return true;
    }

    public string? GetFaviconPath(string urlOrHost)
    {
        var key = GetKey(urlOrHost);
        if (string.IsNullOrEmpty(key)) return null;
        var path = Path.Combine(DirectoryPath, key + ".png");
        return File.Exists(path) ? path : null;
    }

    public async Task<byte[]?> GetFaviconBytesAsync(string urlOrHost, CancellationToken token = default)
    {
        int gen = Generation;
        var key = GetKey(urlOrHost);
        if (string.IsNullOrEmpty(key)) return null;
        var path = Path.Combine(DirectoryPath, key + ".png");
        if (!File.Exists(path)) return null;

        var sem = GetLock(key);
        await sem.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (gen != Generation || !File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (gen != Generation) return null;
            return IsValidPng(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            sem.Release();
        }
    }

    public byte[]? GetFaviconBytes(string urlOrHost) =>
        GetFaviconBytesAsync(urlOrHost).GetAwaiter().GetResult();

    public bool HasFavicon(string urlOrHost)
    {
        var path = GetFaviconPath(urlOrHost);
        return path is not null && IsValidPng(GetFaviconBytes(urlOrHost));
    }

    public async Task SaveFaviconAsync(string urlOrHost, byte[] pngData, CancellationToken token = default)
    {
        if (pngData is null || pngData.Length == 0 || !IsValidPng(pngData)) return;
        var key = GetKey(urlOrHost);
        if (string.IsNullOrEmpty(key)) return;

        int gen = Generation;
        var sem = GetLock(key);
        await sem.WaitAsync(token).ConfigureAwait(false);
        string? tempPath = null;
        try
        {
            if (gen != Generation) return;
            Directory.CreateDirectory(DirectoryPath);
            var targetPath = Path.Combine(DirectoryPath, key + ".png");
            tempPath = Path.Combine(DirectoryPath, $"{key}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(tempPath, pngData, token).ConfigureAwait(false);

            if (gen != Generation)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return;
            }

            File.Move(tempPath, targetPath, overwrite: true);
            tempPath = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            // Cache failures must never break navigation or callers.
        }
        finally
        {
            if (tempPath is not null)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
            sem.Release();
        }
    }

    public async Task ClearAsync()
    {
        Interlocked.Increment(ref _generation);
        var activeLocks = _locks.Values.ToList();
        foreach (var sem in activeLocks)
        {
            try { await sem.WaitAsync().ConfigureAwait(false); } catch { }
        }
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Cache purge failures are swallowed safely.
        }
        finally
        {
            foreach (var sem in activeLocks)
            {
                try { sem.Release(); } catch { }
            }
            _locks.Clear();
        }
    }

    public void Clear() => ClearAsync().GetAwaiter().GetResult();

    private SemaphoreSlim GetLock(string key) => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
}
