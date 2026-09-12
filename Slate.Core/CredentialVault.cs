using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slate.Core;

public interface ICredentialProtector
{
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
}
public sealed class CredentialConflictException : Exception
{
    public CredentialConflictException() : base("This credential changed. Review it again before saving.") { }
}

/// <summary>Platform-independent storage/policy. The application supplies Windows protection.</summary>
public sealed class CredentialVault(string directory, ICredentialProtector protector)
{
    private sealed record Entry(CredentialMetadata Metadata, byte[] ProtectedSecret);
    private sealed record Database(int Version, List<Entry> Entries);
    private sealed record Envelope(Guid Id, string Origin, string Username, long Revision, string Password);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(directory, "credentials.v1.json");
    private List<Entry>? _entries;
    private bool _readOnly;
    public bool RecoveredFromBackup => _readOnly;
    private const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumEntries = 10_000;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };

    private async Task<T> Run<T>(Func<T> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { Load(); return operation(); }
                catch (CredentialConflictException) { throw; }
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
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new Envelope(metadata.Id, metadata.Origin, metadata.Username, metadata.Revision, request.Password), Json);
                byte[] protectedBytes;
                try { protectedBytes = protector.Protect(bytes); }
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new Envelope(metadata.Id, metadata.Origin, metadata.Username, metadata.Revision, password), Json);
        byte[] protectedBytes;
        try { protectedBytes = protector.Protect(bytes); }
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
        var bytes = protector.Unprotect(entry.ProtectedSecret);
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json) ?? throw new CredentialVaultException();
            var m = entry.Metadata;
            if (envelope.Id != m.Id || envelope.Origin != m.Origin || envelope.Username != m.Username || envelope.Revision != m.Revision)
                throw new CredentialVaultException();
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

    private void Load()
    {
        if (_entries is not null) return;
        Directory.CreateDirectory(directory);
        if (!File.Exists(_path))
        {
            if (File.Exists(_path + ".bak")) { _entries = Read(_path + ".bak"); _readOnly = true; }
            else _entries = [];
            return;
        }
        try { _entries = Read(_path); }
        catch (NotSupportedException) { throw; } // Never roll an unknown future version back to an older backup.
        catch
        {
            if (!File.Exists(_path + ".bak")) throw;
            _entries = Read(_path + ".bak"); _readOnly = true;
        }
    }

    private static List<Entry> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new CredentialVaultException();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 12 });
        if (!document.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int number)) throw new CredentialVaultException();
        if (number != 1) throw new NotSupportedException(); // Check before deserializing future schema fields.
        var db = document.RootElement.Deserialize<Database>(Json) ?? throw new CredentialVaultException();
        if (db.Entries is null || db.Entries.Count > MaximumEntries) throw new CredentialVaultException();
        var ids = new HashSet<Guid>(); var keys = new HashSet<(string, string)>();
        foreach (var entry in db.Entries)
        {
            var m = entry?.Metadata ?? throw new CredentialVaultException();
            ValidateInput(m.Origin, m.Username, "validation");
            if (m.Id == Guid.Empty || m.Revision < 1 || m.Created == default || m.Updated < m.Created ||
                !ids.Add(m.Id) || !keys.Add((m.Origin, m.Username)) || entry!.ProtectedSecret is not { Length: > 0 and <= 65536 }) throw new CredentialVaultException();
        }
        return db.Entries;
    }

    private void Write(List<Entry> next)
    {
        if (_readOnly || next.Count > MaximumEntries) throw new CredentialVaultException();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Database(1, next), Json); // Ciphertext only.
        if (bytes.Length > MaximumBytes) throw new CredentialVaultException();
        string temporary = _path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", true);
            else File.Move(temporary, _path);
            _entries = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
