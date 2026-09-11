using System.Security.Cryptography;
using Slate.Core;

namespace Slate.Tests;

internal static class PasswordImportIntegrationTests
{
    private static void Assert(bool condition, string message = "Password import integration assertion failed.")
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FaultingProtector(params int[] failingProtectCalls) : ICredentialProtector
    {
        private readonly HashSet<int> _failingCalls = [.. failingProtectCalls];
        private readonly Dictionary<(string Token, string Entropy), byte[]> _values = [];
        public int ProtectCalls { get; private set; }

        public byte[] Protect(byte[] plaintext, byte[]? entropy = null)
        {
            ProtectCalls++;
            if (_failingCalls.Contains(ProtectCalls)) throw new CryptographicException("Synthetic protected-write failure.");
            byte[] token = Guid.NewGuid().ToByteArray();
            _values[(Convert.ToHexString(token), Convert.ToHexString(entropy ?? []))] = plaintext.ToArray();
            return token;
        }

        public byte[] Unprotect(byte[] ciphertext, byte[]? entropy = null)
        {
            if (!_values.TryGetValue((Convert.ToHexString(ciphertext), Convert.ToHexString(entropy ?? [])), out var plaintext))
                throw new CryptographicException("Synthetic protected-read failure.");
            return plaintext.ToArray();
        }

        public byte[] Protect(byte[] plaintext) => Protect(plaintext, null);
        public byte[] Unprotect(byte[] ciphertext) => Unprotect(ciphertext, null);
    }

