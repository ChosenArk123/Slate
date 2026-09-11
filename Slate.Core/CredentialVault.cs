using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slate.Core;

public interface ICredentialProtector
{
    byte[] Protect(byte[] plaintext, byte[]? entropy = null) => Protect(plaintext);
    byte[] Unprotect(byte[] ciphertext, byte[]? entropy = null) => Unprotect(ciphertext);
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed record CredentialMetadata(Guid Id, string Origin, string Username, DateTimeOffset Created,
    DateTimeOffset Updated, DateTimeOffset? LastUsed, long Revision);
public enum CredentialChange { Save, Update, Unchanged }
public sealed record CredentialDecision(CredentialChange Change, CredentialMetadata? Existing);
public sealed record CredentialAssessmentRequest(string Origin, string Username, string Password);
public sealed record CredentialSaveRequest(string Origin, string Username, string Password, CredentialMetadata? Expected);
public sealed class CredentialVaultException : Exception
{
    public CredentialVaultException() : base("The password vault is unavailable. Its files have been preserved.") { }
    public CredentialVaultException(string message) : base(message) { }
    public CredentialVaultException(string message, Exception inner) : base(message, inner) { }
}
public sealed class CredentialConflictException : Exception
{
    public CredentialConflictException() : base("This credential changed. Review it again before saving.") { }
    public CredentialConflictException(string message) : base(message) { }
    public CredentialConflictException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Platform-independent storage/policy. The application supplies Windows protection.</summary>
public sealed class CredentialVault
{
    private sealed record Entry(CredentialMetadata Metadata, byte[] ProtectedSecret);
    private sealed record Database(
        int SchemaVersion,
        Guid VaultId,
        long VaultRevision,
        DateTimeOffset LastModified,
        string EntriesDigest,
        List<Entry> Entries);
    private sealed record Envelope(
        Guid VaultId,
        Guid Id,
        string Origin,
        string Username,
        long Revision,
        DateTimeOffset Created,
        DateTimeOffset Updated,
        string Password);
    private sealed record VaultAnchor(
        Guid VaultId,
        long HighWaterRevision,
        string? EntriesDigest = null);

    private sealed record V1Database(int Version, List<Entry> Entries);
    private sealed record V1Envelope(Guid Id, string Origin, string Username, long Revision, string Password);

    private sealed class RollbackDetectedException : Exception
    {
        public RollbackDetectedException() : base("Rollback detected.") { }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _path;
    private readonly string _anchorPath;
    private readonly string _legacyPath;
    private readonly string _migratedPath;
    private readonly ICredentialProtector _protector;
    private Guid _vaultId;
    private long _vaultRevision;
    private List<Entry>? _entries;
    private bool _readOnly;
    public bool RecoveredFromBackup => _readOnly;
    private const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumEntries = 10_000;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };

    public CredentialVault(string directory, ICredentialProtector protector, string? anchorPath = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;

        if (directory.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _directory = Path.GetDirectoryName(directory) ?? ".";
            _path = directory;
        }
        else
        {
            _directory = directory;
            _path = Path.Combine(directory, "credentials.v2.json");
        }

        _anchorPath = anchorPath ?? Path.Combine(_directory, "vault.anchor");
        _legacyPath = Path.Combine(_directory, "credentials.v1.json");
        _migratedPath = Path.Combine(_directory, "credentials.v1.json.migrated");
    }

    public static CredentialVault Load(string directory, ICredentialProtector protector, string? anchorPath = null) =>
        new(directory, protector, anchorPath);

