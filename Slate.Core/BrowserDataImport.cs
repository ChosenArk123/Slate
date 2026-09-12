using System.Text;

namespace Slate.Core;

[Flags]
public enum BrowserDataKinds
{
    None = 0,
    Passwords = 1 << 0,
    Bookmarks = 1 << 1,
    History = 1 << 2,
    Settings = 1 << 3
}

public sealed record BrowserDataImportDescriptor(
    string Id,
    string DisplayName,
    string Description,
    BrowserDataKinds Kinds,
    IReadOnlyList<string> SupportedExtensions,
    bool IsAvailable,
    string? UnavailableReason = null);

public interface IBrowserDataImportFormat
{
    BrowserDataImportDescriptor Descriptor { get; }
    bool CanImport(IReadOnlyList<string> headers);
    BrowserDataImportPackage Parse(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows);
}

public sealed class ImportedCredential(string origin, string username, string password, int sourceRow)
{
    public string Origin { get; } = origin;
    public string Username { get; } = username;
    public string Password { get; private set; } = password;
    public int SourceRow { get; } = sourceRow;
    internal void ClearSecret() => Password = "";
}

public sealed class BrowserDataImportPackage(
    string sourceName,
    BrowserDataKinds availableKinds,
    IReadOnlyList<ImportedCredential> credentials,
    int rejectedRows,
    int duplicateRows,
    int conflictingRows,
    int invalidUrlRows = 0,
    int missingUsernameRows = 0,
    int missingPasswordRows = 0,
    int unsupportedRows = 0) : IDisposable
{
    public string SourceName { get; } = sourceName;
    public BrowserDataKinds AvailableKinds { get; } = availableKinds;
    public IReadOnlyList<ImportedCredential> Credentials { get; } = credentials;
    public int RejectedRows { get; } = rejectedRows;
    public int DuplicateRows { get; } = duplicateRows;
    public int ConflictingRows { get; } = conflictingRows;
    public int InvalidUrlRows { get; } = invalidUrlRows;
    public int MissingUsernameRows { get; } = missingUsernameRows;
    public int MissingPasswordRows { get; } = missingPasswordRows;
    public int UnsupportedRows { get; } = unsupportedRows;
    public int TotalRows => Credentials.Count + RejectedRows + DuplicateRows + ConflictingRows;
    public void Dispose()
    {
        foreach (var credential in Credentials) credential.ClearSecret();
    }
}

public sealed class BrowserDataImportException : Exception
{
    public BrowserDataImportException(string message) : base(message) { }
    public BrowserDataImportException(string message, Exception inner) : base(message, inner) { }
}

public sealed record BrowserDataImportResult
{
    public int TotalRows { get; }
    public int Total => TotalRows;
    public int Imported { get; }
    public int Skipped { get; }
    public int Duplicates { get; }
    public int Conflicts { get; }
    public int Invalid { get; }
    public int Failed { get; }
    public BrowserDataKinds Kinds { get; }
    public string? SourceName { get; }
    public bool HasChanges => Imported > 0;
    public bool HasIssues => Conflicts > 0 || Invalid > 0 || Failed > 0;

    public BrowserDataImportResult(int totalRows, int imported, int skipped, int duplicates, int conflicts,
        int invalid, int failed, BrowserDataKinds kinds = BrowserDataKinds.Passwords, string? sourceName = null)
    {
        if (new[] { totalRows, imported, skipped, duplicates, conflicts, invalid, failed }.Any(value => value < 0))
            throw new ArgumentOutOfRangeException(nameof(totalRows), "Import result counts cannot be negative.");
        if (kinds == BrowserDataKinds.None || (kinds & ~BrowserDataImportValidation.SupportedKinds) != 0)
            throw new ArgumentOutOfRangeException(nameof(kinds), "Import result kinds are not supported.");
        int classified;
        try { classified = checked(imported + skipped + duplicates + conflicts + invalid + failed); }
        catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(totalRows), "Import result counts are too large."); }
        if (totalRows != classified)
            throw new ArgumentException("Total rows must equal the sum of all import outcomes.", nameof(totalRows));

        TotalRows = totalRows; Imported = imported; Skipped = skipped; Duplicates = duplicates;
        Conflicts = conflicts; Invalid = invalid; Failed = failed; Kinds = kinds; SourceName = sourceName;
    }

    public static BrowserDataImportResult Aggregate(IEnumerable<BrowserDataImportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var items = results.ToArray();
        if (items.Length == 0) return new(0, 0, 0, 0, 0, 0, 0, BrowserDataKinds.Passwords);
        try
        {
            return new BrowserDataImportResult(
                items.Sum(item => checked(item.TotalRows)),
                items.Sum(item => checked(item.Imported)),
                items.Sum(item => checked(item.Skipped)),
                items.Sum(item => checked(item.Duplicates)),
                items.Sum(item => checked(item.Conflicts)),
                items.Sum(item => checked(item.Invalid)),
                items.Sum(item => checked(item.Failed)),
                items.Aggregate(BrowserDataKinds.None, (kinds, item) => kinds | item.Kinds),
                items.Select(item => item.SourceName).Distinct(StringComparer.Ordinal).Count() == 1 ? items[0].SourceName : null);
        }
        catch (OverflowException ex) { throw new BrowserDataImportException("Import result totals are too large.", ex); }
    }
}

