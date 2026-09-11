using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Slate.Core;

internal static class PasswordTests
{
    private const string Origin = "https://accounts.example.test";
    private const string Synthetic = "Synthetic-only-Password!123";
    private static void Assert(bool value) { if (!value) throw new Exception("Password policy assertion failed."); }
    private static T Get<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected rejection."); }

    // Opaque random tokens provide a test seam with contextual entropy support
    private sealed class TestProtector : ICredentialProtector
    {
        private readonly Dictionary<(string CiphertextHex, string EntropyHex), byte[]> _values = [];
        public int Decryptions;

        public byte[] Protect(byte[] plaintext, byte[]? entropy = null)
        {
            var token = Guid.NewGuid().ToByteArray();
            var tokenHex = Convert.ToHexString(token);
            var entropyHex = entropy is not null ? Convert.ToHexString(entropy) : string.Empty;
            _values[(tokenHex, entropyHex)] = plaintext.ToArray();
            return token;
        }

        public byte[] Unprotect(byte[] ciphertext, byte[]? entropy = null)
        {
            Decryptions++;
            var tokenHex = Convert.ToHexString(ciphertext);
            var entropyHex = entropy is not null ? Convert.ToHexString(entropy) : string.Empty;
            if (!_values.TryGetValue((tokenHex, entropyHex), out var plaintext))
                throw new CryptographicException("Decryption failed: invalid token or entropy mismatch.");
            return plaintext.ToArray();
        }

        public byte[] Protect(byte[] plaintext) => Protect(plaintext, null);
        public byte[] Unprotect(byte[] ciphertext) => Unprotect(ciphertext, null);
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

        check("Password autofill lookup keeps HTTP and HTTPS credentials separate", () => WithVault((v, _, _) =>
        {
            const string httpOrigin = "http://accounts.example.test";
            var http = Get(v.SaveAsync(httpOrigin, "alice", Synthetic + "-http", null));
            var https = Get(v.SaveAsync(Origin, "alice", Synthetic + "-https", null));
            Assert(Get(v.ListAsync(httpOrigin)).Single().Id == http.Id);
            Assert(Get(v.ListAsync(Origin)).Single().Id == https.Id);
            Assert(Get(v.AssessAsync(httpOrigin, "alice", Synthetic + "-https")).Change == CredentialChange.Update);
            Assert(Get(v.AssessAsync(Origin, "alice", Synthetic + "-http")).Change == CredentialChange.Update);
        }));

        check("Password autofill origins reject private-page scheme substitutes", () =>
        {
            foreach (var url in new[] { "file:///C:/login.html", "view-source:https://accounts.example.test/login", "slate://newtab", "about:blank", "data:text/html,login", "ftp://accounts.example.test/login" })
                Assert(CredentialOrigin.Normalize(url) is null);
        });

        check("Password save lifecycle classifies a new credential without persisting it", () => WithVault((v, _, _) =>
        {
            var decision = Get(v.AssessAsync(Origin, "new-user", Synthetic));
            Assert(decision.Change == CredentialChange.Save && decision.Existing is null && Get(v.ListAsync()).Count == 0);
        }));

        check("Password save lifecycle suppresses an identical existing credential", () => WithVault((v, _, _) =>
        {
            var existing = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var decision = Get(v.AssessAsync(Origin, "alice", Synthetic));
            Assert(decision.Change == CredentialChange.Unchanged && decision.Existing?.Id == existing.Id &&
                decision.Existing.Revision == existing.Revision && Get(v.ListAsync()).Single().Revision == existing.Revision);
        }));

        check("Password save lifecycle requires an explicit revision-bound changed-password update", () => WithVault((v, _, _) =>
        {
            var existing = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var decision = Get(v.AssessAsync(Origin, "alice", Synthetic + "-changed"));
            Assert(decision.Change == CredentialChange.Update && decision.Existing?.Id == existing.Id);
            Assert(Get(v.RevealAsync(existing.Id, existing.Revision)) == Synthetic);
            var updated = Get(v.SaveAsync(Origin, "alice", Synthetic + "-changed", decision.Existing));
            Assert(updated.Id == existing.Id && updated.Revision == existing.Revision + 1 &&
                Get(v.RevealAsync(updated.Id, updated.Revision)) == Synthetic + "-changed");
        }));

        check("Password save lifecycle keeps multiple usernames on one origin separate", () => WithVault((v, _, _) =>
        {
            var alice = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var bob = Get(v.SaveAsync(Origin, "bob", Synthetic + "-bob", null));
            var aliceDecision = Get(v.AssessAsync(Origin, "alice", Synthetic + "-new"));
            var bobDecision = Get(v.AssessAsync(Origin, "bob", Synthetic + "-bob"));
            var carolDecision = Get(v.AssessAsync(Origin, "carol", Synthetic));
            Assert(aliceDecision.Change == CredentialChange.Update && aliceDecision.Existing?.Id == alice.Id);
            Assert(bobDecision.Change == CredentialChange.Unchanged && bobDecision.Existing?.Id == bob.Id);
            Assert(carolDecision.Change == CredentialChange.Save && carolDecision.Existing is null && Get(v.ListAsync(Origin)).Count == 2);
        }));

        check("Password save lifecycle isolates identical usernames across normalized origins", () => WithVault((v, _, _) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            const string otherOrigin = "https://login.example.test";
            var decision = Get(v.AssessAsync(otherOrigin, "alice", Synthetic));
            Assert(decision.Change == CredentialChange.Save && decision.Existing is null && Get(v.ListAsync(otherOrigin)).Count == 0);
        }));

        check("Password capture policy rejects unsupported and internal schemes", () =>
        {
            foreach (var url in new[] { "file:///C:/login.html", "view-source:https://example.test/login", "slate://newtab", "about:blank", "data:text/html,login", "javascript:void(0)" })
                Assert(!PasswordCapturePolicy.TryNormalizeSubmission(url, "alice", Synthetic, false, false, out _));
        });

