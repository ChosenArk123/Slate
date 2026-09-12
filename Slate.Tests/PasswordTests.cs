using System.Text.Json.Nodes;
using Slate.Core;

namespace Slate.Tests;

internal static class PasswordTests
{
    private const string Origin = "https://accounts.example.test";
    private const string Synthetic = "Synthetic-only-Password!123";
    private static void Assert(bool value) { if (!value) throw new Exception("Password policy assertion failed."); }
    private static T Get<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected rejection."); }
    // Opaque random tokens provide a test seam, not a production cryptographic implementation.
    private sealed class TestProtector : ICredentialProtector
    {
        private readonly Dictionary<string, byte[]> _values = [];
        public int Decryptions;
        public byte[] Protect(byte[] plaintext)
        { var token = Guid.NewGuid().ToByteArray(); _values.Add(Convert.ToHexString(token), plaintext.ToArray()); return token; }
        public byte[] Unprotect(byte[] ciphertext)
        { Decryptions++; return _values[Convert.ToHexString(ciphertext)].ToArray(); }
    }
    private static void WithVault(Action<CredentialVault, string, TestProtector> test)
    {
        string folder = Path.Combine(Path.GetTempPath(), "slate-password-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var protector = new TestProtector();
        try { test(new CredentialVault(folder, protector), folder, protector); }
        finally { Directory.Delete(folder, true); }
    }
    public static void Run(Action<string, Action> check)
    {
        foreach (var (input, expected) in new (string, string)[] {
            ("https://Example.COM/a?q=1#b", "https://example.com"), ("http://EXAMPLE.com:80/", "http://example.com"),
            ("https://example.com:443/", "https://example.com"), ("https://example.com:8443/", "https://example.com:8443"),
            ("http://127.0.0.1:8080/a", "http://127.0.0.1:8080"), ("http://[::1]:80/a", "http://[::1]"),
            ("http://LOCALHOST:3000/a", "http://localhost:3000"), ("https://bücher.example/", "https://xn--bcher-kva.example"),
            ("https://xn--bcher-kva.example", "https://xn--bcher-kva.example") })
            check("Credential origin canonicalization: " + input, () => Assert(CredentialOrigin.Normalize(input) == expected));
        check("Credential origins isolate schemes, sibling subdomains and nondefault ports", () =>
        {
            var values = new[] { "http://example.com", "https://example.com", "https://example.com:8443", "https://a.example.com", "https://b.example.com" };
            Assert(values.Select(CredentialOrigin.Normalize).Distinct().Count() == values.Length);
        });
        check("Credential origins reject malformed, userinfo, nonweb and ambiguous inputs", () =>
        {
            foreach (var value in new string?[] { null, "", "example.com", "https:bad", "javascript:alert(1)", "file:///a", "https://a@b.test", "https://a.test:99999", "https://a.test./", "https://a.test\\@b.test", "https://a.test/\n" })
                Assert(CredentialOrigin.Normalize(value) is null);
        });
        check("Password generator meets length, alphabet and character-class guarantees", () =>
        {
            foreach (int length in new[] { 16, 24, 64, 128 }) for (int i = 0; i < 20; i++)
            {
                string value = PasswordGenerator.Generate(length);
                Assert(value.Length == length && value.All(PasswordGenerator.Alphabet.Contains));
                foreach (string group in new[] { PasswordGenerator.Lower, PasswordGenerator.Upper, PasswordGenerator.Digits, PasswordGenerator.Symbols }) Assert(value.Any(group.Contains));
            }
            Assert(PasswordGenerator.Generate().Length == PasswordGenerator.DefaultLength);
            Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(15));
            Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(129));
        });
        check("Password generation does not collapse to a repeated output", () => Assert(Enumerable.Range(0, 32).Select(_ => PasswordGenerator.Generate()).Distinct().Count() > 1));
        check("Vault exact lookup, multiple accounts and metadata-only lists", () => WithVault((v, _, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null)); Get(v.SaveAsync(Origin, "bob", Synthetic, null));
            Get(v.SaveAsync("http://accounts.example.test", "alice", Synthetic, null));
            Assert(Get(v.ListAsync(Origin)).Count == 2 && Get(v.ListAsync("https://evil.example.test")).Count == 0);
            Assert(p.Decryptions == 0);
        }));
        check("Vault normal update decrypts its revision-bound envelope", () => WithVault((v, _, _) =>
        {
            Assert(Get(v.AssessAsync(Origin, "alice", Synthetic)).Change == CredentialChange.Save);
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            Assert(Get(v.AssessAsync(Origin, "alice", Synthetic)).Change == CredentialChange.Unchanged);
            var update = Get(v.AssessAsync(Origin, "alice", Synthetic + "2")); Assert(update.Change == CredentialChange.Update);
            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "2", update.Existing));
            Assert(first.Id == second.Id && second.Revision == first.Revision + 1 && first.Created == second.Created);
            Assert(Get(v.RevealAsync(second.Id, second.Revision)) == Synthetic + "2");
        }));
        check("Vault rejects duplicate saves and stale updates and deletions", () => WithVault((v, _, _) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            Throws<CredentialConflictException>(() => Get(v.SaveAsync(Origin, "alice", Synthetic, null)));
            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "2", first));
            Throws<CredentialConflictException>(() => Get(v.SaveAsync(Origin, "alice", Synthetic, first)));
            Throws<CredentialConflictException>(() => Get(v.DeleteAsync(first.Id, first.Revision)));
            Get(v.DeleteAsync(second.Id, second.Revision)); Assert(Get(v.ListAsync()).Count == 0);
            Throws<CredentialConflictException>(() => Get(v.SaveAsync(Origin, "alice", Synthetic, second)));
        }));
        check("Vault validates canonical records and input bounds", () => WithVault((v, _, _) =>
        {
            Throws<CredentialVaultException>(() => Get(v.SaveAsync(Origin + "/", "alice", Synthetic, null)));
            Throws<CredentialVaultException>(() => Get(v.SaveAsync(Origin, "a\nb", Synthetic, null)));
            Throws<CredentialVaultException>(() => Get(v.SaveAsync(Origin, "alice", "", null)));
            Throws<CredentialVaultException>(() => Get(v.SaveAsync(Origin, "alice", new string('a', 4097), null)));
        }));
        check("Vault durable replacement keeps ciphertext primary and backup and no temp", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            Get(v.SaveAsync(Origin, "alice", Synthetic + "2", first));
            string path = Path.Combine(folder, "credentials.v1.json");
            Assert(File.Exists(path + ".bak") && !File.Exists(path + ".tmp"));
            Assert(!File.ReadAllText(path).Contains(Synthetic) && !File.ReadAllText(path + ".bak").Contains(Synthetic));
            Assert(Get(new CredentialVault(folder, p).ListAsync()).Count == 1);
        }));
        check("Vault corrupted primary recovers backup read-only and preserves damage", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null)); Get(v.SaveAsync(Origin, "bob", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v1.json"); File.WriteAllText(path, "damaged");
            var recovered = new CredentialVault(folder, p); Assert(Get(recovered.ListAsync()).Count == 1 && recovered.RecoveredFromBackup);
            Assert(Get(recovered.RevealAsync(first.Id, first.Revision)) == Synthetic);
            Throws<CredentialVaultException>(() => Get(recovered.DeleteAsync(first.Id, first.Revision)));
            Assert(File.ReadAllText(path) == "damaged");
        }));
        check("Vault corrupt without backup fails closed", () => WithVault((_, folder, p) =>
        {
            File.WriteAllText(Path.Combine(folder, "credentials.v1.json"), "damaged");
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));
        }));
        check("Vault interrupted temp file does not replace the valid primary", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            File.WriteAllText(Path.Combine(folder, "credentials.v1.json.tmp"), "synthetic interrupted encrypted write");
            Assert(Get(new CredentialVault(folder, p).ListAsync()).Single().Username == "alice");
        }));
        check("Vault failed replacement preserves old file and in-memory records", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v1.json");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws<CredentialVaultException>(() => Get(v.SaveAsync(Origin, "bob", Synthetic, null)));
            Assert(Get(v.ListAsync()).Count == 1 && Get(new CredentialVault(folder, p).ListAsync()).Count == 1);
            Assert(!File.Exists(path + ".tmp"));
        }));
        check("Vault unknown version never falls back to older backup", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null)); Get(v.SaveAsync(Origin, "bob", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v1.json"); var json = JsonNode.Parse(File.ReadAllText(path))!; json["Version"] = 2; json["FutureField"] = true; File.WriteAllText(path, json.ToJsonString());
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));
        }));
        check("Vault rejects malformed metadata and duplicate records", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null)); string path = Path.Combine(folder, "credentials.v1.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!; var entries = json["Entries"]!.AsArray(); entries.Add(entries[0]!.DeepClone());
            File.WriteAllText(path, json.ToJsonString()); Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));
        }));
        check("Vault rejects current metadata paired with ciphertext from an older revision", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v1.json");
            var oldCiphertext = JsonNode.Parse(File.ReadAllText(path))!["Entries"]![0]!["ProtectedSecret"]!.DeepClone();
            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "2", first));
            var current = JsonNode.Parse(File.ReadAllText(path))!;
            current["Entries"]![0]!["ProtectedSecret"] = oldCiphertext;
            File.WriteAllText(path, current.ToJsonString());
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).RevealAsync(second.Id, second.Revision)));
        }));
        check("Vault rejects backup ciphertext transplanted into a newer same-ID record", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "2", first));
            string path = Path.Combine(folder, "credentials.v1.json");
            var current = JsonNode.Parse(File.ReadAllText(path))!;
            var backup = JsonNode.Parse(File.ReadAllText(path + ".bak"))!;
            Assert(current["Entries"]![0]!["Metadata"]!["Id"]!.GetValue<Guid>() ==
                backup["Entries"]![0]!["Metadata"]!["Id"]!.GetValue<Guid>());
            Assert(current["Entries"]![0]!["Metadata"]!["Revision"]!.GetValue<long>() !=
                backup["Entries"]![0]!["Metadata"]!["Revision"]!.GetValue<long>());
            current["Entries"]![0]!["ProtectedSecret"] = backup["Entries"]![0]!["ProtectedSecret"]!.DeepClone();
            File.WriteAllText(path, current.ToJsonString());
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).RevealAsync(second.Id, second.Revision)));
        }));
        check("Vault encrypted envelope rejects username, origin, ID and revision tampering", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v1.json");
            string original = File.ReadAllText(path);
            var mutations = new Action<JsonObject>[]
            {
                metadata => metadata["Username"] = "mallory",
                metadata => metadata["Origin"] = "https://evil.example.test",
                metadata => metadata["Id"] = Guid.NewGuid(),
                metadata => metadata["Revision"] = metadata["Revision"]!.GetValue<long>() + 1
            };
            foreach (var mutate in mutations)
            {
                var json = JsonNode.Parse(original)!;
                mutate(json["Entries"]![0]!["Metadata"]!.AsObject());
                File.WriteAllText(path, json.ToJsonString());
                var tamperedVault = new CredentialVault(folder, p);
                var tampered = Get(tamperedVault.ListAsync()).Single();
                Throws<CredentialVaultException>(() => Get(tamperedVault.RevealAsync(tampered.Id, tampered.Revision)));
            }
        }));
        check("Vault serializes concurrent writes without losing accounts", () => WithVault((v, folder, p) =>
        {
            Get(Task.WhenAll(Enumerable.Range(0, 20).Select(i => v.SaveAsync(Origin, "synthetic" + i, Synthetic, null))));
            Assert(Get(new CredentialVault(folder, p).ListAsync()).Count == 20);
        }));
        check("Vault use timestamps preserve update revisions", () => WithVault((v, _, _) =>
        {
            var entry = Get(v.SaveAsync(Origin, "alice", Synthetic, null)); Get(v.MarkUsedAsync(entry.Id, entry.Revision));
            var used = Get(v.ListAsync()).Single(); Assert(used.LastUsed is not null && used.Revision == entry.Revision);
        }));
    }
}
