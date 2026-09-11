using Slate.Core;

namespace Slate.Tests;

internal static class BrowserDataImportArchitectureTests
{
    private static void Assert(bool condition, string message = "Browser data import architecture assertion failed.")
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeImporter(
        BrowserDataImportDescriptor descriptor,
        BrowserDataImportResult result,
        BrowserDataImportValidationResult? validation = null) : IBrowserDataImporter
    {
        public BrowserDataImportDescriptor Descriptor { get; } = descriptor;
        public int ValidationCalls { get; private set; }
        public int ImportCalls { get; private set; }
        public BrowserDataImportRequest? LastRequest { get; private set; }

        public BrowserDataImportValidationResult Validate(BrowserDataImportRequest request)
        {
            ValidationCalls++;
            return validation ?? new();
        }

        public Task<BrowserDataImportResult> ImportAsync(BrowserDataImportRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportCalls++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private static BrowserDataImportDescriptor PasswordDescriptor(string id, string name = "Passwords") => new(
        id, name, "Import saved passwords from a supported export", BrowserDataKinds.Passwords, [".csv"], IsAvailable: true);

    public static void Run(Action<string, Action> check)
    {
        check("Import architecture routes a validated password request to exactly one importer", () =>
        {
            var expected = new BrowserDataImportResult(4, 2, 1, 1, 0, 0, 0, BrowserDataKinds.Passwords, "Synthetic browser");
            var importer = new FakeImporter(PasswordDescriptor("passwords-synthetic"), expected);
            var coordinator = new BrowserDataImportCoordinator([importer]);
            var request = new BrowserDataImportRequest("passwords-synthetic", "passwords.csv", BrowserDataKinds.Passwords);

            var actual = coordinator.ImportAsync(request).GetAwaiter().GetResult();

            Assert(actual == expected);
            Assert(importer.ValidationCalls == 1 && importer.ImportCalls == 1 && importer.LastRequest == request);
            Assert(coordinator.Descriptors.Single().Id == "passwords-synthetic");
        });

        check("Import architecture validates source, kind, availability, and importer-specific rules before execution", () =>
        {
            var result = new BrowserDataImportResult(1, 1, 0, 0, 0, 0, 0);
            var importer = new FakeImporter(PasswordDescriptor("passwords-synthetic"), result,
                new BrowserDataImportValidationResult(["The selected source is not ready."]));
            var coordinator = new BrowserDataImportCoordinator([importer]);

            Assert(!coordinator.Validate(new("missing", "passwords.csv", BrowserDataKinds.Passwords)).IsValid);
            Assert(!coordinator.Validate(new("passwords-synthetic", "passwords.txt", BrowserDataKinds.Passwords)).IsValid);
            Assert(!coordinator.Validate(new("passwords-synthetic", "passwords.csv", BrowserDataKinds.Bookmarks)).IsValid);
            Assert(!coordinator.Validate(new("passwords-synthetic", "passwords.csv", BrowserDataKinds.Passwords)).IsValid);
            Throws<BrowserDataImportException>(() => coordinator.ImportAsync(
                new BrowserDataImportRequest("passwords-synthetic", "passwords.csv", BrowserDataKinds.Passwords)).GetAwaiter().GetResult());
            Assert(importer.ImportCalls == 0);

            var unavailable = PasswordDescriptor("passwords-unavailable") with
            {
                IsAvailable = false,
                UnavailableReason = "This source is not available."
            };
            var unavailableCoordinator = new BrowserDataImportCoordinator([new FakeImporter(unavailable, result)]);
            Assert(!unavailableCoordinator.Validate(new("passwords-unavailable", "passwords.csv", BrowserDataKinds.Passwords)).IsValid);
        });

        check("Import architecture rejects invalid and duplicate importer descriptors", () =>
        {
            var result = new BrowserDataImportResult(1, 1, 0, 0, 0, 0, 0);
            var valid = new FakeImporter(PasswordDescriptor("passwords-synthetic"), result);
            var duplicate = new FakeImporter(PasswordDescriptor("passwords-synthetic", "Other passwords"), result);
            Throws<ArgumentException>(() => new BrowserDataImportCoordinator([valid, duplicate]));

            var invalidDescriptor = PasswordDescriptor("bad id") with { SupportedExtensions = ["csv"] };
            var validation = BrowserDataImportValidation.ValidateDescriptor(invalidDescriptor);
            Assert(!validation.IsValid && validation.Errors.Count >= 2);
        });

        check("Import result aggregation preserves every typed outcome count", () =>
        {
            var first = new BrowserDataImportResult(7, 2, 1, 1, 1, 1, 1, BrowserDataKinds.Passwords, "Source A");
            var second = new BrowserDataImportResult(8, 3, 2, 1, 0, 1, 1, BrowserDataKinds.Passwords, "Source B");
            var aggregate = BrowserDataImportResult.Aggregate([first, second]);

            Assert(aggregate.Total == 15 && aggregate.TotalRows == aggregate.Total && aggregate.Imported == 5 && aggregate.Skipped == 3);
            Assert(aggregate.Duplicates == 2 && aggregate.Conflicts == 1 && aggregate.Invalid == 2 && aggregate.Failed == 2);
            Assert(aggregate.Kinds == BrowserDataKinds.Passwords && aggregate.SourceName is null);
            Assert(aggregate.HasChanges && aggregate.HasIssues);
        });

        check("Import coordinator aggregates multiple password operations without parsing their sources", () =>
        {
            var first = new FakeImporter(PasswordDescriptor("passwords-a", "Passwords A"),
                new BrowserDataImportResult(3, 2, 1, 0, 0, 0, 0));
            var second = new FakeImporter(PasswordDescriptor("passwords-b", "Passwords B"),
                new BrowserDataImportResult(4, 1, 0, 1, 1, 0, 1));
            var coordinator = new BrowserDataImportCoordinator([second, first]);
            var aggregate = coordinator.ImportAsync([
                new BrowserDataImportRequest("passwords-a", "a.csv", BrowserDataKinds.Passwords),
                new BrowserDataImportRequest("passwords-b", "b.csv", BrowserDataKinds.Passwords)
            ]).GetAwaiter().GetResult();

            Assert(aggregate.TotalRows == 7 && aggregate.Imported == 3 && aggregate.Skipped == 1);
            Assert(aggregate.Duplicates == 1 && aggregate.Conflicts == 1 && aggregate.Invalid == 0 && aggregate.Failed == 1);
            Assert(first.ImportCalls == 1 && second.ImportCalls == 1);
            Assert(coordinator.Descriptors.Select(descriptor => descriptor.DisplayName).SequenceEqual(["Passwords A", "Passwords B"]));
        });

        check("Import results enforce complete non-secret outcome accounting", () =>
        {
            Throws<ArgumentOutOfRangeException>(() => new BrowserDataImportResult(0, -1, 0, 0, 0, 0, 1));
            Throws<ArgumentException>(() => new BrowserDataImportResult(2, 1, 0, 0, 0, 0, 0));
            Throws<ArgumentOutOfRangeException>(() => new BrowserDataImportResult(1, 1, 0, 0, 0, 0, 0, BrowserDataKinds.None));
            Assert(typeof(BrowserDataImportResult).GetProperties().All(property =>
                !property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)));
        });
    }
}