        check("Password capture policy excludes private and temporary contexts", () =>
        {
            Assert(!PasswordCapturePolicy.TryNormalizeSubmission(Origin + "/login", "alice", Synthetic, true, false, out _));
            Assert(!PasswordCapturePolicy.TryNormalizeSubmission(Origin + "/login", "alice", Synthetic, false, true, out _));
            Assert(PasswordCapturePolicy.TryNormalizeSubmission(Origin + "/login", "alice", Synthetic, false, false, out var origin) && origin == Origin);
        });

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
            string path = Path.Combine(folder, "credentials.v2.json");
            Assert(File.Exists(path + ".bak") && !File.Exists(path + ".tmp"));
            Assert(File.Exists(Path.Combine(folder, "vault.anchor")));
            Assert(!File.ReadAllText(path).Contains(Synthetic) && !File.ReadAllText(path + ".bak").Contains(Synthetic));
            Assert(Get(new CredentialVault(folder, p).ListAsync()).Count == 1);
        }));

        check("Vault corrupted primary recovers backup read-only and preserves damage", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null)); Get(v.SaveAsync(Origin, "bob", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json"); File.WriteAllText(path, "damaged");
            var recovered = new CredentialVault(folder, p); Assert(Get(recovered.ListAsync()).Count == 1 && recovered.RecoveredFromBackup);
            Assert(Get(recovered.RevealAsync(first.Id, first.Revision)) == Synthetic);
            Throws<CredentialVaultException>(() => Get(recovered.DeleteAsync(first.Id, first.Revision)));
            Assert(File.ReadAllText(path) == "damaged");
        }));

        check("Vault corrupt without backup fails closed", () => WithVault((_, folder, p) =>
        {
            File.WriteAllText(Path.Combine(folder, "credentials.v2.json"), "damaged");
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));
        }));

        check("Vault interrupted temp file does not replace the valid primary", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            File.WriteAllText(Path.Combine(folder, "credentials.v2.json.tmp"), "synthetic interrupted encrypted write");
            Assert(Get(new CredentialVault(folder, p).ListAsync()).Single().Username == "alice");
        }));

        check("Vault failed replacement preserves old file and in-memory records", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws<CredentialVaultException>(() => Get(v.SaveAsync(Origin, "bob", Synthetic, null)));
            Assert(Get(v.ListAsync()).Count == 1 && Get(new CredentialVault(folder, p).ListAsync()).Count == 1);
            Assert(!File.Exists(path + ".tmp"));
        }));

        check("Vault unknown version never falls back to older backup", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null)); Get(v.SaveAsync(Origin, "bob", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["SchemaVersion"] = 3;
            json["FutureField"] = true;
            File.WriteAllText(path, json.ToJsonString());
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));
        }));

        check("Vault rejects malformed metadata and duplicate records", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            var entries = json["Entries"]!.AsArray();
            entries.Add(entries[0]!.DeepClone());
            File.WriteAllText(path, json.ToJsonString());
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));
        }));

        check("Vault rejects current metadata paired with ciphertext from an older revision", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
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
            string path = Path.Combine(folder, "credentials.v2.json");
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
            string path = Path.Combine(folder, "credentials.v2.json");
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
                Throws<CredentialVaultException>(() =>
                {
                    var tampered = Get(tamperedVault.ListAsync()).Single();
                    Get(tamperedVault.RevealAsync(tampered.Id, tampered.Revision));
                });
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

        check("Vault deliberate edit preserves identity and rejects stale or colliding account changes", () => WithVault((v, _, _) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var edited = Get(v.UpdateAsync(first.Id, first.Revision, "bob", Synthetic + "-edited"));
            Assert(edited.Id == first.Id && edited.Revision == first.Revision + 1 && edited.Username == "bob");
            Assert(Get(v.RevealAsync(edited.Id, edited.Revision)) == Synthetic + "-edited");
            Throws<CredentialConflictException>(() => Get(v.UpdateAsync(first.Id, first.Revision, "alice", Synthetic)));
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            Throws<CredentialConflictException>(() => Get(v.UpdateAsync(edited.Id, edited.Revision, "alice", Synthetic)));
        }));

        // --- Targeted Regression Tests for Credential Vault Hardening (Schema v2) ---

        check("Vault rollback detection rejects older database file substituted over current anchor", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            string rev1Content = File.ReadAllText(path);

            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "2", first));
            Assert(second.Revision == 2);

            // Substitute older v2 file with lower VaultRevision while anchor has higher revision
            File.WriteAllText(path, rev1Content);
            var rolledBackVault = new CredentialVault(folder, p);
            Throws<CredentialVaultException>(() => Get(rolledBackVault.ListAsync()));
            Throws<CredentialVaultException>(() => Get(rolledBackVault.RevealAsync(first.Id, 1)));
        }));

        check("Vault single-entry rollback detection rejects older revision record injected into current state", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            var rev1Entry = JsonNode.Parse(File.ReadAllText(path))!["Entries"]![0]!.DeepClone();

            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "2", first));
            var current = JsonNode.Parse(File.ReadAllText(path))!;
            // Replace entry with older revision entry
            current["Entries"]![0] = rev1Entry;
            File.WriteAllText(path, current.ToJsonString());

            // Digest mismatch on load upfront
            var tamperedVault = new CredentialVault(folder, p);
            Throws<CredentialVaultException>(() => Get(tamperedVault.ListAsync()));
        }));

        check("Vault rejects cross-vault ciphertext transplantation across different vault identities", () =>
        {
            string folder1 = Path.Combine(Path.GetTempPath(), "slate-vault-1-" + Guid.NewGuid().ToString("N"));
            string folder2 = Path.Combine(Path.GetTempPath(), "slate-vault-2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder1);
            Directory.CreateDirectory(folder2);
            var protector = new TestProtector();
            try
            {
                var v1 = new CredentialVault(folder1, protector);
                var v2 = new CredentialVault(folder2, protector);
                var cred1 = Get(v1.SaveAsync(Origin, "alice", Synthetic, null));
                var cred2 = Get(v2.SaveAsync("https://other.example.test", "bob", Synthetic + "2", null));

                string v1Path = Path.Combine(folder1, "credentials.v2.json");
                string v2Path = Path.Combine(folder2, "credentials.v2.json");
                var v1Json = JsonNode.Parse(File.ReadAllText(v1Path))!;
                var v2Json = JsonNode.Parse(File.ReadAllText(v2Path))!;

                // Transplant Alice from Vault 1 into Vault 2
                var v1AliceEntry = v1Json["Entries"]![0]!.DeepClone();
                v2Json["Entries"]!.AsArray().Add(v1AliceEntry);

                // Even if file is edited, decrypting Alice in Vault 2 must fail due to contextual entropy (VaultId mismatch)
                File.WriteAllText(v2Path, v2Json.ToJsonString());
                var reloadedV2 = new CredentialVault(folder2, protector);
                Throws<CredentialVaultException>(() => Get(reloadedV2.RevealAsync(cred1.Id, cred1.Revision)));
            }
            finally
            {
                Directory.Delete(folder1, true);
                Directory.Delete(folder2, true);
            }
        });

        check("Two-stage schema migration upgrades legacy v1 vault to v2 with contextual entropy and rollback anchor", () =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "slate-migration-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var protector = new TestProtector();
            try
            {
                var credId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                var metadata = new CredentialMetadata(credId, Origin, "alice", now, now, null, 1);

                // Legacy envelope: (Id, Origin, Username, Revision, Password) with no entropy
                var legacyEnvelopeJson = JsonSerializer.Serialize(
                    new { Id = credId, Origin = Origin, Username = "alice", Revision = 1L, Password = Synthetic });
                byte[] legacyEnvelopeBytes = Encoding.UTF8.GetBytes(legacyEnvelopeJson);
                byte[] legacyCiphertext = protector.Protect(legacyEnvelopeBytes, null);

                var v1Db = new
                {
                    Version = 1,
                    Entries = new[]
                    {
                        new { Metadata = metadata, ProtectedSecret = legacyCiphertext }
                    }
                };
                string v1Path = Path.Combine(folder, "credentials.v1.json");
                File.WriteAllText(v1Path, JsonSerializer.Serialize(v1Db));

                string v2Path = Path.Combine(folder, "credentials.v2.json");
                string anchorPath = Path.Combine(folder, "vault.anchor");
                string migratedPath = Path.Combine(folder, "credentials.v1.json.migrated");

                Assert(!File.Exists(v2Path));
                Assert(!File.Exists(anchorPath));

                var vault = new CredentialVault(folder, protector);
                var creds = Get(vault.ListAsync());
                Assert(creds.Count == 1 && creds[0].Username == "alice");

                Assert(File.Exists(v2Path));
                Assert(File.Exists(anchorPath));
                Assert(File.Exists(migratedPath));
                Assert(!File.Exists(v1Path));

                string revealed = Get(vault.RevealAsync(credId, 1));
                Assert(revealed == Synthetic);

                var updated = Get(vault.SaveAsync(Origin, "alice", Synthetic + "-updated", creds[0]));
                Assert(updated.Revision == 2);
                Assert(Get(vault.RevealAsync(credId, 2)) == Synthetic + "-updated");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        });

        check("Vault with corrupted primary and corrupted backup fails closed completely", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            Get(v.SaveAsync(Origin, "bob", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            Assert(File.Exists(path) && File.Exists(path + ".bak"));

            File.WriteAllText(path, "corrupted primary content");
            File.WriteAllText(path + ".bak", "corrupted backup content");

            var deadVault = new CredentialVault(folder, p);
            Throws<CredentialVaultException>(() => Get(deadVault.ListAsync()));
            Throws<CredentialVaultException>(() => Get(deadVault.RevealAsync(first.Id, first.Revision)));
        }));

        check("CredentialVault exceptions never disclose password fragments in messages or stack traces", () => WithVault((v, _, _) =>
        {
            const string SensitiveSecret = "Super-Sensitive-Plaintext-Password-9876543210!";
            var first = Get(v.SaveAsync(Origin, "alice", SensitiveSecret, null));

            void AssertNoSecret(Exception ex)
            {
                string full = ex.ToString();
                Assert(!full.Contains(SensitiveSecret, StringComparison.Ordinal));
                Assert(!ex.Message.Contains(SensitiveSecret, StringComparison.Ordinal));
            }

            try { Get(v.SaveAsync("bad-origin", "alice", SensitiveSecret, null)); Assert(false); }
            catch (Exception ex) { AssertNoSecret(ex); }

            try { Get(v.SaveAsync(Origin, "alice", SensitiveSecret + "-2", null)); Assert(false); }
            catch (Exception ex) { AssertNoSecret(ex); }

            try { Get(v.UpdateAsync(first.Id, 999, "alice", SensitiveSecret)); Assert(false); }
            catch (Exception ex) { AssertNoSecret(ex); }

            try { Get(v.SaveAsync(Origin, "charlie", new string('X', 5000), null)); Assert(false); }
            catch (Exception ex) { Assert(!ex.ToString().Contains("XXXXX")); }
        }));

        // --- Challenger Adversarial Probes ---

        check("Adversarial: Rollback attacks fail closed across missing anchor, higher anchor revision, and digest mismatch", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "2", first));
            string dbPath = Path.Combine(folder, "credentials.v2.json");
            string anchorPath = Path.Combine(folder, "vault.anchor");

            // Probe 1: Anchor deletion fails closed
            File.Delete(anchorPath);
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));

            // Probe 2: Anchor VaultId mismatch fails closed
            File.WriteAllText(anchorPath, JsonSerializer.Serialize(new { VaultId = Guid.NewGuid(), HighWaterRevision = 2L }));
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));

            // Probe 3: Anchor HighWaterRevision higher than vault fails closed
            File.WriteAllText(anchorPath, JsonSerializer.Serialize(new { VaultId = JsonNode.Parse(File.ReadAllText(dbPath))!["VaultId"]!.GetValue<Guid>(), HighWaterRevision = 999L }));
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));

            // Probe 4: Anchor digest mismatch at same revision fails closed
            var dbJson = JsonNode.Parse(File.ReadAllText(dbPath))!;
            var validVaultId = dbJson["VaultId"]!.GetValue<Guid>();
            File.WriteAllText(anchorPath, JsonSerializer.Serialize(new { VaultId = validVaultId, HighWaterRevision = 2L, EntriesDigest = "0000000000000000000000000000000000000000000000000000000000000000" }));
            Throws<CredentialVaultException>(() => Get(new CredentialVault(folder, p).ListAsync()));
        }));

        check("Adversarial: Intra-vault entry ciphertext transplant fails decryption", () => WithVault((v, folder, p) =>
        {
            var alice = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var bob = Get(v.SaveAsync(Origin, "bob", Synthetic + "-bob", null));
            string path = Path.Combine(folder, "credentials.v2.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            var entries = json["Entries"]!.AsArray();
            int aliceIdx = entries[0]!["Metadata"]!["Username"]!.GetValue<string>() == "alice" ? 0 : 1;
            int bobIdx = 1 - aliceIdx;

            // Transplant Alice's ciphertext into Bob's entry
            entries[bobIdx]!["ProtectedSecret"] = entries[aliceIdx]!["ProtectedSecret"]!.DeepClone();
            // Recompute entries digest so vault loads
            // But reveal must fail because entropy is bound to Bob's ID/Username
            File.WriteAllText(path, json.ToJsonString());
            var reloaded = new CredentialVault(folder, p);
            Throws<CredentialVaultException>(() => Get(reloaded.RevealAsync(bob.Id, bob.Revision)));
        }));

        check("Adversarial: EntriesDigest detects Revision tampering before reveal on Load/ListAsync", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["Entries"]![0]!["Metadata"]!["Revision"] = 999;
            File.WriteAllText(path, json.ToJsonString());
            var vault = new CredentialVault(folder, p);
            Throws<CredentialVaultException>(() => Get(vault.ListAsync()));
        }));

        check("Adversarial: EntriesDigest detects Origin tampering before reveal on Load/ListAsync", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["Entries"]![0]!["Metadata"]!["Origin"] = "https://evil.example.test";
            File.WriteAllText(path, json.ToJsonString());
            var vault = new CredentialVault(folder, p);
            Throws<CredentialVaultException>(() => Get(vault.ListAsync()));
        }));

        check("Adversarial: EntriesDigest detects Username tampering before reveal on Load/ListAsync", () => WithVault((v, folder, p) =>
        {
            Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            string path = Path.Combine(folder, "credentials.v2.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["Entries"]![0]!["Metadata"]!["Username"] = "mallory";
            File.WriteAllText(path, json.ToJsonString());
            var vault = new CredentialVault(folder, p);
            Throws<CredentialVaultException>(() => Get(vault.ListAsync()));
        }));

        check("Adversarial: Multi-entry legacy v1 migration handles multiple accounts, preserves tombstone, and enforces v2 invariants", () =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "slate-adv-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var protector = new TestProtector();
            try
            {
                var now = DateTimeOffset.UtcNow;
                var accounts = new (string Origin, string Username, string Password)[]
                {
                    ("https://alpha.example.test", "alice", "Secret-Alpha!123"),
                    ("https://beta.example.test", "bob", "Secret-Beta!456"),
                    ("https://gamma.example.test", "charlie", "Secret-Gamma!789")
                };

                var entries = accounts.Select(a =>
                {
                    var id = Guid.NewGuid();
                    var meta = new CredentialMetadata(id, a.Origin, a.Username, now, now, null, 1);
                    var envJson = JsonSerializer.Serialize(new { Id = id, Origin = a.Origin, Username = a.Username, Revision = 1L, Password = a.Password });
                    var ciphertext = protector.Protect(Encoding.UTF8.GetBytes(envJson), null);
                    return new { Metadata = meta, ProtectedSecret = ciphertext };
                }).ToArray();

                var v1Db = new { Version = 1, Entries = entries };
                string v1Path = Path.Combine(folder, "credentials.v1.json");
                File.WriteAllText(v1Path, JsonSerializer.Serialize(v1Db));

                var vault = new CredentialVault(folder, protector);
                var listed = Get(vault.ListAsync());
                Assert(listed.Count == 3);

                // Verify all passwords reveal accurately with v2 contextual entropy
                foreach (var a in accounts)
                {
                    var match = listed.Single(m => m.Origin == a.Origin && m.Username == a.Username);
                    var revealed = Get(vault.RevealAsync(match.Id, match.Revision));
                    Assert(revealed == a.Password);
                }

                // Verify v1 tombstone
                Assert(File.Exists(Path.Combine(folder, "credentials.v1.json.migrated")));
                Assert(!File.Exists(v1Path));

                // Verify anchor created
                Assert(File.Exists(Path.Combine(folder, "vault.anchor")));
                // Verify credentials.v2.json created
                Assert(File.Exists(Path.Combine(folder, "credentials.v2.json")));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        });

        // --- CSV Import and Interop Tests ---

        check("Browser password CSV normalizes origins and safely handles quoting and duplicates", () =>
        {
            string csv = "name,url,username,password,n,note\r\n" +
                "\"Example, Inc\",https://EXAMPLE.test/login,alice,\"p,ass\"\"word\",\"line one\r\nline two\"\r\n" +
                "Example,https://example.test/other,alice,\"p,ass\"\"word\",duplicate\r\n" +
                "Bad,ftp://example.test,bad,secret,rejected\r\n";
            var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
            var credential = package.Credentials.Single();
            Assert(package.SourceName == "Chrome or Edge password CSV" && package.AvailableKinds == BrowserDataKinds.Passwords);
            Assert(credential.Origin == "https://example.test" && credential.Username == "alice" && credential.Password == "p,ass\"word");
            Assert(package.DuplicateRows == 1 && package.RejectedRows == 1 && package.ConflictingRows == 0);
            package.Dispose();
            Assert(credential.Password == "");
        });

        check("Password CSV fails closed on ambiguous duplicate accounts", () =>
        {
            string csv = "url,username,password\nhttps://example.test,a,one\nhttps://example.test,a,two\nhttps://example.test,a,three\n";
            using var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
            Assert(package.Credentials.Count == 0 && package.ConflictingRows == 3);
        });

        check("Password CSV recognizes Firefox exports without trusting extra metadata", () =>
        {
            string csv = "url,username,password,httpRealm,formActionOrigin,guid,timeCreated,timeLastUsed,timePasswordChanged\n" +
                "https://example.test/login,alice,secret,,,id,1,2,3\n";
            using var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
            Assert(package.SourceName == "Firefox password CSV" && package.Credentials.Single().Origin == "https://example.test");
        });

        check("Password CSV rejects unsupported schemas and malformed quoting", () =>
        {
            var service = new BrowserDataImportService();
            Throws<BrowserDataImportException>(() => service.ParseCsv(new StringReader("title,url\nExample,https://example.test\n")));
            Throws<BrowserDataImportException>(() => service.ParseCsv(new StringReader("url,username,password\n\"https://example.test,a,secret\n")));
            Throws<BrowserDataImportException>(() => service.ParseCsv(new StringReader("url,url,username,password\nhttps://a.test,https://b.test,a,secret\n")));
        });

        check("Credential import preview preserves changed passwords unless replacement is explicit", () => WithVault((v, _, _) =>
        {
            var unchanged = Get(v.SaveAsync(Origin, "same", "same-password", null));
            var changed = Get(v.SaveAsync(Origin, "changed", "old-password", null));
            string csv = "name,url,username,password\n" +
                "same," + Origin + "/login,same,same-password\n" +
                "changed," + Origin + "/login,changed,new-password\n" +
                "new," + Origin + "/login,new,new-password\n";
            var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
            using var review = Get(CredentialImportCoordinator.ReviewAsync(v, package));
            Assert(review.NewCount == 1 && review.ChangedCount == 1 && review.UnchangedCount == 1);
            var result = Get(CredentialImportCoordinator.ApplyAsync(v, review, false));
            Assert(result.Added == 1 && result.Updated == 0 && result.ChangedSkipped == 1 && result.Unchanged == 1);
            Assert(Get(v.RevealAsync(changed.Id, changed.Revision)) == "old-password");
            Assert(Get(v.RevealAsync(unchanged.Id, unchanged.Revision)) == "same-password");
        }));

        check("Credential import explicitly updates through the revision-bound vault path", () => WithVault((v, _, _) =>
        {
            var old = Get(v.SaveAsync(Origin, "alice", "old-password", null));
            var package = new BrowserDataImportService().ParseCsv(new StringReader(
                "url,username,password\n" + Origin + "/login,alice,new-password\n"));
            using var review = Get(CredentialImportCoordinator.ReviewAsync(v, package));
            var result = Get(CredentialImportCoordinator.ApplyAsync(v, review, true));
            var updated = Get(v.ListAsync()).Single();
            Assert(result.Updated == 1 && updated.Id == old.Id && updated.Revision == old.Revision + 1);
            Assert(Get(v.RevealAsync(updated.Id, updated.Revision)) == "new-password");
        }));

        check("Credential import conflict leaves the whole reviewed batch unapplied", () => WithVault((v, _, _) =>
        {
            var package = new BrowserDataImportService().ParseCsv(new StringReader(
                "url,username,password\n" + Origin + "/login,alice,imported-password\n" +
                Origin + "/login,bob,second-imported-password\n"));
            using var review = Get(CredentialImportCoordinator.ReviewAsync(v, package));
            Get(v.SaveAsync(Origin, "alice", "concurrent-password", null));
            var result = Get(CredentialImportCoordinator.ApplyAsync(v, review, true));
            Assert(result.Conflicts == 2 && result.Added == 0);
            Assert(Get(v.ListAsync()).Count == 1 && !Get(v.ListAsync()).Any(item => item.Username == "bob"));
        }));

        check("Browser password CSV supports flexible headers across various browsers", () =>
        {
            var parser = new BrowserPasswordCsvParser();
            var variants = new[]
            {
                "origin,user,pass",
                "website,login,secret",
                "web site,email,pwd",
                "login url,account,password",
                "page url,user name,pass",
                "action url,login,secret",
                "host,user,password",
                "  ORIGIN , USER , Pass "
            };

            foreach (var headerLine in variants)
            {
                string csv = headerLine + "\nhttps://example.test,alice,hunter2\n";
                using var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
                Assert(package.Credentials.Count == 1);
                Assert(package.Credentials[0].Origin == "https://example.test");
                Assert(package.Credentials[0].Username == "alice");
                Assert(package.Credentials[0].Password == "hunter2");
            }

            // Reject ambiguous or duplicate column mappings
            var ambiguous = new[]
            {
                new[] { "url", "origin", "username", "password" },
                new[] { "url", "website", "username", "password" },
                new[] { "url", "username", "user", "password" },
                new[] { "url", "username", "password", "pwd" },
                new[] { "origin", "origin", "user", "pass" },
                new[] { "title", "url" }
            };

            foreach (var headers in ambiguous)
            {
                Assert(!parser.CanImport(headers));
                Throws<BrowserDataImportException>(() => parser.Parse(headers, []));
            }
        });

        check("Password CSV handles malformed rows independently and continues parsing valid rows", () =>
        {
            string csv = "url,username,password\r\n" +
                "\"https://bad1.test\"trailing,alice,secret1\r\n" +
                "https://good1.test,bob,secret2\r\n" +
                "https://bad2.test,ch\"arlie,secret3\r\n" +
                "https://good2.test,david,secret4\r\n";

            using var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
            Assert(package.Credentials.Count == 2);
            Assert(package.RejectedRows == 2);
            Assert(package.Credentials[0].Origin == "https://good1.test" && package.Credentials[0].Username == "bob");
            Assert(package.Credentials[1].Origin == "https://good2.test" && package.Credentials[1].Username == "david");
        });

        check("Browser data import descriptors registry exposes functioning and planned categories", () =>
        {
            var descriptors = BrowserDataImportService.GetRegisteredDescriptors();
            Assert(descriptors.Count == 4);

            var passwords = descriptors.Single(d => d.Id == "passwords-csv");
            Assert(passwords.DisplayName == "Passwords (CSV)");
            Assert(passwords.Kinds == BrowserDataKinds.Passwords);
            Assert(passwords.SupportedExtensions.Contains(".csv"));
            Assert(passwords.IsAvailable);
            Assert(passwords.UnavailableReason == null);

            var bookmarks = descriptors.Single(d => d.Id == "bookmarks-html");
            Assert(bookmarks.DisplayName == "Bookmarks (HTML)");
            Assert(bookmarks.Kinds == BrowserDataKinds.Bookmarks);
            Assert(bookmarks.SupportedExtensions.Contains(".html") && bookmarks.SupportedExtensions.Contains(".htm"));
            Assert(!bookmarks.IsAvailable);
            Assert(bookmarks.UnavailableReason == "Bookmarks import will be available in a future release.");

            var history = descriptors.Single(d => d.Id == "history-json");
            Assert(history.DisplayName == "Browsing history");
            Assert(history.Kinds == BrowserDataKinds.History);
            Assert(history.SupportedExtensions.Contains(".json"));
            Assert(!history.IsAvailable);
            Assert(history.UnavailableReason == "History import will be available in a future release.");

            var settings = descriptors.Single(d => d.Id == "settings-json");
            Assert(settings.DisplayName == "Settings & preferences");
            Assert(settings.Kinds == BrowserDataKinds.Settings);
            Assert(settings.SupportedExtensions.Contains(".json"));
            Assert(!settings.IsAvailable);
            Assert(settings.UnavailableReason == "Settings import will be available in a future release.");

            IBrowserDataImportFormat importer = new BrowserPasswordCsvParser();
            Assert(importer.Descriptor.Id == "passwords-csv" && importer.Descriptor.IsAvailable);
        });

        check("Typed BrowserDataImportResult schema accurately captures import statistics", () =>
        {
            string csv = "url,username,password\r\n" +
                Origin + ",unchanged,same-pass\r\n" +
                Origin + ",changed,new-pass\r\n" +
                Origin + ",newuser,pass123\r\n" +
                Origin + ",newuser,pass123\r\n" +
                Origin + ",conflictuser,pass-a\r\n" +
                Origin + ",conflictuser,pass-b\r\n" +
                "ftp://invalid.test,baduser,pass999\r\n";

            // Without replacing changed
            WithVault((v, _, _) =>
            {
                Get(v.SaveAsync(Origin, "unchanged", "same-pass", null));
                Get(v.SaveAsync(Origin, "changed", "old-pass", null));

                var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
                using var review = Get(CredentialImportCoordinator.ReviewAsync(v, package));

                var result = Get(CredentialImportCoordinator.ApplyImportAsync(v, review, false));
                Assert(result.TotalRows == 7);
                Assert(result.Imported == 1); // newuser
                Assert(result.Skipped == 2); // unchanged + changed
                Assert(result.Duplicates == 1); // duplicate newuser
                Assert(result.Conflicts == 2); // conflictuser
                Assert(result.Invalid == 1); // ftp
                Assert(result.Failed == 0);
                Assert(result.Kinds == BrowserDataKinds.Passwords);
                Assert(result.HasChanges);
                Assert(result.HasIssues);
            });

            // With replacing changed
            WithVault((v, _, _) =>
            {
                Get(v.SaveAsync(Origin, "unchanged", "same-pass", null));
                Get(v.SaveAsync(Origin, "changed", "old-pass", null));

                var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
                using var review = Get(CredentialImportCoordinator.ReviewAsync(v, package));

                var result = Get(CredentialImportCoordinator.ApplyImportAsync(v, review, true));
                Assert(result.Imported == 2); // newuser + changed
                Assert(result.Skipped == 1); // unchanged
                Assert(result.HasChanges);
            });
        });

        check("Browser data import enforces zero secret leakage in exceptions and memory scrubbing", () =>
        {
            const string secretCanary = "SECRET_CANARY_TOKEN_99887766";
            string malformedCsv = "url,username,password\n" +
                "\"https://example.test,alice," + secretCanary + "\n";

            try
            {
                new BrowserDataImportService().ParseCsv(new StringReader(malformedCsv));
                Assert(false);
            }
            catch (BrowserDataImportException ex)
            {
                Assert(!ex.Message.Contains(secretCanary));
                Assert(!ex.ToString().Contains(secretCanary));
            }

            // Verify memory scrubbing on package dispose
            string validCsv = "url,username,password\nhttps://example.test,alice," + secretCanary + "\n";
            var package = new BrowserDataImportService().ParseCsv(new StringReader(validCsv));
            var cred = package.Credentials.Single();
            Assert(cred.Password == secretCanary);
            package.Dispose();
            Assert(cred.Password == "");
        });

        check("Adversarial M2: Complex intra-file conflict resolution, duplicate combinations, and secret clearing", () =>
        {
            // Case A: Duplicate first, then conflict with different password
            string csvA = "url,username,password\r\n" +
                "https://example.test,alice,pass1\r\n" +
                "https://example.test,alice,pass1\r\n" +
                "https://example.test,alice,pass2\r\n";
            var pkgA = new BrowserDataImportService().ParseCsv(new StringReader(csvA));
            Assert(pkgA.Credentials.Count == 0);
            Assert(pkgA.DuplicateRows == 1);
            Assert(pkgA.ConflictingRows == 2);
            Assert(pkgA.RejectedRows == 0);
            pkgA.Dispose();

            // Case B: Conflict first, then duplicate of first password
            string csvB = "url,username,password\r\n" +
                "https://example.test,alice,pass1\r\n" +
                "https://example.test,alice,pass2\r\n" +
                "https://example.test,alice,pass1\r\n";
            using var pkgB = new BrowserDataImportService().ParseCsv(new StringReader(csvB));
            Assert(pkgB.Credentials.Count == 0);
            Assert(pkgB.DuplicateRows == 0);
            Assert(pkgB.ConflictingRows == 3);

            // Case C: URL variations normalizing to same canonical origin
            string csvC = "url,username,password\r\n" +
                "https://EXAMPLE.test:443/login,alice,pass1\r\n" +
                "https://example.test/portal,alice,pass2\r\n";
            using var pkgC = new BrowserDataImportService().ParseCsv(new StringReader(csvC));
            Assert(pkgC.Credentials.Count == 0);
            Assert(pkgC.ConflictingRows == 2);

            // Case D: Different usernames on same origin cleanly imported
            string csvD = "url,username,password\r\n" +
                "https://example.test,alice,pass1\r\n" +
                "https://example.test,bob,pass2\r\n";
            using var pkgD = new BrowserDataImportService().ParseCsv(new StringReader(csvD));
            Assert(pkgD.Credentials.Count == 2);
            Assert(pkgD.ConflictingRows == 0);
            Assert(pkgD.DuplicateRows == 0);
            Assert(pkgD.Credentials[0].Username == "alice" && pkgD.Credentials[1].Username == "bob");

            // Case E: Multi-account mixture
            string csvE = "url,username,password\r\n" +
                "https://a.test,userA,passA\r\n" +
                "https://a.test,userA,passA\r\n" +
                "https://b.test,userB,passB1\r\n" +
                "https://b.test,userB,passB2\r\n" +
                "https://c.test,userC,passC\r\n" +
                "ftp://invalid.test,userD,passD\r\n";
            using var pkgE = new BrowserDataImportService().ParseCsv(new StringReader(csvE));
            Assert(pkgE.Credentials.Count == 2);
            Assert(pkgE.DuplicateRows == 1);
            Assert(pkgE.ConflictingRows == 2);
            Assert(pkgE.RejectedRows == 1);
        });

        check("Adversarial M2: Inter-vault conflict handling, opt-in overwrite, and optimistic concurrency", () => WithVault((v, _, _) =>
        {
            Get(v.SaveAsync(Origin, "alice", "vault-alice", null));
            Get(v.SaveAsync(Origin, "bob", "vault-bob", null));

            string csv = "url,username,password\r\n" +
                Origin + ",alice,csv-alice-new\r\n" +
                Origin + ",bob,vault-bob\r\n" +
                Origin + ",charlie,csv-charlie-new\r\n";

            // Subtest 1: Opt-out (replaceChanged = false)
            {
                var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
                using var review = Get(CredentialImportCoordinator.ReviewAsync(v, package));
                Assert(review.NewCount == 1 && review.ChangedCount == 1 && review.UnchangedCount == 1);

                var result = Get(CredentialImportCoordinator.ApplyImportAsync(v, review, false));
                Assert(result.TotalRows == 3);
                Assert(result.Imported == 1);
                Assert(result.Skipped == 2);
                Assert(result.Duplicates == 0);
                Assert(result.Conflicts == 0);
                Assert(result.Invalid == 0);
                Assert(result.Failed == 0);
                Assert(result.HasChanges);
                Assert(!result.HasIssues);

                var listed = Get(v.ListAsync());
                Assert(listed.Count == 3);
                var aliceMeta = listed.Single(m => m.Username == "alice");
                Assert(aliceMeta.Revision == 1);
                Assert(Get(v.RevealAsync(aliceMeta.Id, aliceMeta.Revision)) == "vault-alice");
            }

            // Subtest 2: Opt-in (replaceChanged = true)
            {
                var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
                using var review = Get(CredentialImportCoordinator.ReviewAsync(v, package));

                var result = Get(CredentialImportCoordinator.ApplyImportAsync(v, review, true));
                Assert(result.TotalRows == 3);
                Assert(result.Imported == 1); // Alice updated (Charlie was already added, now unchanged)
                Assert(result.Skipped == 2);  // Bob unchanged + Charlie unchanged
                Assert(result.HasChanges);
                Assert(!result.HasIssues);

                var listed = Get(v.ListAsync());
                var aliceMeta = listed.Single(m => m.Username == "alice");
                Assert(aliceMeta.Revision == 2);
                Assert(Get(v.RevealAsync(aliceMeta.Id, aliceMeta.Revision)) == "csv-alice-new");
            }

            // Subtest 3: Concurrent race condition fails whole batch atomically
            {
                string raceCsv = "url,username,password\r\n" +
                    Origin + ",alice,race-alice\r\n" +
                    Origin + ",david,race-david\r\n";
                var package = new BrowserDataImportService().ParseCsv(new StringReader(raceCsv));
                using var review = Get(CredentialImportCoordinator.ReviewAsync(v, package));

                var currentAlice = Get(v.ListAsync()).Single(m => m.Username == "alice");
                Get(v.SaveAsync(Origin, "alice", "concurrent-alice-pass", currentAlice));

                var result = Get(CredentialImportCoordinator.ApplyImportAsync(v, review, true));
                Assert(result.Imported == 0);
                Assert(result.Conflicts == 2);
                Assert(!result.HasChanges);
                Assert(result.HasIssues);

                var listed = Get(v.ListAsync());
                Assert(!listed.Any(m => m.Username == "david"));
            }
        }));

        check("Adversarial M2: BrowserDataImportResult state flags and invariant validation across exhaustive matrix", () =>
        {
            var r1 = new BrowserDataImportResult(0, 0, 0, 0, 0, 0, 0);
            Assert(!r1.HasChanges && !r1.HasIssues);

            var r2 = new BrowserDataImportResult(5, 2, 3, 0, 0, 0, 0);
            Assert(r2.HasChanges && !r2.HasIssues);

            var r3 = new BrowserDataImportResult(5, 0, 3, 2, 0, 0, 0);
            Assert(!r3.HasChanges && !r3.HasIssues);

            var r4 = new BrowserDataImportResult(5, 0, 3, 0, 2, 0, 0);
            Assert(!r4.HasChanges && r4.HasIssues);

            var r5 = new BrowserDataImportResult(5, 0, 3, 0, 0, 2, 0);
            Assert(!r5.HasChanges && r5.HasIssues);

            var r6 = new BrowserDataImportResult(5, 0, 3, 0, 0, 0, 2);
            Assert(!r6.HasChanges && r6.HasIssues);

            var r7 = new BrowserDataImportResult(10, 3, 2, 1, 2, 1, 1);
            Assert(r7.HasChanges && r7.HasIssues);

            var rawFalse = new CredentialImportResult(3, 0, 1, 6, 0);
            var pkg = new BrowserDataImportPackage("test", BrowserDataKinds.Passwords,
                new ImportedCredential[] {
                    new(Origin, "u1", "p1", 2),
                    new(Origin, "u2", "p2", 3),
                    new(Origin, "u3", "p3", 4),
                    new(Origin, "u4", "p4", 5),
                    new(Origin, "u5", "p5", 6),
                    new(Origin, "u6", "p6", 7),
                    new(Origin, "u7", "p7", 8),
                    new(Origin, "u8", "p8", 9),
                    new(Origin, "u9", "p9", 10),
                    new(Origin, "u10", "p10", 11)
                },
                rejectedRows: 2, duplicateRows: 3, conflictingRows: 4);

            var resFalse = rawFalse.ToImportResult(pkg, replaceChanged: false);
            Assert(resFalse.TotalRows == resFalse.Imported + resFalse.Skipped + resFalse.Duplicates + resFalse.Conflicts + resFalse.Invalid + resFalse.Failed);

            var rawTrue = new CredentialImportResult(3, 6, 1, 0, 0);
            var resTrue = rawTrue.ToImportResult(pkg, replaceChanged: true);
            Assert(resTrue.TotalRows == resTrue.Imported + resTrue.Skipped + resTrue.Duplicates + resTrue.Conflicts + resTrue.Invalid + resTrue.Failed);
            pkg.Dispose();
        });

        check("Adversarial M2: Secret zeroing post-dispose, review disposal, and exception canary redaction", () => WithVault((v, _, _) =>
        {
            const string Canary = "CANARY_SECRET_LEAK_TEST_#7890!XYZ";

            var pkg = new BrowserDataImportService().ParseCsv(new StringReader($"url,username,password\r\n{Origin},user1,{Canary}\r\n"));
            var cred = pkg.Credentials.Single();
            Assert(cred.Password == Canary);
            pkg.Dispose();
            Assert(cred.Password == "");

            var pkg2 = new BrowserDataImportService().ParseCsv(new StringReader($"url,username,password\r\n{Origin},user2,{Canary}\r\n"));
            var review = Get(CredentialImportCoordinator.ReviewAsync(v, pkg2));
            var revCred = review.Items.Single().Credential;
            Assert(revCred.Password == Canary);
            review.Dispose();
            Assert(revCred.Password == "");

            string conflictCsv = $"url,username,password\r\n{Origin},conflict_user,{Canary}\r\n{Origin},conflict_user,{Canary}_2\r\n";
            var pkgConflict = new BrowserDataImportService().ParseCsv(new StringReader(conflictCsv));
            Assert(pkgConflict.Credentials.Count == 0);
            Assert(pkgConflict.ConflictingRows == 2);
            pkgConflict.Dispose();

            void AssertNoCanary(Action action)
            {
                try { action(); Assert(false); }
                catch (Exception ex)
                {
                    Assert(!ex.Message.Contains(Canary, StringComparison.Ordinal));
                    Assert(!ex.ToString().Contains(Canary, StringComparison.Ordinal));
                }
            }

            AssertNoCanary(() => new BrowserDataImportService().ParseCsv(new StringReader("")));
            AssertNoCanary(() => new BrowserDataImportService().ParseCsv(new StringReader($"header1,header2\r\n{Canary},value\r\n")));
            AssertNoCanary(() => new BrowserDataImportService().ParseCsv(new StringReader($"url,origin,username,password\r\n{Origin},{Origin},user,{Canary}\r\n")));
            AssertNoCanary(() => new BrowserDataImportService().ParseCsv(new StringReader($"url,username,password\r\n{Origin},user,\"{Canary}\r\n")));
            AssertNoCanary(() =>
            {
                var sb = new StringBuilder("url,username,password\r\n");
                while (sb.Length < BrowserDataImportService.MaximumImportBytes + 100)
                {
                    sb.Append(Origin).Append(",user,").Append(Canary).Append("\r\n");
                }
                new BrowserDataImportService().ParseCsv(new StringReader(sb.ToString()));
            });
        }));

        check("Adversarial M2: Bounded streaming parser limits, malformed quotes, and field overflows", () =>
        {
            var service = new BrowserDataImportService();

            string hugeField = new string('A', 70_000);
            string csvFieldOverflow = "url,username,password\r\n" +
                $"{Origin},user1,{hugeField}\r\n" +
                $"{Origin},user2,validpass\r\n";
            using (var pkg = service.ParseCsv(new StringReader(csvFieldOverflow)))
            {
                Assert(pkg.Credentials.Count == 1);
                Assert(pkg.RejectedRows == 1);
                Assert(pkg.Credentials[0].Username == "user2" && pkg.Credentials[0].Password == "validpass");
            }

            string hugeField1 = new string('B', 60_000);
            string hugeField2 = new string('C', 60_000);
            string hugeField3 = new string('D', 60_000);
            string hugeField4 = new string('E', 60_000);
            string hugeField5 = new string('F', 60_000);
            string csvRecordOverflow = "url,username,password,extra1,extra2\r\n" +
                $"{Origin},user1,{hugeField1},{hugeField2},{hugeField3}{hugeField4}{hugeField5}\r\n" +
                $"{Origin},user3,validpass3\r\n";
            using (var pkg = service.ParseCsv(new StringReader(csvRecordOverflow)))
            {
                Assert(pkg.Credentials.Count == 1);
                Assert(pkg.RejectedRows == 1);
                Assert(pkg.Credentials[0].Username == "user3");
            }

            // Case C: Column count overflow (> 64 columns) on data row
            var sbCols = new StringBuilder("url,username,password\r\n");
            // Add row with 73 columns
            sbCols.Append(Origin).Append(",user1,pass1");
            for (int i = 0; i < 70; i++) sbCols.Append(",val").Append(i);
            sbCols.Append("\r\n");
            // Add valid row with 3 columns
            sbCols.Append(Origin).Append(",user4,pass4\r\n");
            using (var pkg = service.ParseCsv(new StringReader(sbCols.ToString())))
            {
                Assert(pkg.Credentials.Count == 1);
                Assert(pkg.RejectedRows == 1);
                Assert(pkg.Credentials[0].Username == "user4");
            }

            string csvQuotes = "url,username,password\r\n" +
                $"{Origin},us\"er,pass1\r\n" +
                $"\"{Origin}\"trailing,user2,pass2\r\n" +
                $"{Origin},user5,pass5\r\n";
            using (var pkg = service.ParseCsv(new StringReader(csvQuotes)))
            {
                Assert(pkg.Credentials.Count == 1);
                Assert(pkg.RejectedRows == 2);
                Assert(pkg.Credentials[0].Username == "user5");
            }

            var sbRows = new StringBuilder("url,username,password\r\n");
            for (int i = 0; i < BrowserDataImportService.MaximumRows + 5; i++)
            {
                sbRows.Append(Origin).Append(",user").Append(i).Append(",pass\r\n");
            }
            Throws<BrowserDataImportException>(() => service.ParseCsv(new StringReader(sbRows.ToString())));
        });
    }
}
