namespace Slate.Core;

public sealed record BrowserDataImportRequest(string ImporterId, string Source, BrowserDataKinds Kinds);

public sealed class BrowserDataImportValidationResult
{
    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public BrowserDataImportValidationResult(IEnumerable<string>? errors = null)
    {
        Errors = (errors ?? []).Where(error => !string.IsNullOrWhiteSpace(error))
            .Select(error => error.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    }
}

/// <summary>An import operation. Format parsing and destination-specific application stay behind this boundary.</summary>
public interface IBrowserDataImporter
{
    BrowserDataImportDescriptor Descriptor { get; }
    BrowserDataImportValidationResult Validate(BrowserDataImportRequest request);
    Task<BrowserDataImportResult> ImportAsync(BrowserDataImportRequest request, CancellationToken cancellationToken = default);
}

public static class BrowserDataImportValidation
{
    public const BrowserDataKinds SupportedKinds = BrowserDataKinds.Passwords | BrowserDataKinds.Bookmarks |
        BrowserDataKinds.History | BrowserDataKinds.Settings;

    public static BrowserDataImportValidationResult ValidateDescriptor(BrowserDataImportDescriptor? descriptor)
    {
        var errors = new List<string>();
        if (descriptor is null) return new(["The importer descriptor is required."]);
        if (string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.Id.Length > 64 ||
            descriptor.Id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            errors.Add("The importer identifier is invalid.");
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName) || descriptor.DisplayName.Length > 128)
            errors.Add("The importer display name is invalid.");
        if (string.IsNullOrWhiteSpace(descriptor.Description) || descriptor.Description.Length > 512)
            errors.Add("The importer description is invalid.");
        if (descriptor.Kinds == BrowserDataKinds.None || (descriptor.Kinds & ~SupportedKinds) != 0)
            errors.Add("The importer declares unsupported data kinds.");
        if (descriptor.SupportedExtensions is null || descriptor.SupportedExtensions.Any(extension =>
                string.IsNullOrWhiteSpace(extension) || extension[0] != '.' || extension.Length > 16 ||
                extension.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '.'))))
            errors.Add("The importer declares an invalid file extension.");
        else if (descriptor.SupportedExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != descriptor.SupportedExtensions.Count)
            errors.Add("The importer declares duplicate file extensions.");
        if (descriptor.IsAvailable ? !string.IsNullOrWhiteSpace(descriptor.UnavailableReason) : string.IsNullOrWhiteSpace(descriptor.UnavailableReason))
            errors.Add(descriptor.IsAvailable ? "An available importer cannot have an unavailable reason." : "An unavailable importer requires a reason.");
        return new(errors);
    }

    public static BrowserDataImportValidationResult ValidateRequest(
        BrowserDataImportRequest? request, BrowserDataImportDescriptor? descriptor)
    {
        var errors = new List<string>();
        if (request is null) return new(["The import request is required."]);
        if (descriptor is null)
        {
            errors.Add("The selected importer is not registered.");
            return new(errors);
        }
        if (!string.Equals(request.ImporterId, descriptor.Id, StringComparison.Ordinal))
            errors.Add("The import request does not match the selected importer.");
        if (!descriptor.IsAvailable) errors.Add(descriptor.UnavailableReason ?? "The selected importer is unavailable.");
        if (request.Kinds == BrowserDataKinds.None || (request.Kinds & ~SupportedKinds) != 0 ||
            (request.Kinds & ~descriptor.Kinds) != 0)
            errors.Add("The requested data kinds are not supported by this importer.");
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 32_767)
            errors.Add("An import source is required.");
        else if (descriptor.SupportedExtensions.Count > 0)
        {
            string extension;
            try { extension = Path.GetExtension(request.Source); }
            catch { extension = ""; }
            if (!descriptor.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                errors.Add("The import source type is not supported by this importer.");
        }
        return new(errors);
    }
}

/// <summary>Small registry and coordinator for validated browser-data import operations.</summary>
public sealed class BrowserDataImportCoordinator
{
    private readonly IReadOnlyDictionary<string, IBrowserDataImporter> _importers;
    public IReadOnlyList<BrowserDataImportDescriptor> Descriptors { get; }

    public BrowserDataImportCoordinator(IEnumerable<IBrowserDataImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(importers);
        var items = importers.ToArray();
        if (items.Length == 0) throw new ArgumentException("At least one browser-data importer is required.", nameof(importers));
        foreach (var importer in items)
        {
            if (importer is null) throw new ArgumentException("Importers cannot contain null entries.", nameof(importers));
            var validation = BrowserDataImportValidation.ValidateDescriptor(importer.Descriptor);
            if (!validation.IsValid) throw new ArgumentException(validation.Errors[0], nameof(importers));
        }
        if (items.Select(importer => importer.Descriptor.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Importer identifiers must be unique.", nameof(importers));
        _importers = items.ToDictionary(importer => importer.Descriptor.Id, StringComparer.Ordinal);
        Descriptors = items.Select(importer => importer.Descriptor).OrderBy(descriptor => descriptor.DisplayName, StringComparer.Ordinal).ToArray();
    }

    public BrowserDataImportValidationResult Validate(BrowserDataImportRequest? request)
    {
        _importers.TryGetValue(request?.ImporterId ?? "", out var importer);
        var common = BrowserDataImportValidation.ValidateRequest(request, importer?.Descriptor);
        if (!common.IsValid || importer is null || request is null) return common;
        BrowserDataImportValidationResult specific;
        try { specific = importer.Validate(request) ?? new(["The importer returned an invalid validation result."]); }
        catch { specific = new(["The importer could not validate this source."]); }
        return new BrowserDataImportValidationResult(common.Errors.Concat(specific.Errors));
    }

    public async Task<BrowserDataImportResult> ImportAsync(BrowserDataImportRequest request, CancellationToken cancellationToken = default)
    {
        var validation = Validate(request);
        if (!validation.IsValid) throw new BrowserDataImportException(string.Join(" ", validation.Errors));
        var importer = _importers[request.ImporterId];
        var result = await importer.ImportAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new BrowserDataImportException("The importer returned no result.");
        if ((result.Kinds & ~request.Kinds) != 0 || (result.Kinds & ~importer.Descriptor.Kinds) != 0)
            throw new BrowserDataImportException("The importer returned results for unrequested data kinds.");
        return result;
    }

    public async Task<BrowserDataImportResult> ImportAsync(
        IEnumerable<BrowserDataImportRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var requestItems = requests.ToArray();
        if (requestItems.Length == 0) throw new ArgumentException("At least one import request is required.", nameof(requests));
        var results = new List<BrowserDataImportResult>();
        foreach (var request in requestItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ImportAsync(request, cancellationToken).ConfigureAwait(false));
        }
        return BrowserDataImportResult.Aggregate(results);
    }
}