    private static void WithVault(FaultingProtector protector, Action<CredentialVault, string> test)
    {
        string folder = Path.Combine(Path.GetTempPath(), "slate-password-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { test(new CredentialVault(folder, protector), folder); }
        finally { Directory.Delete(folder, true); }
    }

    private static BrowserDataImportPackage Parse(params string[] rows)
    {
        string csv = "url,username,password\r\n" + string.Join("\r\n", rows) + "\r\n";
        return new BrowserDataImportService().ParseCsv(new StringReader(csv));
    }

    public static void Run(Action<string, Action> check)
    {
        check("Password import writes through the normal protected credential-vault API", () =>
            WithVault(new FaultingProtector(), (vault, folder) =>
            {
                const string secret = "Imported-Normal-Path!123";
                using var package = Parse("https://example.test/login,alice," + secret);
                var result = CredentialImportCoordinator.ImportAsync(vault, package).GetAwaiter().GetResult();
                var metadata = vault.ListAsync().GetAwaiter().GetResult().Single();

                Assert(result.Imported == 1 && result.Total == 1 && metadata.Origin == "https://example.test");
                Assert(vault.RevealAsync(metadata.Id, metadata.Revision).GetAwaiter().GetResult() == secret);
                string stored = File.ReadAllText(Path.Combine(folder, "credentials.v2.json"));
                Assert(!stored.Contains(secret, StringComparison.Ordinal));
            }));

        check("Password import classifies an exact existing credential as a duplicate without writing", () =>
            WithVault(new FaultingProtector(), (vault, _) =>
            {
                const string secret = "Exact-Duplicate!123";
                var original = vault.SaveAsync("https://example.test", "alice", secret, null).GetAwaiter().GetResult();
                using var package = Parse("https://example.test/login,alice," + secret);
                var result = CredentialImportCoordinator.ImportAsync(vault, package).GetAwaiter().GetResult();
                var retained = vault.ListAsync().GetAwaiter().GetResult().Single();

                Assert(result.Imported == 0 && result.Duplicates == 1 && result.Conflicts == 0 && result.Failed == 0);
                Assert(retained.Id == original.Id && retained.Revision == original.Revision);
            }));

        check("Password import reports a changed existing password as a conflict without overwriting", () =>
            WithVault(new FaultingProtector(), (vault, _) =>
            {
                const string oldSecret = "Existing-Password!123";
                var original = vault.SaveAsync("https://example.test", "alice", oldSecret, null).GetAwaiter().GetResult();
                using var package = Parse("https://example.test/login,alice,Imported-Different!456");
                var result = CredentialImportCoordinator.ImportAsync(vault, package).GetAwaiter().GetResult();
                var retained = vault.ListAsync().GetAwaiter().GetResult().Single();

                Assert(result.Imported == 0 && result.Conflicts == 1 && result.Duplicates == 0 && result.Failed == 0);
                Assert(retained.Id == original.Id && retained.Revision == original.Revision);
                Assert(vault.RevealAsync(retained.Id, retained.Revision).GetAwaiter().GetResult() == oldSecret);
            }));

        check("Password import stores different usernames on one origin as separate credentials", () =>
            WithVault(new FaultingProtector(), (vault, _) =>
            {
                using var package = Parse(
                    "https://example.test/login,alice,Alice-Password!123",
                    "https://example.test/login,bob,Bob-Password!456");
                var result = CredentialImportCoordinator.ImportAsync(vault, package).GetAwaiter().GetResult();
                var accounts = vault.ListAsync("https://example.test").GetAwaiter().GetResult();

                Assert(result.Imported == 2 && result.Total == 2 && accounts.Count == 2);
                Assert(accounts.Select(account => account.Username).OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(["alice", "bob"]));
            }));

        check("Password import counts a vault write failure without exposing its secret", () =>
            WithVault(new FaultingProtector(1), (vault, _) =>
            {
                const string secret = "Failure-Canary!123";
                using var package = Parse("https://failure.test,alice," + secret);
                var result = CredentialImportCoordinator.ImportAsync(vault, package).GetAwaiter().GetResult();

                Assert(result.Total == 1 && result.Imported == 0 && result.Failed == 1);
                Assert(vault.ListAsync().GetAwaiter().GetResult().Count == 0);
                Assert(!result.ToString().Contains(secret, StringComparison.Ordinal));
            }));

        check("Password import preserves partial success across an isolated vault write failure", () =>
            WithVault(new FaultingProtector(2), (vault, _) =>
            {
                using var package = Parse(
                    "https://one.test,one,First-Imported!123",
                    "https://two.test,two,Failed-Imported!456",
                    "https://three.test,three,Third-Imported!789");
                var result = CredentialImportCoordinator.ImportAsync(vault, package).GetAwaiter().GetResult();
                var accounts = vault.ListAsync().GetAwaiter().GetResult();

                Assert(result.Total == 3 && result.Imported == 2 && result.Failed == 1);
                Assert(accounts.Select(account => account.Username).OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(["one", "three"]));
            }));

        check("Imported passwords persist across credential-vault reload", () =>
        {
            var protector = new FaultingProtector();
            WithVault(protector, (vault, folder) =>
            {
                const string firstSecret = "Reloaded-First!123";
                const string secondSecret = "Reloaded-Second!456";
                using var package = Parse(
                    "https://reload.test,alice," + firstSecret,
                    "https://reload.test,bob," + secondSecret);
                var result = CredentialImportCoordinator.ImportAsync(vault, package).GetAwaiter().GetResult();
                var reloaded = new CredentialVault(folder, protector);
                var accounts = reloaded.ListAsync("https://reload.test").GetAwaiter().GetResult();

                Assert(result.Imported == 2 && accounts.Count == 2);
                var alice = accounts.Single(account => account.Username == "alice");
                var bob = accounts.Single(account => account.Username == "bob");
                Assert(reloaded.RevealAsync(alice.Id, alice.Revision).GetAwaiter().GetResult() == firstSecret);
                Assert(reloaded.RevealAsync(bob.Id, bob.Revision).GetAwaiter().GetResult() == secondSecret);
            });
        });

        check("Password import carries parser-invalid origins into typed invalid counts without vault writes", () =>
            WithVault(new FaultingProtector(), (vault, _) =>
            {
                using var package = Parse(
                    "ftp://invalid.test,bad,Invalid-Origin!123",
                    "https://valid.test,good,Valid-Origin!456");
                var result = CredentialImportCoordinator.ImportAsync(vault, package).GetAwaiter().GetResult();

                Assert(result.Total == 2 && result.Imported == 1 && result.Invalid == 1);
                Assert(vault.ListAsync().GetAwaiter().GetResult().Single().Origin == "https://valid.test");
            }));
    }
}