/// <summary>A format adapter. Future bookmark, history, or settings formats plug into the same registry.</summary>
public interface IBrowserDataImportParser
{
    string Name { get; }
    BrowserDataKinds SupportedKinds { get; }
    bool Matches(IReadOnlyList<string> headers);
    BrowserDataImportPackage Parse(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows);
}

public sealed class BrowserDataImportService
{
    public const long MaximumImportBytes = 16 * 1024 * 1024;
    public const int MaximumRows = 10_000;
    public const int MaximumColumns = 64;
    public const int MaximumFieldCharacters = 65_536;
    public const int MaximumRecordCharacters = 262_144;
    public const int MaximumUrlCharacters = 8_192;
    public const int MaximumUsernameCharacters = 1_024;
    public const int MaximumPasswordCharacters = 4_096;
    private readonly IReadOnlyList<IBrowserDataImportFormat> _importers;

    public static IReadOnlyList<BrowserDataImportDescriptor> GetRegisteredDescriptors() =>
    [
        new BrowserDataImportDescriptor(
            "passwords-csv",
            "Passwords (CSV)",
            "Import credentials from Chrome, Edge, or Firefox password export files",
            BrowserDataKinds.Passwords,
            [".csv"],
            IsAvailable: true),
        new BrowserDataImportDescriptor(
            "bookmarks-html",
            "Bookmarks (HTML)",
            "Import bookmarks from Netscape HTML bookmark backups",
            BrowserDataKinds.Bookmarks,
            [".html", ".htm"],
            IsAvailable: false,
            UnavailableReason: "Bookmarks import will be available in a future release."),
        new BrowserDataImportDescriptor(
            "history-json",
            "Browsing history",
            "Import browsing history from supported browser exports",
            BrowserDataKinds.History,
            [".json"],
            IsAvailable: false,
            UnavailableReason: "History import will be available in a future release."),
        new BrowserDataImportDescriptor(
            "settings-json",
            "Settings & preferences",
            "Import browser preferences and search engine settings",
            BrowserDataKinds.Settings,
            [".json"],
            IsAvailable: false,
            UnavailableReason: "Settings import will be available in a future release.")
    ];

    public BrowserDataImportService(IEnumerable<IBrowserDataImportFormat>? importers = null)
    {
        _importers = (importers ?? [new BrowserPasswordCsvParser()]).ToArray();
        if (_importers.Count == 0) throw new ArgumentException("At least one browser-data importer is required.", nameof(importers));
    }

    public BrowserDataImportService(IEnumerable<IBrowserDataImportParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        var list = new List<IBrowserDataImportFormat>();
        foreach (var parser in parsers)
        {
            if (parser is IBrowserDataImportFormat importer) list.Add(importer);
            else list.Add(new ParserAdapter(parser));
        }
        if (list.Count == 0) throw new ArgumentException("At least one browser-data parser is required.", nameof(parsers));
        _importers = list;
    }

    private sealed class ParserAdapter(IBrowserDataImportParser parser) : IBrowserDataImportFormat
    {
        public BrowserDataImportDescriptor Descriptor { get; } = new(
            parser.Name.ToLowerInvariant().Replace(' ', '-'),
            parser.Name,
            parser.Name,
            parser.SupportedKinds,
            [".csv"],
            IsAvailable: true);
        public bool CanImport(IReadOnlyList<string> headers) => parser.Matches(headers);
        public BrowserDataImportPackage Parse(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows) =>
            parser.Parse(headers, rows);
    }