    private async Task<T> Run<T>(Func<T> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { Load(); return operation(); }
                catch (CredentialConflictException) { throw; }
                catch (CredentialVaultException) { throw; }
                catch { throw new CredentialVaultException(); } // Never propagate a serializer/crypto exception or its payload.
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<CredentialMetadata>> ListAsync(string? origin = null) => Run<IReadOnlyList<CredentialMetadata>>(() =>
        _entries!.Where(e => origin is null || e.Metadata.Origin == origin).Select(e => e.Metadata).ToArray());

    public Task<string> RevealAsync(Guid id, long revision) => Run(() => Decode(Find(id, revision)));

    public async Task<CredentialDecision> AssessAsync(string origin, string username, string password)
    {
        var decisions = await AssessBatchAsync([new CredentialAssessmentRequest(origin, username, password)]).ConfigureAwait(false);
        return decisions[0];
    }

    public Task<IReadOnlyList<CredentialDecision>> AssessBatchAsync(IEnumerable<CredentialAssessmentRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var batch = requests.ToArray();
        return Run<IReadOnlyList<CredentialDecision>>(() =>
        {
            if (batch.Length > MaximumEntries) throw new CredentialVaultException();
            var existing = _entries!.ToDictionary(entry => (entry.Metadata.Origin, entry.Metadata.Username));
            var decisions = new List<CredentialDecision>(batch.Length);
            foreach (var request in batch)
            {
                ValidateInput(request.Origin, request.Username, request.Password);
                existing.TryGetValue((request.Origin, request.Username), out var entry);
                decisions.Add(new CredentialDecision(entry is null ? CredentialChange.Save :
                    Decode(entry) == request.Password ? CredentialChange.Unchanged : CredentialChange.Update, entry?.Metadata));
            }
            return decisions;
        });
    }

    public async Task<CredentialMetadata> SaveAsync(string origin, string username, string password, CredentialMetadata? expected)
    {
        var saved = await SaveBatchAsync([new CredentialSaveRequest(origin, username, password, expected)]).ConfigureAwait(false);
        return saved[0];
    }

    /// <summary>Applies validated credential saves with one durable vault replacement or no replacement.</summary>
    public Task<IReadOnlyList<CredentialMetadata>> SaveBatchAsync(IEnumerable<CredentialSaveRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var batch = requests.ToArray();
        return Run<IReadOnlyList<CredentialMetadata>>(() =>
        {
            if (batch.Length == 0 || batch.Length > MaximumEntries) throw new CredentialVaultException();
            var next = _entries!.ToList();
            var saved = new List<CredentialMetadata>(batch.Length);
            foreach (var request in batch)
            {
                ValidateInput(request.Origin, request.Username, request.Password);
                var existing = next.SingleOrDefault(e => e.Metadata.Origin == request.Origin && e.Metadata.Username == request.Username);
                if (request.Expected is null ? existing is not null : existing is null ||
                    existing.Metadata.Id != request.Expected.Id || existing.Metadata.Revision != request.Expected.Revision)
                    throw new CredentialConflictException();

                var now = DateTimeOffset.UtcNow;
                var metadata = new CredentialMetadata(existing?.Metadata.Id ?? Guid.NewGuid(), request.Origin, request.Username,
                    existing?.Metadata.Created ?? now, now, existing?.Metadata.LastUsed, (existing?.Metadata.Revision ?? 0) + 1);

                var envelope = new Envelope(_vaultId, metadata.Id, metadata.Origin, metadata.Username, metadata.Revision,
                    metadata.Created, metadata.Updated, request.Password);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
                byte[] protectedBytes;
                var entropy = ComputeEntropy(_vaultId, metadata.Id, metadata.Origin, metadata.Username);
                try { protectedBytes = _protector.Protect(bytes, entropy); }
                finally { CryptographicOperations.ZeroMemory(bytes); }

                var replacement = new Entry(metadata, protectedBytes);
                if (existing is null) next.Add(replacement);
                else next[next.IndexOf(existing)] = replacement;
                saved.Add(metadata);
            }
            Write(next);
            return saved;
        });
    }

    public Task<CredentialMetadata> UpdateAsync(Guid id, long revision, string username, string password) => Run(() =>
    {
        var existing = Find(id, revision);
        ValidateInput(existing.Metadata.Origin, username, password);
        if (_entries!.Any(e => e != existing && e.Metadata.Origin == existing.Metadata.Origin && e.Metadata.Username == username))
            throw new CredentialConflictException();

        var metadata = existing.Metadata with
        {
            Username = username,
            Updated = DateTimeOffset.UtcNow,
            Revision = existing.Metadata.Revision + 1
        };
        var envelope = new Envelope(_vaultId, metadata.Id, metadata.Origin, metadata.Username, metadata.Revision,
            metadata.Created, metadata.Updated, password);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        byte[] protectedBytes;
        var entropy = ComputeEntropy(_vaultId, metadata.Id, metadata.Origin, metadata.Username);
        try { protectedBytes = _protector.Protect(bytes, entropy); }
        finally { CryptographicOperations.ZeroMemory(bytes); }

        var replacement = new Entry(metadata, protectedBytes);
        Write(_entries!.Select(e => e == existing ? replacement : e).ToList());
        return metadata;
    });

    public Task<bool> DeleteAsync(Guid id, long revision) => Run(() =>
    {
        var entry = Find(id, revision);
        Write(_entries!.Where(e => e != entry).ToList()); return true;
    });

    public Task<bool> MarkUsedAsync(Guid id, long revision) => Run(() =>
    {
        var entry = Find(id, revision);
        Write(_entries!.Select(e => e == entry ? e with { Metadata = e.Metadata with { LastUsed = DateTimeOffset.UtcNow } } : e).ToList());
        return true;
    });

    private Entry Find(Guid id, long revision) => _entries!.SingleOrDefault(e => e.Metadata.Id == id && e.Metadata.Revision == revision)
        ?? throw new CredentialConflictException();

    private string Decode(Entry entry)
    {
        var m = entry.Metadata;
        var entropy = ComputeEntropy(_vaultId, m.Id, m.Origin, m.Username);
        byte[] bytes;
        try
        {
            bytes = _protector.Unprotect(entry.ProtectedSecret, entropy);
        }
        catch
        {
            throw new CredentialVaultException();
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json) ?? throw new CredentialVaultException();
            if (envelope.VaultId != _vaultId ||
                envelope.Id != m.Id ||
                envelope.Origin != m.Origin ||
                envelope.Username != m.Username ||
                envelope.Revision != m.Revision ||
                envelope.Created != m.Created ||
                envelope.Updated != m.Updated)
            {
                throw new CredentialVaultException();
            }
            ValidateInput(m.Origin, m.Username, envelope.Password);
            return envelope.Password;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool IsValidCredential(string? origin, string? username, string? password)
    {
        return origin is not null && CredentialOrigin.Normalize(origin) == origin && username is not null && username.Length <= 1024 &&
            !username.Any(char.IsControl) && !string.IsNullOrEmpty(password) && password.Length <= 4096;
    }

    private static void ValidateInput(string origin, string username, string password)
    {
        if (!IsValidCredential(origin, username, password)) throw new CredentialVaultException();
    }

    private static byte[] ComputeEntropy(Guid vaultId, Guid id, string origin, string username)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(vaultId.ToString("N") + id.ToString("N") + origin + username));
    }