    public BrowserDataImportPackage ParseCsv(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var records = ReadCsv(reader);
        if (records.Count == 0) throw new BrowserDataImportException("The selected CSV file is empty.");
        var headers = records[0];
        if (headers.Count > 0) headers[0] = headers[0].TrimStart('\uFEFF');
        var importer = _importers.FirstOrDefault(candidate => candidate.CanImport(headers));
        if (importer is null)
        {
            if (_importers.OfType<BrowserPasswordCsvParser>().Any(p => p.HasDuplicateColumnMappings(headers)))
                throw new BrowserDataImportException("The CSV file has missing or duplicate password columns.");
            throw new BrowserDataImportException("This is not a supported browser password CSV export.");
        }
        return importer.Parse(headers, records.Skip(1).Cast<IReadOnlyList<string>>().ToArray());
    }

    public BrowserDataImportPackage ParseCsv(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new BrowserDataImportException("The selected CSV file cannot be read.");
        try
        {
            if (stream.CanSeek && stream.Length - stream.Position > MaximumImportBytes)
                throw new BrowserDataImportException("The CSV file exceeds the import size limit.");
            using var limited = new SizeLimitedReadStream(stream, MaximumImportBytes);
            using var reader = new StreamReader(limited, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            return ParseCsv(reader);
        }
        catch (DecoderFallbackException)
        {
            throw new BrowserDataImportException("The selected CSV file is not valid UTF-8.");
        }
    }

    private sealed class SizeLimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _remaining = maximumBytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int requested = (int)Math.Min(buffer.Length, _remaining + 1);
            int read = inner.Read(buffer[..requested]);
            _remaining -= read;
            if (_remaining < 0) throw new BrowserDataImportException("The CSV file exceeds the import size limit.");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }

    private static List<List<string>> ReadCsv(TextReader reader)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, afterQuote = false, isRowMalformed = false;
        bool malformedInQuotedField = false;
        int recordCharacters = 0;
        long totalCharacters = 0;

        void MarkMalformed()
        {
            isRowMalformed = true;
            malformedInQuotedField = quoted;
            quoted = false;
            field.Clear();
            row.Clear();
            afterQuote = false;
        }

        void AddCharacter(char value)
        {
            if (field.Length >= MaximumFieldCharacters || ++recordCharacters > MaximumRecordCharacters)
            {
                MarkMalformed();
                return;
            }
            field.Append(value);
        }

        void EndField()
        {
            if (row.Count >= MaximumColumns)
            {
                MarkMalformed();
                return;
            }
            row.Add(field.ToString());
            field.Clear();
            afterQuote = false;
        }

        void EndRecord()
        {
            if (isRowMalformed)
            {
                if (records.Count > MaximumRows) throw new BrowserDataImportException($"The CSV file exceeds the {MaximumRows:N0}-row import limit.");
                records.Add([]);
                isRowMalformed = false;
                malformedInQuotedField = false;
                quoted = false;
                afterQuote = false;
                recordCharacters = 0;
                field.Clear();
                row = [];
                return;
            }

            EndField();
            if (isRowMalformed)
            {
                if (records.Count > MaximumRows) throw new BrowserDataImportException($"The CSV file exceeds the {MaximumRows:N0}-row import limit.");
                records.Add([]);
                isRowMalformed = false;
                malformedInQuotedField = false;
                quoted = false;
                afterQuote = false;
                recordCharacters = 0;
                field.Clear();
                row = [];
                return;
            }

            if (records.Count > MaximumRows) throw new BrowserDataImportException($"The CSV file exceeds the {MaximumRows:N0}-row import limit.");
            records.Add(row);
            row = [];
            recordCharacters = 0;
        }

        while (reader.Read() is int code && code >= 0)
        {
            if (++totalCharacters > MaximumImportBytes)
                throw new BrowserDataImportException("The CSV file exceeds the import size limit.");
            char value = (char)code;

            if (value == '\uFEFF' && records.Count == 0 && row.Count == 0 && field.Length == 0)
            {
                continue;
            }

            if (isRowMalformed)
            {
                if (malformedInQuotedField)
                {
                    if (value == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            reader.Read();
                            if (++totalCharacters > MaximumImportBytes) throw new BrowserDataImportException("The CSV file exceeds the import size limit.");
                        }
                        else
                        {
                            malformedInQuotedField = false;
                        }
                    }
                    continue;
                }

                if (value is '\r' or '\n')
                {
                    if (value == '\r' && reader.Peek() == '\n')
                    {
                        reader.Read();
                        if (++totalCharacters > MaximumImportBytes) throw new BrowserDataImportException("The CSV file exceeds the import size limit.");
                    }
                    EndRecord();
                }
                continue;
            }

            if (quoted)
            {
                if (value == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        if (++totalCharacters > MaximumImportBytes) throw new BrowserDataImportException("The CSV file exceeds the import size limit.");
                        AddCharacter('"');
                    }
                    else { quoted = false; afterQuote = true; }
                }
                else AddCharacter(value);
                continue;
            }

            if (afterQuote && value is not (',' or '\r' or '\n'))
            {
                MarkMalformed();
                continue;
            }

            if (value == '"')
            {
                if (field.Length == 1 && field[0] == '\uFEFF')
                {
                    field.Clear();
                }
                else if (field.Length != 0)
                {
                    MarkMalformed();
                    continue;
                }
                quoted = true;
            }
            else if (value == ',') EndField();
            else if (value is '\r' or '\n')
            {
                if (value == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                    if (++totalCharacters > MaximumImportBytes) throw new BrowserDataImportException("The CSV file exceeds the import size limit.");
                }
                EndRecord();
            }
            else AddCharacter(value);
        }

        if (isRowMalformed)
        {
            if (records.Count > MaximumRows) throw new BrowserDataImportException($"The CSV file exceeds the {MaximumRows:N0}-row import limit.");
            records.Add([]);
        }
        else if (quoted)
        {
            throw new BrowserDataImportException("The CSV file ends inside a quoted field.");
        }
        else if (field.Length > 0 || row.Count > 0 || afterQuote)
        {
            EndRecord();
        }
        return records;
    }
}

public sealed class BrowserPasswordCsvParser : IBrowserDataImportFormat, IBrowserDataImportParser
{
    private static readonly HashSet<string> UrlAliases = new(StringComparer.Ordinal)
    {
        "url", "origin", "website", "web site", "login url", "login_uri", "page url", "action url", "host"
    };

    private static readonly HashSet<string> UsernameAliases = new(StringComparer.Ordinal)
    {
        "username", "username_value", "login", "user", "email", "account", "user name"
    };

    private static readonly HashSet<string> PasswordAliases = new(StringComparer.Ordinal)
    {
        "password", "password_value", "pass", "pwd", "secret"
    };

    public static readonly BrowserDataImportDescriptor DefaultDescriptor = new(
        "passwords-csv",
        "Passwords (CSV)",
        "Import credentials from Chrome, Edge, or Firefox password export files",
        BrowserDataKinds.Passwords,
        [".csv"],
        IsAvailable: true);

    public BrowserDataImportDescriptor Descriptor => DefaultDescriptor;
    public string Name => "Chromium or Firefox password CSV";
    public BrowserDataKinds SupportedKinds => BrowserDataKinds.Passwords;

    internal static bool TryResolveColumns(
        IReadOnlyList<string> headers,
        out int urlColumn,
        out int usernameColumn,
        out int passwordColumn,
        out bool hasDuplicateMapping)
    {
        urlColumn = -1;
        usernameColumn = -1;
        passwordColumn = -1;
        hasDuplicateMapping = false;

        var urlIndices = new List<int>();
        var userIndices = new List<int>();
        var passIndices = new List<int>();

        for (int i = 0; i < headers.Count; i++)
        {
            string name = headers[i].Trim().ToLowerInvariant();
            if (UrlAliases.Contains(name)) urlIndices.Add(i);
            else if (UsernameAliases.Contains(name)) userIndices.Add(i);
            else if (PasswordAliases.Contains(name)) passIndices.Add(i);
        }

        if (urlIndices.Count > 1 || userIndices.Count > 1 || passIndices.Count > 1)
        {
            hasDuplicateMapping = true;
            return false;
        }

        if (urlIndices.Count == 1 && userIndices.Count == 1 && passIndices.Count == 1)
        {
            urlColumn = urlIndices[0];
            usernameColumn = userIndices[0];
            passwordColumn = passIndices[0];
            return true;
        }

        return false;
    }

    internal bool HasDuplicateColumnMappings(IReadOnlyList<string> headers)
    {
        TryResolveColumns(headers, out _, out _, out _, out bool duplicate);
        return duplicate;
    }

    public bool CanImport(IReadOnlyList<string> headers) =>
        TryResolveColumns(headers, out _, out _, out _, out _);

    public bool Matches(IReadOnlyList<string> headers) => CanImport(headers);