    private static string ComputeEntriesDigest(IEnumerable<Entry> entries)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sorted = entries.OrderBy(e => e.Metadata.Id.ToString("N"), StringComparer.Ordinal);
        foreach (var entry in sorted)
        {
            var idBytes = Encoding.UTF8.GetBytes(entry.Metadata.Id.ToString("N"));
            var originBytes = Encoding.UTF8.GetBytes(entry.Metadata.Origin);
            var usernameBytes = Encoding.UTF8.GetBytes(entry.Metadata.Username);
            var revBytes = BitConverter.GetBytes(entry.Metadata.Revision);
            if (BitConverter.IsLittleEndian) Array.Reverse(revBytes);
            var secretLengthBytes = BitConverter.GetBytes(entry.ProtectedSecret.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(secretLengthBytes);

            sha.AppendData(idBytes);
            sha.AppendData(originBytes);
            sha.AppendData(usernameBytes);
            sha.AppendData(revBytes);
            sha.AppendData(secretLengthBytes);
            sha.AppendData(entry.ProtectedSecret);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private void Load()
    {
        if (_entries is not null) return;
        Directory.CreateDirectory(_directory);

        if (!File.Exists(_path))
        {
            if (File.Exists(_legacyPath))
            {
                MigrateV1();
                return;
            }

            if (File.Exists(_path + ".bak"))
            {
                var backupDb = Read(_path + ".bak");
                _entries = backupDb.Entries;
                _vaultId = backupDb.VaultId;
                _vaultRevision = backupDb.VaultRevision;
                _readOnly = true;
                return;
            }

            _entries = [];
            _vaultId = Guid.NewGuid();
            _vaultRevision = 0;
            return;
        }

        try
        {
            var db = Read(_path);

            if (!File.Exists(_anchorPath))
            {
                throw new RollbackDetectedException();
            }

            var anchor = ReadAnchor(_anchorPath);
            if (db.VaultId != anchor.VaultId || db.VaultRevision < anchor.HighWaterRevision)
            {
                throw new RollbackDetectedException();
            }

            if (db.VaultRevision == anchor.HighWaterRevision &&
                anchor.EntriesDigest is not null &&
                !string.Equals(db.EntriesDigest, anchor.EntriesDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new RollbackDetectedException();
            }

            if (db.VaultRevision > anchor.HighWaterRevision)
            {
                WriteAnchor(db.VaultId, db.VaultRevision, db.EntriesDigest);
            }

            _entries = db.Entries;
            _vaultId = db.VaultId;
            _vaultRevision = db.VaultRevision;
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (RollbackDetectedException)
        {
            throw new CredentialVaultException();
        }
        catch
        {
            if (!File.Exists(_path + ".bak")) throw new CredentialVaultException();

            try
            {
                var backupDb = Read(_path + ".bak");
                _entries = backupDb.Entries;
                _vaultId = backupDb.VaultId;
                _vaultRevision = backupDb.VaultRevision;
                _readOnly = true;
            }
            catch
            {
                throw new CredentialVaultException();
            }
        }
    }

    private static Database Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new CredentialVaultException();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (!root.TryGetProperty("SchemaVersion", out var version) || !version.TryGetInt32(out int number))
            throw new CredentialVaultException();
        if (number != 2) throw new NotSupportedException();

        var db = root.Deserialize<Database>(Json) ?? throw new CredentialVaultException();
        if (db.VaultId == Guid.Empty || db.VaultRevision < 1 || string.IsNullOrWhiteSpace(db.EntriesDigest) ||
            db.Entries is null || db.Entries.Count > MaximumEntries)
            throw new RollbackDetectedException();

        var computedDigest = ComputeEntriesDigest(db.Entries);
        if (!string.Equals(db.EntriesDigest, computedDigest, StringComparison.OrdinalIgnoreCase))
            throw new RollbackDetectedException();

        var ids = new HashSet<Guid>();
        var keys = new HashSet<(string, string)>();
        foreach (var entry in db.Entries)
        {
            var m = entry?.Metadata ?? throw new RollbackDetectedException();
            ValidateInput(m.Origin, m.Username, "validation");
            if (m.Id == Guid.Empty || m.Revision < 1 || m.Created == default || m.Updated < m.Created ||
                !ids.Add(m.Id) || !keys.Add((m.Origin, m.Username)) || entry!.ProtectedSecret is not { Length: > 0 and <= 65536 })
                throw new RollbackDetectedException();
        }
        return db;
    }

    private static VaultAnchor ReadAnchor(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new CredentialVaultException();
        return JsonSerializer.Deserialize<VaultAnchor>(stream, Json) ?? throw new CredentialVaultException();
    }

    private void WriteAnchor(Guid vaultId, long revision, string entriesDigest)
    {
        var anchor = new VaultAnchor(vaultId, revision, entriesDigest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(anchor, Json);
        string temp = _anchorPath + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            if (File.Exists(_anchorPath)) File.Replace(temp, _anchorPath, null);
            else File.Move(temp, _anchorPath);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static List<Entry> ReadV1(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new CredentialVaultException();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (!root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int number))
            throw new CredentialVaultException();
        if (number != 1) throw new NotSupportedException();
        var db = root.Deserialize<V1Database>(Json) ?? throw new CredentialVaultException();
        if (db.Entries is null || db.Entries.Count > MaximumEntries) throw new CredentialVaultException();

        var ids = new HashSet<Guid>();
        var keys = new HashSet<(string, string)>();
        foreach (var entry in db.Entries)
        {
            var m = entry?.Metadata ?? throw new CredentialVaultException();
            ValidateInput(m.Origin, m.Username, "validation");
            if (m.Id == Guid.Empty || m.Revision < 1 || m.Created == default || m.Updated < m.Created ||
                !ids.Add(m.Id) || !keys.Add((m.Origin, m.Username)) || entry!.ProtectedSecret is not { Length: > 0 and <= 65536 })
                throw new CredentialVaultException();
        }
        return db.Entries;
    }

    private void MigrateV1()
    {
        var v1Entries = ReadV1(_legacyPath);
        var vaultId = Guid.NewGuid();
        long vaultRevision = 1;
        var now = DateTimeOffset.UtcNow;

        var v2Entries = new List<Entry>(v1Entries.Count);
        foreach (var v1Entry in v1Entries)
        {
            var m = v1Entry.Metadata;
            byte[] decryptedBytes;
            try
            {
                decryptedBytes = _protector.Unprotect(v1Entry.ProtectedSecret, entropy: null);
            }
            catch
            {
                throw new CredentialVaultException();
            }

            string password;
            try
            {
                var v1Envelope = JsonSerializer.Deserialize<V1Envelope>(decryptedBytes, Json) ?? throw new CredentialVaultException();
                if (v1Envelope.Id != m.Id || v1Envelope.Origin != m.Origin || v1Envelope.Username != m.Username || v1Envelope.Revision != m.Revision)
                    throw new CredentialVaultException();
                password = v1Envelope.Password;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decryptedBytes);
            }

            var v2Envelope = new Envelope(vaultId, m.Id, m.Origin, m.Username, m.Revision, m.Created, m.Updated, password);
            var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(v2Envelope, Json);
            byte[] protectedBytes;
            var entropy = ComputeEntropy(vaultId, m.Id, m.Origin, m.Username);
            try
            {
                protectedBytes = _protector.Protect(envelopeBytes, entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
            }

            v2Entries.Add(new Entry(m, protectedBytes));
        }

        var digest = ComputeEntriesDigest(v2Entries);
        var db = new Database(2, vaultId, vaultRevision, now, digest, v2Entries);
        var dbBytes = JsonSerializer.SerializeToUtf8Bytes(db, Json);

        string tempDb = _path + ".tmp";
        try
        {
            using (var fs = new FileStream(tempDb, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(dbBytes);
                fs.Flush(true);
            }
            File.Move(tempDb, _path);
            WriteAnchor(vaultId, vaultRevision, digest);

            File.Move(_legacyPath, _migratedPath, true);
            if (File.Exists(_legacyPath + ".bak"))
            {
                File.Move(_legacyPath + ".bak", _migratedPath + ".bak", true);
            }

            _entries = v2Entries;
            _vaultId = vaultId;
            _vaultRevision = vaultRevision;
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    private void Write(List<Entry> next)
    {
        if (_readOnly || next.Count > MaximumEntries) throw new CredentialVaultException();
        if (_vaultId == Guid.Empty) _vaultId = Guid.NewGuid();
        var nextRevision = _vaultRevision + 1;
        var now = DateTimeOffset.UtcNow;
        var digest = ComputeEntriesDigest(next);
        var db = new Database(2, _vaultId, nextRevision, now, digest, next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(db, Json);
        if (bytes.Length > MaximumBytes) throw new CredentialVaultException();

        string temporary = _path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(true);
            }

            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", true);
            else File.Move(temporary, _path);

            WriteAnchor(_vaultId, nextRevision, digest);

            _entries = next;
            _vaultRevision = nextRevision;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