    public BrowserDataImportPackage Parse(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (!TryResolveColumns(headers, out int urlColumn, out int usernameColumn, out int passwordColumn, out _))
            throw new BrowserDataImportException("The CSV file has missing or duplicate password columns.");

        var headerSet = headers.Select(h => h.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        string source = headerSet.Contains("guid") || headerSet.Contains("timepasswordchanged") || headerSet.Contains("httprealm")
            ? "Firefox password CSV"
            : headerSet.Contains("name") || headerSet.Contains("note")
                ? "Chrome or Edge password CSV"
                : "Browser password CSV";
        int highestRequired = Math.Max(urlColumn, Math.Max(usernameColumn, passwordColumn));
        var accepted = new Dictionary<(string Origin, string Username), ImportedCredential>();
        var conflicted = new HashSet<(string Origin, string Username)>();
        int rejected = 0, duplicates = 0, conflicts = 0;
        int invalidUrls = 0, missingUsernames = 0, missingPasswords = 0, unsupported = 0;

        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Count <= highestRequired) { rejected++; unsupported++; continue; }
            string url = row[urlColumn];
            string username = row[usernameColumn];
            string password = row[passwordColumn];
            if (string.IsNullOrWhiteSpace(url)) { rejected++; invalidUrls++; continue; }
            if (username.Length == 0) missingUsernames++;
            if (password.Length == 0) { rejected++; missingPasswords++; continue; }
            if (url.Length > BrowserDataImportService.MaximumUrlCharacters ||
                username.Length > BrowserDataImportService.MaximumUsernameCharacters || username.Any(char.IsControl) ||
                password.Length > BrowserDataImportService.MaximumPasswordCharacters)
            {
                rejected++; unsupported++;
                continue;
            }
            string? origin = CredentialOrigin.Normalize(url);
            if (origin is null) { rejected++; invalidUrls++; continue; }

            var key = (origin, username);
            if (conflicted.Contains(key)) { conflicts++; continue; }
            if (accepted.TryGetValue(key, out var prior))
            {
                if (prior.Password == password) duplicates++;
                else
                {
                    prior.ClearSecret();
                    accepted.Remove(key);
                    conflicted.Add(key);
                    conflicts += 2;
                }
                continue;
            }
            accepted.Add(key, new ImportedCredential(origin, username, password, index + 2));
        }

        return new BrowserDataImportPackage(source, BrowserDataKinds.Passwords, accepted.Values.ToArray(), rejected, duplicates, conflicts,
            invalidUrls, missingUsernames, missingPasswords, unsupported);
    }
}

public sealed record CredentialImportReview(
    int Total, int ReadyToImport, int Duplicates, int Conflicts, int Invalid,
    int InvalidUrls, int MissingUsernames, int MissingPasswords, int UnsupportedRows);

/// <summary>Reviews and applies password imports through the ordinary revision-bound vault path.</summary>
public sealed class CredentialImportCoordinator(CredentialVault vault)
{
    public async Task<CredentialImportReview> ReviewAsync(BrowserDataImportPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var decisions = await vault.AssessBatchAsync(package.Credentials.Select(item =>
            new CredentialAssessmentRequest(item.Origin, item.Username, item.Password))).ConfigureAwait(false);
        return new CredentialImportReview(
            package.TotalRows,
            decisions.Count(decision => decision.Change == CredentialChange.Save),
            package.DuplicateRows + decisions.Count(decision => decision.Change == CredentialChange.Unchanged),
            package.ConflictingRows + decisions.Count(decision => decision.Change == CredentialChange.Update),
            package.RejectedRows,
            package.InvalidUrlRows,
            package.MissingUsernameRows,
            package.MissingPasswordRows,
            package.UnsupportedRows);
    }

    public async Task<BrowserDataImportResult> ImportAsync(BrowserDataImportPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        int imported = 0, duplicates = package.DuplicateRows, conflicts = package.ConflictingRows, failed = 0;
        foreach (var item in package.Credentials)
        {
            try
            {
                var decision = await vault.AssessAsync(item.Origin, item.Username, item.Password).ConfigureAwait(false);
                if (decision.Change == CredentialChange.Unchanged) { duplicates++; continue; }
                if (decision.Change == CredentialChange.Update) { conflicts++; continue; }
                try
                {
                    await vault.SaveAsync(item.Origin, item.Username, item.Password, expected: null).ConfigureAwait(false);
                    imported++;
                }
                catch (CredentialConflictException)
                {
                    var raced = await vault.AssessAsync(item.Origin, item.Username, item.Password).ConfigureAwait(false);
                    if (raced.Change == CredentialChange.Unchanged) duplicates++;
                    else if (raced.Change == CredentialChange.Update) conflicts++;
                    else failed++;
                }
            }
            catch (CredentialConflictException) { conflicts++; }
            catch (CredentialVaultException) { failed++; }
        }
        return new BrowserDataImportResult(package.TotalRows, imported, 0, duplicates, conflicts,
            package.RejectedRows, failed, BrowserDataKinds.Passwords, package.SourceName);
    }
}

